using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class AssistantIntegrationSelectionTests : AssistantIntegrationTestFixture
    {
        [Test]
        public void EmptyProjectDefaultsToSingleCodexSelection()
        {
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(ProjectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["codex"]);
            Assert.IsFalse(selections["claude"]);
        }

        [Test]
        public void ClaudeRootRuleDefaultsToSingleClaudeSelection()
        {
            File.WriteAllText(Path.Combine(ProjectRoot, "CLAUDE.md"), "# Claude");
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(ProjectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["claude"]);
            Assert.IsFalse(selections["codex"]);
        }

        [Test]
        public void AgentsRootRuleDefaultsToSingleCodexSelection()
        {
            File.WriteAllText(Path.Combine(ProjectRoot, "AGENTS.md"), "# Agents");
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(ProjectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["codex"]);
            Assert.IsFalse(selections["claude"]);
        }

        [Test]
        public void CodexPluginDirectoryDefaultsToSingleCodexSelection()
        {
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ".codex-plugin"));
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(ProjectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["codex"]);
            Assert.IsFalse(selections["claude"]);
        }

        [Test]
        public void CursorPluginDirectoryDefaultsToSingleCursorSelection()
        {
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ".cursor-plugin"));
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(ProjectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["cursor"]);
            Assert.IsFalse(selections["codex"]);
        }

        [Test]
        public void CodexWinsWhenClaudeAndAgentsRootRulesBothExist()
        {
            File.WriteAllText(Path.Combine(ProjectRoot, "CLAUDE.md"), "# Claude");
            File.WriteAllText(Path.Combine(ProjectRoot, "AGENTS.md"), "# Agents");
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(ProjectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["codex"]);
            Assert.IsFalse(selections["claude"]);
        }

        [Test]
        public void LegacySharedSkillDirectoryDoesNotDetectEveryAssistant()
        {
            var sharedSkillDirectory = Path.Combine(ProjectRoot, ".skills", "aibridge");
            Directory.CreateDirectory(sharedSkillDirectory);
            File.WriteAllText(Path.Combine(sharedSkillDirectory, "SKILL.md"), "# AIBridge");
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(ProjectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["codex"]);
            Assert.IsFalse(selections["claude"]);
            Assert.IsFalse(AssistantIntegrationDetector.Detect(ProjectRoot, targets.First(target => target.Id == "claude")).IsDetected);
            Assert.IsFalse(AssistantIntegrationDetector.Detect(ProjectRoot, targets.First(target => target.Id == "codex")).IsDetected);
        }

        [Test]
        public void ZCodeDirectoryDefaultsToSingleZCodeSelection()
        {
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ".zcode"));
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(ProjectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["zcode"]);
            Assert.IsFalse(selections["codex"]);
        }

        [Test]
        public void DeepSeekHarnessDirectoryDefaultsToSingleHarnessSelection()
        {
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ".dsh"));
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(ProjectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["deepseek-harness"]);
            Assert.IsFalse(selections["codex"]);
        }

        [Test]
        public void WorkBuddyRootRuleDefaultsToSingleWorkBuddySelection()
        {
            File.WriteAllText(Path.Combine(ProjectRoot, "CODEBUDDY.md"), "# WorkBuddy");
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(ProjectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["workbuddy"]);
            Assert.IsFalse(selections["codex"]);
        }
    }
}
