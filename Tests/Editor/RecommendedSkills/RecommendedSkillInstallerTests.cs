using System.IO;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class RecommendedSkillInstallerTests : RecommendedSkillsTestFixture
    {
        [Test]
        public void RemoveDeletesDirectoryAndInstallRecord()
        {
            AssistantIntegrationSelectionSettings.SetSelected("codex", true);
            var skillDirectory = Path.Combine(ProjectRoot, ".codex", "skills", "tdd");
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: tdd\n---\n# TDD");
            RecommendedSkillInstallRegistry.Upsert(ProjectRoot, new InstalledSkillRecord
            {
                Name = "tdd",
                RepositoryId = "test",
                RepositoryUrl = "https://example.com/repo.git",
                SourceRelativePath = "skills/tdd",
                BranchOrTag = "main",
                Commit = "abc123",
                InstallRootDirectory = ".codex/skills",
                InstalledAtUtcTicks = 1
            });

            var result = RecommendedSkillInstaller.Remove(ProjectRoot, new RecommendedSkillInfo { Name = "tdd" });

            Assert.IsTrue(result.Success);
            Assert.IsFalse(Directory.Exists(skillDirectory));
            Assert.IsNull(RecommendedSkillInstallRegistry.Find(ProjectRoot, "tdd"));
        }

        [Test]
        public void InstallerUsesSelectedToolSkillRoots()
        {
            AssistantIntegrationSelectionSettings.SetSelected("codex", true);
            AssistantIntegrationSelectionSettings.SetSelected("cursor", true);
            var repositoryRoot = Path.Combine(ProjectRoot, "repo");
            var sourceSkillDirectory = Path.Combine(repositoryRoot, "skills", "tdd");
            Directory.CreateDirectory(sourceSkillDirectory);
            File.WriteAllText(Path.Combine(sourceSkillDirectory, "SKILL.md"), "---\nname: tdd\n---\n# TDD");
            RunGit(repositoryRoot, "init");
            RunGit(repositoryRoot, "checkout -B main");
            RunGit(repositoryRoot, "config user.email test@example.com");
            RunGit(repositoryRoot, "config user.name Test");
            RunGit(repositoryRoot, "add .");
            RunGit(repositoryRoot, "commit -m init");
            var repository = new RecommendedSkillRepository
            {
                Id = "local",
                RepositoryUrl = repositoryRoot,
                BranchOrTag = "main",
                ScanRootRelativePath = "skills"
            };
            var skill = new RecommendedSkillInfo
            {
                Name = "tdd",
                SourceRelativePath = "skills/tdd"
            };

            var result = RecommendedSkillInstaller.Install(ProjectRoot, repository, skill, true);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(File.Exists(Path.Combine(ProjectRoot, ".agents", "skills", "tdd", "SKILL.md")));
            Assert.IsTrue(File.Exists(Path.Combine(ProjectRoot, ".cursor", "skills", "tdd", "SKILL.md")));
            Assert.IsFalse(Directory.Exists(Path.Combine(ProjectRoot, ".skills", "tdd")));
        }

        [Test]
        public void InstallerFallsBackToCodexSkillRootWhenNoToolIsSelected()
        {
            foreach (var target in AssistantIntegrationRegistry.GetTargets())
            {
                AssistantIntegrationSelectionSettings.SetSelected(target.Id, false);
            }

            var roots = RecommendedSkillInstaller.GetSelectedInstallRootDirectories(ProjectRoot);

            CollectionAssert.AreEqual(new[] { ".agents/skills" }, roots);
        }

        [Test]
        public void RefreshReportsMissingGitWithoutRawProcessError()
        {
            RecommendedSkillGitClient.GitExecutablePathForTests = "aibridge_missing_git_executable";
            var repository = CreateRecommendedSkillRepository();

            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                RecommendedSkillInstaller.RefreshRepository(ProjectRoot, repository));

            StringAssert.Contains("Git", ex.Message);
            StringAssert.Contains("PATH", ex.Message);
        }

        [Test]
        public void InstallReturnsFailureWhenGitIsMissing()
        {
            RecommendedSkillGitClient.GitExecutablePathForTests = "aibridge_missing_git_executable";
            var repository = CreateRecommendedSkillRepository();
            var skill = new RecommendedSkillInfo
            {
                Name = "tdd",
                SourceRelativePath = "skills/tdd"
            };

            var result = RecommendedSkillInstaller.Install(ProjectRoot, repository, skill, true);

            Assert.IsFalse(result.Success);
            StringAssert.Contains("Git", result.Message);
            StringAssert.Contains("PATH", result.Message);
        }
    }
}
