using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AIBridge.Internal.Json;
using AIBridge.Runtime.Internal;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    [InitializeOnLoad]
    public partial class CodeCommand
    {
        private const string PipelineAdapterTypeName = "AIBridge.Editor.UnityPipelineAdapter.UnityPipelineAdapter, cn.lys.aibridge.Editor.UnityPipelineAdapter";
        private const string PipelineOperationsDirectory = "code-operations";

        static CodeCommand()
        {
#if UNITY_2020_2_OR_NEWER
            // 导入 worker 也加载 Editor 程序集，但它不是任务所属 Editor，不能改写共享操作状态。
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
#endif
            try { RecoverPipelineOperation(); }
            catch (Exception)
            {
                // 可选状态存储失败不能令核心类型初始化永久失败；不输出异常正文。
                UnityEngine.Debug.LogWarning("[AIBridge] Pipeline operation recovery storage is unavailable; query code status before retrying.");
            }
        }

        private static void RecoverPipelineOperation()
        {
            var record = LoadPipelineRecoveryRecord(out var unreadable);
            ApplyRecoveredPipelineRecord(record, unreadable);
        }

        // 读取与应用分开，使恢复测试只扫描自己的样本，不依赖真实工程历史日志。
        private static void ApplyRecoveredPipelineRecord(PipelineOperationRecord record, bool unreadable)
        {
            if (record == null)
            {
                if (!unreadable) return;
                record = CreateCorruptPipelineRecord();
                _activeOperation = new PipelineUnknownOperation(record);
                return;
            }
            if (!record.active)
            {
                if (record.busy) _activeOperation = new PipelineUnknownOperation(record);
                return;
            }
            // Pipeline 的内存 job 不跨 reload；本地状态只保留 unknown，不重放原请求。
            record.active = false;
            record.busy = true;
            record.mayStillBeRunning = true;
            record.status = "unknown";
            record.errorCode = "pipelineStateLost";
            record.error = "Pipeline operation state was lost during reload or Editor restart; do not replay automatically.";
            if (!record.resultWritten)
            {
                record.resultWritten = true;
                WriteResultFile(FailureWithData(record.requestId, record.error, record));
            }
            _activeOperation = new PipelineUnknownOperation(record);
            SavePipelineRecord(record);
        }

        private static CommandResult StartPipelineDryRun(CommandRequest request, SourceText source)
        {
            return StartPipelineOperation(request, source, null, null);
        }

        private static CommandResult StartPipelineOperation(CommandRequest request, SourceText source, string entry, string argumentsJson)
        {
            var run = entry != null;
            if (!TryReadPipelineFlag(request, "job", false, out var job))
                return CommandResult.Failure(request.id, "Pipeline job must be a boolean value.");
            if (job && !run) return CommandResult.Failure(request.id, "Detached jobs currently require code run.");
            if (job && (!TryReadPipelineFlag(request, "confirm", false, out var jobConfirmed) || !jobConfirmed))
                return CommandResult.Failure(request.id, "Detached jobs have no upstream execution timeout and require --confirm true.");
            // Pipeline 默认仅编译；执行必须携带字面 bool 确认，不能把数字或错误参数强转为授权。
            if (!TryReadPipelineFlag(request, "dry-run", true, out var dryRun)
                || !TryReadPipelineFlag(request, "confirm", false, out var confirmed))
                return CommandResult.Failure(request.id, "Pipeline dry-run and confirm must be boolean values.");
            if (!dryRun && !confirmed)
                return CommandResult.Failure(request.id, "Pipeline code execution requires a per-request --confirm true.");
            var timeoutMs = Mathf.Clamp(request.GetParam("timeout", DefaultTimeoutMs), MinTimeoutMs, MaxTimeoutMs);
            var existing = LoadPipelineRecordWithStatus(request.id, out var requestStateInvalid);
            if (existing != null)
            {
                if (existing.busy) RetainCorruptOperation(existing.requestId);
                return FailureWithData(request.id, "This Pipeline operation id already exists; query code status instead of replaying it.", existing);
            }
            var requestRecordPath = PipelineRecordPath(request.id);
            var latestRecordPath = PipelineRecordPath(null);
            if (!TryGetRecordFileExists(requestRecordPath, out var requestFileExists)
                || !TryGetRecordFileExists(latestRecordPath, out var latestFileExists))
                return FailureWithData(request.id, "Pipeline operation storage is unavailable; refusing to submit.",
                    new { status = "unknown", errorCode = "pipelineStateInvalid", requestId = request.id, busy = true, mayStillBeRunning = true });
            var latest = LoadPipelineRecoveryRecord(out var latestStateInvalid);
            if (requestStateInvalid || latestStateInvalid || requestFileExists || (latest != null && latest.busy)
                || (latestFileExists && latest == null))
            {
                RetainCorruptOperation(request.id);
                return FailureWithData(request.id, "Pipeline operation state is unavailable or busy; refusing to overwrite or replay the operation id.",
                    new { status = "unknown", errorCode = "pipelineStateInvalid", requestId = request.id, busy = true, mayStillBeRunning = true });
            }
            // 持久操作状态必须先核验；缺可选包也不能掩盖损坏日志或丢失 busy 占位。
            var type = System.Type.GetType(PipelineAdapterTypeName);
            var signature = run
                ? new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(bool), typeof(bool) }
                : new[] { typeof(string), typeof(int), typeof(bool), typeof(bool) };
            var methodName = job ? "PrepareRunJob" : (run ? "RunAsync" : "EvaluateAsync");
            var method = type == null ? null : type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic,
                null, signature, null);
            if (method == null)
                return FailureWithData(request.id, "Pipeline eval adapter is unavailable.",
                    new { backend = "pipeline", status = "unavailable", errorCode = "pipelineAdapterUnavailable" });
            var record = CreatePipelineRecord(request.id, source, timeoutMs);
            record.operationKind = dryRun ? "compile_only" : "execute";
            record.dryRun = dryRun;
            record.confirmed = confirmed;
            record.pipelineCommand = run ? "script run" : "script eval";
            record.entry = entry;
            record.argumentsSha256 = run ? HashText(argumentsJson) : null;
            if (job) return StartPipelineRunJob(request, source, entry, argumentsJson, record, method);
            // 先持久化意图再提交，避免提交后 reload 被误认为从未发送。记录不包含源码或凭据。
            SavePipelineRecord(record);
            try
            {
                var arguments = run
                    ? new object[] { source.OriginalCode, source.Path, entry, argumentsJson, timeoutMs, dryRun, confirmed }
                    : new object[] { BuildPipelineSource(source), timeoutMs, dryRun, confirmed };
                var task = method.Invoke(null, arguments) as Task<PipelineCodeResult>;
                if (task == null) throw new InvalidOperationException("Pipeline adapter returned an invalid operation.");
                _activeOperation = new PipelineDryRunAsyncOperation(record, task);
                EditorApplication.update -= OnAsyncUpdate;
                EditorApplication.update += OnAsyncUpdate;
                return null;
            }
            catch (Exception)
            {
                // 适配器同步拒绝发生在 HTTP 提交前。异常文本不透出，避免凭据意外泄露。
                record.active = false;
                record.busy = false;
                record.status = "not_submitted";
                record.errorCode = "pipelineSubmissionRejected";
                record.error = "Pipeline rejected submission; check its package version, authorization and current Editor descriptor.";
                record.resultWritten = true;
                SavePipelineRecord(record);
                return FailureWithData(request.id, record.error, record);
            }
        }

        private static bool TryReadPipelineFlag(CommandRequest request, string key, bool fallback, out bool value)
        {
            value = fallback;
            if (!request.HasParam(key)) return true;
            if (!(request.@params[key] is bool flag)) return false;
            value = flag;
            return true;
        }

        private static string BuildPipelineSource(SourceText source)
        {
            // Pipeline 0.10 的 eval 包装偏移不可靠；命名 #line 让 fileLine 精确映射用户原始行。
            var path = (source.Path ?? "inline").Replace("\\", "/").Replace("\"", "_").Replace("\r", "_").Replace("\n", "_");
            return "#line 1 \"" + path + "\"\n" + source.OriginalCode;
        }

        private static PipelineOperationRecord CreatePipelineRecord(string requestId, SourceText source, int timeoutMs)
        {
            return new PipelineOperationRecord
            {
                operationId = requestId, requestId = requestId, source = source.Kind, sourcePath = source.Path,
                sourceSha256 = HashText(source.OriginalCode), timeoutMs = timeoutMs,
                editorPid = Process.GetCurrentProcess().Id, unityVersion = Application.unityVersion,
                projectPath = GetProjectRoot(), startedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
        }

        private static CommandResult GetCodeStatus(CommandRequest request)
        {
            var requestId = request.GetParam<string>("requestId", null);
            if (_activeOperation != null && (string.IsNullOrEmpty(requestId) || requestId == _activeOperation.RequestId))
                return CommandResult.Success(request.id, _activeOperation.BuildStatusData());
            var record = LoadPipelineRecordWithStatus(requestId, out var unreadable);
            if (record != null)
            {
                if (!string.IsNullOrEmpty(requestId)) return CommandResult.Success(request.id, record);
                return CommandResult.Success(request.id, new { active = false, busy = false, status = "idle", lastOperation = record });
            }
            if (unreadable)
                return FailureWithData(request.id, "Persisted Pipeline operation state is corrupt or unreadable; do not replay automatically.", CreateCorruptPipelineRecord());
            if (!string.IsNullOrEmpty(requestId))
                return FailureWithData(request.id, "No persisted Pipeline code operation matches requestId.",
                    new { status = "not_found", requestId = requestId });
            return CommandResult.Success(request.id, BuildStatusData());
        }

        private static CommandResult CancelPipelineWait(CommandRequest request, PipelineDryRunAsyncOperation operation)
        {
            var result = operation.CancelForOriginalRequest();
            if (result != null) WriteResultFile(result);
            // 不释放 single-flight；取消等待不是确认停止远端编译。update 会在 Task 结束后释放。
            return CommandResult.Success(request.id, new
            {
                canceled = false, waitCanceled = true, remoteCancellationConfirmed = false,
                requestId = operation.RequestId, status = "cancel_requested", activeOperation = operation.BuildStatusData()
            });
        }

        private static string HashText(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty))).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string PipelineRecordPath(string requestId)
        {
            var filename = string.IsNullOrEmpty(requestId) ? "latest" : HashText(requestId);
            return Path.Combine(GetProjectRoot(), ".aibridge", PipelineOperationsDirectory, filename + ".json");
        }

        private static void SavePipelineRecord(PipelineOperationRecord record)
        {
            var path = PipelineRecordPath(record.requestId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var json = AIBridgeJson.Serialize(record, true);
            // 全局占位先落盘；随后单记录写失败仍保留保守的 busy，不能误当从未提交。
            AIBridgeAtomicFile.WriteTextAtomic(PipelineRecordPath(null), json, Encoding.UTF8, ensureDirectory: true, requireAtomicReplace: true);
            AIBridgeAtomicFile.WriteTextAtomic(path, json, Encoding.UTF8, ensureDirectory: true, requireAtomicReplace: true);
        }

        private static PipelineOperationRecord LoadPipelineRecoveryRecord(out bool unreadable)
        {
            var latest = LoadPipelineRecordWithStatus(null, out unreadable);
            if (latest != null || unreadable) return latest;
            return FindUnindexedPipelineRecord(Path.GetDirectoryName(PipelineRecordPath(null)), out unreadable);
        }

        private static PipelineOperationRecord FindUnindexedPipelineRecord(string directory, out bool unreadable)
        {
            unreadable = false;
            // latest 索引丢失时，不能忽略仍在执行的单请求日志；只读恢复，绝不重放。
            try
            {
                string[] files;
                try { files = Directory.GetFiles(directory, "*.json"); }
                catch (DirectoryNotFoundException) { return null; }
                if (files.Length > 4096) { unreadable = true; return null; }
                PipelineOperationRecord busyRecord = null;
                foreach (var file in files)
                {
                    var data = AIBridgeJson.DeserializeObjectStrict(File.ReadAllText(file, Encoding.UTF8));
                    var id = data == null ? null : ReadRecordText(data, "requestId");
                    if (string.IsNullOrEmpty(id) || !string.Equals(Path.GetFileName(file), HashText(id) + ".json", StringComparison.Ordinal))
                    { unreadable = true; return null; }
                    var record = LoadPipelineRecordFile(file, id, out var invalid);
                    if (invalid || record == null) { unreadable = true; return null; }
                    if (record.active || record.busy) busyRecord = record;
                }
                return busyRecord;
            }
            catch { unreadable = true; return null; }
        }

        private static PipelineOperationRecord LoadPipelineRecord(string requestId)
        {
            return LoadPipelineRecordWithStatus(requestId, out _);
        }

        private static PipelineOperationRecord LoadPipelineRecordWithStatus(string requestId, out bool unreadable)
        {
            return LoadPipelineRecordFile(PipelineRecordPath(requestId), requestId, out unreadable);
        }

        private static PipelineOperationRecord LoadPipelineRecordFile(string path, string requestId, out bool unreadable)
        {
            unreadable = false;
            if (!TryGetRecordFileExists(path, out var exists))
            {
                unreadable = true;
                return null;
            }
            if (!exists) return null;
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var data = AIBridgeJson.DeserializeObjectStrict(json);
                if (data == null) { unreadable = true; return null; }
                var record = JsonUtility.FromJson<PipelineOperationRecord>(json);
                if (record == null || record.backend != "pipeline" || string.IsNullOrEmpty(record.requestId))
                {
                    unreadable = true;
                    return null;
                }
                // JsonUtility 会丢失 object/Nullable<T>；由纯托管 JSON 恢复执行值、耗时和诊断。
                // 否则 Task 结束释放占位后，status 从磁盘读取就会丢失迟到结果。
                // 缺字段不能被 JsonUtility 的默认 false 解释成已安全结束。
                foreach (var key in new[] { "active", "busy", "mayStillBeRunning", "dryRun", "resultWritten" })
                    if (!data.TryGetValue(key, out var flag) || !(flag is bool))
                    { unreadable = true; return null; }
                if (string.IsNullOrEmpty(ReadRecordText(data, "status"))
                    || (record.active && !record.busy)
                    || (record.mayStillBeRunning && !record.busy && !record.waitCanceled)
                    || (record.status == "unknown" && !record.busy && !record.waitCanceled)
                    || ((record.status == "executed" || record.status == "validated")
                        && (record.active || record.busy || record.mayStillBeRunning
                            || (record.status == "executed" && record.dryRun)
                            || (record.status == "validated" && !record.dryRun))))
                { unreadable = true; return null; }
                if (!ValidDetachedRecord(data, record)) { unreadable = true; return null; }
                record.resultValue = data.TryGetValue("resultValue", out var resultValue) ? AIBridgeJson.PreserveObjectNulls(resultValue) : null;
                record.executionTimeMs = null;
                if (data.TryGetValue("executionTimeMs", out var executionTime) && executionTime != null)
                {
                    if (!(executionTime is long milliseconds) || milliseconds < 0)
                    { unreadable = true; return null; }
                    record.executionTimeMs = milliseconds;
                }
                record.diagnostics = new List<PipelineCodeDiagnostic>();
                if (data != null && data.TryGetValue("diagnostics", out var items) && items is List<object> diagnostics)
                {
                    foreach (var item in diagnostics)
                    {
                        var d = item as Dictionary<string, object>;
                        if (d == null) continue;
                        record.diagnostics.Add(new PipelineCodeDiagnostic
                        {
                            severity = ReadRecordText(d, "severity"), code = ReadRecordText(d, "code"),
                            message = ReadRecordText(d, "message"), file = ReadRecordText(d, "file"),
                            line = ReadRecordCoordinate(d, "line"), column = ReadRecordCoordinate(d, "column")
                        });
                    }
                }
                if (!string.IsNullOrEmpty(requestId) && record.requestId != requestId)
                {
                    unreadable = true;
                    return null;
                }
                return record;
            }
            catch { unreadable = true; return null; }
        }

        private static void RetainCorruptOperation(string requestId)
        {
            if (_activeOperation != null) return;
            var record = LoadPipelineRecoveryRecord(out var unreadable);
            if (record == null || unreadable)
                record = CreateCorruptPipelineRecord();
            else if (!record.busy)
                record = CreateCorruptPipelineRecord();
            if (_activeOperation == null)
                _activeOperation = new PipelineUnknownOperation(record);
        }

        private static bool TryGetRecordFileExists(string path, out bool exists)
        {
            exists = false;
            try
            {
                // File.Exists 会把无权限/IO 错误静默当作不存在，不能作为允许再次提交的依据。
                var attributes = File.GetAttributes(path);
                exists = true;
                return (attributes & FileAttributes.Directory) == 0;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch { return false; }
        }

        private static PipelineOperationRecord CreateCorruptPipelineRecord()
        {
            return new PipelineOperationRecord
            {
                requestId = "unknown",
                operationId = "unknown",
                active = false,
                busy = true,
                mayStillBeRunning = true,
                status = "unknown",
                errorCode = "pipelineStateInvalid",
                error = "Persisted Pipeline operation state is corrupt or unreadable; do not replay automatically."
            };
        }

        private static string ReadRecordText(Dictionary<string, object> data, string key)
        {
            return data.TryGetValue(key, out var value) ? value as string : null;
        }

        private static int? ReadRecordCoordinate(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out var value) && value is long number && number > 0 && number <= int.MaxValue)
                return (int)number;
            return null;
        }

        [Serializable]
        private sealed class PipelineOperationRecord
        {
            public string operationId;
            public string requestId;
            public string backend = "pipeline";
            public string status = "running";
            public bool enabled = true;
            public bool dryRun = true;
            public bool confirmed;
            public string operationKind = "compile_only";
            public string pipelineCommand = "script eval";
            public bool detached;
            public bool upstreamExecutionTimeoutDisabled;
            public string remoteJobId;
            public string jobState;
            public string targetIdentity;
            public string adapterGeneration;
            public bool executionEnded;
            public bool cancellationRequested;
            public bool sideEffectsUnknown = true;
            public string lastObservedAt;
            public string entry;
            public string argumentsSha256;
            public bool active = true;
            public bool busy = true;
            public bool timedOut;
            public bool waitCanceled;
            public bool mayStillBeRunning;
            public bool resultWritten;
            public string source;
            public string sourcePath;
            public string sourceSha256;
            public string projectPath;
            public int editorPid;
            public string unityVersion;
            public string startedAt;
            public int timeoutMs;
            public long elapsedMs;
            public int httpStatus;
            public string errorCode;
            public string error;
            public string errorDetails;
            public object resultValue;
            public string resultMessage;
            public string output;
            public long? executionTimeMs;
            public List<PipelineCodeDiagnostic> diagnostics = new List<PipelineCodeDiagnostic>();
            public List<string> compileErrors = new List<string>();
        }

        private sealed class PipelineUnknownOperation : ICodeOperation
        {
            private readonly PipelineOperationRecord _record;
            public PipelineUnknownOperation(PipelineOperationRecord record) { _record = record; }
            public string RequestId => _record.requestId;
            public bool Step(out CommandResult result, out bool shouldRelease)
            {
                result = null; shouldRelease = false; return false;
            }
            public object BuildStatusData() { return _record; }
            public CommandResult CancelForOriginalRequest()
            {
                // 仅显式 code cancel 可释放失联的本地占位；不把 unknown 改成 canceled。
                _record.active = false;
                _record.busy = false;
                _record.status = "unknown";
                _record.mayStillBeRunning = true;
                _record.waitCanceled = true;
                SavePipelineRecord(_record);
                return null;
            }
        }

        private sealed class PipelineDryRunAsyncOperation : ICodeOperation
        {
            private readonly PipelineOperationRecord _record;
            private readonly Task<PipelineCodeResult> _task;
            private readonly Stopwatch _watch = Stopwatch.StartNew();

            public PipelineDryRunAsyncOperation(PipelineOperationRecord record, Task<PipelineCodeResult> task)
            {
                _record = record;
                _task = task;
            }

            public string RequestId => _record.requestId;

            public bool Step(out CommandResult result, out bool shouldRelease)
            {
                var originalWritten = _record.resultWritten;
                try { return StepCore(out result, out shouldRelease); }
                catch (Exception)
                {
                    // 落盘失败不消费原回执，也不能触发第二次网络请求；保守保留 unknown。
                    _record.active = false; _record.busy = true; _record.mayStillBeRunning = true;
                    _record.status = "unknown"; _record.errorCode = "pipelineStateWriteFailed";
                    _record.error = "Pipeline state could not be persisted; no request was replayed.";
                    _activeOperation = new PipelineUnknownOperation(_record);
                    EditorApplication.update -= OnAsyncUpdate;
                    result = originalWritten ? null : FailureWithData(RequestId, _record.error, _record);
                    _record.resultWritten = true; shouldRelease = false; return true;
                }
            }

            private bool StepCore(out CommandResult result, out bool shouldRelease)
            {
                result = null;
                shouldRelease = _task.IsCompleted;
                _record.elapsedMs = _watch.ElapsedMilliseconds;
                if (_task.IsCompleted)
                {
                    _watch.Stop();
                    ApplyCompletedResult();
                    if (_record.mayStillBeRunning)
                    {
                        _record.busy = true;
                        shouldRelease = false;
                        _activeOperation = new PipelineUnknownOperation(_record);
                        EditorApplication.update -= OnAsyncUpdate;
                    }
                    SavePipelineRecord(_record);
                    if (!_record.resultWritten)
                    {
                        _record.resultWritten = true;
                        SavePipelineRecord(_record);
                        result = _record.status == "validated" || _record.status == "executed" ? CommandResult.Success(RequestId, _record)
                            : FailureWithData(RequestId, _record.error ?? "Pipeline operation failed.", _record);
                    }
                    return true;
                }
                if (!_record.resultWritten && _watch.ElapsedMilliseconds >= _record.timeoutMs)
                {
                    _record.timedOut = true;
                    _record.mayStillBeRunning = true;
                    _record.resultWritten = true;
                    _record.status = "timed_out_waiting_for_pipeline";
                    _record.error = "Pipeline code wait timed out; query code status and do not replay automatically.";
                    SavePipelineRecord(_record);
                    result = FailureWithData(RequestId, _record.error, _record);
                    return true;
                }
                return false;
            }

            private void ApplyCompletedResult()
            {
                _record.active = false;
                _record.busy = false;
                _record.elapsedMs = _watch.ElapsedMilliseconds;
                if (_task.IsFaulted || _task.IsCanceled)
                {
                    // 不透出异常对象：第三方异常可能带有认证头或请求正文。
                    if (_task.IsFaulted) { var observed = _task.Exception; }
                    _record.status = "unknown";
                    _record.mayStillBeRunning = true;
                    _record.errorCode = "pipelineTaskUnknown";
                    _record.error = "Pipeline operation ended without a confirmed result; do not replay automatically.";
                    return;
                }
                var response = _task.Result;
                if (response == null) response = new PipelineCodeResult { status = "unknown", mayStillBeRunning = true, errorCode = "pipelineResultMissing" };
                _record.status = response.status;
                _record.httpStatus = response.httpStatus;
                _record.timedOut = _record.timedOut || response.httpStatus == 504
                    || response.errorCode == "TIMEOUT" || response.errorCode == "pipelineRunTimeout";
                _record.error = response.error;
                _record.errorCode = response.errorCode;
                _record.errorDetails = response.errorDetails;
                _record.resultValue = AIBridgeJson.PreserveObjectNulls(response.resultValue);
                _record.resultMessage = response.message;
                _record.output = response.output;
                _record.executionTimeMs = response.executionTimeMs;
                _record.mayStillBeRunning = response.mayStillBeRunning;
                _record.diagnostics = response.diagnostics ?? new List<PipelineCodeDiagnostic>();
                foreach (var diagnostic in _record.diagnostics)
                {
                    if (string.IsNullOrEmpty(diagnostic.file)) diagnostic.file = _record.sourcePath;
                    if (string.Equals(diagnostic.severity, "error", StringComparison.OrdinalIgnoreCase))
                        _record.compileErrors.Add(diagnostic.message);
                }
            }

            public CommandResult CancelForOriginalRequest()
            {
                _record.waitCanceled = true;
                _record.mayStillBeRunning = !_task.IsCompleted;
                _record.status = _record.dryRun ? "cancel_requested" : "execution_wait_canceled";
                _record.elapsedMs = _watch.ElapsedMilliseconds;
                var write = !_record.resultWritten;
                _record.resultWritten = true;
                SavePipelineRecord(_record);
                return write ? FailureWithData(RequestId, "Pipeline wait canceled; remote cancellation is not confirmed.", _record) : null;
            }

            public object BuildStatusData()
            {
                _record.elapsedMs = _watch.ElapsedMilliseconds;
                return _record;
            }
        }
    }
}
