using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class RecommendedSkillPluginAdapterTests : RecommendedSkillsTestFixture
    {
        [Test]
        public void GeneratesRootCodexPluginIndex()
        {
            AIBridgeProjectSettings.Instance.SkillRootDirectory = ".skill";

            SkillPluginAdapter.GenerateAll(ProjectRoot);

            var codexPluginJson = File.ReadAllText(Path.Combine(ProjectRoot, ".codex-plugin", "plugin.json"));
            StringAssert.Contains("\"name\": \"aibridge-skills\"", codexPluginJson);
            StringAssert.Contains("\"skills\": \"./.skill/\"", codexPluginJson);
            Assert.IsFalse(Directory.Exists(Path.Combine(ProjectRoot, "plugins", "aibridge-skills")));
        }

        [Test]
        public void GeneratesPluginIndexForDefaultSharedDirectory()
        {
            SkillPluginAdapter.GenerateAll(ProjectRoot);

            var claudePluginJson = File.ReadAllText(Path.Combine(ProjectRoot, ".claude-plugin", "plugin.json"));
            var codexPluginJson = File.ReadAllText(Path.Combine(ProjectRoot, ".codex-plugin", "plugin.json"));
            var cursorPluginJson = File.ReadAllText(Path.Combine(ProjectRoot, ".cursor-plugin", "plugin.json"));
            StringAssert.Contains("\"skills\": \"./.aibridge/skills/\"", claudePluginJson);
            StringAssert.Contains("\"skills\": \"./.aibridge/skills/\"", codexPluginJson);
            StringAssert.Contains("\"skills\": \"./.aibridge/skills/\"", cursorPluginJson);
            StringAssert.Contains("\"displayName\": \"AIBridge Skills\"", cursorPluginJson);
            StringAssert.Contains("\"interface\"", codexPluginJson);
        }

        [Test]
        public void GeneratesRootCursorPluginIndexForCustomDirectory()
        {
            AIBridgeProjectSettings.Instance.SkillRootDirectory = ".skill";

            SkillPluginAdapter.GenerateAll(ProjectRoot);

            var cursorPluginJson = File.ReadAllText(Path.Combine(ProjectRoot, ".cursor-plugin", "plugin.json"));
            StringAssert.Contains("\"name\": \"aibridge-skills\"", cursorPluginJson);
            StringAssert.Contains("\"displayName\": \"AIBridge Skills\"", cursorPluginJson);
            StringAssert.Contains("\"skills\": \"./.skill/\"", cursorPluginJson);
        }

        [Test]
        public void AppendsCustomSkillDirectoryToExistingPluginIndex()
        {
            AIBridgeProjectSettings.Instance.SkillRootDirectory = ".skill";
            var manifestDirectory = Path.Combine(ProjectRoot, ".codex-plugin");
            Directory.CreateDirectory(manifestDirectory);
            File.WriteAllText(
                Path.Combine(manifestDirectory, "plugin.json"),
                "{ \"name\": \"existing\", \"skills\": \"./vendor-skills/\", \"custom\": true }");

            SkillPluginAdapter.GenerateAll(ProjectRoot);

            var codexPluginJson = File.ReadAllText(Path.Combine(manifestDirectory, "plugin.json"));
            StringAssert.Contains("\"name\": \"existing\"", codexPluginJson);
            StringAssert.Contains("\"custom\": true", codexPluginJson);
            StringAssert.Contains("\"./vendor-skills/\"", codexPluginJson);
            StringAssert.Contains("\"./.skill/\"", codexPluginJson);
        }

        [Test]
        public void CanRemovePreviousCustomSkillDirectoryFromExistingPluginIndex()
        {
            var manifestDirectory = Path.Combine(ProjectRoot, ".codex-plugin");
            Directory.CreateDirectory(manifestDirectory);
            File.WriteAllText(
                Path.Combine(manifestDirectory, "plugin.json"),
                "{ \"name\": \"existing\", \"skills\": [\"./vendor-skills/\", \"./.skill/\"], \"custom\": true }");
            var codexTarget = AssistantIntegrationRegistry.GetTargets().First(target => target.Id == "codex");

            SkillPluginAdapter.CleanupSkillRootForTargets(ProjectRoot, new[] { codexTarget }, ".skill");

            var codexPluginJson = File.ReadAllText(Path.Combine(manifestDirectory, "plugin.json"));
            StringAssert.Contains("\"./vendor-skills/\"", codexPluginJson);
            Assert.IsFalse(codexPluginJson.Contains("\"./.skill/\""));
        }

        [Test]
        public void CleanupRemovesOnlyAIBridgePluginIndex()
        {
            AIBridgeProjectSettings.Instance.SkillRootDirectory = ".skills";
            var marketplaceDirectory = Path.Combine(ProjectRoot, ".agents", "plugins");
            Directory.CreateDirectory(marketplaceDirectory);
            File.WriteAllText(
                Path.Combine(marketplaceDirectory, "marketplace.json"),
                "{ \"name\": \"existing\", \"plugins\": [{ \"name\": \"other\", \"source\": { \"source\": \"local\", \"path\": \"./plugins/other\" } }, { \"name\": \"aibridge-skills\", \"source\": { \"source\": \"local\", \"path\": \"./plugins/aibridge-skills\" } }] }");
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ".codex-plugin"));
            File.WriteAllText(
                Path.Combine(ProjectRoot, ".codex-plugin", "plugin.json"),
                "{ \"name\": \"aibridge-skills\", \"skills\": \"./.skills/\" }");
            Directory.CreateDirectory(Path.Combine(ProjectRoot, "plugins", "aibridge-skills", ".codex-plugin"));
            File.WriteAllText(
                Path.Combine(ProjectRoot, "plugins", "aibridge-skills", ".codex-plugin", "plugin.json"),
                "{ \"name\": \"aibridge-skills\" }");
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ".skills", "aibridge"));
            File.WriteAllText(Path.Combine(ProjectRoot, ".skills", "aibridge", "SKILL.md"), "# Shared AIBridge");
            var codexTarget = AssistantIntegrationRegistry.GetTargets().First(target => target.Id == "codex");

            SkillPluginAdapter.CleanupForTargets(ProjectRoot, new[] { codexTarget });

            var marketplaceJson = File.ReadAllText(Path.Combine(marketplaceDirectory, "marketplace.json"));
            StringAssert.Contains("\"name\": \"other\"", marketplaceJson);
            Assert.IsFalse(marketplaceJson.Contains("\"name\": \"aibridge-skills\""));
            Assert.IsFalse(Directory.Exists(Path.Combine(ProjectRoot, ".codex-plugin")));
            Assert.IsFalse(Directory.Exists(Path.Combine(ProjectRoot, "plugins", "aibridge-skills")));
            Assert.IsTrue(File.Exists(Path.Combine(ProjectRoot, ".skills", "aibridge", "SKILL.md")));
        }
    }
}
