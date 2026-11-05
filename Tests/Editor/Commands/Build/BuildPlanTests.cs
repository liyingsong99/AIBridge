using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIBridge.Editor.Tests
{
    public sealed class BuildPlanTests
    {
        private string _scenePath;

        [SetUp]
        public void SetUp()
        {
            _scenePath = "Assets/AIBridgeBuildFixture_" + Guid.NewGuid().ToString("N") + ".unity";
            var candidates = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
            if (candidates.Length == 0) Assert.Ignore("No saved SceneAsset exists for a copy-only fixture.");
            Assert.That(AssetDatabase.CopyAsset(AssetDatabase.GUIDToAssetPath(candidates[0]), _scenePath), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_scenePath)) AssetDatabase.DeleteAsset(_scenePath);
        }

        [Test]
        public void DryRunValidatesWithoutJournalOrOutputSideEffects()
        {
            var latest = global::AIBridge.Runtime.Internal.AIBridgeBuildStore.RecordPath(Directory.GetCurrentDirectory(), null);
            var before = File.Exists(latest) ? File.ReadAllBytes(latest) : null;
            var result = new BuildCommand().Execute(Request());
            Assert.That(result.success, Is.True, result.error);
            Assert.That(File.Exists(latest) ? File.ReadAllBytes(latest) : null, Is.EqualTo(before));
        }

        [Test]
        public void PlanUsesOnlyActiveTargetAndExplicitDevelopmentFlag()
        {
            var request = Request(); request.@params["development"] = true;
            var plan = AIBridgeBuildPlan.Parse(request);
            Assert.That(plan.DryRun, Is.True); Assert.That(plan.Development, Is.True);
            var options = plan.Options("controlled-output");
            Assert.That(options.target, Is.EqualTo(EditorUserBuildSettings.activeBuildTarget));
            Assert.That(options.scenes, Is.EqualTo(new[] { _scenePath }));
            Assert.That(options.options, Is.EqualTo(BuildOptions.Development));
        }

        [TestCase("dry-run", "false")]
        [TestCase("confirm", 1)]
        [TestCase("development", "true")]
        public void SafetyFlagsRequireActualBooleans(string key, object value)
        {
            var request = Request(); request.@params[key] = value;
            Assert.That(new BuildCommand().Execute(request).success, Is.False);
        }

        [Test]
        public void ActualBuildNeedsPerRequestConfirmationBeforeQueuing()
        {
            var request = Request(); request.@params["dry-run"] = false;
            Assert.That(new BuildCommand().Execute(request).success, Is.False);
        }

        [TestCase("outputPath", "Assets/overwritten.exe")]
        [TestCase("options", "AutoRunPlayer")]
        [TestCase("backend", "pipeline")]
        [TestCase("target", "Android")]
        public void UnsupportedOrTargetSwitchingIsRejected(string key, object value)
        {
            var request = Request();
            if (key == "target" && EditorUserBuildSettings.activeBuildTarget.ToString() == (string)value) value = "StandaloneWindows64";
            request.@params[key] = value;
            Assert.That(new BuildCommand().Execute(request).success, Is.False);
        }

        [TestCase("[]")]
        [TestCase("[null]")]
        [TestCase("[\"Assets/../outside.unity\"]")]
        [TestCase("[\"Packages/scene.unity\"]")]
        [TestCase("[\"Assets/missing.unity\"]")]
        [TestCase("[1]")]
        [TestCase("{}")]
        public void SceneInputMustBeExistingSavedAssets(string value)
        {
            var request = Request(); request.@params["scenes"] = value;
            Assert.That(new BuildCommand().Execute(request).success, Is.False);
        }

        [Test]
        public void DuplicateScenesAreRejected()
        {
            var request = Request(); request.@params["scenes"] = "[\"" + _scenePath + "\",\"" + _scenePath + "\"]";
            Assert.That(new BuildCommand().Execute(request).success, Is.False);
        }

        [TestCase("StandaloneWindows64", "Player.exe")]
        [TestCase("StandaloneOSX", "Player.app")]
        [TestCase("StandaloneLinux64", "Player.x86_64")]
        [TestCase("Android", "Player.apk")]
        [TestCase("iOS", "Player")]
        [TestCase("WebGL", "Player")]
        public void OutputAlwaysUsesNewBuildIdRoot(string target, string expected)
        {
            var id = new string('a', 32); var root = Directory.GetCurrentDirectory();
            Assert.That(AIBridgeBuildPlan.OutputPath(root, id, target),
                Is.EqualTo(Path.Combine(AIBridgeBuildPlan.OutputDirectory(root, id), expected)));
        }

        private CommandRequest Request()
        {
            return new CommandRequest { id = "build-plan-" + Guid.NewGuid().ToString("N"), type = "build",
                @params = new Dictionary<string, object> { { "action", "run" }, { "scenes", "[\"" + _scenePath + "\"]" } } };
        }
    }
}
