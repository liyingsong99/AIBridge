using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class UnityCliPipelineInstallerTests
    {
        [TestCase("2019.4.40f1", false)]
        [TestCase("2023.2.20f1", false)]
        [TestCase("6000.0.51f1", true)]
        [TestCase("6000.6.0a1", true)]
        [TestCase("6001.0.0f1", true)]
        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("invalid", false)]
        [TestCase("invalid.0", false)]
        public void UnityVersionGate(string version, bool expected)
        {
            Assert.AreEqual(expected, UnityCliPipelineInstallerRules.SupportsUnity(version));
        }

        [TestCase(false, null, true)]
        [TestCase(false, "", true)]
        [TestCase(true, "", false)]
        [TestCase(false, "0.10.0-exp.1", false)]
        [TestCase(true, "0.10.0-exp.1", false)]
        public void ActiveRequestOrPendingRecoveryBlocksNewInstallation(bool busy, string pending, bool expected)
        {
            Assert.AreEqual(expected, UnityCliPipelineInstallerRules.CanStart(busy, pending));
        }

        [TestCase("com.unity.pipeline", true)]
        [TestCase("com.unity.pipeline.other", false)]
        [TestCase("com.unity.cli", false)]
        [TestCase(null, false)]
        public void OnlyExactInstalledPackageIsPreserved(string name, bool expected)
        {
            Assert.AreEqual(expected, UnityCliPipelineInstallerRules.IsInstalled(name));
        }

        [Test]
        public void SelectsRegistryLatestCompatibleIncludingExperimentalVersion()
        {
            Assert.AreEqual("0.10.0-exp.1", UnityCliPipelineInstallerRules.SelectVersion(
                "com.unity.pipeline", "0.10.0-exp.1", new[] { "0.9.0", "0.10.0-exp.1", "0.11.0-exp.1" }));
        }

        [Test]
        public void DoesNotGuessVersionWhenLatestCompatibleIsMissingFromCompatible()
        {
            Assert.IsNull(UnityCliPipelineInstallerRules.SelectVersion(
                "com.unity.pipeline", "0.10.0-exp.1", new[] { "0.9.0" }));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void DoesNotFallbackWhenLatestCompatibleIsEmpty(string latest)
        {
            Assert.IsNull(UnityCliPipelineInstallerRules.SelectVersion("com.unity.pipeline", latest, new[] { "0.9.0" }));
        }

        [Test]
        public void MissingOrEmptyCompatibilityListRejectsSelection()
        {
            Assert.IsNull(UnityCliPipelineInstallerRules.SelectVersion("com.unity.pipeline", "0.9.0", null));
            Assert.IsNull(UnityCliPipelineInstallerRules.SelectVersion("com.unity.pipeline", "0.9.0", new string[0]));
        }

        [Test]
        public void SearchResultForDifferentPackageCannotBeInstalled()
        {
            Assert.IsNull(UnityCliPipelineInstallerRules.SelectVersion("com.unity.other", "0.9.0", new[] { "0.9.0" }));
        }

        [TestCase("com.unity.pipeline", "0.10.0-exp.1", "0.10.0-exp.1", true)]
        [TestCase("com.unity.pipeline", "0.9.0", "0.10.0-exp.1", false)]
        [TestCase("com.unity.other", "0.10.0-exp.1", "0.10.0-exp.1", false)]
        [TestCase("com.unity.pipeline", null, "0.10.0-exp.1", false)]
        [TestCase("com.unity.pipeline", "", "", false)]
        public void CompletionRequiresExactPackageAndVersion(string name, string installed, string target, bool expected)
        {
            Assert.AreEqual(expected, UnityCliPipelineInstallerRules.MatchesTarget(name, installed, target));
        }
    }
}
