using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using AIBridge.Internal.Json;
using UnityEditor;

namespace AIBridge.Editor
{
    public partial class CodeCommand
    {
        private static CommandResult StartPipelineRunJob(CommandRequest request, SourceText source, string entry,
            string argumentsJson, PipelineOperationRecord record, MethodInfo method)
        {
            PipelineRunJobSession session;
            try
            {
                session = method.Invoke(null, new object[] { source.OriginalCode, source.Path, entry, argumentsJson,
                    record.timeoutMs, record.dryRun, record.confirmed }) as PipelineRunJobSession;
                if (session == null) throw new InvalidOperationException();
            }
            catch (Exception)
            {
                return CommandResult.Failure(request.id, "Pipeline detached preparation was rejected before submission.");
            }
            record.detached = true;
            record.upstreamExecutionTimeoutDisabled = true;
            record.operationKind = record.dryRun ? "detached_compile_only" : "detached_execute";
            record.targetIdentity = session.TargetIdentity;
            record.adapterGeneration = session.Generation;
            try
            {
                // 凭据仅在 session；意图和目标指纹必须在唯一一次 POST 之前完成落盘。
                SavePipelineRecord(record);
            }
            catch (Exception)
            {
                session.Release(false);
                record.active = false; record.busy = true; record.mayStillBeRunning = true; record.status = "unknown";
                record.errorCode = "pipelineJobIntentWriteFailed";
                record.error = "Job intent could not be persisted; no submission was made. Explicitly release the local unknown state before a new request.";
                _activeOperation = new PipelineUnknownOperation(record);
                return FailureWithData(request.id, record.error, record);
            }
            try
            {
                _activeOperation = new PipelineJobOperation(record, session, session.SubmitAsync());
                EditorApplication.update -= OnAsyncUpdate;
                EditorApplication.update += OnAsyncUpdate;
                return null;
            }
            catch (Exception)
            {
                // 从调用 SubmitAsync 开始就无法由本层证明未发送；不能当作安全的同步拒绝。
                session.Release(false);
                record.active = false; record.busy = true; record.mayStillBeRunning = true; record.status = "unknown";
                record.errorCode = "pipelineJobSubmissionUnknown";
                record.error = "Detached submission result is unknown; do not replay automatically.";
                _activeOperation = new PipelineUnknownOperation(record);
                SavePipelineRecord(record);
                return FailureWithData(request.id, record.error, record);
            }
        }

        private static CommandResult CancelRemotePipelineJob(CommandRequest request)
        {
            foreach (var key in request.@params.Keys)
                if (key != "action" && key != "requestId" && key != "confirm")
                    return CommandResult.Failure(request.id, "job_cancel accepts only requestId and confirm.");
            if (!TryReadPipelineFlag(request, "confirm", false, out var confirmed) || !confirmed)
                return CommandResult.Failure(request.id, "Remote cancellation requires --confirm true; side effects are not rolled back.");
            var id = request.GetParam<string>("requestId", null);
            if (string.IsNullOrEmpty(id) || !(_activeOperation is PipelineJobOperation operation) || operation.RequestId != id)
                return CommandResult.Failure(request.id, "Only an active, registered job in this Editor generation can be canceled.");
            return operation.RequestRemoteCancel(request.id);
        }

        private static bool ValidDetachedRecord(System.Collections.Generic.Dictionary<string, object> data, PipelineOperationRecord record)
        {
            var marked = data.TryGetValue("detached", out var detached);
            if (marked && !(detached is bool)) return false;
            if (!record.detached)
                return string.IsNullOrEmpty(record.remoteJobId) && string.IsNullOrEmpty(record.targetIdentity)
                    && string.IsNullOrEmpty(record.adapterGeneration) && !record.upstreamExecutionTimeoutDisabled
                    && !(record.operationKind ?? string.Empty).StartsWith("detached_", StringComparison.Ordinal);
            foreach (var key in new[] { "upstreamExecutionTimeoutDisabled", "executionEnded", "cancellationRequested", "sideEffectsUnknown", "confirmed" })
                if (!data.TryGetValue(key, out var flag) || !(flag is bool)) return false;
            if (!record.confirmed || !record.upstreamExecutionTimeoutDisabled || !record.sideEffectsUnknown
                || record.pipelineCommand != "script run" || !IsHex(record.targetIdentity, 64) || !IsHex(record.adapterGeneration, 32)) return false;
            if (record.operationKind != (record.dryRun ? "detached_compile_only" : "detached_execute")) return false;
            if (record.remoteJobId != null && !PipelineJobProtocol.IsJobId(record.remoteJobId)) return false;
            if (record.executionEnded && (record.active || record.busy || record.mayStillBeRunning || record.remoteJobId == null)) return false;
            if (!record.executionEnded && !record.busy && !record.waitCanceled) return false;
            if ((record.status == "executed" || record.status == "validated" || record.status == "canceled" || record.status == "failed")
                && !record.executionEnded) return false;
            return true;
        }

        private static bool IsHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (var c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private sealed class PipelineJobOperation : ICodeOperation
        {
            private readonly PipelineOperationRecord _record;
            private readonly PipelineRunJobSession _session;
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            private Task<PipelineJobObservation> _task;
            private Task<PipelineJobObservation> _cancelTask;
            private string _cancelRequestId;
            private bool _submission = true;
            private long _nextPoll;
            private bool _unknown;
            private bool _localReleased;
            private bool _storageFailed;
            private string _cancelCompletionId;

            public PipelineJobOperation(PipelineOperationRecord record, PipelineRunJobSession session, Task<PipelineJobObservation> submission)
            {
                _record = record; _session = session; _task = submission;
                if (submission == null) throw new ArgumentException("Job submission task is required.");
            }
            public string RequestId => _record.requestId;

            public bool Step(out CommandResult result, out bool shouldRelease)
            {
                if (_storageFailed) { result = null; shouldRelease = false; return false; }
                var originalWritten = _record.resultWritten;
                try { return StepCore(out result, out shouldRelease); }
                catch (Exception)
                {
                    // 落盘或本地编排失败不能重新消费已完成任务，更不能重发 POST。保留占位。
                    _storageFailed = true; _unknown = true;
                    _record.status = "unknown"; _record.active = false; _record.busy = true;
                    _record.executionEnded = false; _record.mayStillBeRunning = true;
                    _record.errorCode = "pipelineJobStateWriteFailed";
                    _record.error = "Job state could not be persisted; no request was replayed. Do not assume execution stopped.";
                    var cancelId = _cancelCompletionId ?? _cancelRequestId;
                    _cancelCompletionId = null; _cancelRequestId = null;
                    if (cancelId != null) WriteResultFile(FailureWithData(cancelId, _record.error, _record));
                    result = originalWritten ? null : FailureWithData(RequestId, _record.error, _record);
                    _record.resultWritten = true; shouldRelease = false; return true;
                }
            }

            private bool StepCore(out CommandResult result, out bool shouldRelease)
            {
                result = null; shouldRelease = false;
                _record.elapsedMs = _watch.ElapsedMilliseconds;
                if (_task != null && _task.IsCompleted)
                {
                    var observation = ReadTask(_task); _task = null;
                    ApplyObservation(observation, _submission); _submission = false;
                    _nextPoll = _watch.ElapsedMilliseconds + 500;
                }
                if (_cancelTask != null && _cancelTask.IsCompleted) CompleteRemoteCancel();
                if (_cancelRequestId != null && _cancelTask == null && _task == null)
                {
                    if (_record.executionEnded) CompleteRemoteCancelWithState();
                    else if (_unknown) RejectPendingCancel();
                    else _cancelTask = _session.CancelAsync(true);
                }
                if (_record.executionEnded)
                {
                    _record.active = false; _record.busy = false;
                    _session.Release(true);
                    shouldRelease = true;
                }
                else if (!_unknown && !_localReleased && _task == null && _cancelTask == null && _watch.ElapsedMilliseconds >= _nextPoll)
                    _task = _session.ObserveAsync();
                if (_localReleased && _task == null && _cancelTask == null)
                {
                    _session.Release(_record.executionEnded); shouldRelease = true;
                    _record.active = false; _record.busy = false; SavePipelineRecord(_record);
                }
                if (!_record.resultWritten && (_record.executionEnded || _unknown || _watch.ElapsedMilliseconds >= _record.timeoutMs))
                {
                    if (!_record.executionEnded && !_unknown)
                    {
                        _record.timedOut = true;
                        _record.error = "Local job wait expired; upstream execution has no timeout. Query code status; do not replay.";
                    }
                    _record.resultWritten = true; SavePipelineRecord(_record);
                    result = _record.status == "executed" || _record.status == "validated"
                        ? CommandResult.Success(RequestId, _record)
                        : FailureWithData(RequestId, _record.error ?? "Pipeline job did not succeed.", _record);
                    return true;
                }
                return shouldRelease;
            }

            private static PipelineJobObservation ReadTask(Task<PipelineJobObservation> task)
            {
                if (task.IsFaulted) { var observed = task.Exception; }
                return !task.IsFaulted && !task.IsCanceled && task.Result != null ? task.Result
                    : new PipelineJobObservation { errorCode = "pipelineJobTaskUnknown", error = "Job observation failed; do not replay." };
            }

            private void ApplyObservation(PipelineJobObservation value, bool submission)
            {
                _record.httpStatus = value.httpStatus;
                if (!value.valid || value.status != "observed" || value.command != "script run"
                    || !PipelineJobProtocol.IsJobId(value.jobId)
                    || (!submission && value.jobId != _record.remoteJobId))
                {
                    _unknown = true; _record.status = "unknown"; _record.mayStillBeRunning = true;
                    _record.errorCode = value.errorCode ?? "pipelineJobResponseInvalid";
                    _record.error = "Pipeline job state is unknown; do not replay or assume execution stopped.";
                    SavePipelineRecord(_record); return;
                }
                _record.remoteJobId = value.jobId; _record.jobState = value.state;
                _record.cancellationRequested = value.cancellationRequested;
                _record.lastObservedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                _record.mayStillBeRunning = !value.executionEnded;
                _record.executionEnded = value.executionEnded;
                _record.status = "job_" + value.state;
                _record.error = null; _record.errorCode = null;
                if (value.executionEnded) ApplyJobResult(PipelineCodeProtocol.ParseRunJobResult(value, _record.dryRun));
                _record.active = !_record.executionEnded; _record.busy = !_record.executionEnded;
                SavePipelineRecord(_record);
            }

            private void ApplyJobResult(PipelineCodeResult value)
            {
                _record.status = value.status; _record.error = value.error; _record.errorCode = value.errorCode;
                _record.errorDetails = value.errorDetails; _record.resultMessage = value.message;
                _record.resultValue = AIBridgeJson.PreserveObjectNulls(value.resultValue);
                _record.executionTimeMs = value.executionTimeMs; _record.diagnostics = value.diagnostics;
                // envelope 的 terminal 只证明父作业终止；坏业务 payload 仍保守锁住本地。
                if (value.mayStillBeRunning)
                {
                    _record.executionEnded = false; _record.mayStillBeRunning = true; _unknown = true;
                }
            }

            public object BuildStatusData() { return _record; }

            public CommandResult RequestRemoteCancel(string requestId)
            {
                if (_unknown || _localReleased || _cancelRequestId != null || !PipelineJobProtocol.IsJobId(_record.remoteJobId))
                    return CommandResult.Failure(requestId, "Job is unknown, unbound or already has a cancellation request pending.");
                // 等当前 GET 完成再发送唯一一次取消 POST，不与轮询争用同一会话。
                _cancelRequestId = requestId;
                return null;
            }

            private void CompleteRemoteCancel()
            {
                var value = ReadTask(_cancelTask); _cancelTask = null;
                _cancelCompletionId = _cancelRequestId; _cancelRequestId = null;
                ApplyObservation(value, false);
                var accepted = value.valid && !_unknown && (value.cancellationRequested || value.executionEnded);
                WriteResultFile(accepted ? CommandResult.Success(_cancelCompletionId, new
                {
                    requestAccepted = true, executionEnded = _record.executionEnded, notStarted = value.notStarted,
                    sideEffectsUnknown = true, remoteJobId = _record.remoteJobId, jobState = value.state
                }) : FailureWithData(_cancelCompletionId, "Remote cancellation was not confirmed; do not assume execution stopped.", _record));
                _cancelCompletionId = null;
            }

            private void CompleteRemoteCancelWithState()
            {
                WriteResultFile(CommandResult.Success(_cancelRequestId, new
                {
                    requestAccepted = false, executionEnded = true, sideEffectsUnknown = true,
                    remoteJobId = _record.remoteJobId, jobState = _record.jobState,
                    message = "Job ended before cancellation was sent; no cancellation POST was made."
                }));
                _cancelRequestId = null;
            }

            private void RejectPendingCancel()
            {
                WriteResultFile(FailureWithData(_cancelRequestId, "Job observation became unknown; no cancellation was replayed.", _record));
                _cancelRequestId = null;
            }

            public CommandResult CancelForOriginalRequest()
            {
                _localReleased = true; _record.waitCanceled = true;
                var write = !_record.resultWritten; _record.resultWritten = true;
                _record.mayStillBeRunning = !_record.executionEnded;
                SavePipelineRecord(_record);
                _storageFailed = false;
                return write ? FailureWithData(RequestId, "Local job waiting released; remote execution may continue.", _record) : null;
            }
        }
    }
}
