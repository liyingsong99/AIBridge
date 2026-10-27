using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public abstract class AssistantIntegrationTestFixture
    {
        protected string ProjectRoot { get; private set; }

        private AIBridgeEditorLanguage _originalLanguage;
        private bool _originalLanguageInitialized;
        private bool _originalOnboardingCompleted;
        private int _originalOnboardingVersion;
        private string _originalSkillRootDirectory;
        private bool _originalCodeIndexEnabled;
        private Dictionary<string, bool> _originalSelections;

        [SetUp]
        public void SetUp()
        {
            ProjectRoot = Path.Combine(Path.GetTempPath(), "AIBridgeAssistantTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(ProjectRoot);
            CaptureProjectSettings();
            ResetProjectSettings();
        }

        [TearDown]
        public void TearDown()
        {
            ResetProjectSettings();
            RestoreProjectSettings();

            if (Directory.Exists(ProjectRoot))
            {
                Directory.Delete(ProjectRoot, true);
            }
        }

        private void CaptureProjectSettings()
        {
            var settings = AIBridgeProjectSettings.Instance;
            _originalLanguage = settings.EditorLanguage;
            _originalLanguageInitialized = settings.EditorLanguageInitialized;
            _originalOnboardingCompleted = settings.OnboardingCompleted;
            _originalOnboardingVersion = settings.OnboardingVersion;
            _originalSkillRootDirectory = settings.SkillRootDirectory;
            _originalCodeIndexEnabled = settings.CodeIndex.EnableCodeIndex;
            _originalSelections = new Dictionary<string, bool>();
            foreach (var target in AssistantIntegrationRegistry.GetTargets())
            {
                bool selected;
                if (settings.TryGetAssistantSelection(target.Id, out selected))
                {
                    _originalSelections[target.Id] = selected;
                }
            }
        }

        private void RestoreProjectSettings()
        {
            var settings = AIBridgeProjectSettings.Instance;
            settings.EditorLanguage = _originalLanguage;
            settings.EditorLanguageInitialized = _originalLanguageInitialized;
            settings.OnboardingCompleted = _originalOnboardingCompleted;
            settings.OnboardingVersion = _originalOnboardingVersion;
            settings.SkillRootDirectory = _originalSkillRootDirectory;
            settings.CodeIndex.EnableCodeIndex = _originalCodeIndexEnabled;
            foreach (var target in AssistantIntegrationRegistry.GetTargets())
            {
                bool selected;
                if (_originalSelections.TryGetValue(target.Id, out selected))
                {
                    settings.SetAssistantSelection(target.Id, selected);
                }
                else
                {
                    settings.ClearAssistantSelection(target.Id);
                }
            }

            settings.SaveSettings();
        }

        protected static void ClearAssistantSelections()
        {
            var targets = AssistantIntegrationRegistry.GetTargets();
            foreach (var target in targets)
            {
                AIBridgeProjectSettings.Instance.ClearAssistantSelection(target.Id);
            }
        }

        protected static int CountSelectedTargets(Dictionary<string, bool> selections)
        {
            var count = 0;
            foreach (var selection in selections)
            {
                if (selection.Value)
                {
                    count++;
                }
            }

            return count;
        }

        private static void ResetProjectSettings()
        {
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("codex");
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("claude");
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("cursor");
            ClearAssistantSelections();
            AIBridgeProjectSettings.Instance.SkillRootDirectory = AIBridgeProjectSettings.DefaultSkillRootDirectory;
            AIBridgeProjectSettings.Instance.EditorLanguage = AIBridgeEditorLanguage.English;
            AIBridgeProjectSettings.Instance.EditorLanguageInitialized = true;
            AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex = AIBridgeProjectSettings.DefaultCodeIndexEnabled;
        }
    }
}
