using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AIBridge.Runtime.Internal;
using NUnit.Framework;
using UnityEditor;

namespace AIBridge.Editor.Tests
{
    public sealed class BuildTrackerTests
    {
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        private string _id, _buildId, _root, _scene;
        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>();
        private object _active, _plan;
        private Func<BuildPlayerOptions, AIBridgeBuildReport> _executor;
        private int _calls;

        [SetUp]
        public void SetUp()
        {
            _calls = 0;
            _files.Clear();
            _root = Directory.GetCurrentDirectory(); _id = "build-tracker-fixture-" + Guid.NewGuid().ToString("N");
            _buildId = Guid.NewGuid().ToString("N");
            _active = Get("_active"); _plan = Get("_plan"); _executor = AIBridgeBuildTracker.BuildExecutor;
            if (_active != null) Assert.Ignore("A real build operation is active.");
            var latest = AIBridgeBuildStore.RecordPath(_root, null); Backup(latest); Backup(AIBridgeBuildStore.RecordPath(_root, _id));
            _scene = "Assets/AIBridgeBuildTrackerFixture_" + Guid.NewGuid().ToString("N") + ".unity";
            var candidates = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
            if (candidates.Length == 0) Assert.Ignore("Saved scene fixture unavailable.");
            Assert.That(AssetDatabase.CopyAsset(AssetDatabase.GUIDToAssetPath(candidates[0]), _scene), Is.True);
            AIBridgeBuildTracker.BuildExecutor = options =>
            {
                _calls++;
                return new AIBridgeBuildReport { result = "Succeeded", target = options.target.ToString(), outputPath = options.locationPathName,
                    files = new List<AIBridgeBuildFile>(), steps = new List<AIBridgeBuildStep>() };
            };
        }

        [TearDown]
        public void TearDown()
        {
            AIBridgeBuildTracker.BuildExecutor = _executor;
            Set("_active", _active); Set("_plan", _plan);
            foreach (var file in _files)
            {
                if (file.Value == null) { if (File.Exists(file.Key)) File.Delete(file.Key); }
                else File.WriteAllBytes(file.Key, file.Value);
            }
            if (!string.IsNullOrEmpty(_scene)) AssetDatabase.DeleteAsset(_scene);
            if (_buildId != null)
            {
                var own = AIBridgeBuildPlan.OutputDirectory(_root, _buildId);
                if (Directory.Exists(own)) Directory.Delete(own, true);
            }
        }

        [Test]
        public void QueuedIntentInvokesOnceAndStoresConfirmedPureReport()
        {
            Install(); Invoke("ExecutePending"); Invoke("ExecutePending");
            Assert.That(_calls, Is.EqualTo(1));
            var loaded = Load(); Assert.That(loaded.status, Is.EqualTo("completed"));
            Assert.That(loaded.success, Is.True); Assert.That(loaded.busy, Is.False);
            Assert.That(loaded.report.totalErrors, Is.Zero); Assert.That(loaded.report.totalSizeBytes, Is.Zero);
        }

        [Test]
        public void ExecutorExceptionBecomesUnknownAndNeverAutomaticallyReplays()
        {
            Install(); AIBridgeBuildTracker.BuildExecutor = options => { _calls++; throw new IOException(); };
            Invoke("ExecutePending"); Invoke("ExecutePending");
            Assert.That(_calls, Is.EqualTo(1)); var loaded = Load();
            Assert.That(loaded.status, Is.EqualTo("unknown")); Assert.That(loaded.success, Is.Null); Assert.That(loaded.busy, Is.True);
        }

        [Test]
        public void ReloadRecoversQueuedRecordWithoutReconstructingExecutionPlan()
        {
            Install(); Set("_active", null); Set("_plan", null); Invoke("Recover"); Invoke("ExecutePending");
            Assert.That(_calls, Is.Zero); Assert.That(Load().status, Is.EqualTo("unknown"));
            var result = new BuildCommand().Execute(new CommandRequest { id = "release-" + _id, type = "build",
                @params = new Dictionary<string, object> { { "action", "release" }, { "requestId", _id }, { "confirm", true } } });
            Assert.That(result.success, Is.True, result.error); Assert.That(Load().released, Is.True); Assert.That(Load().busy, Is.False);
        }

        [Test]
        public void BuildingJournalSaveFailureNeverCallsExecutorOrLosesUnknownState()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows file sharing fixture.");
            Install();
            using (var held = new FileStream(AIBridgeBuildStore.RecordPath(_root, null), FileMode.Open, FileAccess.Read, FileShare.Read))
            { Invoke("ExecutePending"); Invoke("ExecutePending"); }
            Assert.That(_calls, Is.Zero); Assert.That(Get("_active"), Is.Not.Null);
            var record = (AIBridgeBuildRecord)Get("_active"); Assert.That(record.status, Is.EqualTo("unknown"));
        }

        [Test]
        public void ExistingOutputIsNeverReusedOrOverwritten()
        {
            Install(); var own = AIBridgeBuildPlan.OutputDirectory(_root, _buildId); Directory.CreateDirectory(own);
            var marker = Path.Combine(own, "preserve.txt"); File.WriteAllText(marker, "preserve");
            Invoke("ExecutePending"); Assert.That(_calls, Is.Zero); Assert.That(File.ReadAllText(marker), Is.EqualTo("preserve"));
            Assert.That(Load().status, Is.EqualTo("unknown"));
        }

        [Test]
        public void LatestOnlyBusyRecordIsRecoveredRatherThanForgotten()
        {
            Install(); File.Delete(AIBridgeBuildStore.RecordPath(_root, _id));
            Set("_active", null); Set("_plan", null); Invoke("Recover");
            Assert.That(((AIBridgeBuildRecord)Get("_active")).status, Is.EqualTo("unknown"));
            Assert.That(_calls, Is.Zero); Assert.That(Load().busy, Is.True);
        }

        [Test]
        public void OldGenerationQueuedRecordCanBeExplicitlyReleasedWithoutPlan()
        {
            Install(); Set("_active", null); Set("_plan", null);
            var result = new BuildCommand().Execute(new CommandRequest { id = "release-old-" + _id, type = "build",
                @params = new Dictionary<string, object> { { "action", "release" }, { "requestId", _id }, { "confirm", true } } });
            Assert.That(result.success, Is.True, result.error); Assert.That(Load().status, Is.EqualTo("unknown"));
            Assert.That(Load().released, Is.True); Assert.That(_calls, Is.Zero);
        }

        [Test]
        public void NewCodeCannotStartWhileBuildIsQueued()
        {
            Install();
            var result = new CodeCommand().Execute(new CommandRequest { id = "code-build-busy", type = "code",
                @params = new Dictionary<string, object> { { "action", "execute" }, { "code", "return 1;" } } });
            Assert.That(result.success, Is.False); Assert.That(result.error, Does.Contain("build")); Assert.That(_calls, Is.Zero);
        }

        [Test]
        public void ReleaseWriteFailureKeepsOriginalBusyState()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows file sharing fixture.");
            Install(); AIBridgeBuildTracker.BuildExecutor = options => { throw new IOException(); }; Invoke("ExecutePending");
            using (var held = new FileStream(AIBridgeBuildStore.RecordPath(_root, _id), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = new BuildCommand().Execute(new CommandRequest { id = "release-fails-" + _id, type = "build",
                    @params = new Dictionary<string, object> { { "action", "release" }, { "requestId", _id }, { "confirm", true } } });
                Assert.That(result.success, Is.False);
            }
            var current = (AIBridgeBuildRecord)Get("_active"); Assert.That(current.busy, Is.True); Assert.That(current.released, Is.False);
        }

        [Test]
        public void EditorMissingReportIsFailure()
        {
            var result = new BuildCommand().Execute(new CommandRequest { id = "missing-report", type = "build",
                @params = new Dictionary<string, object> { { "action", "report" }, { "requestId", _id } } });
            Assert.That(result.success, Is.False);
        }

        private void Install()
        {
            var record = new AIBridgeBuildRecord { requestId = _id, buildId = _buildId, projectPath = _root,
                unityVersion = UnityEngine.Application.unityVersion, generation = new string('b', 32), editorPid = 123,
                status = "queued", target = EditorUserBuildSettings.activeBuildTarget.ToString(),
                outputPath = AIBridgeBuildPlan.OutputPath(_root, _buildId, EditorUserBuildSettings.activeBuildTarget.ToString()),
                queuedAt = DateTime.UtcNow.ToString("o"), busy = true, scenes = new List<string> { _scene } };
            AIBridgeBuildStore.Save(_root, record); Set("_active", record);
            Set("_plan", new AIBridgeBuildPlan { Target = EditorUserBuildSettings.activeBuildTarget, Scenes = new[] { _scene } });
        }
        private AIBridgeBuildRecord Load()
        {
            var value = AIBridgeBuildStore.Load(_root, _id, out var bad); Assert.That(bad, Is.False); Assert.That(value, Is.Not.Null); return value;
        }
        private void Backup(string path) { _files[path] = File.Exists(path) ? File.ReadAllBytes(path) : null; }
        private static object Get(string name) { return typeof(AIBridgeBuildTracker).GetField(name, StaticPrivate).GetValue(null); }
        private static void Set(string name, object value) { typeof(AIBridgeBuildTracker).GetField(name, StaticPrivate).SetValue(null, value); }
        private static void Invoke(string name) { typeof(AIBridgeBuildTracker).GetMethod(name, StaticPrivate).Invoke(null, null); }
    }
}
