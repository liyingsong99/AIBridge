using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AIBridge.Internal.Json;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class PipelineCompileEngineTests
    {
        private string _root;
        private DateTimeOffset _now;
        private PipelineCompileContext _context;
        private Session _session;
        private int _prepares;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "AIBridgeCompileEngine_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root); _now = DateTimeOffset.Parse("2026-11-05T06:00:00Z");
            _session = new Session(); _prepares = 0;
            _context = new PipelineCompileContext { Root = _root, Store = new PipelineCompileStore(_root), EditorPid = 123,
                UnityVersion = "6000.0.51f1", Generation = new string('a', 32), Clock = () => _now, NativeActivity = () => null,
                Prepare = timeout => { _prepares++; return _session; } };
        }
        [TearDown]
        public void TearDown() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

        [Test]
        public void PersistedBoundIntentPrecedesSinglePostAndSameIdDoesNotReplay()
        {
            _session.BeforeSubmit = () =>
            {
                var record = _context.Store.Load("original", out var invalid);
                Assert.That(invalid, Is.False); Assert.That(record.status, Is.EqualTo("submitting"));
                Assert.That(record.submissionStarted && record.busy, Is.True);
                Assert.That(record.targetIdentity, Is.EqualTo(_session.TargetIdentity));
            };
            var engine = new PipelineCompileEngine(_context); engine.Start("original", 10000);
            engine.Start("original", 10000); Assert.That(_session.Submits, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => engine.Start("other", 10000));
            Assert.That(_prepares, Is.EqualTo(1));
        }

        [TestCase("compiling")] [TestCase("up_to_date")] [TestCase("failed")]
        public void EveryAcknowledgementKeepsGateAndNeverClaimsCompilationSuccess(string acknowledgement)
        {
            var engine = new PipelineCompileEngine(_context); engine.Start("original", 10000);
            _session.Completion.SetResult(Ack(acknowledgement)); engine.Tick();
            var record = engine.Query("original");
            Assert.That(record.status, Is.EqualTo("acknowledged")); Assert.That(record.busy && engine.HasBusy, Is.True);
            var wire = AIBridgeJson.Serialize(PipelineCompileStore.ToData(record));
            Assert.That(wire, Does.Contain("\"compilationSuccess\":null"));
            Assert.That(_session.Releases, Is.Zero);
            var released = engine.Release("original", true);
            Assert.That(released.status, Is.EqualTo("unknown")); Assert.That(released.released, Is.True);
            Assert.That(released.busy || engine.HasBusy, Is.False); Assert.That(_session.Releases, Is.EqualTo(1));
        }

        [Test]
        public void TimeoutAndLateResponseRemainUnknownUntilExactRelease()
        {
            var engine = new PipelineCompileEngine(_context); engine.Start("original", 1000);
            _now = _now.AddMilliseconds(1000); engine.Tick(); _session.Completion.SetResult(Ack("up_to_date")); engine.Tick();
            Assert.That(engine.Query("original").status, Is.EqualTo("unknown")); Assert.That(engine.HasBusy, Is.True);
            Assert.Throws<ArgumentException>(() => engine.Release("original", false));
            Assert.Throws<InvalidOperationException>(() => engine.Release("missing", true));
            engine.Release("original", true); Assert.That(engine.HasBusy, Is.False);
            engine.Start("original", 1000); Assert.That(_session.Submits, Is.EqualTo(1));
        }

        [Test]
        public void ReloadRecoversBusyAsUnknownWithoutPreparationOrGlobalStatus()
        {
            var original = new PipelineCompileEngine(_context); original.Start("original", 10000);
            var recovered = new PipelineCompileEngine(_context);
            Assert.That(recovered.Query("original").status, Is.EqualTo("unknown"));
            Assert.That(recovered.HasBusy, Is.True); Assert.That(_prepares, Is.EqualTo(1));
            Assert.That(_session.Submits, Is.EqualTo(1));
        }

        [Test]
        public void MissingOrThrowingIdleProbePreventsIntentAndPrepare()
        {
            var engine = new PipelineCompileEngine(_context); _context.NativeActivity = null;
            Assert.Throws<InvalidOperationException>(() => engine.Start("original", 10000));
            Assert.That(_context.Store.Load("original", out var invalid), Is.Null); Assert.That(invalid, Is.False);
            _context.NativeActivity = () => throw new InvalidOperationException();
            Assert.Throws<InvalidOperationException>(() => engine.Start("original", 10000));
            Assert.That(_prepares, Is.Zero);
        }

        [Test]
        public void PreparationFailureIsNotSubmittedAndDoesNotHoldGate()
        {
            _context.Prepare = timeout => throw new NotSupportedException();
            var engine = new PipelineCompileEngine(_context); var record = engine.Start("original", 10000);
            Assert.That(record.status, Is.EqualTo("not_submitted")); Assert.That(record.submissionStarted || engine.HasBusy, Is.False);
            Assert.That(_session.Submits, Is.Zero);
        }

        [Test]
        public void ResponseIdentityDriftAndFaultStayUnknown()
        {
            var engine = new PipelineCompileEngine(_context); engine.Start("original", 10000);
            _session.Identity = new string('d', 64); _session.Completion.SetResult(Ack("up_to_date")); engine.Tick();
            Assert.That(engine.Query("original").status, Is.EqualTo("unknown")); Assert.That(engine.HasBusy, Is.True);
        }

        [Test]
        public void CapacityRejectsBeforeIntentOrPrepareAndExistingIdStillReads()
        {
            _context.Store = new PipelineCompileStore(_root, 1); _context.Prepare = timeout => throw new NotSupportedException();
            var engine = new PipelineCompileEngine(_context); engine.Start("original", 10000);
            Assert.Throws<IOException>(() => engine.Start("second", 10000));
            Assert.That(engine.HasBusy, Is.False); Assert.That(engine.Start("original", 10000).status, Is.EqualTo("not_submitted"));
        }

        [TestCase(1)] [TestCase(2)]
        public void IntentOrBindingIoFailureNeverPostsAndExactReleaseCanRecover(int failureWrite)
        {
            var journal = new FailingJournal(_context.Store) { FailWrite = failureWrite }; _context.Store = journal;
            var engine = new PipelineCompileEngine(_context); var record = engine.Start("original", 10000);
            Assert.That(record.status, Is.EqualTo("unknown")); Assert.That(engine.HasBusy, Is.True);
            Assert.That(_session.Submits, Is.Zero); Assert.That(_prepares, Is.EqualTo(failureWrite == 1 ? 0 : 1));
            journal.FailWrite = 0; engine.Release("original", true); Assert.That(engine.HasBusy, Is.False);
            Assert.That(engine.Query("original").released, Is.True);
        }

        [Test]
        public void AcknowledgementSaveFailureAndReleaseFailureCannotClearBusy()
        {
            var journal = new FailingJournal(_context.Store) { FailWrite = 3 }; _context.Store = journal;
            var engine = new PipelineCompileEngine(_context); engine.Start("original", 10000);
            _session.Completion.SetResult(Ack("up_to_date")); engine.Tick();
            Assert.That(engine.Query("original").status, Is.EqualTo("unknown"));
            journal.FailWrite = 4; Assert.Throws<IOException>(() => engine.Release("original", true));
            Assert.That(engine.HasBusy, Is.True); Assert.That(_session.Releases, Is.Zero);
            journal.FailWrite = 0; engine.Release("original", true); Assert.That(engine.HasBusy, Is.False);
        }

        [Test]
        public void PublicProjectionKeepsNullableCompilationResultForUnknown()
        {
            var engine = new PipelineCompileEngine(_context); engine.Start("original", 1000); _now = _now.AddSeconds(1); engine.Tick();
            var wire = AIBridgeJson.DeserializeObjectStrict(AIBridgeJson.Serialize(PipelineCompileTracker.Projection(engine.Query("original"))));
            foreach (var key in new[] { "compilationSuccess", "errorCount", "warningCount", "duration" })
            { Assert.That(wire.ContainsKey(key), Is.True); Assert.That(wire[key], Is.Null); }
            Assert.That(wire["operationCorrelation"], Is.False); Assert.That(wire["acknowledgementConfirmed"], Is.False);
        }

        private sealed class FailingJournal : IPipelineCompileJournal
        {
            private readonly IPipelineCompileJournal _inner;
            internal int FailWrite, Writes;
            internal FailingJournal(IPipelineCompileJournal inner) { _inner = inner; }
            public PipelineCompileRecord Load(string id, out bool invalid) { return _inner.Load(id, out invalid); }
            public PipelineCompileRecord FindOutstanding(out bool invalid) { return _inner.FindOutstanding(out invalid); }
            public PipelineCompileRecord FindLatest(out bool invalid) { return _inner.FindLatest(out invalid); }
            public void RequireCapacityForNewRequest() { _inner.RequireCapacityForNewRequest(); }
            public void Save(PipelineCompileRecord record)
            { if (++Writes == FailWrite) throw new IOException("Controlled IO failure."); _inner.Save(record); }
        }

        private static PipelineCompileResult Ack(string acknowledgement)
        {
            var data = new Dictionary<string, object> { { "acknowledgement", acknowledgement }, { "executedAt", "2026-11-05T06:00:00Z" },
                { "warnings", null }, { "compilationSuccess", null }, { "errorCount", null }, { "warningCount", null },
                { "duration", null }, { "operationCorrelation", false } };
            return new PipelineCompileResult { valid = true, acknowledgement = acknowledgement, httpStatus = 200, data = AIBridgeJson.PreserveObjectNulls(data) };
        }
        private sealed class Session : PipelineCompileSession
        {
            internal string Identity = new string('b', 64);
            internal TaskCompletionSource<PipelineCompileResult> Completion = new TaskCompletionSource<PipelineCompileResult>();
            internal Action BeforeSubmit; internal int Submits, Releases;
            public override string TargetIdentity => Identity;
            public override string Generation => new string('c', 32);
            public override Task<PipelineCompileResult> SubmitAsync() { Submits++; BeforeSubmit?.Invoke(); return Completion.Task; }
            public override void Release() { Releases++; }
        }
    }
}
