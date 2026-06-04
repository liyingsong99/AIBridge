using System;
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
            StringAssert.Contains("asset search/find --format paths", rootRule);
            StringAssert.Contains("Unity imported asset", rootRule);
            StringAssert.Contains("this root rule or the workflow", rootRule);
            StringAssert.Contains("probes harness readiness", rootRule);
            StringAssert.Contains("Harness capability snapshot", rootRule);
            StringAssert.Contains("without loading `aibridge-development-workflow`", rootRule);
            StringAssert.Contains("simple search/display", rootRule);
            StringAssert.Contains("risk review/validation verdict", rootRule);
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
            StringAssert.Contains("asset search/find --format paths", rootRule);
        }

        [Test]
        public void SimplifiedChineseRootRuleKeepsQuickTasksOutOfWorkflow()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");

            AIBridgeProjectSettings.Instance.EditorLanguage = AIBridgeEditorLanguage.SimplifiedChinese;
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var rootRule = File.ReadAllText(Path.Combine(ProjectRoot, "AGENTS.md"));
            StringAssert.Contains("RootRule 只提供 compact 摘要", rootRule);
            StringAssert.Contains("读取完整 snapshot 或运行完整探测", rootRule);
            StringAssert.Contains("不加载 `aibridge-development-workflow`", rootRule);
            StringAssert.Contains("不输出审查/验证/根因结论", rootRule);
            StringAssert.Contains("工作流任务先加载", rootRule);
            Assert.Less(
                rootRule.IndexOf("**路由原则**", StringComparison.Ordinal),
                rootRule.IndexOf("**项目版本**", StringComparison.Ordinal));
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
            StringAssert.Contains("Unity 已导入资源路径查找", workflowSkill);
            StringAssert.Contains("asset search/find --format paths", workflowSkill);
            StringAssert.Contains("字面量字符串", workflowSkill);
            StringAssert.Contains("rg -n", workflowSkill);
            StringAssert.Contains("Harness 能力探测模式", workflowSkill);
            StringAssert.Contains("references/harness-readiness.md", workflowSkill);
            StringAssert.Contains("【入口：Preflight / Skill 路由】", workflowSkill);
            StringAssert.Contains("【模式：调试诊断分支】", workflowSkill);
            StringAssert.Contains("-> 基线证据收集", workflowSkill);
            StringAssert.Contains("activeSkills", workflowSkill);
            StringAssert.Contains("快速任务不进入本 Skill", workflowSkill);
            StringAssert.Contains("如误入本 Skill，停止展开分支和 Harness 探测", workflowSkill);
            StringAssert.Contains("风险审查/验证结论", workflowSkill);
            StringAssert.Contains("优先使用 RootRule compact 摘要", workflowSkill);
            StringAssert.Contains("Workflow report/manifest 默认作为 artifact ref", workflowSkill);

            var preferencesPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "project-workflow-preferences.md");
            var preferences = File.ReadAllText(preferencesPath);
            StringAssert.Contains("Code Index 偏好", preferences);
            Assert.Less(
                preferences.IndexOf("## 启用分支", StringComparison.Ordinal),
                preferences.IndexOf("- Settings Hash:", StringComparison.Ordinal));

            var branchSelectionPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "branch-selection.md");
            var branchSelection = File.ReadAllText(branchSelectionPath);
            StringAssert.Contains("【入口：Preflight / Skill 路由】", branchSelection);
            StringAssert.Contains("【模式：<启用分支之一>】", branchSelection);
            StringAssert.Contains("-> <当前步骤>", branchSelection);
            StringAssert.Contains("<当前步骤正在收集或产出的内容>", branchSelection);
            Assert.IsFalse(branchSelection.Contains("【任务分流步骤】"));
            Assert.IsFalse(branchSelection.Contains("【分支模式】"));
            Assert.IsFalse(branchSelection.Contains("说明：<当前步骤正在收集或产出的内容>"));
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
            StringAssert.Contains("\"snapshotPath\"", snapshot);
            StringAssert.Contains("capabilities.json", snapshot);
            StringAssert.Contains("\"codeIndex\"", snapshot);
            StringAssert.Contains("\"enabled\": true", snapshot);
            StringAssert.Contains("\"externalExecutor\": \"unknown\"", snapshot);
        }
    }
}
