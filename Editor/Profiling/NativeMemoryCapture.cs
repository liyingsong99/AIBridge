using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    // 启动恢复与纯 Engine 分开，fixture 构造 Engine/Context 不触发真实项目日志初始化。
    [InitializeOnLoad]
    internal static class NativeMemoryCaptureBootstrap
    {
        static NativeMemoryCaptureBootstrap()
        {
#if UNITY_2020_2_OR_NEWER
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
#endif
            EditorApplication.delayCall += NativeMemoryCapture.Initialize;
        }
    }

    internal static class NativeMemoryCapture
    {
        private static Engine _engine;
        internal sealed class Context
        {
            internal string ProjectRoot;
            internal string Generation = Guid.NewGuid().ToString("N");
            internal int EditorPid;
            internal INativeMemorySnapshotApi Api;
            internal NativeMemoryJournal Journal;
            internal Func<string> BusyReason;
            internal Func<DateTime> UtcNow = () => DateTime.UtcNow;
            internal Func<object> Acquire = AcquireGate;
            internal Action<object> Release = ProfilerNativeGate.Release;
        }

        internal static void Initialize()
        {
            if (_engine != null) return;
            try
            {
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                _engine = new Engine(new Context
                {
                    ProjectRoot = root, EditorPid = Process.GetCurrentProcess().Id,
                    Api = NativeMemorySnapshotApi.Resolve(), Journal = new NativeMemoryJournal(root), BusyReason = EditorBusyReason
                });
                EditorApplication.update += Tick;
            }
            catch (Exception) { /* 初始化失败由 Execute 返回 unavailable，绝不猜为空闲。 */ }
        }

        internal static CommandResult Execute(CommandRequest request, string action)
        {
            Initialize();
            return _engine == null ? Failure(request, "unavailable", "Memory snapshot journal/context is unavailable.")
                : _engine.Execute(request, action);
        }

        private static void Tick() { if (_engine != null) _engine.Tick(); }
        private static object AcquireGate() { object lease; return ProfilerNativeGate.TryAcquire(out lease) ? lease : null; }
        private static string EditorBusyReason()
        {
            if (EditorApplication.isCompiling) return "Editor is compiling.";
            if (EditorApplication.isUpdating) return "Editor is importing/updating assets.";
            if (BuildPipeline.isBuildingPlayer || AIBridgeBuildTracker.Busy) return "An Editor build is active or unknown.";
            if (CodeCommand.HasActiveCodeOperation) return "An AIBridge code operation is active or unknown.";
            if ((PipelineTestTracker.HasBusy || PipelineCompileTracker.HasBusy)) return "A Pipeline test is active or unknown.";
            return null;
        }

        internal sealed class Engine
        {
            private readonly Context _context;
            private readonly Dictionary<string, Operation> _operations = new Dictionary<string, Operation>(StringComparer.Ordinal);
            private readonly ConcurrentQueue<Completion> _callbacks = new ConcurrentQueue<Completion>();
            private bool _corrupt;
            private object _lease;
            private string _lastId;
            internal bool Busy => _corrupt || AnyBusy();

            internal Engine(Context context)
            {
                _context = context;
                Recover();
            }

            private void Recover()
            {
                var records = _context.Journal.ReadAll(out _corrupt);
                foreach (var record in records)
                {
                    if (record.busy)
                    {
                        // reload 不重建 delegate、不调用 API。保守未知，需精确 requestId 本地释放。
                        NativeMemoryJournal.NormalizeUnknown(record, "Editor generation changed; native result is unknown. No snapshot was replayed.");
                        // 写失败不把已识别的占位永久变成corrupt；原日志仍可在IO恢复后按精确ID释放。
                        try { _context.Journal.Save(record); } catch (Exception) { }
                    }
                    _operations[record.requestId] = new Operation { Record = record, Recovered = true };
                    if (_lastId == null || string.CompareOrdinal(record.queuedAt, _operations[_lastId].Record.queuedAt) > 0) _lastId = record.requestId;
                }
                if (_corrupt || AnyBusy()) _lease = _context.Acquire();
            }

            internal CommandResult Execute(CommandRequest request, string action)
            {
                try
                {
                    ValidateArguments(request, action);
                    if (action == "load_snapshot")
                        return Failure(request, "unsupported", "Snapshot analysis loading is unsupported: no installed package/Open API has been verified. Capture uses the built-in API and does not require that package.");
                    if (action == "snapshot_status") return Status(request);
                    if (action == "release_snapshot") return Release(request);
                    return Queue(request);
                }
                catch (NotSupportedException ex) { return Failure(request, "unsupported", ex.Message); }
                catch (Exception ex) { return Failure(request, "rejected", "Memory snapshot request rejected: " + ex.GetType().Name + ": " + ex.Message); }
            }

            private CommandResult Queue(CommandRequest request)
            {
                if (!NativeMemoryJournal.ValidId(request.id)) throw new ArgumentException("A bounded original requestId is required.");
                if (_corrupt) return Failure(request, "journal_invalid", "Memory journal is corrupt/unreadable; no native call was made.");
                var records = _context.Journal.ReadAll(out var invalid);
                if (invalid) { HoldCorrupt(); return Failure(request, "journal_invalid", "Memory journal is corrupt/unreadable; no native call was made."); }
                foreach (var record in records)
                {
                    if (record.requestId == request.id) return Failure(request, "already_registered", "This requestId is already registered; it will not be replayed.");
                    if (!record.busy) continue;
                    if (!_operations.ContainsKey(record.requestId))
                    {
                        record.status = "unknown"; record.error = "Earlier busy journal is not owned by this generation; no replay was made.";
                        record.nativeMayStillRun = true; record.result = null; record.sizeBytes = null; record.completedAt = null;
                        _operations.Add(record.requestId, new Operation { Record = record, Recovered = true });
                    }
                    if (_lease == null) _lease = _context.Acquire();
                    return Failure(request, "busy", "An earlier memory snapshot is busy/unknown; inspect and explicitly release its exact requestId.");
                }
                // 读取上限包含latest索引；新ID必须预留一个索引槽，旧记录终态/release不受此新建限制。
                if (records.Count >= _context.Journal.RecordLimit - 1)
                    return Failure(request, "journal_capacity", "Memory operation journal capacity reached; no native call was made.");
                if (AnyBusy()) return Failure(request, "busy", "A memory snapshot is already busy/unknown.");
                if (!_context.Api.Support.Capture) return Failure(request, "unsupported", "Exact built-in snapshot or Profiler state signature is unavailable.");
                var state = CheckState();
                var pathValue = Value(request, "path");
                if (request.@params.ContainsKey("path") && (!(pathValue is string) || string.IsNullOrWhiteSpace((string)pathValue))) throw new ArgumentException("path must be a string.");
                var output = NativeMemoryJournal.ResolveSnapshotPath(_context.ProjectRoot,
                    (string)pathValue ?? NativeMemoryJournal.OutputDirectory + "/" + Guid.NewGuid().ToString("N") + ".snap");
                NativeMemoryJournal.AssertAbsent(output);
                var temp = NativeMemoryJournal.ResolveSnapshotPath(_context.ProjectRoot,
                    NativeMemoryJournal.OutputDirectory + "/" + Guid.NewGuid().ToString("N") + ".snap");
                NativeMemoryJournal.AssertAbsent(temp);
                if (_lease == null) _lease = _context.Acquire();
                if (_lease == null) return Failure(request, "busy", "Native Profiler/Memory gate is busy.");
                var recordNew = new NativeMemoryRecord
                {
                    requestId = request.id, operationId = Guid.NewGuid().ToString("N"), projectPath = Path.GetFullPath(_context.ProjectRoot),
                    generation = _context.Generation, editorPid = _context.EditorPid, targetGuid = state.CurrentTargetGuid,
                    targetName = state.CurrentTargetName, outputPath = output, tempPath = temp, status = "queued", queuedAt = Now(),
                    busy = true, timeoutMs = Timeout(request)
                };
                var operation = new Operation { Record = recordNew, Deadline = _context.UtcNow().AddMilliseconds(recordNew.timeoutMs) };
                _operations.Add(recordNew.requestId, operation); _lastId = recordNew.requestId;
                try { _context.Journal.Save(recordNew); }
                catch (Exception)
                {
                    Unknown(operation, "Intent persistence failed; no native call was made. Explicit local release is required.", false);
                    return Failure(request, "intent_write_failed", recordNew.error, recordNew);
                }
                return CommandResult.Success(request.id, NativeMemoryJournal.ToData(recordNew));
            }

            internal void Tick()
            {
                // reload 恢复时若 gate 暂由另一操作持有，后续仍须取得保守占位，不能静默变 idle。
                if ((_corrupt || AnyBusy()) && _lease == null) _lease = _context.Acquire();
                Completion completion;
                while (_callbacks.TryDequeue(out completion))
                {
                    Operation operation;
                    if (!_operations.TryGetValue(completion.RequestId, out operation) || operation.Recovered || operation.CallbackConsumed
                        || operation.Record.released || operation.Record.operationId != completion.OperationId
                        || operation.Record.generation != completion.Generation) continue;
                    operation.CallbackConsumed = true;
                    try { Complete(operation, completion); }
                    catch (Exception) { Unknown(operation, "Native callback could not be safely published/persisted; retained temporary output, do not replay.", true); }
                }
                foreach (var operation in new List<Operation>(_operations.Values))
                {
                    var record = operation.Record;
                    if (record.released || operation.Recovered || !record.busy || operation.StorageFailed) continue;
                    if (record.status == "queued") Start(operation);
                    if ((record.status == "capturing" || record.status == "queued") && _context.UtcNow() >= operation.Deadline)
                        Unknown(operation, "Local wait timed out; the native snapshot may still run. No cancellation was sent.", operation.Invoked);
                }
            }

            private void Start(Operation operation)
            {
                if (_context.UtcNow() >= operation.Deadline)
                {
                    Unknown(operation, "Local queue wait expired before invocation; no native call was made.", false); return;
                }
                try
                {
                    var state = CheckState();
                    if (state.CurrentTargetGuid != operation.Record.targetGuid || state.CurrentTargetName != operation.Record.targetName)
                        throw new InvalidOperationException("Profiler target changed before capture.");
                    var record = operation.Record;
                    NativeMemoryJournal.ResolveSnapshotPath(_context.ProjectRoot, record.outputPath);
                    NativeMemoryJournal.ResolveSnapshotPath(_context.ProjectRoot, record.tempPath);
                    NativeMemoryJournal.AssertAbsent(record.outputPath); NativeMemoryJournal.AssertAbsent(record.tempPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(record.tempPath));
                    NativeMemoryJournal.ResolveSnapshotPath(_context.ProjectRoot, record.tempPath);
                    record.status = "capturing"; record.startedAt = Now(); record.nativeMayStillRun = true;
                    _context.Journal.Save(record);
                    operation.Invoked = true;
                    var operationId = record.operationId; var generation = record.generation; var requestId = record.requestId;
                    // 原生线程只复制回执入队；不碰 Unity、文件、日志、gate 或可变操作记录。
                    operation.Callback = (path, success) => _callbacks.Enqueue(new Completion
                    {
                        RequestId = requestId, OperationId = operationId, Generation = generation, Path = path, Success = success
                    });
                    _context.Api.TakeSnapshot(record.tempPath, operation.Callback);
                }
                catch (Exception)
                {
                    if (operation.Invoked) Unknown(operation, "Native invocation threw; snapshot completion is unknown, do not replay.", true);
                    else
                    {
                        // capturing 意图写入失败也保守锁住，原生调用不会继续。
                        if (operation.Record.status == "capturing") Unknown(operation, "Capturing intent could not be persisted; no native call was made.", false);
                        else Finish(operation, false, "Capture precondition changed; no native call was made.", null);
                    }
                }
            }

            private void Complete(Operation operation, Completion completion)
            {
                var record = operation.Record;
                if (!operation.Invoked || completion.Generation != _context.Generation || completion.Path != record.tempPath)
                {
                    Unknown(operation, "Callback path/generation did not match the original capture; retained temporary output.", true); return;
                }
                NativeMemoryJournal.ResolveSnapshotPath(_context.ProjectRoot, record.tempPath);
                if (!completion.Success) { Finish(operation, false, "Native snapshot callback reported false.", null); return; }
                var state = CheckState();
                if (state.CurrentTargetGuid != record.targetGuid || state.CurrentTargetName != record.targetName)
                {
                    Unknown(operation, "Profiler target changed before callback publication; output provenance is unknown.", true); return;
                }
                using (var stream = new FileStream(record.tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    NativeMemoryJournal.ResolveSnapshotPath(_context.ProjectRoot, record.tempPath);
                    if (stream.Length <= 0) { Finish(operation, false, "Native snapshot callback produced an empty file.", null); return; }
                    var size = stream.Length;
                    // 不覆盖发布；File.Move只调用一次。异常后保留unknown，不删旧目标/临时结果。
                    stream.Dispose();
                    NativeMemoryJournal.ResolveSnapshotPath(_context.ProjectRoot, record.outputPath);
                    NativeMemoryJournal.AssertAbsent(record.outputPath);
                    File.Move(record.tempPath, record.outputPath);
                    Finish(operation, true, null, size);
                }
            }

            private void Finish(Operation operation, bool success, string error, long? size)
            {
                var record = operation.Record;
                record.status = success ? "completed" : "failed"; record.completedAt = Now(); record.busy = false;
                record.result = success; record.sizeBytes = size; record.error = error; record.nativeMayStillRun = false;
                try { _context.Journal.Save(record); }
                catch (Exception) { Unknown(operation, "Terminal journal persistence failed; native outcome must be inspected manually.", operation.Invoked); return; }
                operation.Callback = null;
                ReleaseGateIfIdle();
            }

            private void Unknown(Operation operation, string reason, bool mayStillRun)
            {
                var r = operation.Record;
                r.status = "unknown"; r.busy = !r.released; r.result = null; r.sizeBytes = null; r.completedAt = null;
                r.error = reason; r.nativeMayStillRun = mayStillRun;
                try { _context.Journal.Save(r); }
                catch (Exception) { operation.StorageFailed = true; }
            }

            private CommandResult Status(CommandRequest request)
            {
                var id = OptionalId(request);
                if (id != null && _operations.TryGetValue(id, out var operation)) return CommandResult.Success(request.id, NativeMemoryJournal.ToData(operation.Record));
                if (id == null)
                {
                    var all = _context.Journal.ReadAll(out var invalid);
                    if (_corrupt || invalid) return Failure(request, "journal_invalid", "Memory journal is corrupt/unreadable; idle cannot be established.");
                    var busy = all.FindAll(r => r.busy);
                    if (busy.Count > 1) return Failure(request, "multiple_unknown", "Multiple earlier busy records require exact requestId release.", busy);
                    if (busy.Count == 1)
                    {
                        if (_operations.TryGetValue(busy[0].requestId, out operation)) return CommandResult.Success(request.id, NativeMemoryJournal.ToData(operation.Record));
                        return Failure(request, "external_unknown", "An earlier busy record is not owned by this generation; idle cannot be established.", busy[0]);
                    }
                    if (_lastId != null) return CommandResult.Success(request.id, NativeMemoryJournal.ToData(_operations[_lastId].Record));
                    return CommandResult.Success(request.id, new { status = "idle", busy = false });
                }
                var record = _context.Journal.Load(id, out var bad);
                if (bad || record == null) return Failure(request, bad ? "journal_invalid" : "not_found", "No readable matching memory operation.");
                return CommandResult.Success(request.id, NativeMemoryJournal.ToData(record));
            }

            private CommandResult Release(CommandRequest request)
            {
                var id = OptionalId(request);
                if (id == null) throw new ArgumentException("release_snapshot requires exact --requestId.");
                // 每次精确释放前重扫：临时IO恢复可以解锁，未知腐坏或其它busy绝不能被此ID掩盖。
                var records = _context.Journal.ReadAll(out var invalid);
                if (invalid) { HoldCorrupt(); return Failure(request, "journal_invalid", "Memory journal is corrupt/unreadable; no local release was persisted."); }
                Operation operation;
                if (!_operations.TryGetValue(id, out operation))
                {
                    var record = records.Find(r => r.requestId == id);
                    if (record == null) return Failure(request, "not_found", "No readable matching memory operation can be released.");
                    operation = new Operation { Record = record, Recovered = true }; _operations[id] = operation;
                }
                var persisted = records.Find(r => r.requestId == id);
                if (persisted != null && !NativeMemoryJournal.SameOperation(operation.Record, persisted))
                { HoldCorrupt(); return Failure(request, "journal_invalid", "Memory operation identity changed; no local release was persisted."); }
                foreach (var record in records)
                {
                    if (record.requestId == id || !record.busy || _operations.ContainsKey(record.requestId)) continue;
                    NativeMemoryJournal.NormalizeUnknown(record, "Earlier busy journal requires exact local release; no snapshot was replayed.");
                    _operations.Add(record.requestId, new Operation { Record = record, Recovered = true });
                }
                if (operation.Recovered && operation.Record.busy && (operation.Record.status == "queued" || operation.Record.status == "capturing"))
                    NativeMemoryJournal.NormalizeUnknown(operation.Record, "Earlier capture is not owned by this generation; no snapshot was replayed.");
                if (!operation.Record.busy || operation.Record.status != "unknown")
                    return Failure(request, "not_unknown", "Only an unknown operation may be explicitly released locally.");
                operation.Record.released = true; operation.Record.busy = false;
                try { _context.Journal.Save(operation.Record); }
                catch (Exception)
                {
                    operation.Record.released = false; operation.Record.busy = true;
                    throw;
                }
                operation.StorageFailed = false; _corrupt = false;
                // 保留 delegate/临时文件；迟到回调因released被忽略。绝不调用不存在的 native cancel。
                ReleaseGateIfIdle();
                return CommandResult.Success(request.id, new
                {
                    localReleased = true, nativeCancellationConfirmed = false, nativeMayStillRun = operation.Record.nativeMayStillRun,
                    requestId = id, operation = NativeMemoryJournal.ToData(operation.Record)
                });
            }

            private void HoldCorrupt() { _corrupt = true; if (_lease == null) _lease = _context.Acquire(); }
            private bool AnyBusy() { foreach (var operation in _operations.Values) if (operation.Record.busy) return true; return false; }
            private void ReleaseGateIfIdle()
            {
                if (_corrupt || AnyBusy() || _lease == null) return;
                _context.Release(_lease); _lease = null;
            }
            private NativeMemorySnapshotState CheckState()
            {
                var busy = _context.BusyReason();
                if (!string.IsNullOrEmpty(busy)) throw new InvalidOperationException(busy);
                var state = _context.Api.ReadState();
                if (state == null || state.ProfilerEnabled || state.CurrentTargetGuid != -1 || !NativeMemoryJournal.ValidId(state.CurrentTargetName))
                    throw new NotSupportedException("Capture requires exact local Editor target guid=-1 and Profiler enabled=false.");
                return state;
            }
            private string Now() { return _context.UtcNow().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
            private sealed class Operation
            {
                internal NativeMemoryRecord Record;
                internal DateTime Deadline;
                internal bool Invoked, Recovered, CallbackConsumed, StorageFailed;
                internal Action<string, bool> Callback;
            }
            private sealed class Completion
            {
                internal string RequestId, OperationId, Generation, Path;
                internal bool Success;
            }
        }

        private static void ValidateArguments(CommandRequest request, string action)
        {
            if (request == null || request.@params == null) throw new ArgumentException("Request parameters are required.");
            string[] allowed;
            if (action == "capture_snapshot") allowed = new[] { "action", "confirm", "timeout", "path", "target" };
            else if (action == "snapshot_status") allowed = new[] { "action", "requestId" };
            else if (action == "release_snapshot") allowed = new[] { "action", "requestId", "confirm" };
            else if (action == "load_snapshot") allowed = new[] { "action", "path", "target", "confirm" };
            else throw new ArgumentException("Unknown snapshot action.");
            foreach (var key in request.@params.Keys) if (Array.IndexOf(allowed, key) < 0) throw new ArgumentException("Unsupported snapshot parameter: " + key);
            if (action == "capture_snapshot" || action == "release_snapshot")
                if (!(Value(request, "confirm") is bool confirm) || !confirm) throw new ArgumentException("Literal boolean --confirm true is required.");
            if (action == "capture_snapshot")
            {
                Timeout(request);
                var target = Value(request, "target");
                if (request.@params.ContainsKey("target") && (!(target is string) || (string)target != "editor"))
                    throw new NotSupportedException("Only explicit target editor is supported; Player capture is unsupported.");
            }
        }
        private static int Timeout(CommandRequest request)
        {
            var value = Value(request, "timeout");
            if (value == null && !request.@params.ContainsKey("timeout")) return 10000;
            long number = -1;
            if (value is int integer) number = integer;
            else if (value is long longValue) number = longValue;
            if (number < 1000 || number > 60000) throw new ArgumentException("timeout must be integer 1000..60000 (local wait only).");
            return (int)number;
        }
        private static object Value(CommandRequest request, string key) { return request.@params.TryGetValue(key, out var value) ? value : null; }
        private static string OptionalId(CommandRequest request)
        {
            if (!request.@params.ContainsKey("requestId")) return null;
            var id = Value(request, "requestId") as string;
            if (!NativeMemoryJournal.ValidId(id)) throw new ArgumentException("A valid original requestId is required.");
            return id;
        }
        private static CommandResult Failure(CommandRequest request, string code, string error, object operation = null)
        {
            var result = CommandResult.Failure(request == null ? null : request.id, error);
            if (operation is NativeMemoryRecord record) operation = NativeMemoryJournal.ToData(record);
            else if (operation is List<NativeMemoryRecord> records)
                operation = records.ConvertAll(NativeMemoryJournal.ToData);
            result.data = new { code, operation, nativeCancellationConfirmed = false };
            return result;
        }
    }
}
