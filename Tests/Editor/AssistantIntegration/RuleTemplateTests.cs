using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class RuleTemplateTests : AssistantIntegrationTestFixture
    {
        [Test]
        public void AssistantTargetsUseSharedRootRuleTemplate()
        {
            var targets = AssistantIntegrationRegistry.GetTargets();

            Assert.IsTrue(targets.All(target => target.RootRuleTemplateRelativePath == "Templates~/Rules/AIBridge.RootRule.md"));
        }

        [Test]
        public void SharedRootRuleTemplateRoutesThroughWorkflowWithoutSkillIndex()
        {
            var template = RuleTemplateLoader.Load(ProjectRoot, "Templates~/Rules/AIBridge.RootRule.md");

            StringAssert.Contains("{{WORKFLOW_SKILL_ENTRY}}", template.Body);
            StringAssert.Contains("{{SKILL_ROOT_RULE}}", template.Body);
            StringAssert.Contains("{{UNITY_VERSION_RULE}}", template.Body);
            StringAssert.Contains("{{CSHARP_VERSION_RULE}}", template.Body);
            StringAssert.Contains("{{CODE_INDEX_CAPABILITY_RULE}}", template.Body);
            Assert.IsFalse(template.Body.Contains("{{SKILL_INDEX}}"));
        }

        [Test]
        public void CodeIndexSkillInstallsOnlyWhenFeatureEnabled()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");

            AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex = true;
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            Assert.IsTrue(File.Exists(Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-code-index", "SKILL.md")));
        }

        [Test]
        public void DisabledCodeIndexRemovesStaleSkillAndRendersCapabilityRule()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");
            var staleSkillDirectory = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-code-index");
            Directory.CreateDirectory(staleSkillDirectory);
            File.WriteAllText(Path.Combine(staleSkillDirectory, "SKILL.md"), "# stale");

            AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex = false;
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            Assert.IsFalse(Directory.Exists(staleSkillDirectory));
            var rootRule = File.ReadAllText(Path.Combine(ProjectRoot, "AGENTS.md"));
            StringAssert.Contains("Code Index: disabled", rootRule);
            StringAssert.Contains("Do not call `code_index`", rootRule);
        }

        [Test]
        public void ProjectAgentsTemplateHasNoUnresolvedVersionTokens()
        {
            var template = RuleTemplateLoader.Load(ProjectRoot, "Templates~/ProjectRules/AGENTS.zh-CN.md");

            var rendered = SkillInstaller.ApplyProjectVersionTokens(template.Body);

            Assert.IsFalse(rendered.Contains("{{UNITY_VERSION}}"));
            Assert.IsFalse(rendered.Contains("{{CSHARP_LANGUAGE_VERSION}}"));
        }
    }
}
