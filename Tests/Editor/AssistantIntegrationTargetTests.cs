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
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("cursor");
            ClearAssistantSelections();
            AIBridgeProjectSettings.Instance.SkillRootDirectory = AIBridgeProjectSettings.DefaultSkillRootDirectory;
            AIBridgeProjectSettings.Instance.EditorLanguage = AIBridgeEditorLanguage.English;
            AIBridgeProjectSettings.Instance.EditorLanguageInitialized = true;
        }

        [TearDown]
        public void TearDown()
        {
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("codex");
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("claude");
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("cursor");
            ClearAssistantSelections();
            AIBridgeProjectSettings.Instance.SkillRootDirectory = AIBridgeProjectSettings.DefaultSkillRootDirectory;
            AIBridgeProjectSettings.Instance.EditorLanguage = AIBridgeEditorLanguage.English;
            AIBridgeProjectSettings.Instance.EditorLanguageInitialized = true;
            RecommendedSkillGitClient.GitExecutablePathForTests = _originalGitExecutablePath;

            if (Directory.Exists(_projectRoot))
            {
                Directory.Delete(_projectRoot, true);
            }
        }

        [Test]
        public void CodexSkillRootUsesToolDefaultWhenAgentsDirectoryExists()
        {
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".agents"));
            var target = CreateTarget("codex", ".codex/skills/aibridge");

            Assert.AreEqual(".codex/skills", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".codex/skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".codex/skills/aibridge/SKILL.md", target.GetResolvedSkillFileRelativePath(_projectRoot));
            Assert.AreEqual(".codex/skills/aibridge-prefab-patch/SKILL.md", target.GetResolvedSiblingSkillFileRelativePath(_projectRoot, "aibridge-prefab-patch"));
        }

        [Test]
        public void CodexSkillRootUsesToolDefaultWhenAgentsDirectoryIsMissing()
        {
            var target = CreateTarget("codex", ".codex/skills/aibridge");

            Assert.AreEqual(".codex/skills", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".codex/skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
        }

        [Test]
        public void LegacyPerAssistantSkillRootDoesNotOverrideToolDefaultDirectory()
        {
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".agents"));
            AIBridgeProjectSettings.Instance.SetAssistantSkillRootDirectory("codex", ".legacy-skills");
            var target = CreateTarget("codex", ".codex/skills/aibridge");

            Assert.AreEqual(".codex/skills", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".codex/skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".codex/skills/aibridge-prefab-patch/SKILL.md", target.GetResolvedSiblingSkillFileRelativePath(_projectRoot, "aibridge-prefab-patch"));
        }

        [Test]
        public void ProjectSkillRootDirectoryCanUseCustomDirectory()
        {
            AIBridgeProjectSettings.Instance.SkillRootDirectory = ".skill";
            var target = CreateTarget("claude", ".claude/skills/aibridge");

            Assert.AreEqual(".skill", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".skill/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".skill/aibridge-prefab-patch/SKILL.md", target.GetResolvedSiblingSkillFileRelativePath(_projectRoot, "aibridge-prefab-patch"));
        }

        [Test]
        public void AssistantTargetsUseSharedRootRuleTemplate()
        {
            var targets = AssistantIntegrationRegistry.GetTargets();

            Assert.IsTrue(targets.All(target => target.RootRuleTemplateRelativePath == "Templates~/Rules/AIBridge.RootRule.md"));
        }

        [Test]
        public void SharedRootRuleTemplateRoutesThroughWorkflowWithoutSkillIndex()
        {
            var template = RuleTemplateLoader.Load(_projectRoot, "Templates~/Rules/AIBridge.RootRule.md");

            StringAssert.Contains("{{WORKFLOW_SKILL_ENTRY}}", template.Body);
            StringAssert.Contains("{{SKILL_ROOT_RULE}}", template.Body);
            StringAssert.Contains("{{UNITY_VERSION_RULE}}", template.Body);
            StringAssert.Contains("{{CSHARP_VERSION_RULE}}", template.Body);
            Assert.IsFalse(template.Body.Contains("{{SKILL_INDEX}}"));
        }

        [Test]
        public void ProjectAgentsTemplateVersionTokensAreRendered()
        {
            var template = RuleTemplateLoader.Load(_projectRoot, "Templates~/ProjectRules/AGENTS.zh-CN.md");

            var rendered = SkillInstaller.ApplyProjectVersionTokens(template.Body);

            Assert.IsFalse(rendered.Contains("{{UNITY_VERSION}}"));
            Assert.IsFalse(rendered.Contains("{{CSHARP_LANGUAGE_VERSION}}"));
            StringAssert.Contains(UnityEngine.Application.unityVersion, rendered);
            StringAssert.Contains("C# ", rendered);
        }

        [Test]
        public void NonCodexSkillRootUsesToolDefaultDirectory()
        {
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".agents"));
            var target = CreateTarget("claude", ".claude/skills/aibridge");

            Assert.AreEqual(".claude/skills", target.GetResolvedSkillRootDirectoryRelativePath(_projectRoot));
            Assert.AreEqual(".claude/skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(_projectRoot));
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

        private static void ClearAssistantSelections()
        {
            var targets = AssistantIntegrationRegistry.GetTargets();
            foreach (var target in targets)
            {
                AIBridgeProjectSettings.Instance.ClearAssistantSelection(target.Id);
            }
        }

        private static int CountSelectedTargets(System.Collections.Generic.Dictionary<string, bool> selections)
        {
            var count = 0;
            foreach (var selection in selections)
            {
                if (selection.Value)
                {
                    count++;
                }
            }

            return count;
        }

        [Test]
        public void EmptyProjectDefaultsToSingleCodexSelection()
        {
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(_projectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["codex"]);
            Assert.IsFalse(selections["claude"]);
        }

        [Test]
        public void ClaudeRootRuleDefaultsToSingleClaudeSelection()
        {
            File.WriteAllText(Path.Combine(_projectRoot, "CLAUDE.md"), "# Claude");
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(_projectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["claude"]);
            Assert.IsFalse(selections["codex"]);
        }

        [Test]
        public void AgentsRootRuleDefaultsToSingleCodexSelection()
        {
            File.WriteAllText(Path.Combine(_projectRoot, "AGENTS.md"), "# Agents");
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(_projectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["codex"]);
            Assert.IsFalse(selections["claude"]);
        }

        [Test]
        public void CodexPluginDirectoryDefaultsToSingleCodexSelection()
        {
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".codex-plugin"));
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(_projectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["codex"]);
            Assert.IsFalse(selections["claude"]);
        }

        [Test]
        public void CursorPluginDirectoryDefaultsToSingleCursorSelection()
        {
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".cursor-plugin"));
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(_projectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["cursor"]);
            Assert.IsFalse(selections["codex"]);
        }

        [Test]
        public void CodexWinsWhenClaudeAndAgentsRootRulesBothExist()
        {
            File.WriteAllText(Path.Combine(_projectRoot, "CLAUDE.md"), "# Claude");
            File.WriteAllText(Path.Combine(_projectRoot, "AGENTS.md"), "# Agents");
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(_projectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["codex"]);
            Assert.IsFalse(selections["claude"]);
        }

        [Test]
        public void LegacySharedSkillDirectoryDoesNotDetectEveryAssistant()
        {
            var sharedSkillDirectory = Path.Combine(_projectRoot, ".skills", "aibridge");
            Directory.CreateDirectory(sharedSkillDirectory);
            File.WriteAllText(Path.Combine(sharedSkillDirectory, "SKILL.md"), "# AIBridge");
            var targets = AssistantIntegrationRegistry.GetTargets();

            var selections = AssistantIntegrationSelectionSettings.LoadSelections(_projectRoot, targets);

            Assert.AreEqual(1, CountSelectedTargets(selections));
            Assert.IsTrue(selections["codex"]);
            Assert.IsFalse(selections["claude"]);
            Assert.IsFalse(AssistantIntegrationDetector.Detect(_projectRoot, targets.First(target => target.Id == "claude")).IsDetected);
            Assert.IsFalse(AssistantIntegrationDetector.Detect(_projectRoot, targets.First(target => target.Id == "codex")).IsDetected);
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
        public void DefaultRecommendedSkillRepositoriesUseSuperpowersFirst()
        {
            var repositories = RecommendedSkillRepositories.GetDefaultRepositories();

            Assert.AreEqual("obra-superpowers", repositories.First().Id);
        }

        [Test]
        public void RepositoryWebUrlRemovesGitSuffix()
        {
            var url = AIBridgeSettingsWindow.GetRepositoryWebUrl("https://github.com/obra/superpowers.git");

            Assert.AreEqual("https://github.com/obra/superpowers", url);
        }

        [Test]
        public void DefaultRecommendedSkillRepositoriesUseLocalizedDescriptions()
        {
            AIBridgeProjectSettings.Instance.EditorLanguage = AIBridgeEditorLanguage.English;
            var englishRepositories = RecommendedSkillRepositories.GetDefaultRepositories();

            StringAssert.Contains("workflow Skills", englishRepositories.First().Description);

            AIBridgeProjectSettings.Instance.EditorLanguage = AIBridgeEditorLanguage.SimplifiedChinese;
            var simplifiedChineseRepositories = RecommendedSkillRepositories.GetDefaultRepositories();

            StringAssert.Contains("工作流 Skills", simplifiedChineseRepositories.First().Description);
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
        public void SkillPluginAdapterGeneratesRootCodexPluginIndex()
        {
            AIBridgeProjectSettings.Instance.SkillRootDirectory = ".skill";

            SkillPluginAdapter.GenerateAll(_projectRoot);

            var codexPluginJson = File.ReadAllText(Path.Combine(_projectRoot, ".codex-plugin", "plugin.json"));
            StringAssert.Contains("\"name\": \"aibridge-skills\"", codexPluginJson);
            StringAssert.Contains("\"skills\": \"./.skill/\"", codexPluginJson);
            Assert.IsFalse(Directory.Exists(Path.Combine(_projectRoot, "plugins", "aibridge-skills")));
        }

        [Test]
        public void SkillPluginAdapterDoesNotGeneratePluginIndexForDefaultToolDirectories()
        {
            SkillPluginAdapter.GenerateAll(_projectRoot);

            Assert.IsFalse(File.Exists(Path.Combine(_projectRoot, ".claude-plugin", "plugin.json")));
            Assert.IsFalse(File.Exists(Path.Combine(_projectRoot, ".codex-plugin", "plugin.json")));
            Assert.IsFalse(File.Exists(Path.Combine(_projectRoot, ".cursor-plugin", "plugin.json")));
        }

        [Test]
        public void SkillPluginAdapterGeneratesRootCursorPluginIndexForCustomDirectory()
        {
            AIBridgeProjectSettings.Instance.SkillRootDirectory = ".skill";

            SkillPluginAdapter.GenerateAll(_projectRoot);

            var cursorPluginJson = File.ReadAllText(Path.Combine(_projectRoot, ".cursor-plugin", "plugin.json"));
            StringAssert.Contains("\"name\": \"aibridge-skills\"", cursorPluginJson);
            StringAssert.Contains("\"displayName\": \"AIBridge Skills\"", cursorPluginJson);
            StringAssert.Contains("\"skills\": \"./.skill/\"", cursorPluginJson);
        }

        [Test]
        public void SkillPluginAdapterAppendsCustomSkillDirectoryToExistingPluginIndex()
        {
            AIBridgeProjectSettings.Instance.SkillRootDirectory = ".skill";
            var manifestDirectory = Path.Combine(_projectRoot, ".codex-plugin");
            Directory.CreateDirectory(manifestDirectory);
            File.WriteAllText(
                Path.Combine(manifestDirectory, "plugin.json"),
                "{ \"name\": \"existing\", \"skills\": \"./vendor-skills/\", \"custom\": true }");

            SkillPluginAdapter.GenerateAll(_projectRoot);

            var codexPluginJson = File.ReadAllText(Path.Combine(manifestDirectory, "plugin.json"));
            StringAssert.Contains("\"name\": \"existing\"", codexPluginJson);
            StringAssert.Contains("\"custom\": true", codexPluginJson);
            StringAssert.Contains("\"./vendor-skills/\"", codexPluginJson);
            StringAssert.Contains("\"./.skill/\"", codexPluginJson);
        }

        [Test]
        public void SkillPluginAdapterCanRemovePreviousCustomSkillDirectoryFromExistingPluginIndex()
        {
            var manifestDirectory = Path.Combine(_projectRoot, ".codex-plugin");
            Directory.CreateDirectory(manifestDirectory);
            File.WriteAllText(
                Path.Combine(manifestDirectory, "plugin.json"),
                "{ \"name\": \"existing\", \"skills\": [\"./vendor-skills/\", \"./.skill/\"], \"custom\": true }");
            var codexTarget = AssistantIntegrationRegistry.GetTargets().First(target => target.Id == "codex");

            SkillPluginAdapter.CleanupSkillRootForTargets(_projectRoot, new[] { codexTarget }, ".skill");

            var codexPluginJson = File.ReadAllText(Path.Combine(manifestDirectory, "plugin.json"));
            StringAssert.Contains("\"./vendor-skills/\"", codexPluginJson);
            Assert.IsFalse(codexPluginJson.Contains("\"./.skill/\""));
        }

        [Test]
        public void SkillPluginAdapterCleanupRemovesOnlyAIBridgePluginIndex()
        {
            AIBridgeProjectSettings.Instance.SkillRootDirectory = ".skills";
            var marketplaceDirectory = Path.Combine(_projectRoot, ".agents", "plugins");
            Directory.CreateDirectory(marketplaceDirectory);
            File.WriteAllText(
                Path.Combine(marketplaceDirectory, "marketplace.json"),
                "{ \"name\": \"existing\", \"plugins\": [{ \"name\": \"other\", \"source\": { \"source\": \"local\", \"path\": \"./plugins/other\" } }, { \"name\": \"aibridge-skills\", \"source\": { \"source\": \"local\", \"path\": \"./plugins/aibridge-skills\" } }] }");
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".codex-plugin"));
            File.WriteAllText(
                Path.Combine(_projectRoot, ".codex-plugin", "plugin.json"),
                "{ \"name\": \"aibridge-skills\", \"skills\": \"./.skills/\" }");
            Directory.CreateDirectory(Path.Combine(_projectRoot, "plugins", "aibridge-skills", ".codex-plugin"));
            File.WriteAllText(
                Path.Combine(_projectRoot, "plugins", "aibridge-skills", ".codex-plugin", "plugin.json"),
                "{ \"name\": \"aibridge-skills\" }");
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".skills", "aibridge"));
            File.WriteAllText(Path.Combine(_projectRoot, ".skills", "aibridge", "SKILL.md"), "# Shared AIBridge");
            var codexTarget = AssistantIntegrationRegistry.GetTargets().First(target => target.Id == "codex");

            SkillPluginAdapter.CleanupForTargets(_projectRoot, new[] { codexTarget });

            var marketplaceJson = File.ReadAllText(Path.Combine(marketplaceDirectory, "marketplace.json"));
            StringAssert.Contains("\"name\": \"other\"", marketplaceJson);
            Assert.IsFalse(marketplaceJson.Contains("\"name\": \"aibridge-skills\""));
            Assert.IsFalse(Directory.Exists(Path.Combine(_projectRoot, ".codex-plugin")));
            Assert.IsFalse(Directory.Exists(Path.Combine(_projectRoot, "plugins", "aibridge-skills")));
            Assert.IsTrue(File.Exists(Path.Combine(_projectRoot, ".skills", "aibridge", "SKILL.md")));
        }

        [Test]
        public void RecommendedSkillRemoveDeletesDirectoryAndInstallRecord()
        {
            AssistantIntegrationSelectionSettings.SetSelected("codex", true);
            var skillDirectory = Path.Combine(_projectRoot, ".codex", "skills", "tdd");
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
                InstallRootDirectory = ".codex/skills",
                InstalledAtUtcTicks = 1
            });

            var result = RecommendedSkillInstaller.Remove(_projectRoot, new RecommendedSkillInfo { Name = "tdd" });

            Assert.IsTrue(result.Success);
            Assert.IsFalse(Directory.Exists(skillDirectory));
            Assert.IsNull(RecommendedSkillInstallRegistry.Find(_projectRoot, "tdd"));
        }

        [Test]
        public void RecommendedSkillInstallerUsesSelectedToolSkillRoots()
        {
            AssistantIntegrationSelectionSettings.SetSelected("codex", true);
            AssistantIntegrationSelectionSettings.SetSelected("cursor", true);
            var repositoryRoot = Path.Combine(_projectRoot, "repo");
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

            var result = RecommendedSkillInstaller.Install(_projectRoot, repository, skill, true);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(File.Exists(Path.Combine(_projectRoot, ".codex", "skills", "tdd", "SKILL.md")));
            Assert.IsTrue(File.Exists(Path.Combine(_projectRoot, ".cursor", "skills", "tdd", "SKILL.md")));
            Assert.IsFalse(Directory.Exists(Path.Combine(_projectRoot, ".skills", "tdd")));
        }

        [Test]
        public void RecommendedSkillInstallerFallsBackToCodexSkillRootWhenNoToolIsSelected()
        {
            foreach (var target in AssistantIntegrationRegistry.GetTargets())
            {
                AssistantIntegrationSelectionSettings.SetSelected(target.Id, false);
            }

            var roots = RecommendedSkillInstaller.GetSelectedInstallRootDirectories(_projectRoot);

            CollectionAssert.AreEqual(new[] { ".codex/skills" }, roots);
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

        private static void RunGit(string workingDirectory, string arguments)
        {
            var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.Arguments = arguments;
            process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode);
        }
    }
}
