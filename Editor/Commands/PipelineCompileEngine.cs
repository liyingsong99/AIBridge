using System;
using System.Globalization;
using System.Threading.Tasks;

namespace AIBridge.Editor
{
    internal interface IPipelineCompileJournal
    {
        PipelineCompileRecord Load(string id, out bool invalid);
        PipelineCompileRecord FindOutstanding(out bool invalid);
        PipelineCompileRecord FindLatest(out bool invalid);
        void RequireCapacityForNewRequest();
        void Save(PipelineCompileRecord record);
    }

    internal sealed class PipelineCompileContext
    {
        internal string Root, UnityVersion, Generation;
        internal int EditorPid;
        internal IPipelineCompileJournal Store;
        internal Func<DateTimeOffset> Clock;
        internal Func<string> NativeActivity;
        internal Func<int, PipelineCompileSession> Prepare;
    }

    /// <summary>只确认触发应答；全局编译槽永远不能认证原请求成功。</summary>
    internal sealed class PipelineCompileEngine
    {
        private readonly PipelineCompileContext _context;
        private PipelineCompileRecord _active;
        private PipelineCompileSession _session;
        private Task<PipelineCompileResult> _task;
        private bool _storageBlocked;
        private bool _updating;

        internal PipelineCompileEngine(PipelineCompileContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            var record = context.Store.FindOutstanding(out var invalid);
            _storageBlocked = invalid;
            if (!invalid && record != null)
            { _active = record; Unknown(record, "pipelineCompileStateLost"); Save(record); }
        }

        internal bool HasBusy
        {
            get
            {
                if (_active != null && _active.busy) return true;
                var outstanding = _context.Store.FindOutstanding(out var invalid);
                _storageBlocked = invalid;
                if (outstanding != null && !invalid)
                { _active = outstanding; Unknown(_active, "pipelineCompileStateLost"); Save(_active); }
                return _storageBlocked || _active != null && _active.busy;
            }
        }

        internal PipelineCompileRecord Start(string id, int timeout)
        {
            if (!PipelineTestStore.IsRequestId(id) || timeout < 1000 || timeout > 60000) throw new ArgumentException();
            Tick();
            var previous = _context.Store.Load(id, out var invalid);
            if (invalid) throw new InvalidOperationException();
            if (_active != null && _active.requestId == id) return Copy(_active);
            if (previous != null) return Copy(previous);
            if (HasBusy) throw new InvalidOperationException("A compile request is active or unknown.");
            RequireIdle(); _context.Store.RequireCapacityForNewRequest();
            var record = new PipelineCompileRecord { requestId = id, projectPath = _context.Root,
                editorPid = _context.EditorPid, unityVersion = _context.UnityVersion, generation = _context.Generation,
                queuedAt = Now(), timeoutMs = timeout };
            _active = record;
            if (Save(record)) PrepareAndSubmit(record);
            return Copy(record);
        }

        private void PrepareAndSubmit(PipelineCompileRecord record)
        {
            string identity, generation;
            try
            {
                _session = _context.Prepare(record.timeoutMs);
                identity = _session == null ? null : _session.TargetIdentity;
                generation = _session == null ? null : _session.Generation;
                if (!PipelineTestStore.IsIdentity(identity) || !PipelineTestStore.IsGeneration(generation)) throw new InvalidOperationException();
                RequireIdle();
            }
            catch (Exception)
            {
                record.status = "not_submitted"; record.busy = false; record.endedAt = Now();
                record.errorCode = "pipelineCompilePreparationUnavailable";
                record.error = "Compile preparation or idle probe failed; no POST was started.";
                if (Save(record)) { ReleaseSession(); _active = null; }
                return;
            }
            record.targetIdentity = identity; record.sessionGeneration = generation;
            record.submissionStarted = true; record.status = "submitting";
            // 可能提交标志必须先持久化；保存不确定时不调用 Submit。
            if (!Save(record)) return;
            try
            {
                _task = _session.SubmitAsync();
                if (_task == null) { Unknown(record, "pipelineCompileTransportUnknown"); Save(record); }
            }
            catch (Exception) { Unknown(record, "pipelineCompileTransportUnknown"); Save(record); }
        }

        internal void Tick()
        {
            if (_updating || _active == null || _task == null || _active.status != "submitting") return;
            _updating = true;
            try
            {
                var queued = DateTimeOffset.Parse(_active.queuedAt, CultureInfo.InvariantCulture);
                var now = _context.Clock();
                if (now < queued || (now - queued).TotalMilliseconds >= _active.timeoutMs)
                { Unknown(_active, "pipelineCompileWaitTimeout"); Save(_active); return; }
                if (!_task.IsCompleted) return;
                var result = !_task.IsFaulted && !_task.IsCanceled ? _task.Result : null;
                if (_task.IsFaulted) { var ignored = _task.Exception; }
                if (_session == null || _session.TargetIdentity != _active.targetIdentity
                    || _session.Generation != _active.sessionGeneration || result == null || !result.valid
                    || result.httpStatus != 200 || result.data == null
                    || (result.acknowledgement != "compiling" && result.acknowledgement != "up_to_date" && result.acknowledgement != "failed"))
                    Unknown(_active, "pipelineCompileOutcomeUnknown");
                else Acknowledge(result);
                if (!Save(_active)) return;
                if (!_active.busy) { ReleaseSession(); _active = null; _task = null; }
            }
            catch (Exception) { Unknown(_active, "pipelineCompileObservationUnknown"); Save(_active); }
            finally { _updating = false; }
        }

        private void Acknowledge(PipelineCompileResult result)
        {
            _active.status = "acknowledged"; _active.acknowledgement = result.acknowledgement;
            // 应答只确认Refresh调用；三个状态都不能证明本次编译生命周期已结束。
            _active.busy = true; _active.endedAt = Now();
            _active.httpStatus = 200; _active.data = result.data;
            _active.errorCode = result.errorCode; _active.error = result.error;
        }

        internal PipelineCompileRecord Query(string id)
        {
            Tick();
            if (id != null && !PipelineTestStore.IsRequestId(id)) throw new ArgumentException();
            if (_active != null && (id == null || id == _active.requestId)) return Copy(_active);
            var record = id == null ? _context.Store.FindLatest(out var invalid) : _context.Store.Load(id, out invalid);
            if (invalid) throw new InvalidOperationException("Compile journal unavailable; do not replay.");
            return record == null ? null : Copy(record);
        }

        internal PipelineCompileRecord Release(string id, bool confirm)
        {
            if (!confirm || !PipelineTestStore.IsRequestId(id)) throw new ArgumentException();
            var record = Query(id);
            if (record == null || record.released || !record.busy
                || (record.status != "unknown" && record.status != "acknowledged"))
                throw new InvalidOperationException("Only an exact unknown or acknowledged compile request can be released locally.");
            Unknown(record, "pipelineCompileLocallyReleased"); record.busy = false; record.released = true;
            _context.Store.Save(record);
            if (_active != null && _active.requestId == id) { ReleaseSession(); _active = null; _task = null; }
            return Copy(record);
        }

        private bool Save(PipelineCompileRecord record)
        {
            try { _context.Store.Save(record); return true; }
            catch (Exception) { Unknown(record, "pipelineCompileJournalUnavailable"); return false; }
        }
        private static void Unknown(PipelineCompileRecord record, string code)
        {
            record.status = "unknown"; record.busy = true; record.released = false;
            record.acknowledgement = null; record.data = null; record.errorCode = code;
            record.error = "Compilation outcome is unknown; query the original request locally and do not replay.";
        }
        private void RequireIdle()
        {
            if (_context.NativeActivity == null || _context.NativeActivity() != null) throw new InvalidOperationException("Editor activity is not confirmed idle.");
        }
        private void ReleaseSession()
        {
            try { if (_session != null) _session.Release(); } catch (Exception) { }
            _session = null;
        }
        private string Now() { return _context.Clock().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
        private static PipelineCompileRecord Copy(PipelineCompileRecord record) { return PipelineCompileStore.Clone(record); }
    }
}
