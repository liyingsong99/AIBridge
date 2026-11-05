using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIBridge.Internal.Json;
using UnityEngine;

namespace AIBridge.Editor.UnityPipelineAdapter
{
    public static partial class UnityPipelineAdapter
    {
        private static readonly string JobGeneration = Guid.NewGuid().ToString("N");

        internal static PipelineRunJobSession PrepareRunJob(string source, string sourcePath, string entry,
            string argumentsJson, int timeoutMs, bool dryRun, bool confirmed)
        {
            if (!PipelineCodeProtocol.IsExecutionAuthorized() || !confirmed)
                throw new InvalidOperationException("Pipeline job requires authorization and per-request confirmation.");
            if (timeoutMs < 1000 || timeoutMs > 60000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            var descriptorPath = GetPipelineDescriptorPath();
            if (!TryReadPortDescriptor(descriptorPath, out var descriptor))
                throw new InvalidOperationException("Pipeline target is unavailable.");
            var package = FindPipelinePackage();
            if (package == null || package.Version != PipelineCodeProtocol.SupportedVersion)
                throw new InvalidOperationException("Pipeline job protocol version is unsupported.");
            lock (EvalLock)
            {
                if (s_EvalRequestActive) throw new InvalidOperationException("Pipeline code request is busy.");
                s_EvalRequestActive = true;
            }
            PipelineRunSource.Snapshot snapshot = null;
            try
            {
                snapshot = PipelineRunSource.Capture(Directory.GetParent(Application.dataPath).FullName, source, sourcePath);
                return new RunJobSession(snapshot, descriptor, descriptorPath, entry, argumentsJson, timeoutMs, dryRun);
            }
            catch
            {
                snapshot?.DeleteAfterConfirmedOutcome();
                lock (EvalLock) { s_EvalRequestActive = false; }
                throw;
            }
        }

        private sealed class RunJobSession : PipelineRunJobSession
        {
            private readonly PipelineRunSource.Snapshot _snapshot;
            private readonly PipelineServerDescriptor _descriptor;
            private readonly string _descriptorPath;
            private readonly string _body;
            private readonly int _timeoutMs;
            private readonly string _identity;
            private string _jobId;
            private int _submitted;
            private int _ioActive;
            private int _released;
            private bool _observedEnd;

            public RunJobSession(PipelineRunSource.Snapshot snapshot, PipelineServerDescriptor descriptor,
                string descriptorPath, string entry, string argumentsJson, int timeoutMs, bool dryRun)
            {
                _snapshot = snapshot; _descriptor = descriptor; _descriptorPath = descriptorPath; _timeoutMs = timeoutMs;
                var data = AIBridgeJson.DeserializeObjectStrict(PipelineCodeProtocol.BuildRunRequest(
                    snapshot.Path, entry, argumentsJson, timeoutMs, dryRun));
                data["job"] = true;
                _body = AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(data));
                // 不对 token 做可离线比较的落盘摘要；世代由本 domain 的随机值独立标识。
                var identity = AIBridgeJson.Serialize(new Dictionary<string, object> { { "pid", descriptor.pid },
                    { "port", descriptor.port }, { "project", descriptor.projectPath },
                    { "version", descriptor.unityVersion }, { "startedAt", descriptor.startedAt } });
                using (var sha = SHA256.Create())
                    _identity = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
            }

            public override string TargetIdentity => _identity;
            public override string Generation => JobGeneration;

            public override Task<PipelineJobObservation> SubmitAsync()
            {
                if (!PipelineCodeProtocol.IsExecutionAuthorized()) return Task.FromResult(UnknownJob("pipelineAuthorizationDenied"));
                if (Interlocked.CompareExchange(ref _submitted, 1, 0) != 0)
                    return Task.FromResult(UnknownJob("pipelineJobReplayDenied"));
                return StartIo(true, false);
            }

            public override Task<PipelineJobObservation> ObserveAsync()
            {
                if (!PipelineCodeProtocol.IsExecutionAuthorized()) return Task.FromResult(UnknownJob("pipelineAuthorizationDenied"));
                return StartIo(false, false);
            }

            public override Task<PipelineJobObservation> CancelAsync(bool confirmed)
            {
                if (!PipelineCodeProtocol.IsExecutionAuthorized() || !confirmed)
                    return Task.FromResult(UnknownJob("pipelineConfirmationRequired"));
                return StartIo(false, true);
            }

            private Task<PipelineJobObservation> StartIo(bool submit, bool cancel)
            {
                if (Volatile.Read(ref _released) != 0 || (!submit && !PipelineJobProtocol.IsJobId(_jobId)))
                    return Task.FromResult(UnknownJob("pipelineJobBindingLost"));
                if (Interlocked.CompareExchange(ref _ioActive, 1, 0) != 0)
                    return Task.FromResult(UnknownJob("pipelineJobObservationBusy"));
                try { return Task.Run(() => Exchange(submit, cancel)); }
                catch { Interlocked.Exchange(ref _ioActive, 0); throw; }
            }

            private PipelineJobObservation Exchange(bool submit, bool cancel)
            {
                try
                {
                    if (!DescriptorIsUnchanged(_descriptorPath, _descriptor)) return UnknownJob("pipelineTargetChanged");
                    if (!submit && !cancel)
                        return Remember(ReadJobOnce(_descriptor, _descriptorPath, _jobId, "script run", _timeoutMs));
                    var path = submit ? "/api/exec?omit_nulls=false" : "/api/job/cancel?omit_nulls=false";
                    var request = CreateAuthenticatedRequest(_descriptor, path, _timeoutMs);
                    request.Method = "POST"; request.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(submit ? _body : PipelineJobProtocol.BuildCancelRequest(_jobId));
                    request.ContentLength = bytes.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                    using (var response = (HttpWebResponse)request.GetResponse()) return ReadResponse(response, submit);
                }
                catch (WebException exception)
                {
                    using (var response = exception.Response as HttpWebResponse)
                        if (response != null) return ReadResponse(response, submit);
                    return UnknownJob("pipelineJobTransportUnknown");
                }
                catch (Exception) { return UnknownJob("pipelineJobTransportUnknown"); }
                finally { Interlocked.Exchange(ref _ioActive, 0); }
            }

            private PipelineJobObservation ReadResponse(HttpWebResponse response, bool submit)
            {
                try
                {
                    if (!submit) return Remember(ReadJobResponse(response, _descriptor, _descriptorPath, _jobId, "script run"));
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        var buffer = new char[MaxResponseChars + 1];
                        var count = 0;
                        while (count < buffer.Length)
                        {
                            var read = reader.Read(buffer, count, buffer.Length - count);
                            if (read == 0) break;
                            count += read;
                        }
                        if (count > MaxResponseChars) return UnknownJob("pipelineJobResponseLimit");
                        var value = PipelineJobProtocol.ParseSubmission(new string(buffer, 0, count), (int)response.StatusCode, "script run");
                        RedactJobObservation(value, _descriptor.evalToken);
                        if (!DescriptorIsUnchanged(_descriptorPath, _descriptor)) return UnknownJob("pipelineTargetChanged");
                        if (value.valid) _jobId = value.jobId;
                        return value;
                    }
                }
                catch (Exception) { return UnknownJob("pipelineJobResponseInvalid"); }
            }

            private PipelineJobObservation Remember(PipelineJobObservation value)
            {
                if (value.valid && value.executionEnded) _observedEnd = true;
                return value;
            }

            public override void Release(bool executionEnded)
            {
                // 持有中的网络请求可能仍在使用快照，必须等本次 IO 完成；不提供强制删除接口。
                if (Volatile.Read(ref _ioActive) != 0) return;
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                if ((executionEnded && _observedEnd) || Volatile.Read(ref _submitted) == 0)
                    _snapshot.DeleteAfterConfirmedOutcome();
                else lock (EvalLock) { UnknownRunSnapshots.Add(_snapshot); }
                lock (EvalLock) { s_EvalRequestActive = false; }
            }
        }
    }
}
