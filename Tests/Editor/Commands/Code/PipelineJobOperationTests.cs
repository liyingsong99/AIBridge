using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AIBridge.Internal.Json;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public sealed class PipelineJobOperationTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private const string JobId = "0123456789abcdef0123456789abcdef";
        private readonly Dictionary<string, byte[]> _backups = new Dictionary<string, byte[]>();
        private string _id;
        private object _savedActive;
        private object _record;
        private object _operation;
        private FakeSession _session;

        [SetUp]
        public void SetUp()
        {
            var bridge = AIBridge.BridgeDirectory;
            _savedActive = Active.GetValue(null);
            _id = "pipeline-job-fixture-" + Guid.NewGuid().ToString("N");
            Backup(PathFor(null)); Backup(PathFor(_id));
            var type = typeof(CodeCommand).GetNestedType("PipelineOperationRecord", BindingFlags.NonPublic);
            _record = Activator.CreateInstance(type, true);
            Set(_record, "requestId", _id); Set(_record, "operationId", _id);
            Set(_record, "pipelineCommand", "script run"); Set(_record, "detached", true);
            Set(_record, "upstreamExecutionTimeoutDisabled", true); Set(_record, "confirmed", true);
            Set(_record, "operationKind", "detached_compile_only");
            Set(_record, "targetIdentity", new string('a', 64)); Set(_record, "adapterGeneration", new string('b', 32));
            Set(_record, "timeoutMs", 60000);
            _session = new FakeSession();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                foreach (var pair in _backups)
                {
                    if (pair.Value != null) File.WriteAllBytes(pair.Key, pair.Value);
                    else if (File.Exists(pair.Key)) File.Delete(pair.Key);
                }
            }
            finally { Active.SetValue(null, _savedActive); _backups.Clear(); }
        }

        [Test]
        public void SubmissionBindsIdBeforePollingAndNeverMeansExecutionEnded()
        {
            Create(Task.FromResult(Submission()));
            Step(out _, out var release);
            Assert.That(Get<string>("remoteJobId"), Is.EqualTo(JobId));
            Assert.That(Get<string>("status"), Is.EqualTo("job_queued"));
            Assert.That(Get<bool>("executionEnded"), Is.False);
            Assert.That(release, Is.False); Assert.That(_session.Observations, Is.Zero);
            var disk = Load();
            Assert.That((string)disk["remoteJobId"], Is.EqualTo(JobId));
            Assert.That(disk.ContainsKey("evalToken"), Is.False);
        }

        [Test]
        public void LocalTimeoutWritesOnceAndLateResultPersistsWithoutReplaying()
        {
            Set(_record, "timeoutMs", 0);
            Create(Task.FromResult(Submission()));
            Assert.That(Step(out var first, out var release), Is.True);
            Assert.That(first.success, Is.False); Assert.That(release, Is.False);
            Assert.That(Get<bool>("timedOut"), Is.True); Assert.That(_session.Cancellations, Is.Zero);
            Poll();
            _session.Observation.SetResult(Terminal());
            Step(out var late, out release);
            Assert.That(late, Is.Null); Assert.That(release, Is.True);
            Assert.That(_session.EndedRelease, Is.True);
            Assert.That(Get<string>("status"), Is.EqualTo("validated"));
            Assert.That((string)Load()["status"], Is.EqualTo("validated"));
        }

        [Test]
        public void UnknownSubmissionNeverPollsAndKeepsBusyUntilExplicitLocalRelease()
        {
            Create(Task.FromResult(new PipelineJobObservation { httpStatus = 404 }));
            Step(out var result, out var release);
            Assert.That(result.success, Is.False); Assert.That(release, Is.False);
            Assert.That(Get<bool>("busy"), Is.True); Assert.That(Get<bool>("mayStillBeRunning"), Is.True);
            Assert.That(Get<string>("remoteJobId"), Is.Null);
            Poll(); Assert.That(_session.Observations, Is.Zero);
            Invoke("CancelForOriginalRequest"); Step(out _, out release);
            Assert.That(release, Is.True); Assert.That(_session.EndedRelease, Is.False);
            Assert.That(Get<bool>("waitCanceled"), Is.True);
        }

        [Test]
        public void Poll404DoesNotReleaseOrAutomaticallyCancel()
        {
            Create(Task.FromResult(Submission())); Step(out _, out _); Poll();
            _session.Observation.SetResult(new PipelineJobObservation { httpStatus = 404 });
            Step(out _, out var release);
            Assert.That(release, Is.False); Assert.That(Get<string>("status"), Is.EqualTo("unknown"));
            Assert.That(Get<string>("remoteJobId"), Is.EqualTo(JobId));
            Assert.That(_session.Releases, Is.Zero); Assert.That(_session.Cancellations, Is.Zero);
        }

        [Test]
        public void LocalReleaseWaitsForInFlightGetAndDoesNotSendCancel()
        {
            Create(Task.FromResult(Submission())); Step(out _, out _); Poll();
            Invoke("CancelForOriginalRequest"); Step(out _, out var release);
            Assert.That(release, Is.False); Assert.That(_session.Releases, Is.Zero);
            _session.Observation.SetResult(Running()); Step(out _, out release);
            Assert.That(release, Is.True); Assert.That(_session.Releases, Is.EqualTo(1));
            Assert.That(_session.EndedRelease, Is.False); Assert.That(_session.Cancellations, Is.Zero);
        }

        [Test]
        public void CompletedRunBusinessFailureIsPersistedAsFailed()
        {
            Set(_record, "dryRun", false); Set(_record, "operationKind", "detached_execute");
            Create(Task.FromResult(Submission())); Step(out _, out _); Poll();
            _session.Observation.SetResult(Terminal(false)); Step(out var result, out var release);
            Assert.That(result.success, Is.False); Assert.That(release, Is.True);
            Assert.That(Get<string>("status"), Is.EqualTo("failed"));
            Assert.That(Get<bool>("executionEnded"), Is.True);
            Assert.That(Get<bool>("busy"), Is.False); Assert.That(_session.EndedRelease, Is.True);
        }

        [Test]
        public void MalformedBusinessPayloadStaysUnknownEvenWhenJobEnvelopeIsTerminal()
        {
            var terminal = Terminal(); terminal.resultValue = null;
            Create(Task.FromResult(Submission())); Step(out _, out _); Poll();
            _session.Observation.SetResult(terminal); Step(out _, out var release);
            Assert.That(release, Is.False); Assert.That(Get<string>("status"), Is.EqualTo("unknown"));
            Assert.That(Get<bool>("busy"), Is.True); Assert.That(_session.Releases, Is.Zero);
        }

        [Test]
        public void RemoteCancelIsSerializedAfterCurrentGetAndDoesNotClaimNoSideEffects()
        {
            Create(Task.FromResult(Submission())); Step(out _, out _); Poll();
            var cancelId = "cancel-" + _id;
            Backup(Path.Combine(AIBridge.BridgeDirectory, "results", cancelId + ".json"));
            var pending = _operation.GetType().GetMethod("RequestRemoteCancel").Invoke(_operation, new object[] { cancelId });
            Assert.That(pending, Is.Null); Assert.That(_session.Cancellations, Is.Zero);
            _session.Observation.SetResult(Running()); Step(out _, out _);
            Assert.That(_session.Cancellations, Is.EqualTo(1));
            var canceled = Running(); canceled.state = "canceled"; canceled.executionEnded = true; canceled.cancellationRequested = true;
            canceled.completedAt = "2026-11-05T01:02:00Z";
            _session.Cancellation.SetResult(canceled); Step(out _, out var release);
            Assert.That(release, Is.True); Assert.That(Get<string>("status"), Is.EqualTo("canceled"));
            var receipt = AIBridgeJson.DeserializeObjectStrict(File.ReadAllText(
                Path.Combine(AIBridge.BridgeDirectory, "results", cancelId + ".json")));
            var data = (Dictionary<string, object>)receipt["data"];
            Assert.That(data["requestAccepted"], Is.EqualTo(true)); Assert.That(data["sideEffectsUnknown"], Is.EqualTo(true));
        }

        [Test]
        public void RemoteCancelSaveFailureNeverRepeatsPostAndKeepsUnknownBusy()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows file sharing fixture.");
            Create(Task.FromResult(Submission())); Step(out _, out _);
            var cancelId = "cancel-io-" + _id;
            var receiptPath = Path.Combine(AIBridge.BridgeDirectory, "results", cancelId + ".json");
            Backup(receiptPath);
            _operation.GetType().GetMethod("RequestRemoteCancel").Invoke(_operation, new object[] { cancelId });
            Step(out _, out _);
            var running = Running(); running.cancellationRequested = true;
            _session.Cancellation.SetResult(running);
            using (var held = new FileStream(PathFor(null), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.That(Step(out var original, out var release), Is.True);
                Assert.That(original, Is.Not.Null); Assert.That(original.success, Is.False); Assert.That(release, Is.False);
                Step(out _, out _); Step(out _, out _);
                Assert.That(_session.Cancellations, Is.EqualTo(1)); Assert.That(_session.Observations, Is.Zero);
                Assert.That(Get<string>("status"), Is.EqualTo("unknown")); Assert.That(Get<bool>("busy"), Is.True);
            }
            Assert.That(File.Exists(receiptPath), Is.True);
        }

        [Test]
        public void CorruptDetachedBindingIsNeverLoadedAsValid()
        {
            Create(Task.FromResult(Submission())); Step(out _, out _);
            var data = Load(); data.Remove("detached");
            File.WriteAllText(PathFor(_id), AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(data)));
            var args = new object[] { _id, false };
            var loaded = typeof(CodeCommand).GetMethod("LoadPipelineRecordWithStatus", PrivateStatic).Invoke(null, args);
            Assert.That(loaded, Is.Null); Assert.That(args[1], Is.EqualTo(true));
        }

        [Test]
        public void ReloadKeepsBoundJobUnknownWithoutReconstructingSession()
        {
            Create(Task.FromResult(Submission())); Step(out _, out _);
            Set(_record, "resultWritten", true);
            typeof(CodeCommand).GetMethod("SavePipelineRecord", PrivateStatic).Invoke(null, new[] { _record });
            Active.SetValue(null, null);
            typeof(CodeCommand).GetMethod("RecoverPipelineOperation", PrivateStatic).Invoke(null, null);
            Assert.That(Active.GetValue(null).GetType().Name, Is.EqualTo("PipelineUnknownOperation"));
            Assert.That(_session.Observations, Is.Zero); Assert.That(_session.Cancellations, Is.Zero);
            var data = Load(); Assert.That(data["status"], Is.EqualTo("unknown"));
            Assert.That(data["remoteJobId"], Is.EqualTo(JobId)); Assert.That(data["busy"], Is.EqualTo(true));
        }

        [Test]
        public void TerminalWaitUsesPersistedOutcomeAndNeverSubmits()
        {
            Create(Task.FromResult(Submission())); Step(out _, out _); Poll();
            _session.Observation.SetResult(Terminal()); Step(out _, out _);
            Active.SetValue(null, null);
            var result = new CodeCommand().Execute(new CommandRequest
            {
                id = "wait-" + _id, type = "code", @params = new Dictionary<string, object>
                    { { "action", "job_wait" }, { "requestId", _id }, { "timeout", 1000 } }
            });
            Assert.That(result, Is.Not.Null); Assert.That(result.success, Is.True);
            Assert.That(_session.Cancellations, Is.Zero);
        }

        [Test]
        public void UnknownWaitReturnsWithoutNetworkOrReconstructingSession()
        {
            Create(Task.FromResult(new PipelineJobObservation { httpStatus = 404 })); Step(out _, out _);
            var result = new CodeCommand().Execute(new CommandRequest
            {
                id = "wait-unknown-" + _id, type = "code", @params = new Dictionary<string, object>
                    { { "action", "job_wait" }, { "requestId", _id } }
            });
            Assert.That(result, Is.Not.Null); Assert.That(result.success, Is.False);
            Assert.That(_session.Observations, Is.Zero); Assert.That(_session.Cancellations, Is.Zero);
        }

        [Test]
        public void MalformedCancelTerminalNeverConfirmsExecutionEnded()
        {
            Create(Task.FromResult(Submission())); Step(out _, out _);
            var cancelId = "cancel-malformed-" + _id;
            var receiptPath = Path.Combine(AIBridge.BridgeDirectory, "results", cancelId + ".json"); Backup(receiptPath);
            _operation.GetType().GetMethod("RequestRemoteCancel").Invoke(_operation, new object[] { cancelId }); Step(out _, out _);
            var terminal = Terminal(); terminal.resultValue = null;
            _session.Cancellation.SetResult(terminal); Step(out _, out var release);
            Assert.That(release, Is.False); Assert.That(Get<string>("status"), Is.EqualTo("unknown"));
            var receipt = AIBridgeJson.DeserializeObjectStrict(File.ReadAllText(receiptPath));
            Assert.That(receipt["success"], Is.EqualTo(false));
            var data = (Dictionary<string, object>)receipt["data"];
            Assert.That(data["executionEnded"], Is.EqualTo(false));
        }

        [Test]
        public void UnknownLocalCancelNormalizesPreviouslyActiveRecord()
        {
            Create(Task.FromResult(Submission())); Step(out _, out _);
            var type = typeof(CodeCommand).GetNestedType("PipelineUnknownOperation", BindingFlags.NonPublic);
            var unknown = Activator.CreateInstance(type, new[] { _record });
            unknown.GetType().GetMethod("CancelForOriginalRequest").Invoke(unknown, null);
            var args = new object[] { _id, false };
            var loaded = typeof(CodeCommand).GetMethod("LoadPipelineRecordWithStatus", PrivateStatic).Invoke(null, args);
            Assert.That(loaded, Is.Not.Null); Assert.That(args[1], Is.EqualTo(false));
            Assert.That(Get<bool>("active"), Is.False); Assert.That(Get<bool>("busy"), Is.False);
            Assert.That(Get<bool>("waitCanceled"), Is.True); Assert.That(Get<string>("status"), Is.EqualTo("unknown"));
        }

        [Test]
        public void SynchronousPipelineSaveFailureReturnsReceiptAndRetainsUnknown()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows sharing fixture.");
            Set(_record, "detached", false); Set(_record, "upstreamExecutionTimeoutDisabled", false);
            Set(_record, "targetIdentity", null); Set(_record, "adapterGeneration", null); Set(_record, "operationKind", "compile_only");
            typeof(CodeCommand).GetMethod("SavePipelineRecord", PrivateStatic).Invoke(null, new[] { _record });
            var type = typeof(CodeCommand).GetNestedType("PipelineDryRunAsyncOperation", BindingFlags.NonPublic);
            var done = Task.FromResult(new PipelineCodeResult { success = true, status = "validated" });
            _operation = Activator.CreateInstance(type, new object[] { _record, done }); Active.SetValue(null, _operation);
            using (var held = new FileStream(PathFor(null), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.That(Step(out var receipt, out var release), Is.True);
                Assert.That(receipt, Is.Not.Null); Assert.That(receipt.success, Is.False); Assert.That(release, Is.False);
                Assert.That(Active.GetValue(null).GetType().Name, Is.EqualTo("PipelineUnknownOperation"));
                Assert.That(Get<string>("status"), Is.EqualTo("unknown")); Assert.That(Get<bool>("busy"), Is.True);
            }
        }

        private void Create(Task<PipelineJobObservation> submission)
        {
            var type = typeof(CodeCommand).GetNestedType("PipelineJobOperation", BindingFlags.NonPublic);
            _operation = Activator.CreateInstance(type, new object[] { _record, _session, submission });
            Active.SetValue(null, _operation);
        }
        private void Poll()
        {
            Set(_operation, "_nextPoll", -1L); Step(out _, out _);
        }
        private bool Step(out CommandResult result, out bool release)
        {
            var args = new object[] { null, false };
            var value = (bool)_operation.GetType().GetMethod("Step").Invoke(_operation, args);
            result = (CommandResult)args[0]; release = (bool)args[1]; return value;
        }
        private object Invoke(string name) { return _operation.GetType().GetMethod(name).Invoke(_operation, null); }
        private T Get<T>(string name) { return (T)_record.GetType().GetField(name).GetValue(_record); }
        private static void Set(object target, string name, object value)
        {
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(target, value);
        }
        private static FieldInfo Active => typeof(CodeCommand).GetField("_activeOperation", PrivateStatic);
        private static string PathFor(string id) { return (string)typeof(CodeCommand).GetMethod("PipelineRecordPath", PrivateStatic).Invoke(null, new object[] { id }); }
        private void Backup(string path) { _backups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null); }
        private Dictionary<string, object> Load() { return AIBridgeJson.DeserializeObjectStrict(File.ReadAllText(PathFor(_id))); }
        private static PipelineJobObservation Submission()
        {
            return new PipelineJobObservation { valid = true, httpStatus = 200, status = "observed",
                command = "script run", jobId = JobId, state = "queued" };
        }
        private static PipelineJobObservation Running()
        {
            var value = Submission(); value.state = "running";
            value.enqueuedAt = "2026-11-05T01:00:00Z"; value.startedAt = "2026-11-05T01:01:00Z"; return value;
        }
        private static PipelineJobObservation Terminal(bool success = true)
        {
            var value = Running(); value.state = "completed"; value.executionEnded = true; value.completedAt = "2026-11-05T01:02:00Z";
            value.resultValue = new Dictionary<string, object>
            {
                { "command", "script run" }, { "success", success }, { "result", null },
                { "message", success ? "Compiled successfully (dry run; nothing was loaded or executed)." : null }, { "error", success ? null : "Entry Point Not Found" }, { "errorDetails", success ? null : "No matching fixture method." },
                { "compileMs", 1L }, { "executeMs", 0L }, { "assemblyName", success ? null : "fixture" },
                { "diagnostics", new List<object>() }
            };
            return value;
        }

        private sealed class FakeSession : PipelineRunJobSession
        {
            public int Observations, Cancellations, Releases;
            public bool EndedRelease;
            public readonly TaskCompletionSource<PipelineJobObservation> Observation = new TaskCompletionSource<PipelineJobObservation>();
            public readonly TaskCompletionSource<PipelineJobObservation> Cancellation = new TaskCompletionSource<PipelineJobObservation>();
            public override string TargetIdentity => new string('a', 64);
            public override string Generation => new string('b', 32);
            public override Task<PipelineJobObservation> SubmitAsync() { throw new InvalidOperationException("Tests must never submit."); }
            public override Task<PipelineJobObservation> ObserveAsync() { Observations++; return Observation.Task; }
            public override Task<PipelineJobObservation> CancelAsync(bool confirmed) { Cancellations++; return Cancellation.Task; }
            public override void Release(bool ended) { Releases++; EndedRelease = ended; }
        }
    }
}
