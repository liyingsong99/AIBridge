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
            StringAssert.Contains("{{HARNESS_CAPABILITY_RULE}}", template.Body);
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
        public void EnabledCodeIndexRendersCodeLookupRouting()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");

            AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex = true;
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var rootRule = File.ReadAllText(Path.Combine(ProjectRoot, "AGENTS.md"));
            StringAssert.Contains("Code Index: enabled", rootRule);
            StringAssert.Contains("C# code lookup or source navigation", rootRule);
            StringAssert.Contains("load `aibridge-code-index` first", rootRule);
            StringAssert.Contains("this root rule or the workflow", rootRule);
            StringAssert.Contains("probes harness readiness", rootRule);
            StringAssert.Contains("Harness capability snapshot", rootRule);
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
        public void SkillInstallTargetRejectsPackageSourceSkillRoot()
        {
            var sourceSkillRoot = Path.Combine(ProjectRoot, "Packages", "cn.lys.aibridge", "Skill~");

            Assert.IsTrue(SkillInstaller.IsUnsafeSkillInstallTarget(sourceSkillRoot, sourceSkillRoot));
            Assert.IsTrue(SkillInstaller.IsUnsafeSkillInstallTarget(sourceSkillRoot, Path.Combine(sourceSkillRoot, "aibridge")));
            Assert.IsFalse(SkillInstaller.IsUnsafeSkillInstallTarget(sourceSkillRoot, Path.Combine(ProjectRoot, ".codex", "skills", "aibridge")));
        }

        [Test]
        public void DevelopmentWorkflowRoutesCSharpLookupToCodeIndex()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");

            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var workflowSkillPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "SKILL.md");
            var workflowSkill = File.ReadAllText(workflowSkillPath);
            StringAssert.Contains("C# 代码查找", workflowSkill);
            StringAssert.Contains("优先加入 `aibridge-code-index`", workflowSkill);
            StringAssert.Contains("字面量字符串", workflowSkill);
            StringAssert.Contains("Harness 能力探测模式", workflowSkill);
            StringAssert.Contains("references/harness-readiness.md", workflowSkill);
        }

        [Test]
        public void ProjectAgentsTemplateHasNoUnresolvedVersionTokens()
        {
            var template = RuleTemplateLoader.Load(ProjectRoot, "Templates~/ProjectRules/AGENTS.zh-CN.md");

            var rendered = SkillInstaller.ApplyProjectVersionTokens(template.Body);

            Assert.IsFalse(rendered.Contains("{{UNITY_VERSION}}"));
            Assert.IsFalse(rendered.Contains("{{CSHARP_LANGUAGE_VERSION}}"));
        }

        [Test]
        public void InstallWritesHarnessCapabilitySnapshot()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");

            AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex = true;
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var snapshotPath = HarnessCapabilitySnapshot.GetSnapshotPath(ProjectRoot);
            Assert.IsTrue(File.Exists(snapshotPath), snapshotPath);

            var snapshot = File.ReadAllText(snapshotPath);
            StringAssert.Contains("\"schemaVersion\"", snapshot);
            StringAssert.Contains("\"capabilities.json\"", snapshot);
            StringAssert.Contains("\"codeIndex\"", snapshot);
            StringAssert.Contains("\"enabled\": true", snapshot);
            StringAssert.Contains("\"externalExecutor\": \"unknown\"", snapshot);
        }
    }
}
