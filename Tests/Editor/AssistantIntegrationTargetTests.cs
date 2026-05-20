using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class AssistantIntegrationTargetTests
    {
        private string _projectRoot;

        [SetUp]
        public void SetUp()
        {
            _projectRoot = Path.Combine(Path.GetTempPath(), "AIBridgeTargetTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_projectRoot);
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

            Assert.AreEqual("skills", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual("skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
            Assert.AreEqual("skills/aibridge/SKILL.md", target.GetResolvedSkillFileRelativePath(_projectRoot));
            Assert.AreEqual("skills/aibridge-prefab-patch/SKILL.md", target.GetResolvedSiblingSkillFileRelativePath(_projectRoot, "aibridge-prefab-patch"));
        }

        [Test]
        public void CodexSkillRootUsesSharedSkillsWhenAgentsDirectoryIsMissing()
        {
            var target = CreateTarget("codex", ".codex/skills/aibridge");

            Assert.AreEqual("skills", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual("skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
        }

        [Test]
        public void LegacyPerAssistantSkillRootDoesNotOverrideSharedDirectory()
        {
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".agents"));
            AIBridgeProjectSettings.Instance.SetAssistantSkillRootDirectory("codex", ".legacy-skills");
            var target = CreateTarget("codex", ".codex/skills/aibridge");

            Assert.AreEqual("skills", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual("skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
            Assert.AreEqual("skills/aibridge-prefab-patch/SKILL.md", target.GetResolvedSiblingSkillFileRelativePath(_projectRoot, "aibridge-prefab-patch"));
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

            Assert.AreEqual("skills", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual("skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
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
    }
}
