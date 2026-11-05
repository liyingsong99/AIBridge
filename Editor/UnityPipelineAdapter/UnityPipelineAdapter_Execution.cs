using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIBridge.Internal.Json;

namespace AIBridge.Editor.UnityPipelineAdapter
{
    public static partial class UnityPipelineAdapter
    {
        // 会话共享一次提交和本地租约；unknown只能显式释放，不发送远端取消。
        private sealed class BoundExecution<TResult>
        {
            private readonly PipelineServerDescriptor _descriptor;
            private readonly string _path, _body;
            private readonly int _timeout, _limit;
            private readonly Func<string, int, TResult> _parse;
            private readonly Func<TResult, TResult> _redact;
            private readonly Func<TResult> _unknown;
            private int _submitted, _ioActive, _releaseRequested, _released;
            internal readonly string Identity;

            internal BoundExecution(PipelineServerDescriptor descriptor, string path, string body, int timeout, int limit,
                Func<string, int, TResult> parse, Func<TResult, TResult> redact, Func<TResult> unknown)
            {
                _descriptor = descriptor; _path = path; _body = body; _timeout = timeout; _limit = limit;
                _parse = parse; _redact = redact; _unknown = unknown;
                var identity = AIBridgeJson.Serialize(new Dictionary<string, object>
                {
                    { "pid", descriptor.pid }, { "port", descriptor.port }, { "project", descriptor.projectPath },
                    { "version", descriptor.unityVersion }, { "startedAt", descriptor.startedAt }
                });
                // 身份摘要不包含token；世代独立随机，凭据仅保留于捕获会话。
                using (var sha = SHA256.Create())
                    Identity = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
            }

            internal Task<TResult> SubmitAsync()
            {
                if (Volatile.Read(ref _releaseRequested) != 0 || Interlocked.CompareExchange(ref _submitted, 1, 0) != 0)
                    return Task.FromResult(_unknown());
                Interlocked.Exchange(ref _ioActive, 1);
                try { return Task.Run(() => Exchange()); }
                catch { Interlocked.Exchange(ref _ioActive, 0); throw; }
            }

            private TResult Exchange()
            {
                try
                {
                    if (!DescriptorIsUnchanged(_path, _descriptor)) return _unknown();
                    var request = CreateAuthenticatedRequest(_descriptor, "/api/exec", _timeout + TransportGraceMs);
                    request.Method = "POST"; request.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(_body); request.ContentLength = bytes.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                    try { using (var response = (HttpWebResponse)request.GetResponse()) return Read(response); }
                    catch (WebException ex)
                    { using (var response = ex.Response as HttpWebResponse) return response == null ? _unknown() : Read(response); }
                }
                catch (Exception) { return _unknown(); }
                finally { Interlocked.Exchange(ref _ioActive, 0); ReleaseIfRequested(); }
            }

            private TResult Read(HttpWebResponse response)
            {
                using (var reader = new StreamReader(response.GetResponseStream(), new UTF8Encoding(false, true), false))
                {
                    var buffer = new char[_limit + 1]; var count = 0;
                    while (count < buffer.Length)
                    {
                        var read = reader.Read(buffer, count, buffer.Length - count);
                        if (read == 0) break; count += read;
                    }
                    if (count > _limit) return _unknown();
                    var parsed = _redact(_parse(new string(buffer, 0, count), (int)response.StatusCode));
                    return DescriptorIsUnchanged(_path, _descriptor) ? parsed : _unknown();
                }
            }

            internal void Release() { Interlocked.Exchange(ref _releaseRequested, 1); ReleaseIfRequested(); }
            private void ReleaseIfRequested()
            {
                if (Volatile.Read(ref _releaseRequested) == 0 || Volatile.Read(ref _ioActive) != 0) return;
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                lock (EvalLock) { s_EvalRequestActive = false; }
            }
        }
    }
}
