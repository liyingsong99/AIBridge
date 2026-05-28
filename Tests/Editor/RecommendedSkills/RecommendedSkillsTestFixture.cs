using System.IO;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public abstract class RecommendedSkillsTestFixture
    {
        private string _originalGitExecutablePath;

        protected string ProjectRoot { get; private set; }

        [SetUp]
        public void SetUp()
        {
            ProjectRoot = Path.Combine(Path.GetTempPath(), "AIBridgeRecommendedSkillTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(ProjectRoot);
            _originalGitExecutablePath = RecommendedSkillGitClient.GitExecutablePathForTests;
            RecommendedSkillGitClient.GitExecutablePathForTests = "git";
            ResetProjectSettings();
        }

        [TearDown]
        public void TearDown()
        {
            ResetProjectSettings();
            RecommendedSkillGitClient.GitExecutablePathForTests = _originalGitExecutablePath;

            if (Directory.Exists(ProjectRoot))
            {
                Directory.Delete(ProjectRoot, true);
            }
        }

        internal static RecommendedSkillRepository CreateRecommendedSkillRepository()
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

        protected static void RunGit(string workingDirectory, string arguments)
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

        private static void ResetProjectSettings()
        {
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("codex");
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("claude");
            AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory("cursor");

            var targets = AssistantIntegrationRegistry.GetTargets();
            foreach (var target in targets)
            {
                AIBridgeProjectSettings.Instance.ClearAssistantSelection(target.Id);
            }

            AIBridgeProjectSettings.Instance.SkillRootDirectory = AIBridgeProjectSettings.DefaultSkillRootDirectory;
            AIBridgeProjectSettings.Instance.EditorLanguage = AIBridgeEditorLanguage.English;
            AIBridgeProjectSettings.Instance.EditorLanguageInitialized = true;
            AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex = AIBridgeProjectSettings.DefaultCodeIndexEnabled;
        }
    }
}
