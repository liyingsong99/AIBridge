using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public abstract class AssistantIntegrationTestFixture
    {
        protected string ProjectRoot { get; private set; }

        [SetUp]
        public void SetUp()
        {
            ProjectRoot = Path.Combine(Path.GetTempPath(), "AIBridgeAssistantTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(ProjectRoot);
            ResetProjectSettings();
        }

        [TearDown]
        public void TearDown()
        {
            ResetProjectSettings();

            if (Directory.Exists(ProjectRoot))
            {
                Directory.Delete(ProjectRoot, true);
            }
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
