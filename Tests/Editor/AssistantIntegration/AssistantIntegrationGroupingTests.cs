using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class AssistantIntegrationGroupingTests : AssistantIntegrationTestFixture
    {
        [Test]
        public void SharedAgentsGroupWritesSingleBlockAndSkillDirectory()
        {
            var targets = GetTargets("codex", "deepseek-harness", "zcode");

            var results = SkillInstaller.InstallAssistantIntegrations(ProjectRoot, targets);

            var agentsMd = File.ReadAllText(Path.Combine(ProjectRoot, "AGENTS.md"));
            Assert.AreEqual(1, CountOccurrences(agentsMd, "<!-- AIBRIDGE:START"));
            StringAssert.Contains("Selected assistants: codex, deepseek-harness, zcode", agentsMd);
            Assert.IsTrue(File.Exists(Path.Combine(ProjectRoot, ".agents", "skills", "aibridge", "SKILL.md")));
            Assert.AreEqual(3, results.Count);
            Assert.IsTrue(results.All(result => result.RootRuleAction == IntegrationAction.CreatedFile));
        }

        [Test]
        public void CleanupKeepsSharedBlockWhenAnotherGroupMemberIsStillSelected()
        {
            var codex = GetTarget("codex");
            var harness = GetTarget("deepseek-harness");
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { codex, harness });

            SkillInstaller.CleanupUnselectedTargets(ProjectRoot, new List<AssistantIntegrationTarget> { codex });

            var agentsMd = File.ReadAllText(Path.Combine(ProjectRoot, "AGENTS.md"));
            Assert.AreEqual(1, CountOccurrences(agentsMd, "<!-- AIBRIDGE:START"));
            Assert.IsTrue(Directory.Exists(Path.Combine(ProjectRoot, ".agents", "skills", "aibridge")));
        }

        [Test]
        public void CleanupRemovesSharedBlockWhenNoGroupMemberIsSelected()
        {
            var codex = GetTarget("codex");
            var harness = GetTarget("deepseek-harness");
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { codex, harness });

            SkillInstaller.CleanupUnselectedTargets(ProjectRoot, new List<AssistantIntegrationTarget>());

            Assert.IsFalse(File.Exists(Path.Combine(ProjectRoot, "AGENTS.md")));
            Assert.IsFalse(Directory.Exists(Path.Combine(ProjectRoot, ".agents", "skills", "aibridge")));
        }

        [Test]
        public void CodexAndClaudeWriteSeparateRuleFilesAndSkillRoots()
        {
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, GetTargets("codex", "claude"));

            Assert.IsTrue(File.Exists(Path.Combine(ProjectRoot, "AGENTS.md")));
            Assert.IsTrue(File.Exists(Path.Combine(ProjectRoot, "CLAUDE.md")));
            Assert.IsTrue(File.Exists(Path.Combine(ProjectRoot, ".agents", "skills", "aibridge", "SKILL.md")));
            Assert.IsTrue(File.Exists(Path.Combine(ProjectRoot, ".claude", "skills", "aibridge", "SKILL.md")));
        }

        [Test]
        public void RootRuleSharedDetectionMatchesAgentsGroup()
        {
            Assert.IsTrue(AssistantIntegrationGroupBuilder.IsRootRuleShared("AGENTS.md"));
            Assert.IsFalse(AssistantIntegrationGroupBuilder.IsRootRuleShared("CLAUDE.md"));
            StringAssert.Contains("DeepSeek Harness", AssistantIntegrationGroupBuilder.GetSharedRootRuleOwners("AGENTS.md"));
        }

        [Test]
        public void CodexUsesOfficialAgentsSkillRoot()
        {
            var codex = GetTarget("codex");

            Assert.AreEqual(".agents/skills", codex.GetResolvedSkillRootDirectoryRelativePath(ProjectRoot));
            CollectionAssert.Contains(codex.LegacySkillRootDirectoryRelativePaths, ".codex/skills");
        }

        private static AssistantIntegrationTarget GetTarget(string id)
        {
            return AssistantIntegrationRegistry.GetTargets().First(target => target.Id == id);
        }

        private static AssistantIntegrationTarget[] GetTargets(params string[] ids)
        {
            return ids.Select(GetTarget).ToArray();
        }

        private static int CountOccurrences(string content, string value)
        {
            var count = 0;
            var index = 0;
            while ((index = content.IndexOf(value, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }
    }
}
