using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class PipelineTestTrackerTests
    {
        private const string Generation = "11111111111111111111111111111111";
        private DateTimeOffset _now;
        private FakeJournal _journal;
        private FakeSession _session;
        private PipelineTestContext _context;
        private int _prepares;
        private int _probes;

        [SetUp]
        public void SetUp()
        {
            _now = DateTimeOffset.Parse("2026-11-05T06:00:00Z");
            _journal = new FakeJournal(); _session = new FakeSession(); _prepares = 0; _probes = 0;
            _context = new PipelineTestContext
            {
                Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pipeline-test-fixture-project")), EditorPid = 1234,
                UnityVersion = "6000.0.51f1", Generation = Generation, Clock = () => _now, Journal = _journal,
                NativeActivity = () => { _probes++; return null; },
                SessionFactory = options => { _prepares++; return _session; }
            };
        }

        [Test]
        public void IntentAndBoundIdentityArePersistedBeforeSingleSubmit()
        {
            _session.BeforeSubmit = () =>
            {
                Assert.That(_journal.Writes.Count, Is.EqualTo(2));
                var intent = _journal.Writes[0]; var bound = _journal.Writes[1];
                Assert.That(intent.status, Is.EqualTo("intent")); Assert.That(intent.submissionStarted, Is.False);
                Assert.That(intent.targetIdentity, Is.Null);
                Assert.That(bound.status, Is.EqualTo("running")); Assert.That(bound.submissionStarted, Is.True);
                Assert.That(bound.targetIdentity, Is.EqualTo(_session.TargetIdentity));
            };
            var engine = Engine(); var started = engine.Start("request_1", Options());
            Assert.That(started.busy && engine.HasBusy, Is.True);
            Assert.That(_prepares, Is.EqualTo(1)); Assert.That(_session.Submits, Is.EqualTo(1));
            Assert.That(_probes, Is.EqualTo(2));
            var duplicate = engine.Start("request_1", Options());
            Assert.That(duplicate.requestId, Is.EqualTo(started.requestId));
            Assert.That(_session.Submits, Is.EqualTo(1)); Assert.That(_prepares, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => engine.Start("request_2", Options()));
        }

        [TestCase(1)] [TestCase(2)]
        public void IntentOrBindingSaveFailurePreventsAllPostAndRetainsUnknown(int failureWrite)
        {
            _journal.FailOnSave = failureWrite;
            var engine = Engine(); var record = engine.Start("request_1", Options());
            Assert.That(record.status, Is.EqualTo("unknown")); Assert.That(engine.HasBusy, Is.True);
            Assert.That(_session.Submits, Is.Zero);
            Assert.That(_prepares, Is.EqualTo(failureWrite == 1 ? 0 : 1));
        }

        [Test]
        public void NativeBusyOrUnsupportedProbeRejectsBeforeIntentAndPreparation()
        {
            _context.NativeActivity = () => "unsupported";
            var engine = Engine();
            Assert.Throws<InvalidOperationException>(() => engine.Start("request_1", Options()));
            Assert.That(_journal.Writes, Is.Empty); Assert.That(_prepares, Is.Zero);
            _context.NativeActivity = () => throw new NotSupportedException("fixture");
            Assert.Throws<NotSupportedException>(() => engine.Start("request_1", Options()));
            Assert.That(_session.Submits, Is.Zero);
        }

        [Test]
        public void GateChangingAfterPreparePreventsSubmitAndRecordsNotSubmitted()
        {
            _context.NativeActivity = () => ++_probes == 1 ? null : "now busy";
            var engine = Engine(); var record = engine.Start("request_1", Options());
            Assert.That(record.status, Is.EqualTo("not_submitted")); Assert.That(record.busy, Is.False);
            Assert.That(_session.Submits, Is.Zero); Assert.That(engine.HasBusy, Is.False);
            Assert.That(_session.Releases, Is.EqualTo(1)); Assert.That(_session.ReleaseEnded, Is.False);
            engine.Start("request_1", Options()); Assert.That(_prepares, Is.EqualTo(1));
        }

        [Test]
        public void AdapterPreparationExceptionOrUnboundSessionNeverPosts()
        {
            _context.SessionFactory = options => throw new InvalidOperationException("fixture-sensitive");
            var engine = Engine(); var record = engine.Start("request_1", Options());
            Assert.That(record.status, Is.EqualTo("not_submitted")); Assert.That(record.error, Does.Not.Contain("fixture-sensitive"));
            _context.SessionFactory = options => _session; _session.Identity = "invalid";
            record = engine.Start("request_2", Options());
            Assert.That(record.status, Is.EqualTo("not_submitted")); Assert.That(_session.Submits, Is.Zero);
        }

        [TestCase("passed", 1, 0, 0, 0)]
        [TestCase("failed", 1, 1, 0, 0)]
        [TestCase("no_tests", 0, 0, 0, 0)]
        [TestCase("skipped", 0, 0, 1, 0)]
        [TestCase("inconclusive", 0, 0, 0, 1)]
        public void ConfirmedBusinessTerminalsReleaseLeaseAndPersistWithoutLosingNull(string status, int pass, int fail, int skip, int inconclusive)
        {
            var engine = Engine(); engine.Start("request_1", Options());
            _session.Completion.SetResult(PipelineTestProtocolTests.Parse(PipelineTestProtocolTests.Inner(pass, fail, skip, inconclusive)));
            engine.Tick();
            var record = engine.Query("request_1");
            Assert.That(record.status, Is.EqualTo(status)); Assert.That(record.busy, Is.False);
            Assert.That(record.executionEnded, Is.True); Assert.That(record.success, Is.EqualTo(status == "passed"));
            Assert.That(engine.HasBusy, Is.False); Assert.That(_session.ReleaseEnded, Is.True);
            Assert.That(_session.Releases, Is.EqualTo(1));
            var data = (Dictionary<string, object>)record.data;
            Assert.That(data.ContainsKey("warnings") && data["warnings"] == null, Is.True);
            var itemList = (List<object>)data["results"];
            if (itemList.Count > 0) Assert.That(((Dictionary<string, object>)itemList[0]).ContainsKey("Message"), Is.True);
            engine.Start("request_1", Options()); Assert.That(_session.Submits, Is.EqualTo(1));
        }

        [Test]
        public void TimeoutRemainsUnknownBusyAndLateTerminalCannotOverwriteIt()
        {
            var engine = Engine(); engine.Start("request_1", Options());
            _now = _now.AddMilliseconds(120000); engine.Tick();
            var record = engine.Query("request_1");
            Assert.That(record.status, Is.EqualTo("unknown")); Assert.That(record.errorCode, Is.EqualTo("pipelineTestWaitTimeout"));
            _session.Completion.SetResult(PipelineTestProtocolTests.Parse(PipelineTestProtocolTests.Inner()));
            engine.Tick();
            Assert.That(engine.Query("request_1").status, Is.EqualTo("unknown"));
            Assert.That(engine.HasBusy, Is.True); Assert.That(_session.Releases, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => engine.Start("request_2", Options()));
        }

        [TestCase("fault")] [TestCase("cancel")] [TestCase("unknown")] [TestCase("badResult")]
        public void TransportOrBadAdapterOutcomeNeverReleasesLease(string kind)
        {
            var engine = Engine(); engine.Start("request_1", Options());
            if (kind == "fault") _session.Completion.SetException(new IOException("fixture-sensitive"));
            else if (kind == "cancel") _session.Completion.SetCanceled();
            else if (kind == "unknown") _session.Completion.SetResult(PipelineTestProtocol.Unknown(504));
            else _session.Completion.SetResult(new PipelineTestResult { valid = true, executionEnded = true, status = "passed", success = false });
            engine.Tick();
            var record = engine.Query("request_1");
            Assert.That(record.status, Is.EqualTo("unknown")); Assert.That(record.data, Is.Null);
            Assert.That(record.busy && engine.HasBusy, Is.True); Assert.That(_session.Releases, Is.Zero);
            Assert.That(record.error, Does.Not.Contain("fixture-sensitive"));
        }

        [Test]
        public void SubmitThrowIsNeverRetriedAndRequestIdCannotBeReplayed()
        {
            _session.ThrowOnSubmit = true; var engine = Engine();
            Assert.That(engine.Start("request_1", Options()).status, Is.EqualTo("unknown"));
            engine.Start("request_1", Options()); engine.Query(null); engine.Tick();
            Assert.That(_session.Submits, Is.EqualTo(1));
        }

        [Test]
        public void IdentityOrSessionGenerationChangeRejectsOtherwiseCompletedResult()
        {
            var engine = Engine(); engine.Start("request_1", Options()); _session.Identity = new string('b', 64);
            _session.Completion.SetResult(PipelineTestProtocolTests.Parse(PipelineTestProtocolTests.Inner())); engine.Tick();
            Assert.That(engine.Query(null).status, Is.EqualTo("unknown")); Assert.That(engine.HasBusy, Is.True);
        }

        [Test]
        public void TerminalJournalWriteFailureRetainsUnknownAndBusy()
        {
            var engine = Engine(); engine.Start("request_1", Options()); _journal.FailOnSave = 3;
            _session.Completion.SetResult(PipelineTestProtocolTests.Parse(PipelineTestProtocolTests.Inner())); engine.Tick();
            Assert.That(engine.Query(null).status, Is.EqualTo("unknown")); Assert.That(engine.HasBusy, Is.True);
        }

        [Test]
        public void ReloadConvertsRunningOrIntentToUnknownWithoutPreparingOrSubmitting()
        {
            var engine = Engine(); engine.Start("request_1", Options());
            var next = new PipelineTestEngine(_context);
            Assert.That(next.Query("request_1").status, Is.EqualTo("unknown"));
            Assert.That(next.HasBusy, Is.True); Assert.That(_prepares, Is.EqualTo(1)); Assert.That(_session.Submits, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => next.Start("request_2", Options()));
        }

        [Test]
        public void ExplicitReleaseOnlyWorksForExactUnknownAndDoesNotCancelOrAdoptLateResults()
        {
            var engine = Engine(); engine.Start("request_1", Options());
            Assert.Throws<InvalidOperationException>(() => engine.Release("request_1", true));
            _now = _now.AddMinutes(3); engine.Tick();
            Assert.Throws<ArgumentException>(() => engine.Release("request_1", false));
            Assert.Throws<InvalidOperationException>(() => engine.Release("other_request", true));
            var released = engine.Release("request_1", true);
            Assert.That(released.released, Is.True); Assert.That(released.busy, Is.False); Assert.That(engine.HasBusy, Is.False);
            Assert.That(_session.ReleaseEnded, Is.False); Assert.That(_session.Releases, Is.EqualTo(1));
            _session.Completion.SetResult(PipelineTestProtocolTests.Parse(PipelineTestProtocolTests.Inner())); engine.Tick();
            Assert.That(engine.Query("request_1").status, Is.EqualTo("unknown"));
            engine.Start("request_1", Options()); Assert.That(_session.Submits, Is.EqualTo(1));
        }

        [Test]
        public void ReleaseWriteFailureDoesNotClearInMemoryBusy()
        {
            var engine = Engine(); engine.Start("request_1", Options()); _now = _now.AddMinutes(3); engine.Tick();
            _journal.FailOnSave = _journal.Writes.Count + 1;
            Assert.Throws<IOException>(() => engine.Release("request_1", true));
            Assert.That(engine.HasBusy, Is.True); Assert.That(_session.Releases, Is.Zero);
        }

        [Test]
        public void CorruptJournalBlocksNewRequestsAndNeverPosts()
        {
            _journal.Invalid = true; var engine = Engine(); Assert.That(engine.HasBusy, Is.True);
            Assert.Throws<InvalidOperationException>(() => engine.Start("request_1", Options()));
            Assert.That(_prepares, Is.Zero); Assert.That(_session.Submits, Is.Zero);
        }

        [Test]
        public void QueryOnlyReadsJournalAndDoesNotPrepareOrReadRemoteGlobalStatus()
        {
            var engine = Engine(); Assert.That(engine.Query(null), Is.Null); Assert.That(engine.Query("missing"), Is.Null);
            Assert.That(_prepares, Is.Zero); Assert.That(_probes, Is.Zero);
        }

        [TestCase("../request")] [TestCase("request.json")] [TestCase("")] [TestCase(null)]
        public void InvalidIdIsRejectedBeforeSessionPreparation(string id)
        { Assert.Throws<ArgumentException>(() => Engine().Start(id, Options())); Assert.That(_prepares, Is.Zero); }

        private PipelineTestEngine Engine() { return new PipelineTestEngine(_context); }
        private static PipelineTestOptions Options() { return PipelineTestProtocolTests.Options; }

        private sealed class FakeSession : PipelineTestSession
        {
            internal string Identity = new string('a', 64);
            internal TaskCompletionSource<PipelineTestResult> Completion = new TaskCompletionSource<PipelineTestResult>();
            internal Action BeforeSubmit; internal int Submits; internal int Releases; internal bool ReleaseEnded; internal bool ThrowOnSubmit;
            public override string TargetIdentity => Identity;
            public override string Generation => PipelineTestTrackerTests.Generation;
            public override Task<PipelineTestResult> SubmitAsync()
            { Submits++; BeforeSubmit?.Invoke(); if (ThrowOnSubmit) throw new IOException("fixture-sensitive"); return Completion.Task; }
            public override void Release(bool executionEnded) { Releases++; ReleaseEnded = executionEnded; }
        }

        private sealed class FakeJournal : IPipelineTestJournal
        {
            internal readonly List<PipelineTestRecord> Writes = new List<PipelineTestRecord>();
            internal readonly Dictionary<string, PipelineTestRecord> Records = new Dictionary<string, PipelineTestRecord>();
            internal int FailOnSave; internal bool Invalid; private int _attempts;
            public PipelineTestRecord Load(string id, out bool unreadable)
            { unreadable = Invalid; return Records.TryGetValue(id, out var value) ? PipelineTestStore.Clone(value) : null; }
            public PipelineTestRecord FindLatest(out bool unreadable)
            { unreadable = Invalid; PipelineTestRecord value = null; foreach (var record in Records.Values) value = record; return value == null ? null : PipelineTestStore.Clone(value); }
            public PipelineTestRecord FindOutstanding(out bool unreadable)
            { unreadable = Invalid; foreach (var record in Records.Values) if (record.busy) return PipelineTestStore.Clone(record); return null; }
            public void RequireCapacityForNewRequest() { }
            public void Save(PipelineTestRecord record)
            {
                if (++_attempts == FailOnSave) throw new IOException("fixture-sensitive");
                var clone = PipelineTestStore.Clone(record); Writes.Add(clone); Records[record.requestId] = clone;
            }
        }
    }
}
