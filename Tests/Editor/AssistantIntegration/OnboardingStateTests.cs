using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class OnboardingStateTests
    {
        private string _projectRoot;
        private bool _originalOnboardingCompleted;
        private int _originalOnboardingVersion;
        private bool _originalEditorLanguageInitialized;
        private AIBridgeEditorLanguage _originalEditorLanguage;

        [SetUp]
        public void SetUp()
        {
            _projectRoot = Path.Combine(Path.GetTempPath(), "AIBridgeOnboardingTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_projectRoot);

            var settings = AIBridgeProjectSettings.Instance;
            _originalOnboardingCompleted = settings.OnboardingCompleted;
            _originalOnboardingVersion = settings.OnboardingVersion;
            _originalEditorLanguageInitialized = settings.EditorLanguageInitialized;
            _originalEditorLanguage = settings.EditorLanguage;
        }

        [TearDown]
        public void TearDown()
        {
            var settings = AIBridgeProjectSettings.Instance;
            settings.OnboardingCompleted = _originalOnboardingCompleted;
            settings.OnboardingVersion = _originalOnboardingVersion;
            settings.EditorLanguageInitialized = _originalEditorLanguageInitialized;
            settings.EditorLanguage = _originalEditorLanguage;
            settings.SaveSettings();

            if (Directory.Exists(_projectRoot))
            {
                Directory.Delete(_projectRoot, true);
            }
        }

        [Test]
        public void NeedsWizardTracksCompletionFlag()
        {
            AIBridgeProjectSettings.Instance.OnboardingCompleted = false;
            Assert.IsTrue(AIBridgeOnboardingState.NeedsWizard());

            AIBridgeOnboardingState.MarkCompleted();

            Assert.IsFalse(AIBridgeOnboardingState.NeedsWizard());
            Assert.IsTrue(AIBridgeProjectSettings.Instance.EditorLanguageInitialized);
            Assert.AreEqual(AIBridgeOnboardingState.CurrentVersion, AIBridgeProjectSettings.Instance.OnboardingVersion);
        }

        [Test]
        public void EmptyProjectDefaultsToCodex()
        {
            var selected = AIBridgeOnboardingState.ResolveInitialSelection(_projectRoot);

            Assert.AreEqual(1, selected.Count);
            Assert.AreEqual("codex", selected[0].Id);
        }

        [Test]
        public void ToolSpecificDirectoryWinsOverSharedRootRule()
        {
            File.WriteAllText(Path.Combine(_projectRoot, "AGENTS.md"), "# Agents");
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".zcode"));

            var selected = AIBridgeOnboardingState.ResolveInitialSelection(_projectRoot);

            Assert.AreEqual(1, selected.Count);
            Assert.AreEqual("zcode", selected[0].Id);
        }

        [Test]
        public void SharedAgentsRootRuleOnlyPrefersFirstGroupMember()
        {
            File.WriteAllText(Path.Combine(_projectRoot, "AGENTS.md"), "# Agents");

            var selected = AIBridgeOnboardingState.ResolveInitialSelection(_projectRoot);

            Assert.AreEqual(1, selected.Count);
            Assert.AreEqual("codex", selected[0].Id);
        }

        [Test]
        public void UniqueRootRuleSelectsOwningTool()
        {
            File.WriteAllText(Path.Combine(_projectRoot, "CLAUDE.md"), "# Claude");

            var selected = AIBridgeOnboardingState.ResolveInitialSelection(_projectRoot);

            Assert.AreEqual(1, selected.Count);
            Assert.AreEqual("claude", selected[0].Id);
        }

        [Test]
        public void ToolDirectoriesCanSelectMultipleTools()
        {
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".claude"));
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".cursor"));

            var selected = AIBridgeOnboardingState.ResolveInitialSelection(_projectRoot);

            CollectionAssert.AreEquivalent(new[] { "claude", "cursor" }, selected.Select(target => target.Id).ToArray());
        }
    }
}
