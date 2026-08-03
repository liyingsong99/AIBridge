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

            StringAssert.Contains("{{CLI_PATH_RULE}}", template.Body);
            StringAssert.Contains("{{WORKFLOW_SKILL_ENTRY}}", template.Body);
            StringAssert.Contains("{{SKILL_ROOT_RULE}}", template.Body);
            StringAssert.Contains("{{UNITY_VERSION_RULE}}", template.Body);
            StringAssert.Contains("{{CSHARP_VERSION_RULE}}", template.Body);
            StringAssert.Contains("{{OBJECT_ID_RULE}}", template.Body);
            StringAssert.DoesNotContain("HOST_EXEC", template.Body);
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
            var expectedCliPath = GetExpectedProjectCliPath();

            AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex = true;
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var rootRule = File.ReadAllText(Path.Combine(ProjectRoot, "AGENTS.md"));
            StringAssert.Contains("Code Index: enabled", rootRule);
            StringAssert.Contains("Load `aibridge-code-index`", rootRule);
            StringAssert.Contains("`symbol`/`definition`", rootRule);
            StringAssert.Contains("do not call `$CLI harness status` to re-check", rootRule);
            StringAssert.Contains("Details: `aibridge-code-index` Skill", rootRule);
            StringAssert.DoesNotContain("class, interface, enum, field, property, method", rootRule);
            StringAssert.DoesNotContain("plain file content search", rootRule);
            StringAssert.Contains("this root rule or the workflow", rootRule);
            StringAssert.Contains("reads harness-readiness as Preflight gate", rootRule);
            StringAssert.DoesNotContain("probes harness readiness", rootRule);
            StringAssert.Contains("Project-side capabilities are authoritative", rootRule);
            StringAssert.Contains("do not call `$CLI harness status` for Code Index", rootRule);
            StringAssert.Contains("project-local AIBridge CLI", rootRule);
            StringAssert.Contains("$CLI = \"" + expectedCliPath + "\"", rootRule);
            StringAssert.Contains("& $CLI <command> [action] [options]", rootRule);
            StringAssert.DoesNotContain("Host Exec", rootRule);
            StringAssert.DoesNotContain("$CLI exec", rootRule);
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
            StringAssert.Contains("Do not load `aibridge-code-index`", rootRule);
            StringAssert.Contains("or call `code_index`", rootRule);
            StringAssert.DoesNotContain("class, interface, enum", rootRule);
            StringAssert.DoesNotContain("file content search", rootRule);
        }

        [Test]
        public void SimplifiedChineseRootRuleKeepsQuickTasksOutOfWorkflow()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");
            var expectedCliPath = GetExpectedProjectCliPath();

            AIBridgeProjectSettings.Instance.EditorLanguage = AIBridgeEditorLanguage.SimplifiedChinese;
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var rootRule = File.ReadAllText(Path.Combine(ProjectRoot, "AGENTS.md"));
            StringAssert.Contains("项目本地 AIBridge CLI", rootRule);
            StringAssert.Contains("$CLI = \"" + expectedCliPath + "\"", rootRule);
            StringAssert.Contains("& $CLI <command> [action] [options]", rootRule);
            StringAssert.Contains("项目侧能力以本 Root Rule 与已安装 workflow 规则为准", rootRule);
            StringAssert.Contains("不要为 Code Index、Skill 或 assistant 开关调用 `$CLI harness status`", rootRule);
            StringAssert.Contains("不加载 `aibridge-development-workflow`", rootRule);
            StringAssert.Contains("不输出审查/验证/根因结论", rootRule);
            StringAssert.Contains("工作流任务先加载", rootRule);
            StringAssert.DoesNotContain("Host Exec", rootRule);
            StringAssert.DoesNotContain("$CLI exec", rootRule);
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

            AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex = true;
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var workflowSkillPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "SKILL.md");
            var workflowSkill = File.ReadAllText(workflowSkillPath);
            StringAssert.Contains("如误入本 Skill", workflowSkill);
            StringAssert.Contains("references/project-workflow-preferences.md", workflowSkill);
            StringAssert.Contains("references/branch-selection.md", workflowSkill);
            StringAssert.Contains("references/harness-readiness-detail.md", workflowSkill);
            StringAssert.Contains("aibridge-code-index", workflowSkill);
            StringAssert.Contains("compile unity", workflowSkill);
            StringAssert.Contains("compile dotnet", workflowSkill);
            StringAssert.Contains("workflow import", workflowSkill);
            StringAssert.Contains("勿用 `$CLI harness status` 做 enablement/freshness 预检", workflowSkill);
            StringAssert.Contains("输出格式与 Skill 列出策略见 `branch-selection.md`", workflowSkill);
            StringAssert.DoesNotContain("【模式：<分支>】", workflowSkill);
            StringAssert.DoesNotContain("asset search/find --format paths", workflowSkill);
            StringAssert.DoesNotContain("使用 Skills：", workflowSkill);
            StringAssert.DoesNotContain("【模式：调试诊断分支】", workflowSkill);
            Assert.Less(workflowSkill.Length, 4000, "workflow skill should stay compact");

            var preferencesPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "project-workflow-preferences.md");
            var preferences = File.ReadAllText(preferencesPath);
            StringAssert.Contains("Code Index：", preferences);
            StringAssert.Contains("已启用", preferences);
            StringAssert.Contains("快速 C# 声明文件定位", preferences);
            Assert.Less(
                preferences.IndexOf("## 启用分支", StringComparison.Ordinal),
                preferences.IndexOf("- Settings Hash:", StringComparison.Ordinal));

            var branchSelectionPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "branch-selection.md");
            var branchSelection = File.ReadAllText(branchSelectionPath);
            StringAssert.Contains("【模式：<启用分支之一>】", branchSelection);
            StringAssert.Contains("aibridge-code-index、按需 aibridge-workflow-orchestration", branchSelection);
            StringAssert.Contains("项目侧能力以 Root Rule / preferences 为准", branchSelection);
            StringAssert.Contains("勿用 harness status 做 enablement 预检", branchSelection);
            StringAssert.Contains("Preflight / Skill 路由` 只做内部选路", branchSelection);
            StringAssert.Contains("需求讨论分支", branchSelection);
            StringAssert.Contains("方案写入规则见 `references/branches/requirements.md`", branchSelection);
            StringAssert.DoesNotContain("需求讨论模式", branchSelection);
            StringAssert.DoesNotContain("-> <当前步骤>", branchSelection);
            StringAssert.DoesNotContain("<当前步骤正在收集或产出的内容>", branchSelection);
            Assert.IsFalse(branchSelection.Contains("【任务分流步骤】"));
            Assert.IsFalse(branchSelection.Contains("【分支模式】"));
            Assert.IsFalse(branchSelection.Contains("【模式：Harness"));
            Assert.IsFalse(branchSelection.Contains("【入口：Preflight / Skill 路由】"));
            Assert.IsFalse(branchSelection.Contains("说明：<当前步骤正在收集或产出的内容>"));
            Assert.IsFalse(branchSelection.Contains("使用 Skills："));

            var reviewBranchPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "branches", "review.md");
            var reviewBranch = File.ReadAllText(reviewBranchPath);
            StringAssert.Contains("C# 声明定位", reviewBranch);
            StringAssert.DoesNotContain("宿主自带的文本搜索与文件读取工具", reviewBranch);
            StringAssert.DoesNotContain("text_index", reviewBranch);

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
        public void DisabledCodeIndexOmitsCodeIndexFromGeneratedWorkflowRules()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");

            AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex = false;
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var preferencesPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "project-workflow-preferences.md");
            var preferences = File.ReadAllText(preferencesPath);
            StringAssert.Contains("Code Index：已关闭", preferences);
            StringAssert.Contains("禁止加载 `aibridge-code-index`", preferences);
            StringAssert.DoesNotContain("优先用于快速 C# 声明文件定位", preferences);

            var branchSelectionPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "branch-selection.md");
            var branchSelection = File.ReadAllText(branchSelectionPath);
            StringAssert.DoesNotContain("aibridge-code-index", branchSelection);
            StringAssert.Contains("按需 aibridge-workflow-orchestration", branchSelection);
        }

        [Test]
        public void ProjectAgentsTemplateHasNoUnresolvedProjectTokens()
        {
            var template = RuleTemplateLoader.Load(ProjectRoot, "Templates~/ProjectRules/AGENTS.zh-CN.md");

            var rendered = SkillInstaller.ApplyProjectTemplateTokens(template.Body);

            Assert.IsFalse(rendered.Contains("{{UNITY_VERSION}}"));
            Assert.IsFalse(rendered.Contains("{{CSHARP_LANGUAGE_VERSION}}"));
            Assert.IsFalse(rendered.Contains("{{AIBRIDGE_CLI_PATH}}"));
            StringAssert.Contains(GetExpectedProjectCliPath(), rendered);
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

        private static string GetExpectedProjectCliPath()
        {
#if UNITY_EDITOR_WIN
            return "./.aibridge/cli/AIBridgeCLI.exe";
#else
            return "./.aibridge/cli/AIBridgeCLI";
#endif
        }
    }
}
