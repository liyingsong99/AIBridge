using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.PackageManager;

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
            StringAssert.Contains("{{HOST_EXEC_RULE}}", template.Body);
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
            StringAssert.Contains("Host Exec", rootRule);
            StringAssert.Contains("$CLI exec run --stdin", rootRule);
            StringAssert.Contains("$CLI exec batch --stdin", rootRule);
            StringAssert.Contains("including quick search/display tasks", rootRule);
            StringAssert.Contains("never append a raw shell command after `--stdin`", rootRule);
            StringAssert.DoesNotContain("In AIBridge workflow tasks", rootRule);
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
            StringAssert.Contains("外部 host 工具", rootRule);
            StringAssert.Contains("$CLI exec run --stdin", rootRule);
            StringAssert.Contains("快速查找/显示任务也适用", rootRule);
            StringAssert.Contains("禁止把裸 shell 命令追加在 `--stdin` 后面", rootRule);
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
            StringAssert.Contains("快速任务不进入本 Skill", workflowSkill);
            StringAssert.Contains("references/project-workflow-preferences.md", workflowSkill);
            StringAssert.Contains("references/branch-selection.md", workflowSkill);
            StringAssert.Contains("references/harness-readiness-detail.md", workflowSkill);
            StringAssert.Contains("编排分支按需加载 `aibridge-workflow-orchestration`", workflowSkill);
            StringAssert.Contains("C# 语义关系查询", workflowSkill);
            StringAssert.Contains("aibridge-code-index", workflowSkill);
            StringAssert.Contains("Unity 已导入资源路径查找", workflowSkill);
            StringAssert.Contains("asset search/find --format paths", workflowSkill);
            StringAssert.Contains("$CLI text_index search \"literal\"", workflowSkill);
            StringAssert.Contains("$CLI compile unity", workflowSkill);
            StringAssert.Contains("compile dotnet", workflowSkill);
            StringAssert.Contains("workflow import", workflowSkill);
            StringAssert.Contains("compact-first", workflowSkill);
            StringAssert.Contains("不把 stale snapshot", workflowSkill);
            StringAssert.DoesNotContain("使用 Skills：", workflowSkill);
            StringAssert.DoesNotContain("【模式：调试诊断分支】", workflowSkill);
            Assert.Less(workflowSkill.Length, 6000, "workflow skill should stay compact");

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
            StringAssert.Contains("aibridge-code-index、text_index、rg fallback", branchSelection);
            StringAssert.Contains("Harness 判定是 Preflight gate", branchSelection);
            StringAssert.Contains("fresh 且不影响工具选择时不单独输出", branchSelection);
            StringAssert.Contains("需求讨论分支", branchSelection);
            StringAssert.DoesNotContain("需求讨论模式", branchSelection);
            StringAssert.DoesNotContain("-> <当前步骤>", branchSelection);
            StringAssert.DoesNotContain("<当前步骤正在收集或产出的内容>", branchSelection);
            Assert.IsFalse(branchSelection.Contains("【任务分流步骤】"));
            Assert.IsFalse(branchSelection.Contains("【分支模式】"));
            Assert.IsFalse(branchSelection.Contains("【模式：Harness"));
            Assert.IsFalse(branchSelection.Contains("说明：<当前步骤正在收集或产出的内容>"));
            Assert.IsFalse(branchSelection.Contains("使用 Skills："));

            var reviewBranchPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "branches", "review.md");
            var reviewBranch = File.ReadAllText(reviewBranchPath);
            StringAssert.Contains("字面量、注释、普通代码内容或非语义文本搜索优先使用 `$CLI text_index search \"literal\"`", reviewBranch);

            var sourceBranchSelectionPath = Path.Combine(GetPackageRoot(), "Skill~", "aibridge-development-workflow", "references", "branch-selection.md");
            var sourceBranchSelection = File.ReadAllText(sourceBranchSelectionPath);
            StringAssert.Contains("source Skill 的 fallback", sourceBranchSelection);
            Assert.AreEqual(
                ExtractMarkdownSection(sourceBranchSelection, "## 工作流生命周期", "## 需求讨论分支"),
                ExtractMarkdownSection(branchSelection, "## 工作流生命周期", "## 需求讨论分支"));
            Assert.AreEqual(
                ExtractMarkdownSection(sourceBranchSelection, "## Skill 列出策略", "## 输出格式"),
                ExtractMarkdownSection(branchSelection, "## Skill 列出策略", "## 输出格式"));
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

        private static string ExtractMarkdownSection(string markdown, string header, string nextHeader)
        {
            var start = markdown.IndexOf(header, StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, header);
            var end = markdown.IndexOf(nextHeader, start, StringComparison.Ordinal);
            Assert.Greater(end, start, nextHeader);
            return markdown.Substring(start, end - start).Replace("\r\n", "\n").Trim();
        }

        private static string GetPackageRoot()
        {
            var packageInfo = PackageInfo.FindForAssembly(typeof(AIBridgeProjectSettings).Assembly);
            if (packageInfo != null && !string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
            {
                return packageInfo.resolvedPath;
            }

            return Directory.GetCurrentDirectory();
        }
    }
}
