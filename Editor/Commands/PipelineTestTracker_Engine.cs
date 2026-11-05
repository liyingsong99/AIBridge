using System;
using System.Globalization;
using System.Threading.Tasks;

namespace AIBridge.Editor
{
    [Serializable]
    public sealed class PipelineTestRecord
    {
        public int schema = 1;
        public string backend = "pipeline";
        public string requestId;
        public string status = "intent";
        public bool busy = true;
        public bool released;
        public bool submissionStarted;
        public bool executionEnded;
        public bool? success;
        public string projectPath;
        public int editorPid;
        public string unityVersion;
        public string generation;
        public string targetIdentity;
        public string sessionGeneration;
        public string queuedAt;
        public string endedAt;
        public string filter;
        public string filterType;
        public int timeoutMs;
        public int httpStatus;
        public string errorCode;
        public string error;
        public object data;
    }

    internal interface IPipelineTestJournal
    {
        PipelineTestRecord Load(string requestId, out bool unreadable);
        PipelineTestRecord FindLatest(out bool unreadable);
        PipelineTestRecord FindOutstanding(out bool unreadable);
        void Save(PipelineTestRecord record);
        void RequireCapacityForNewRequest();
    }

    internal sealed class PipelineTestContext
    {
        internal string Root;
        internal int EditorPid;
        internal string UnityVersion;
        internal string Generation;
        internal Func<DateTimeOffset> Clock;
        // null 表示经 native tracker 和真实 UTF 活跃探针共同确认 idle；异常必须失败关闭。
        internal Func<string> NativeActivity;
        internal Func<PipelineTestOptions, PipelineTestSession> SessionFactory;
        internal IPipelineTestJournal Journal;
    }

    /// <summary>无 Unity/HTTP 的单领域执行器，Tick 由主线程驱动；磁盘记录永不重建会话或重放提交。</summary>
    internal sealed class PipelineTestEngine
    {
        private readonly PipelineTestContext _context;
        private PipelineTestRecord _active;
        private PipelineTestSession _session;
        private Task<PipelineTestResult> _task;
        private bool _storageBlocked;
        private bool _updating;

        internal PipelineTestEngine(PipelineTestContext context)
        {
            if (context == null || context.Clock == null || context.Journal == null || context.SessionFactory == null
                || context.NativeActivity == null || string.IsNullOrWhiteSpace(context.Root) || context.EditorPid <= 0
                || string.IsNullOrWhiteSpace(context.UnityVersion) || !PipelineTestStore.IsGeneration(context.Generation))
                throw new ArgumentException("Pipeline test context is incomplete.", nameof(context));
            _context = context;
            Recover();
        }

        internal bool HasBusy
        {
            get
            {
                if (_storageBlocked || (_active != null && _active.busy)) return true;
                var prior = _context.Journal.FindOutstanding(out var invalid);
                if (invalid) { _storageBlocked = true; return true; }
                if (prior != null) { RecoverRecord(prior); return true; }
                return false;
            }
        }

        internal PipelineTestRecord Start(string requestId, PipelineTestOptions options)
        {
            if (!PipelineTestStore.IsRequestId(requestId)) throw new ArgumentException("Invalid requestId.", nameof(requestId));
            if (options == null) throw new ArgumentNullException(nameof(options));
            Tick();
            var previous = _context.Journal.Load(requestId, out var invalid);
            if (invalid) throw new InvalidOperationException("Pipeline test request journal is unreadable; it cannot be replayed.");
            if (_active != null && _active.requestId == requestId) return Snapshot(_active);
            if (previous != null) return Snapshot(previous);
            if (HasBusy) throw new InvalidOperationException("A Pipeline test is active or unknown, or its journal is unreadable.");
            RequireIdle();
            _context.Journal.RequireCapacityForNewRequest();
            var record = NewRecord(requestId, options);
            _active = record;
            if (!TrySave(record)) return Snapshot(record);
            PrepareAndSubmit(record, options);
            return Snapshot(record);
        }

        private void PrepareAndSubmit(PipelineTestRecord record, PipelineTestOptions options)
        {
            string identity;
            string generation;
            try
            {
                _session = _context.SessionFactory(options);
                identity = _session == null ? null : _session.TargetIdentity;
                generation = _session == null ? null : _session.Generation;
                if (!PipelineTestStore.IsIdentity(identity) || !PipelineTestStore.IsGeneration(generation))
                    throw new InvalidOperationException("Pipeline test preparation did not establish a bound identity.");
                RequireIdle();
            }
            catch (Exception)
            {
                record.status = "not_submitted"; record.busy = false; record.endedAt = Now();
                record.errorCode = "pipelineTestPreparationUnavailable";
                record.error = "Pipeline test preparation or the native activity probe is unavailable; no POST was started.";
                if (TrySave(record)) { ReleaseSession(false); _active = null; }
                return;
            }
            record.targetIdentity = identity; record.sessionGeneration = generation;
            // 在调用 Submit 前记下可能已发起；落盘不确定时只保留 unknown，绝不调用或重试 Submit。
            record.submissionStarted = true; record.status = "running";
            if (!TrySave(record)) return;
            try
            {
                _task = _session.SubmitAsync();
                if (_task == null) SetUnknown(record, "pipelineTestTransportUnknown");
            }
            catch (Exception) { SetUnknown(record, "pipelineTestTransportUnknown"); }
            if (record.status == "unknown") TrySave(record);
        }

        internal PipelineTestRecord Query(string requestId)
        {
            Tick();
            if (requestId != null && !PipelineTestStore.IsRequestId(requestId)) throw new ArgumentException("Invalid requestId.");
            if (_active != null && (requestId == null || _active.requestId == requestId)) return Snapshot(_active);
            var record = requestId == null ? _context.Journal.FindLatest(out var unreadable)
                : _context.Journal.Load(requestId, out unreadable);
            if (unreadable) throw new InvalidOperationException("Pipeline test journal is corrupt or unreadable; do not replay.");
            return record == null ? null : Snapshot(record);
        }

        internal void Tick()
        {
            if (_updating || _active == null || _task == null || _active.status != "running") return;
            _updating = true;
            try
            {
                // 本地超时后不接纳迟到业务终态；释放也不意味着远端停止，不能允许迟到任务覆盖新记录。
                PipelineTestProtocol.ValidUtc(_active.queuedAt, out var queued);
                var now = _context.Clock();
                if (now < queued) { SetUnknown(_active, "pipelineTestClockChanged"); TrySave(_active); return; }
                if ((now - queued).TotalMilliseconds >= _active.timeoutMs)
                { SetUnknown(_active, "pipelineTestWaitTimeout"); TrySave(_active); return; }
                if (!_task.IsCompleted) return;
                if (_task.IsCanceled || _task.IsFaulted)
                { if (_task.IsFaulted) { var ignored = _task.Exception; } SetUnknown(_active, "pipelineTestTransportUnknown"); }
                else if (_session == null || _session.TargetIdentity != _active.targetIdentity || _session.Generation != _active.sessionGeneration)
                    SetUnknown(_active, "pipelineTestTargetChanged");
                else ApplyResult(_task.Result);
                if (!TrySave(_active)) return;
                if (!_active.busy) { ReleaseSession(true); _active = null; _task = null; }
            }
            catch (Exception)
            {
                // 会话身份属性或本地时钟/读取异常也只能保留未知，不释放测试执行门。
                SetUnknown(_active, "pipelineTestObservationUnknown"); TrySave(_active);
            }
            finally { _updating = false; }
        }

        private void ApplyResult(PipelineTestResult result)
        {
            if (result == null || !result.valid || !result.executionEnded || !IsTerminal(result.status)
                || result.success != (result.status == "passed") || result.httpStatus != 200 || result.data == null)
            {
                if (result != null && result.httpStatus >= 0 && result.httpStatus <= 599) _active.httpStatus = result.httpStatus;
                SetUnknown(_active, "pipelineTestOutcomeUnknown"); return;
            }
            _active.status = result.status; _active.success = result.success; _active.busy = false;
            _active.executionEnded = true; _active.endedAt = Now(); _active.httpStatus = result.httpStatus;
            _active.data = result.data; _active.errorCode = result.errorCode; _active.error = result.error;
        }

        internal PipelineTestRecord Release(string requestId, bool confirmed)
        {
            if (!confirmed || !PipelineTestStore.IsRequestId(requestId))
                throw new ArgumentException("Local release requires an exact requestId and confirm=true.");
            var record = Query(requestId);
            if (record == null || record.status != "unknown" || record.released)
                throw new InvalidOperationException("Only a registered unknown Pipeline test can be released locally.");
            record.released = true; record.busy = false;
            // 原子发布失败保持内存及原记录 busy；不据内存改写宣称释放成功。
            _context.Journal.Save(record);
            if (_active != null && _active.requestId == requestId)
            { ReleaseSession(false); _active = null; _task = null; }
            return Snapshot(record);
        }

        private void Recover()
        {
            var record = _context.Journal.FindOutstanding(out var invalid);
            _storageBlocked = invalid;
            if (!invalid && record != null) RecoverRecord(record);
        }

        private void RecoverRecord(PipelineTestRecord record)
        {
            _active = record;
            // 当前 domain 无任务引用，即使PID/generation看似相同也不能重新 Prepare/Submit。
            SetUnknown(record, "pipelineTestStateLost"); TrySave(record);
        }

        private bool TrySave(PipelineTestRecord record)
        {
            try { _context.Journal.Save(record); return true; }
            catch (Exception) { SetUnknown(record, "pipelineTestJournalUnavailable"); return false; }
        }

        private PipelineTestRecord NewRecord(string id, PipelineTestOptions options)
        {
            return new PipelineTestRecord { requestId = id, projectPath = _context.Root, editorPid = _context.EditorPid,
                unityVersion = _context.UnityVersion, generation = _context.Generation, queuedAt = Now(),
                filter = options.Filter, filterType = options.FilterType, timeoutMs = options.TimeoutMs };
        }

        private void RequireIdle()
        {
            var refusal = _context.NativeActivity();
            if (refusal != null) throw new InvalidOperationException("Pipeline tests require an idle native test runner and Editor.");
        }

        private void ReleaseSession(bool ended)
        {
            try { if (_session != null) _session.Release(ended); }
            catch (Exception) { /* 远端没有cancel；清理异常不能重放或篡改已持久化结果。 */ }
            _session = null;
        }

        private string Now() { return _context.Clock().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
        private static PipelineTestRecord Snapshot(PipelineTestRecord record) { return PipelineTestStore.Clone(record); }
        internal static bool IsTerminal(string status)
        { return status == "passed" || status == "failed" || status == "no_tests" || status == "skipped" || status == "inconclusive"; }
        private static void SetUnknown(PipelineTestRecord record, string code)
        {
            record.status = "unknown"; record.busy = true; record.released = false; record.success = null;
            record.executionEnded = false; record.data = null; record.errorCode = code;
            record.error = "Pipeline test outcome is unknown; query the original request locally and do not replay.";
        }
    }
}
