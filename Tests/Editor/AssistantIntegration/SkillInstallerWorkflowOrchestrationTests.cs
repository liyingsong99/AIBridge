using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class SkillInstallerWorkflowOrchestrationTests : AssistantIntegrationTestFixture
    {
        [Test]
        public void WorkflowOrchestrationSkillInstallsWithReferencesByDefault()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");
            AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex = false;

            var results = SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var skillDirectory = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-workflow-orchestration");
            Assert.IsTrue(File.Exists(Path.Combine(skillDirectory, "SKILL.md")));
            Assert.IsTrue(File.Exists(Path.Combine(skillDirectory, "references", "orchestration-patterns.md")));
            Assert.IsTrue(File.Exists(Path.Combine(skillDirectory, "references", "recipe-schema.md")));
            Assert.IsTrue(File.Exists(Path.Combine(skillDirectory, "references", "builtin-recipes.md")));
            Assert.IsFalse(Directory.GetFiles(skillDirectory, "*.meta", SearchOption.AllDirectories).Any());
            Assert.IsTrue(results.Single().AdditionalSkillFilePaths.Any(path => path.Replace('\\', '/').EndsWith("/aibridge-workflow-orchestration/SKILL.md")));
        }

        [Test]
        public void DevelopmentWorkflowRoutesToWorkflowOrchestrationSkill()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");

            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var workflowSkillPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "SKILL.md");
            var workflowSkill = File.ReadAllText(workflowSkillPath);
            StringAssert.Contains("aibridge-workflow-orchestration", workflowSkill);
            StringAssert.Contains("workflow recipe", workflowSkill);

            var orchestrationBranchPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "branches", "orchestration.md");
            var orchestrationBranch = File.ReadAllText(orchestrationBranchPath);
            StringAssert.Contains("加载 `aibridge-workflow-orchestration`", orchestrationBranch);
            StringAssert.Contains("多 Agent", orchestrationBranch);
        }

        [Test]
        public void WorkflowInstallsHarnessReadinessReference()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");

            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var readinessPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "harness-readiness.md");
            var detailPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "harness-readiness-detail.md");
            Assert.IsTrue(File.Exists(readinessPath));
            Assert.IsTrue(File.Exists(detailPath));

            var readiness = File.ReadAllText(readinessPath);
            StringAssert.Contains("Harness Preflight gate", readiness);
            StringAssert.Contains("compact-first", readiness);
            StringAssert.Contains("harness-readiness-detail.md", readiness);
            StringAssert.Contains("`fresh` 且不影响当前工具选择时", readiness);
            StringAssert.Contains("不要输出未经当前 compact status 支撑的 Code Index、Unity Editor、Runtime 等能力结论", readiness);
            StringAssert.DoesNotContain("最小探测矩阵", readiness);
            StringAssert.DoesNotContain("Fallback 规则", readiness);
            StringAssert.DoesNotContain("Resume 规则", readiness);
            Assert.IsFalse(readiness.Contains("【模式：Harness"));

            var detail = File.ReadAllText(detailPath);
            StringAssert.Contains("最小探测矩阵", detail);
            StringAssert.Contains("Fallback 规则", detail);
            StringAssert.Contains("Resume 规则", detail);
            StringAssert.Contains("EvidenceRef", detail);
            StringAssert.Contains("CommandEvidence", detail);
            StringAssert.Contains("快速定位 C# 声明文件或声明位置", detail);
            StringAssert.Contains("snapshot / name index 可用", detail);
            StringAssert.Contains("$CLI harness status --detail full", detail);
            StringAssert.Contains("$CLI harness status --include-snapshot true", detail);
            StringAssert.Contains("workflow finish --status passed", detail);
            Assert.IsFalse(detail.Contains("【模式：Harness"));
        }

        [Test]
        public void GeneratedWorkflowSkillFilesUseUtf8WithoutBom()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");

            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var aibridgeSkillPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge", "SKILL.md");
            var workflowSkillPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "SKILL.md");
            var preferencesPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "project-workflow-preferences.md");
            var branchSelectionPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "branch-selection.md");
            var readinessDetailPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "harness-readiness-detail.md");
            var branchManifestPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "implementation-branch.manifest.json");

            AssertNoUtf8Bom(aibridgeSkillPath);
            AssertNoUtf8Bom(workflowSkillPath);
            AssertNoUtf8Bom(preferencesPath);
            AssertNoUtf8Bom(branchSelectionPath);
            AssertNoUtf8Bom(readinessDetailPath);
            AssertNoUtf8Bom(branchManifestPath);
            AssertStartsWithFrontmatter(aibridgeSkillPath);
            AssertStartsWithFrontmatter(workflowSkillPath);
        }

        [Test]
        public void WorkflowInstallsImplementationBranchManifest()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");

            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var manifestPath = Path.Combine(ProjectRoot, ".codex", "skills", "aibridge-development-workflow", "references", "implementation-branch.manifest.json");
            Assert.IsTrue(File.Exists(manifestPath));

            var manifest = File.ReadAllText(manifestPath);
            StringAssert.Contains("\"branchId\": \"implementation\"", manifest);
            StringAssert.Contains("\"id\": \"implementation-locate\"", manifest);
            StringAssert.Contains("\"id\": \"implementation-verify\"", manifest);
            StringAssert.Contains("\"kind\": \"OptionalFlow\"", manifest);
        }

        private static void AssertNoUtf8Bom(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            Assert.IsFalse(hasBom, path);
        }

        private static void AssertStartsWithFrontmatter(string path)
        {
            var text = File.ReadAllText(path);
            Assert.IsTrue(text.StartsWith("---\n") || text.StartsWith("---\r\n"), path);
        }
    }
}
