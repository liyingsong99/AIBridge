using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class AssistantIntegrationTargetTests
    {
        private string _projectRoot;
        private string _originalGitExecutablePath;

        [SetUp]
        public void SetUp()
        {
            _projectRoot = Path.Combine(Path.GetTempPath(), "AIBridgeTargetTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_projectRoot);
            _originalGitExecutablePath = RecommendedSkillGitClient.GitExecutablePathForTests;
            RecommendedSkillGitClient.GitExecutablePathForTests = "git";
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("codex");
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("claude");
            AIBridgeProjectSettings.Instance.SkillRootDirectory = AIBridgeProjectSettings.DefaultSkillRootDirectory;
        }

        [TearDown]
        public void TearDown()
        {
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("codex");
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("claude");
            AIBridgeProjectSettings.Instance.SkillRootDirectory = AIBridgeProjectSettings.DefaultSkillRootDirectory;
            RecommendedSkillGitClient.GitExecutablePathForTests = _originalGitExecutablePath;

            if (Directory.Exists(_projectRoot))
            {
                Directory.Delete(_projectRoot, true);
            }
        }

        [Test]
        public void CodexSkillRootUsesSharedSkillsWhenAgentsDirectoryExists()
        {
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".agents"));
            var target = CreateTarget("codex", ".codex/skills/aibridge");

            Assert.AreEqual(".skills", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".skills/aibridge/SKILL.md", target.GetResolvedSkillFileRelativePath(_projectRoot));
            Assert.AreEqual(".skills/aibridge-prefab-patch/SKILL.md", target.GetResolvedSiblingSkillFileRelativePath(_projectRoot, "aibridge-prefab-patch"));
        }

        [Test]
        public void CodexSkillRootUsesSharedSkillsWhenAgentsDirectoryIsMissing()
        {
            var target = CreateTarget("codex", ".codex/skills/aibridge");

            Assert.AreEqual(".skills", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
        }

        [Test]
        public void LegacyPerAssistantSkillRootDoesNotOverrideSharedDirectory()
        {
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".agents"));
            AIBridgeProjectSettings.Instance.SetAssistantSkillRootDirectory("codex", ".legacy-skills");
            var target = CreateTarget("codex", ".codex/skills/aibridge");

            Assert.AreEqual(".skills", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".skills/aibridge-prefab-patch/SKILL.md", target.GetResolvedSiblingSkillFileRelativePath(_projectRoot, "aibridge-prefab-patch"));
        }

        [Test]
        public void ProjectSkillRootDirectoryCanUseCustomSharedDirectory()
        {
            AIBridgeProjectSettings.Instance.SkillRootDirectory = ".skill";
            var target = CreateTarget("claude", ".claude/skills/aibridge");

            Assert.AreEqual(".skill", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".skill/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".skill/aibridge-prefab-patch/SKILL.md", target.GetResolvedSiblingSkillFileRelativePath(_projectRoot, "aibridge-prefab-patch"));
        }

        [Test]
        public void NonCodexSkillRootUsesSharedSkillsDirectory()
        {
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".agents"));
            var target = CreateTarget("claude", ".claude/skills/aibridge");

            Assert.AreEqual(".skills", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
        }

        private static AssistantIntegrationTarget CreateTarget(string id, string skillDirectoryRelativePath)
        {
            return new AssistantIntegrationTarget
            {
                Id = id,
                SupportsSkillDirectory = true,
                SkillDirectoryRelativePath = skillDirectoryRelativePath,
                SkillFileName = "SKILL.md"
            };
        }

        [Test]
        public void RecommendedSkillManifestParserReadsPluginSkillList()
        {
            var repositoryRoot = Path.Combine(_projectRoot, "repo");
            var skillDirectory = Path.Combine(repositoryRoot, "skills", "engineering", "tdd");
            var manifestDirectory = Path.Combine(repositoryRoot, ".claude-plugin");
            Directory.CreateDirectory(skillDirectory);
            Directory.CreateDirectory(manifestDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: tdd\ndescription: Test-driven development workflow.\n---\n# TDD");
            File.WriteAllText(Path.Combine(manifestDirectory, "plugin.json"), "{ \"skills\": [\"./skills/engineering/tdd\"] }");

            var repository = new RecommendedSkillRepository
            {
                Id = "test",
                RepositoryUrl = "https://example.com/repo.git",
                BranchOrTag = "main",
                ManifestRelativePath = ".claude-plugin/plugin.json",
                ScanRootRelativePath = "skills"
            };

            var skills = RecommendedSkillManifestParser.LoadSkills(repository, repositoryRoot, "abc123");

            Assert.AreEqual(1, skills.Count);
            Assert.AreEqual("tdd", skills[0].Name);
            Assert.AreEqual("skills/engineering/tdd", skills[0].SourceRelativePath);
            Assert.AreEqual("abc123", skills[0].Commit);
        }

        [Test]
        public void RecommendedSkillManifestParserReadsMarketplacePluginSkillList()
        {
            var repositoryRoot = Path.Combine(_projectRoot, "repo");
            var skillDirectory = Path.Combine(repositoryRoot, "skills", "docx");
            var manifestDirectory = Path.Combine(repositoryRoot, ".claude-plugin");
            Directory.CreateDirectory(skillDirectory);
            Directory.CreateDirectory(manifestDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: docx\ndescription: Word document workflow.\n---\n# DOCX");
            File.WriteAllText(Path.Combine(manifestDirectory, "marketplace.json"), "{ \"plugins\": [{ \"name\": \"document-skills\", \"source\": \"./\", \"skills\": [\"./skills/docx\"] }] }");

            var repository = new RecommendedSkillRepository
            {
                Id = "test",
                RepositoryUrl = "https://example.com/repo.git",
                BranchOrTag = "main",
                ManifestRelativePath = ".claude-plugin/marketplace.json",
                ScanRootRelativePath = "skills"
            };

            var skills = RecommendedSkillManifestParser.LoadSkills(repository, repositoryRoot, "abc123");

            Assert.AreEqual(1, skills.Count);
            Assert.AreEqual("docx", skills[0].Name);
            Assert.AreEqual("skills/docx", skills[0].SourceRelativePath);
        }

        [Test]
        public void DefaultRecommendedSkillRepositoriesIncludeAnthropicSkills()
        {
            var repositories = RecommendedSkillRepositories.GetDefaultRepositories();

            Assert.IsTrue(repositories.Any(repository => repository.Id == "anthropic-skills"
                && repository.RepositoryUrl == "https://github.com/anthropics/skills.git"
                && repository.ManifestRelativePath == ".claude-plugin/marketplace.json"));
        }

        [Test]
        public void DefaultRecommendedSkillRepositoriesIncludeSuperpowersSkills()
        {
            var repositories = RecommendedSkillRepositories.GetDefaultRepositories();

            Assert.IsTrue(repositories.Any(repository => repository.Id == "obra-superpowers"
                && repository.RepositoryUrl == "https://github.com/obra/superpowers.git"
                && repository.ManifestRelativePath == ".claude-plugin/plugin.json"
                && repository.ScanRootRelativePath == "skills"));
        }

        [Test]
        public void RecommendedSkillManifestParserScansWhenManifestHasNoSkillList()
        {
            var repositoryRoot = Path.Combine(_projectRoot, "repo");
            var skillDirectory = Path.Combine(repositoryRoot, "skills", "test-driven-development");
            var manifestDirectory = Path.Combine(repositoryRoot, ".claude-plugin");
            Directory.CreateDirectory(skillDirectory);
            Directory.CreateDirectory(manifestDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: test-driven-development\ndescription: TDD workflow.\n---\n# TDD");
            File.WriteAllText(Path.Combine(manifestDirectory, "plugin.json"), "{ \"name\": \"superpowers\", \"description\": \"Core skills library\" }");

            var repository = new RecommendedSkillRepository
            {
                Id = "test",
                RepositoryUrl = "https://example.com/repo.git",
                BranchOrTag = "main",
                ManifestRelativePath = ".claude-plugin/plugin.json",
                ScanRootRelativePath = "skills"
            };

            var skills = RecommendedSkillManifestParser.LoadSkills(repository, repositoryRoot, "abc123");

            Assert.AreEqual(1, skills.Count);
            Assert.AreEqual("test-driven-development", skills[0].Name);
            Assert.AreEqual("skills/test-driven-development", skills[0].SourceRelativePath);
        }

        [Test]
        public void RecommendedSkillManifestParserScansWhenManifestMissing()
        {
            var repositoryRoot = Path.Combine(_projectRoot, "repo");
            var skillDirectory = Path.Combine(repositoryRoot, "skills", "diagnose");
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: diagnose\ndescription: Diagnose problems.\n---\n# Diagnose");

            var repository = new RecommendedSkillRepository
            {
                Id = "test",
                RepositoryUrl = "https://example.com/repo.git",
                BranchOrTag = "main",
                ManifestRelativePath = ".claude-plugin/plugin.json",
                ScanRootRelativePath = "skills"
            };

            var skills = RecommendedSkillManifestParser.LoadSkills(repository, repositoryRoot, "abc123");

            Assert.AreEqual("diagnose", skills.Single().Name);
        }

        [Test]
        public void RecommendedSkillManifestParserSkipsPathOutsideRepository()
        {
            var repositoryRoot = Path.Combine(_projectRoot, "repo");
            var externalSkillDirectory = Path.Combine(_projectRoot, "external-skill");
            var manifestDirectory = Path.Combine(repositoryRoot, ".claude-plugin");
            Directory.CreateDirectory(externalSkillDirectory);
            Directory.CreateDirectory(manifestDirectory);
            File.WriteAllText(Path.Combine(externalSkillDirectory, "SKILL.md"), "---\nname: unsafe\ndescription: Unsafe path.\n---\n# Unsafe");
            File.WriteAllText(Path.Combine(manifestDirectory, "plugin.json"), "{ \"skills\": [\"../external-skill\"] }");

            var repository = new RecommendedSkillRepository
            {
                Id = "test",
                RepositoryUrl = "https://example.com/repo.git",
                BranchOrTag = "main",
                ManifestRelativePath = ".claude-plugin/plugin.json",
                ScanRootRelativePath = "skills"
            };

            var skills = RecommendedSkillManifestParser.LoadSkills(repository, repositoryRoot, "abc123");

            Assert.AreEqual(0, skills.Count);
        }

        [Test]
        public void SkillPluginAdapterMergesCodexMarketplaceEntries()
        {
            AIBridgeProjectSettings.Instance.SkillRootDirectory = ".skill";
            var marketplaceDirectory = Path.Combine(_projectRoot, ".agents", "plugins");
            Directory.CreateDirectory(marketplaceDirectory);
            File.WriteAllText(
                Path.Combine(marketplaceDirectory, "marketplace.json"),
                "{ \"name\": \"existing\", \"plugins\": [{ \"name\": \"other\", \"source\": { \"source\": \"local\", \"path\": \"./plugins/other\" }, \"policy\": { \"installation\": \"AVAILABLE\", \"authentication\": \"ON_INSTALL\" }, \"category\": \"Productivity\" }] }");

            SkillPluginAdapter.GenerateAll(_projectRoot);

            var marketplaceJson = File.ReadAllText(Path.Combine(marketplaceDirectory, "marketplace.json"));
            StringAssert.Contains("\"name\": \"other\"", marketplaceJson);
            StringAssert.Contains("\"name\": \"aibridge-skills\"", marketplaceJson);

            var codexPluginJson = File.ReadAllText(Path.Combine(_projectRoot, "plugins", "aibridge-skills", ".codex-plugin", "plugin.json"));
            StringAssert.Contains("\"skills\": \"./../../.skill/\"", codexPluginJson);
        }

        [Test]
        public void RecommendedSkillRemoveDeletesDirectoryAndInstallRecord()
        {
            var skillDirectory = Path.Combine(_projectRoot, ".skills", "tdd");
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: tdd\n---\n# TDD");
            RecommendedSkillInstallRegistry.Upsert(_projectRoot, new InstalledSkillRecord
            {
                Name = "tdd",
                RepositoryId = "test",
                RepositoryUrl = "https://example.com/repo.git",
                SourceRelativePath = "skills/tdd",
                BranchOrTag = "main",
                Commit = "abc123",
                InstalledAtUtcTicks = 1
            });

            var result = RecommendedSkillInstaller.Remove(_projectRoot, new RecommendedSkillInfo { Name = "tdd" });

            Assert.IsTrue(result.Success);
            Assert.IsFalse(Directory.Exists(skillDirectory));
            Assert.IsNull(RecommendedSkillInstallRegistry.Find(_projectRoot, "tdd"));
        }

        [Test]
        public void RecommendedSkillRefreshReportsMissingGitWithoutRawProcessError()
        {
            RecommendedSkillGitClient.GitExecutablePathForTests = "aibridge_missing_git_executable";
            var repository = CreateRecommendedSkillRepository();

            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                RecommendedSkillInstaller.RefreshRepository(_projectRoot, repository));

            StringAssert.Contains("Git", ex.Message);
            StringAssert.Contains("PATH", ex.Message);
        }

        [Test]
        public void RecommendedSkillInstallReturnsFailureWhenGitIsMissing()
        {
            RecommendedSkillGitClient.GitExecutablePathForTests = "aibridge_missing_git_executable";
            var repository = CreateRecommendedSkillRepository();
            var skill = new RecommendedSkillInfo
            {
                Name = "tdd",
                SourceRelativePath = "skills/tdd"
            };

            var result = RecommendedSkillInstaller.Install(_projectRoot, repository, skill, true);

            Assert.IsFalse(result.Success);
            StringAssert.Contains("Git", result.Message);
            StringAssert.Contains("PATH", result.Message);
        }

        private static RecommendedSkillRepository CreateRecommendedSkillRepository()
        {
            return new RecommendedSkillRepository
            {
                Id = "test",
                RepositoryUrl = "https://example.com/repo.git",
                BranchOrTag = "main",
                ManifestRelativePath = ".claude-plugin/plugin.json",
                ScanRootRelativePath = "skills"
            };
        }
    }
}
