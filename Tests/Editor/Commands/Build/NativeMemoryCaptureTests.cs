using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using AIBridge.Internal.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace AIBridge.Editor.Tests
{
    public sealed class NativeMemoryCaptureTests
    {
        private string _root;
        private DateTime _now;
        private FakeApi _api;
        private FakeGate _gate;
        private NativeMemoryJournal _journal;
        private NativeMemoryCapture.Context _context;
        private NativeMemoryCapture.Engine _engine;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "aibridge-memory-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _now = new DateTime(2026, 11, 5, 1, 2, 3, DateTimeKind.Utc);
            _api = new FakeApi(); _gate = new FakeGate(); _journal = new NativeMemoryJournal(_root);
            _context = Context(_journal, _gate);
            _engine = new NativeMemoryCapture.Engine(_context);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private NativeMemoryCapture.Context Context(NativeMemoryJournal journal, FakeGate gate)
        {
            return new NativeMemoryCapture.Context
            {
                ProjectRoot = _root, EditorPid = 1234, Generation = Guid.NewGuid().ToString("N"), Api = _api,
                Journal = journal, UtcNow = () => _now, BusyReason = () => null,
                Acquire = gate.Acquire, Release = gate.Release
            };
        }

        private static CommandRequest Request(string id = "capture-one", params object[] pairs)
        {
            var result = new CommandRequest { id = id, type = "memory", @params = new Dictionary<string, object> { { "confirm", true }, { "timeout", 1000 } } };
            for (var i = 0; i < pairs.Length; i += 2) result.@params[(string)pairs[i]] = pairs[i + 1];
            return result;
        }
        private static CommandRequest Query(string id = "capture-one", bool release = false)
        {
            return new CommandRequest { id = "query-" + Guid.NewGuid().ToString("N"), type = "memory",
                @params = release ? new Dictionary<string, object> { { "requestId", id }, { "confirm", true } }
                    : new Dictionary<string, object> { { "requestId", id } } };
        }
        private NativeMemoryRecord Record(string id = "capture-one")
        {
            Assert.That(_engine.Execute(Query(id), "snapshot_status").success, Is.True);
            return LiveRecord(_engine, id);
        }
        private NativeMemoryRecord Queue(string id = "capture-one")
        {
            var result = _engine.Execute(Request(id), "capture_snapshot");
            Assert.That(result.success, Is.True, result.error);
            return LiveRecord(_engine, id);
        }

        [Test]
        public void QueuePersistsBeforeNextTickAndCapturesOnlyOnce()
        {
            var r = Queue();
            Assert.That(r.status, Is.EqualTo("queued")); Assert.That(_api.Calls, Is.Zero); Assert.That(_gate.Busy, Is.True);
            var persisted = _journal.Load(r.requestId, out var bad);
            Assert.That(bad, Is.False); Assert.That(persisted.status, Is.EqualTo("queued"));
            _engine.Tick(); _engine.Tick();
            Assert.That(_api.Calls, Is.EqualTo(1)); Assert.That(r.status, Is.EqualTo("capturing"));
            Assert.That(_journal.Load(r.requestId, out bad).startedAt, Is.Not.Null);
            Assert.That(_engine.Execute(Request(), "capture_snapshot").success, Is.False);
        }

        [Test]
        public void MatchingNonemptyCallbackPublishesOnceAndReleasesOnlyAfterPersistence()
        {
            var r = Queue(); _engine.Tick();
            var bytes = new byte[] { 1, 2, 3, 4 }; File.WriteAllBytes(_api.Path, bytes);
            _api.Callback(_api.Path, true);
            Assert.That(r.status, Is.EqualTo("capturing")); Assert.That(File.Exists(r.outputPath), Is.False);
            _engine.Tick();
            Assert.That(r.status, Is.EqualTo("completed")); Assert.That(r.result, Is.True); Assert.That(r.sizeBytes, Is.EqualTo(4));
            Assert.That(_gate.Busy, Is.False); Assert.That(File.ReadAllBytes(r.outputPath), Is.EqualTo(bytes));
            _api.Callback(_api.Path, false); _engine.Tick();
            Assert.That(r.status, Is.EqualTo("completed")); Assert.That(_gate.Releases, Is.EqualTo(1));
            Assert.That(_journal.Load(r.requestId, out var bad).result, Is.True); Assert.That(bad, Is.False);
        }

        [Test]
        public void NativeCallbackCanArriveOnWorkerAndDoesNotFinalizeThere()
        {
            var r = Queue(); _engine.Tick(); File.WriteAllBytes(_api.Path, new byte[] { 4 });
            var thread = new Thread(() => _api.Callback(_api.Path, true)); thread.Start(); thread.Join();
            Assert.That(r.busy, Is.True); Assert.That(_gate.Releases, Is.Zero);
            _engine.Tick(); Assert.That(r.status, Is.EqualTo("completed"));
        }

        [Test]
        public void FalseCallbackCannotSucceedEvenWithNonemptyTemporary()
        {
            var r = Queue(); _engine.Tick(); File.WriteAllBytes(_api.Path, new byte[] { 1 });
            _api.Callback(_api.Path, false); _engine.Tick();
            Assert.That(r.status, Is.EqualTo("failed")); Assert.That(r.result, Is.False);
            Assert.That(File.Exists(r.outputPath), Is.False); Assert.That(File.Exists(r.tempPath), Is.True);
            Assert.That(_gate.Busy, Is.False);
        }

        [Test]
        public void EmptyCallbackFileCannotSucceed()
        {
            var r = Queue(); _engine.Tick(); File.WriteAllBytes(_api.Path, new byte[0]);
            _api.Callback(_api.Path, true); _engine.Tick();
            Assert.That(r.status, Is.EqualTo("failed")); Assert.That(r.result, Is.False); Assert.That(r.sizeBytes, Is.Null);
            Assert.That(File.Exists(r.outputPath), Is.False);
        }

        [Test]
        public void WrongCallbackPathStaysUnknownAndDuplicateCannotRepairIt()
        {
            var r = Queue(); _engine.Tick(); File.WriteAllBytes(_api.Path, new byte[] { 1 });
            _api.Callback(r.outputPath, true); _engine.Tick();
            AssertUnknown(r); Assert.That(_gate.Busy, Is.True); Assert.That(File.Exists(r.tempPath), Is.True);
            _api.Callback(r.tempPath, true); _engine.Tick();
            AssertUnknown(r); Assert.That(File.Exists(r.outputPath), Is.False);
        }

        [Test]
        public void TimeoutKeepsBusyAndLateOriginalCallbackCanComplete()
        {
            var r = Queue(); _engine.Tick(); var callback = _api.Callback;
            _now = _now.AddMilliseconds(1001); _engine.Tick();
            AssertUnknown(r); Assert.That(r.nativeMayStillRun, Is.True); Assert.That(_gate.Busy, Is.True);
            File.WriteAllBytes(r.tempPath, new byte[] { 1 }); callback(r.tempPath, true); _engine.Tick();
            Assert.That(r.status, Is.EqualTo("completed")); Assert.That(_gate.Busy, Is.False); Assert.That(_api.Calls, Is.EqualTo(1));
        }

        [Test]
        public void QueueTimeoutBeforeStartNeverInvokesAndRequiresLocalRelease()
        {
            var r = Queue(); _now = _now.AddSeconds(2); _engine.Tick();
            AssertUnknown(r); Assert.That(r.nativeMayStillRun, Is.False); Assert.That(_api.Calls, Is.Zero); Assert.That(_gate.Busy, Is.True);
            Assert.That(_engine.Execute(Query(release: true), "release_snapshot").success, Is.True);
            Assert.That(_gate.Busy, Is.False); Assert.That(r.released, Is.True);
        }

        [Test]
        public void ExplicitReleaseIsLocalAndIgnoresLateCallbackWithoutTouchingNewOperation()
        {
            var r = Queue(); _engine.Tick(); var oldCallback = _api.Callback;
            _now = _now.AddSeconds(2); _engine.Tick();
            var release = _engine.Execute(Query(release: true), "release_snapshot");
            Assert.That(release.success, Is.True); Assert.That(AIBridgeJson.Serialize(release.data), Does.Contain("\"nativeCancellationConfirmed\":false"));
            var next = Queue("capture-two"); _engine.Tick();
            File.WriteAllBytes(r.tempPath, new byte[] { 8 }); oldCallback(r.tempPath, true); _engine.Tick();
            Assert.That(r.status, Is.EqualTo("unknown")); Assert.That(r.released, Is.True); Assert.That(next.status, Is.EqualTo("capturing"));
            Assert.That(_gate.Busy, Is.True); Assert.That(File.Exists(r.outputPath), Is.False);
        }

        [Test]
        public void ReloadRecoversUnknownWithoutReplayAndExactOldIdCanRelease()
        {
            var r = Queue(); _engine.Tick();
            var newGate = new FakeGate(); var newContext = Context(new NativeMemoryJournal(_root), newGate);
            var reloaded = new NativeMemoryCapture.Engine(newContext); reloaded.Tick();
            Assert.That(reloaded.Execute(Query(), "snapshot_status").success, Is.True);
            var restored = LiveRecord(reloaded, "capture-one");
            AssertUnknown(restored); Assert.That(newGate.Busy, Is.True); Assert.That(_api.Calls, Is.EqualTo(1));
            Assert.That(reloaded.Execute(Query("wrong", true), "release_snapshot").success, Is.False);
            Assert.That(reloaded.Execute(Query(release: true), "release_snapshot").success, Is.True); Assert.That(newGate.Busy, Is.False);
            Assert.That(reloaded.Execute(Request(), "capture_snapshot").success, Is.False, "Registered ID cannot replay even after release.");
        }

        [Test]
        public void MultipleOldBusyRecordsStayBlockedAndReleaseByExactId()
        {
            var first = Queue(); _engine.Tick();
            var clone = Clone(first); clone.requestId = "old-two"; clone.operationId = Guid.NewGuid().ToString("N");
            clone.outputPath = NewPath(); clone.tempPath = NewPath(); _journal.Save(clone);
            var gate = new FakeGate(); var reload = new NativeMemoryCapture.Engine(Context(new NativeMemoryJournal(_root), gate));
            var status = reload.Execute(new CommandRequest { id = "status", @params = new Dictionary<string, object>() }, "snapshot_status");
            Assert.That(status.success, Is.False); Assert.That(reload.Busy, Is.True); Assert.That(gate.Busy, Is.True);
            Assert.That(reload.Execute(Query("capture-one", true), "release_snapshot").success, Is.True); Assert.That(gate.Busy, Is.True);
            Assert.That(reload.Execute(Query("old-two", true), "release_snapshot").success, Is.True); Assert.That(gate.Busy, Is.False);
            Assert.That(_api.Calls, Is.EqualTo(1));
        }

        [Test]
        public void IntentPersistenceFailureNeverInvokesAndRetainsLocalUnknown()
        {
            _journal.Write = (path, json) => { throw new IOException("fixture"); };
            var result = _engine.Execute(Request(), "capture_snapshot"); _engine.Tick();
            Assert.That(result.success, Is.False); Assert.That(_api.Calls, Is.Zero); Assert.That(_gate.Busy, Is.True);
            AssertUnknown(Record());
        }

        [Test]
        public void CapturingIntentFailureNeverInvokes()
        {
            var r = Queue(); _journal.Write = (path, json) => { throw new IOException("fixture"); };
            _engine.Tick(); AssertUnknown(r); Assert.That(_api.Calls, Is.Zero); Assert.That(_gate.Busy, Is.True);
        }

        [Test]
        public void TerminalPersistenceFailureHoldsGateAndNeverPublishesAgain()
        {
            var r = Queue(); _engine.Tick(); File.WriteAllBytes(_api.Path, new byte[] { 1 });
            _journal.Write = (path, json) => { throw new IOException("fixture"); };
            _api.Callback(_api.Path, true); _engine.Tick();
            AssertUnknown(r); Assert.That(_gate.Busy, Is.True); Assert.That(File.Exists(r.outputPath), Is.True);
            _api.Callback(_api.Path, true); _engine.Tick(); Assert.That(_api.Calls, Is.EqualTo(1)); AssertUnknown(r);
        }

        [Test]
        public void ReleasePersistenceFailureDoesNotReleaseGate()
        {
            var r = Queue(); _engine.Tick(); _now = _now.AddSeconds(2); _engine.Tick();
            _journal.Write = (path, json) => { throw new IOException("fixture"); };
            Assert.That(_engine.Execute(Query(release: true), "release_snapshot").success, Is.False);
            Assert.That(_gate.Busy, Is.True); Assert.That(r.busy, Is.True); Assert.That(r.released, Is.False);
        }

        [Test]
        public void ThrowingNativeInvocationIsUnknownAndNotRetried()
        {
            var r = Queue(); _api.ThrowOnCapture = true; _engine.Tick(); _engine.Tick();
            AssertUnknown(r); Assert.That(r.nativeMayStillRun, Is.True); Assert.That(_api.Calls, Is.EqualTo(1));
        }

        [Test]
        public void ExistingOutputIsNotOverwrittenEvenIfCreatedAfterQueue()
        {
            var r = Queue(); _engine.Tick(); File.WriteAllBytes(r.tempPath, new byte[] { 1 });
            File.WriteAllBytes(r.outputPath, new byte[] { 9, 8 });
            _api.Callback(r.tempPath, true); _engine.Tick();
            AssertUnknown(r); Assert.That(File.ReadAllBytes(r.outputPath), Is.EqualTo(new byte[] { 9, 8 }));
            Assert.That(File.Exists(r.tempPath), Is.True);
        }

        [Test]
        public void ExistingOutputRejectedBeforeIntentOrNativeCall()
        {
            var path = NewPath(); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, new byte[] { 7 });
            var result = _engine.Execute(Request("capture-one", "path", path), "capture_snapshot");
            Assert.That(result.success, Is.False); Assert.That(_api.Calls, Is.Zero); Assert.That(_gate.Busy, Is.False);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 7 })); Assert.That(File.Exists(_journal.RecordPath(null)), Is.False);
        }

        [TestCase("../escape.snap")]
        [TestCase(".aibridge/memory/../00000000000000000000000000000000.snap")]
        [TestCase(".aibridge/memory/sub/00000000000000000000000000000000.snap")]
        [TestCase(".aibridge/memory/NUL.snap")]
        [TestCase(".aibridge/memory/not-a-guid.snap")]
        [TestCase("\\\\?\\C:\\device.snap")]
        public void UnsafePathRejectedWithoutCreatingDirectories(string path)
        {
            Assert.That(_engine.Execute(Request("capture-one", "path", path), "capture_snapshot").success, Is.False);
            Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty); Assert.That(_api.Calls, Is.Zero);
        }

        [Test]
        public void ReparseAncestorIsRejectedWithoutFollowingIt()
        {
            var api = typeof(Directory).GetMethod("CreateSymbolicLink", new[] { typeof(string), typeof(string) });
            if (api == null) Assert.Ignore("Host profile has no symbolic-link creation API.");
            var target = Path.Combine(_root, "fixture-target"); Directory.CreateDirectory(target);
            var bridge = Path.Combine(_root, ".aibridge");
            try { api.Invoke(null, new object[] { bridge, target }); }
            catch (Exception) { Assert.Ignore("Host does not permit symbolic-link creation."); }
            try
            {
                Assert.Throws<IOException>(() => NativeMemoryJournal.ResolveSnapshotPath(_root,
                    ".aibridge/memory/00000000000000000000000000000000.snap"));
                Assert.That(Directory.GetFileSystemEntries(target), Is.Empty);
            }
            finally { Directory.Delete(bridge); }
        }

        [TestCase("confirm", "true")]
        [TestCase("confirm", false)]
        [TestCase("timeout", "1000")]
        [TestCase("timeout", 1000.0)]
        [TestCase("timeout", 999)]
        [TestCase("timeout", 60001)]
        [TestCase("target", "player")]
        [TestCase("target", -1)]
        [TestCase("overwrite", true)]
        public void LiteralConfirmationBoundedIntegerAndEditorTargetAreRequired(string key, object value)
        {
            Assert.That(_engine.Execute(Request("capture-one", key, value), "capture_snapshot").success, Is.False);
            Assert.That(_api.Calls, Is.Zero); Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty);
        }

        [TestCase(-2, false)]
        [TestCase(0, false)]
        [TestCase(-1, true)]
        public void ExactEditorGuidAndDisabledProfilerAreRequired(int guid, bool recording)
        {
            _api.State.CurrentTargetGuid = guid; _api.State.ProfilerEnabled = recording;
            Assert.That(_engine.Execute(Request(), "capture_snapshot").success, Is.False);
            Assert.That(_api.Calls, Is.Zero); Assert.That(_gate.Busy, Is.False);
        }

        [TestCase("compiling")]
        [TestCase("importing")]
        [TestCase("build")]
        [TestCase("code")]
        public void BusyContextRejectsBeforeCapture(string busy)
        {
            _context.BusyReason = () => busy;
            Assert.That(_engine.Execute(Request(), "capture_snapshot").success, Is.False); Assert.That(_api.Calls, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PlayModeAllowsCaptureWithLocalEditorAndStoppedProfiler()
        {
            TearDown();
            yield return new EnterPlayMode();
            // 域重载后重建隔离样本；使用真实 Editor 门禁，仅替换原生文件 API，避免每轮测试生成 GB 级快照。
            TearDown();
            SetUp();
            CommandResult queued = null;
            NativeMemoryRecord record = null;
            bool published = false;
            string failure = null;
            try
            {
                if (!EditorApplication.isPlaying) throw new InvalidOperationException("Test did not enter Play Mode.");
                var busyMethod = typeof(NativeMemoryCapture).GetMethod("EditorBusyReason",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (busyMethod == null) throw new MissingMethodException("EditorBusyReason");
                _context.BusyReason = () => (string)busyMethod.Invoke(null, null);
                queued = _engine.Execute(Request(), "capture_snapshot");
                if (queued.success)
                {
                    record = LiveRecord(_engine, "capture-one");
                    _engine.Tick();
                    if (_api.Calls == 1)
                    {
                        File.WriteAllBytes(_api.Path, new byte[] { 1, 2, 3, 4 });
                        _api.Callback(_api.Path, true);
                        _engine.Tick();
                        published = record.status == "completed" && record.result == true && !record.busy
                            && File.Exists(record.outputPath) && new FileInfo(record.outputPath).Length == 4;
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex.ToString();
            }
            finally
            {
                // 断言放在退出 Play Mode 后；即使捕获验证异常，也先清理样本并退出运行态。
                TearDown();
            }
            var succeeded = failure == null && queued != null && queued.success && published;
            var receipt = failure ?? (queued == null ? "No receipt." : AIBridgeJson.Serialize(queued, true));
            yield return new ExitPlayMode();
            Assert.That(succeeded, Is.True, receipt);
        }

        [Test]
        public void GateOwnedByAnotherProfilerOperationRejectsCapture()
        {
            var foreign = _gate.Acquire();
            Assert.That(_engine.Execute(Request(), "capture_snapshot").success, Is.False); Assert.That(_api.Calls, Is.Zero);
            _gate.Release(foreign);
        }

        [Test]
        public void TargetChangeBeforeStartFailsWithoutInvoking()
        {
            var r = Queue(); _api.State.CurrentTargetGuid = 0; _engine.Tick();
            Assert.That(r.status, Is.EqualTo("failed")); Assert.That(_api.Calls, Is.Zero); Assert.That(_gate.Busy, Is.False);
        }

        [Test]
        public void TargetChangeBeforeCallbackCannotPublish()
        {
            var r = Queue(); _engine.Tick(); File.WriteAllBytes(r.tempPath, new byte[] { 1 });
            _api.State.CurrentTargetName = "changed"; _api.Callback(r.tempPath, true); _engine.Tick();
            AssertUnknown(r); Assert.That(File.Exists(r.tempPath), Is.True); Assert.That(File.Exists(r.outputPath), Is.False);
        }

        [Test]
        public void UnsupportedCaptureAndLoadNeverInstallOrInvokeAnything()
        {
            _api.Support.Capture = false;
            Assert.That(_engine.Execute(Request(), "capture_snapshot").success, Is.False);
            var load = _engine.Execute(new CommandRequest { id = "load", @params = new Dictionary<string, object>() }, "load_snapshot");
            Assert.That(load.success, Is.False); Assert.That(AIBridgeJson.Serialize(load.data), Does.Contain("unsupported"));
            Assert.That(_api.Calls, Is.Zero); Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty);
        }

        [Test]
        public void CorruptMissingTypedOrOversizedJournalFailsClosed()
        {
            var r = Queue(); var path = _journal.RecordPath(r.requestId);
            foreach (var json in new[] { "{", "{}", new string('x', NativeMemoryJournal.MaxBytes + 1) })
            {
                File.WriteAllText(path, json);
                var gate = new FakeGate(); var reload = new NativeMemoryCapture.Engine(Context(new NativeMemoryJournal(_root), gate));
                Assert.That(reload.Busy, Is.True); Assert.That(gate.Busy, Is.True);
                Assert.That(reload.Execute(Request("new"), "capture_snapshot").success, Is.False);
            }
            Assert.That(_api.Calls, Is.Zero);
        }

        [Test]
        public void JournalRequiresFieldsTypesIdentityAndPathsAndPreservesNulls()
        {
            var r = Queue(); var json = File.ReadAllText(_journal.RecordPath(r.requestId));
            Assert.That(json, Does.Contain("\"result\":null")); Assert.That(json, Does.Contain("\"sizeBytes\":null"));
            var data = AIBridgeJson.DeserializeObjectStrict(json);
            foreach (var key in new List<string>(data.Keys))
            {
                var broken = AIBridgeJson.DeserializeObjectStrict(json); broken.Remove(key);
                File.WriteAllText(_journal.RecordPath(r.requestId), AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(broken)));
                Assert.That(_journal.Load(r.requestId, out var bad), Is.Null, key); Assert.That(bad, Is.True, key);
            }
            var invalidRecord = Clone(r); invalidRecord.editorPid = 0; Assert.That(_journal.Validate(invalidRecord), Is.False);
            invalidRecord = Clone(r); invalidRecord.projectPath = Path.GetTempPath(); Assert.That(_journal.Validate(invalidRecord), Is.False);
            invalidRecord = Clone(r); invalidRecord.outputPath = invalidRecord.tempPath; Assert.That(_journal.Validate(invalidRecord), Is.False);
        }

        [Test]
        public void JournalPathUsesShaAndMissingLatestDoesNotLoseBusyRecords()
        {
            Assert.That(Path.GetFileName(_journal.RecordPath("abc")), Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad.json"));
            var r = Queue(); File.Delete(_journal.RecordPath(null));
            var gate = new FakeGate(); var reload = new NativeMemoryCapture.Engine(Context(new NativeMemoryJournal(_root), gate));
            Assert.That(reload.Busy, Is.True); Assert.That(gate.Busy, Is.True); Assert.That(_api.Calls, Is.Zero);
            Assert.That(reload.Execute(Query(r.requestId, true), "release_snapshot").success, Is.True);
        }

        [Test]
        public void StaleGenerationCallbackCannotFinalize()
        {
            var r = Queue(); _engine.Tick(); File.WriteAllBytes(r.tempPath, new byte[] { 1 });
            _context.Generation = Guid.NewGuid().ToString("N"); _api.Callback(r.tempPath, true); _engine.Tick();
            AssertUnknown(r); Assert.That(_gate.Busy, Is.True); Assert.That(File.Exists(r.outputPath), Is.False);
        }

        [Test]
        public void MissingConfirmationAndNullArgumentsAreRejectedBeforePersistence()
        {
            var request = Request(); request.@params.Remove("confirm");
            Assert.That(_engine.Execute(request, "capture_snapshot").success, Is.False);
            foreach (var key in new[] { "confirm", "timeout", "path", "target" })
                Assert.That(_engine.Execute(Request("capture-one", key, null), "capture_snapshot").success, Is.False, key);
            Assert.That(_api.Calls, Is.Zero); Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty);
        }

        [Test]
        public void ReflectionRequiresExactSignatureAndNamedFlagsWithoutHardcodedValues()
        {
            var api = new NativeMemorySnapshotApi(typeof(ExactApi), typeof(FakeFlags), typeof(FakeDriver));
            Assert.That(api.Support.Capture, Is.True);
            api.TakeSnapshot("fixture", (path, success) => { });
            Assert.That(ExactApi.Flags, Is.EqualTo(FakeFlags.ManagedObjects | FakeFlags.NativeObjects));
            Assert.That(api.ReadState().CurrentTargetGuid, Is.EqualTo(-1));
            foreach (var badType in new[] { typeof(BoolReturnApi), typeof(WrongCallbackApi), typeof(FourArgsApi) })
                Assert.That(new NativeMemorySnapshotApi(badType, typeof(FakeFlags), typeof(FakeDriver)).Support.Capture, Is.False);
            Assert.That(new NativeMemorySnapshotApi(typeof(MissingFlagApi), typeof(MissingFlags), typeof(FakeDriver)).Support.Capture, Is.False);
            Assert.That(new NativeMemorySnapshotApi(null, typeof(FakeFlags), typeof(FakeDriver)).Support.Capture, Is.False);
            Assert.That(new NativeMemorySnapshotApi(typeof(ExactApi), typeof(FakeFlags), typeof(object)).Support.Capture, Is.False);
        }

        [Test]
        public void OnlineCommandResultPreservesNullableFieldsAcrossAllRecordReceipts()
        {
            var queued = _engine.Execute(Request(), "capture_snapshot");
            AssertWireNulls((Dictionary<string, object>)Wire(queued)["data"]);
            var r = LiveRecord(_engine, "capture-one"); _engine.Tick();
            AssertWireNulls((Dictionary<string, object>)Wire(_engine.Execute(Query(), "snapshot_status"))["data"]);
            _now = _now.AddSeconds(2); _engine.Tick();
            var release = Wire(_engine.Execute(Query(release: true), "release_snapshot"));
            AssertWireNulls((Dictionary<string, object>)((Dictionary<string, object>)release["data"])["operation"]);
            var next = Queue("two"); _engine.Tick(); _api.Callback(next.tempPath, false); _engine.Tick();
            var failed = (Dictionary<string, object>)Wire(_engine.Execute(Query("two"), "snapshot_status"))["data"];
            Assert.That(failed["result"], Is.False); Assert.That(failed.ContainsKey("sizeBytes"), Is.True); Assert.That(failed["sizeBytes"], Is.Null);
            Assert.That(r.released, Is.True);
        }

        [Test]
        public void FailureAndMultipleBusyReceiptsPreserveRecordNulls()
        {
            var writer = _journal.Write;
            _journal.Write = (path, json) => { throw new IOException("fixture"); };
            var failure = (Dictionary<string, object>)Wire(_engine.Execute(Request(), "capture_snapshot"))["data"];
            AssertWireNulls((Dictionary<string, object>)failure["operation"]);
            _journal.Write = writer;
            Assert.That(_engine.Execute(Query(release: true), "release_snapshot").success, Is.True);
            var first = Queue("one"); var second = Clone(first); second.requestId = "two"; second.operationId = Guid.NewGuid().ToString("N");
            second.outputPath = NewPath(); second.tempPath = NewPath(); _journal.Save(second);
            var reload = new NativeMemoryCapture.Engine(Context(new NativeMemoryJournal(_root), new FakeGate()));
            var status = new CommandRequest { id = "status", @params = new Dictionary<string, object>() };
            var data = (Dictionary<string, object>)Wire(reload.Execute(status, "snapshot_status"))["data"];
            var records = (List<object>)data["operation"]; Assert.That(records.Count, Is.EqualTo(2));
            foreach (var record in records) AssertWireNulls((Dictionary<string, object>)record);
        }

        [TestCase("intent", false)]
        [TestCase("capturing", false)]
        [TestCase("terminal", false)]
        [TestCase("release", false)]
        [TestCase("intent", true)]
        [TestCase("capturing", true)]
        [TestCase("terminal", true)]
        [TestCase("release", true)]
        public void SecondWriteFailureRecoversUnknownAndExactReleaseAfterIoRecovery(string stage, bool reload)
        {
            var writer = _journal.Write;
            if (stage != "intent") Queue();
            if (stage == "terminal" || stage == "release") _engine.Tick();
            if (stage == "release") { _now = _now.AddSeconds(2); _engine.Tick(); }
            _journal.Write = (path, json) => { if (path != _journal.RecordPath(null)) throw new IOException("second write"); writer(path, json); };
            if (stage == "intent") Assert.That(_engine.Execute(Request(), "capture_snapshot").success, Is.False);
            else if (stage == "capturing") _engine.Tick();
            else if (stage == "terminal") { _api.Callback(_api.Path, false); _engine.Tick(); }
            else Assert.That(_engine.Execute(Query(release: true), "release_snapshot").success, Is.False);
            var records = _journal.ReadAll(out var invalid);
            Assert.That(invalid, Is.False); Assert.That(records.Count, Is.EqualTo(1)); AssertUnknown(records[0]);
            AssertUnknown(_journal.Load("capture-one", out invalid)); Assert.That(invalid, Is.False);
            _journal.Write = writer;
            var engine = _engine; var gate = _gate;
            if (reload) { gate = new FakeGate(); engine = new NativeMemoryCapture.Engine(Context(new NativeMemoryJournal(_root), gate)); }
            var calls = _api.Calls; engine.Tick(); Assert.That(_api.Calls, Is.EqualTo(calls)); Assert.That(gate.Busy, Is.True);
            Assert.That(engine.Execute(Query("wrong", true), "release_snapshot").success, Is.False);
            Assert.That(engine.Execute(Query(release: true), "release_snapshot").success, Is.True);
            Assert.That(gate.Busy, Is.False); Assert.That(engine.Busy, Is.False);
            var released = _journal.Load("capture-one", out invalid); Assert.That(invalid, Is.False);
            Assert.That(released.released, Is.True); Assert.That(released.busy, Is.False); Assert.That(released.result, Is.Null);
            Assert.That(engine.Execute(Request(), "capture_snapshot").success, Is.False); Assert.That(_api.Calls, Is.EqualTo(calls));
            Assert.That(engine.Execute(Request("new"), "capture_snapshot").success, Is.True);
        }

        [Test]
        public void RecoverWriteFailureAndExternallyFoundCapturingCanBeReleasedAfterIoRecovery()
        {
            var r = Queue(); _engine.Tick(); var writer = _journal.Write;
            _journal.Write = (path, json) => { throw new IOException("fixture"); };
            var gate = new FakeGate(); var reload = new NativeMemoryCapture.Engine(Context(_journal, gate));
            _journal.Write = writer;
            Assert.That(reload.Execute(Query(release: true), "release_snapshot").success, Is.True); Assert.That(gate.Busy, Is.False);
            var old = Clone(r); old.requestId = "external"; old.operationId = Guid.NewGuid().ToString("N");
            old.outputPath = NewPath(); old.tempPath = NewPath(); _journal.Save(old);
            Assert.That(reload.Execute(Query("external", true), "release_snapshot").success, Is.True);
            Assert.That(reload.Busy, Is.False); Assert.That(_api.Calls, Is.EqualTo(1));
        }

        [Test]
        public void PartialLatestForOtherIdCannotBeLostDuringSequentialExactReleases()
        {
            var first = Queue(); var second = Clone(first); second.requestId = "two"; second.operationId = Guid.NewGuid().ToString("N");
            second.outputPath = NewPath(); second.tempPath = NewPath();
            var writer = _journal.Write; _journal.Write = (path, json) => { if (path != _journal.RecordPath(null)) throw new IOException("second write"); writer(path, json); };
            Assert.Throws<IOException>(() => _journal.Save(second));
            var gate = new FakeGate(); var reload = new NativeMemoryCapture.Engine(Context(_journal, gate));
            _journal.Write = writer;
            Assert.That(reload.Execute(Query("capture-one", true), "release_snapshot").success, Is.True); Assert.That(gate.Busy, Is.True);
            AssertUnknown(_journal.Load("two", out var invalid)); Assert.That(invalid, Is.False);
            Assert.That(reload.Execute(Query("two", true), "release_snapshot").success, Is.True); Assert.That(gate.Busy, Is.False);
            Assert.That(_journal.ReadAll(out invalid).FindAll(record => record.busy), Is.Empty); Assert.That(invalid, Is.False); Assert.That(_api.Calls, Is.Zero);
        }

        [Test]
        public void IdentifiableConflictIsUnknownButIdentityConflictRemainsFailClosed()
        {
            var r = Queue(); var latest = Clone(r); latest.status = "capturing"; latest.startedAt = _now.ToString("o"); latest.nativeMayStillRun = true;
            File.WriteAllText(_journal.RecordPath(null), AIBridgeJson.Serialize(NativeMemoryJournal.ToData(latest)));
            AssertUnknown(_journal.Load(r.requestId, out var invalid)); Assert.That(invalid, Is.False);
            latest.operationId = Guid.NewGuid().ToString("N");
            File.WriteAllText(_journal.RecordPath(null), AIBridgeJson.Serialize(NativeMemoryJournal.ToData(latest)));
            var gate = new FakeGate(); var reload = new NativeMemoryCapture.Engine(Context(new NativeMemoryJournal(_root), gate));
            Assert.That(reload.Execute(Query(release: true), "release_snapshot").success, Is.False);
            Assert.That(reload.Busy, Is.True); Assert.That(gate.Busy, Is.True); Assert.That(_api.Calls, Is.Zero);
        }

        [Test]
        public void JournalCapacityRejectsNewIntentWithoutBlockingExistingTerminalOrRelease()
        {
            var journal = new NativeMemoryJournal(_root, 3);
            var gate = new FakeGate(); var engine = new NativeMemoryCapture.Engine(Context(journal, gate));
            Assert.That(engine.Execute(Request("first"), "capture_snapshot").success, Is.True);
            engine.Tick(); _api.Callback(_api.Path, false); engine.Tick();
            Assert.That(engine.Execute(Request("second"), "capture_snapshot").success, Is.True);
            engine.Tick(); _now = _now.AddSeconds(2); engine.Tick();
            Assert.That(engine.Execute(Query("second", true), "release_snapshot").success, Is.True);
            var calls = _api.Calls; var writes = 0; var writer = journal.Write;
            journal.Write = (path, json) => { writes++; writer(path, json); };
            var rejected = engine.Execute(Request("third"), "capture_snapshot");
            Assert.That(rejected.success, Is.False); Assert.That(writes, Is.Zero); engine.Tick();
            Assert.That(_api.Calls, Is.EqualTo(calls)); Assert.That(gate.Busy, Is.False); Assert.That(engine.Busy, Is.False);
            Assert.That(journal.ReadAll(out var invalid).Count, Is.EqualTo(2)); Assert.That(invalid, Is.False);
            Assert.That(File.Exists(journal.RecordPath("third")), Is.False);
        }

        private static NativeMemoryRecord LiveRecord(NativeMemoryCapture.Engine engine, string id)
        {
            // 回执是保留null的快照投影；生命周期断言单独取fixture内活DTO，不把线上DTO暴露回来。
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var operations = (System.Collections.IDictionary)typeof(NativeMemoryCapture.Engine).GetField("_operations", flags).GetValue(engine);
            var operation = operations[id];
            return (NativeMemoryRecord)operation.GetType().GetField("Record", flags).GetValue(operation);
        }
        private static Dictionary<string, object> Wire(CommandResult result)
        {
            return AIBridgeJson.DeserializeObjectStrict(AIBridgeJson.Serialize(result));
        }
        private static void AssertWireNulls(Dictionary<string, object> record)
        {
            Assert.That(record.ContainsKey("result"), Is.True); Assert.That(record["result"], Is.Null);
            Assert.That(record.ContainsKey("sizeBytes"), Is.True); Assert.That(record["sizeBytes"], Is.Null);
        }
        private string NewPath() { return Path.GetFullPath(Path.Combine(_root, NativeMemoryJournal.OutputDirectory, Guid.NewGuid().ToString("N") + ".snap")); }
        private static NativeMemoryRecord Clone(NativeMemoryRecord r)
        {
            return new NativeMemoryRecord
            {
                requestId = r.requestId, operationId = r.operationId, projectPath = r.projectPath, generation = r.generation,
                editorPid = r.editorPid, targetGuid = r.targetGuid, targetName = r.targetName, outputPath = r.outputPath,
                tempPath = r.tempPath, status = r.status, queuedAt = r.queuedAt, startedAt = r.startedAt, completedAt = r.completedAt,
                busy = r.busy, released = r.released, nativeMayStillRun = r.nativeMayStillRun, result = r.result, sizeBytes = r.sizeBytes,
                error = r.error, timeoutMs = r.timeoutMs
            };
        }
        private static void AssertUnknown(NativeMemoryRecord r)
        {
            Assert.That(r.status, Is.EqualTo("unknown")); Assert.That(r.busy, Is.True); Assert.That(r.result, Is.Null); Assert.That(r.sizeBytes, Is.Null);
        }
        private sealed class FakeApi : INativeMemorySnapshotApi
        {
            public NativeMemorySnapshotSupport Support { get; } = new NativeMemorySnapshotSupport { Capture = true };
            internal NativeMemorySnapshotState State = new NativeMemorySnapshotState { CurrentTargetGuid = -1, CurrentTargetName = "Editor", ProfilerEnabled = false };
            internal int Calls; internal bool ThrowOnCapture; internal string Path; internal Action<string, bool> Callback;
            public NativeMemorySnapshotState ReadState() { return State; }
            public void TakeSnapshot(string path, Action<string, bool> callback)
            {
                Calls++; Path = path; Callback = callback; if (ThrowOnCapture) throw new IOException("fixture");
            }
        }
        private sealed class FakeGate
        {
            internal bool Busy; internal int Releases; private object _lease;
            internal object Acquire() { if (Busy) return null; Busy = true; return _lease = new object(); }
            internal void Release(object lease) { Assert.That(lease, Is.SameAs(_lease)); Busy = false; _lease = null; Releases++; }
        }
        [Flags]
        private enum FakeFlags { ManagedObjects = 32, NativeObjects = 512, Other = 2 }
        private enum MissingFlags { Other = 1 }
        private static class FakeDriver
        {
            public static bool enabled => false;
            public static int connectedProfiler => -1;
            public static string GetConnectionIdentifier(int id) { return "Editor"; }
        }
        private static class ExactApi
        {
            internal static FakeFlags Flags;
            public static void TakeSnapshot(string path, Action<string, bool> callback, FakeFlags flags = FakeFlags.Other) { Flags = flags; }
        }
        private static class MissingFlagApi { public static void TakeSnapshot(string path, Action<string, bool> callback, MissingFlags flags) { } }
        private static class BoolReturnApi { public static bool TakeSnapshot(string path, Action<string, bool> callback, FakeFlags flags) { return true; } }
        private static class WrongCallbackApi { public static void TakeSnapshot(string path, Action<bool> callback, FakeFlags flags) { } }
        private static class FourArgsApi { public static void TakeSnapshot(string path, Action<string, bool> callback, FakeFlags flags, bool screenshot) { } }
    }
}
