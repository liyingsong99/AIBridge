using System.IO;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public abstract class RecommendedSkillsTestFixture
    {
        private string _originalGitExecutablePath;

        protected string ProjectRoot { get; private set; }

        private AIBridgeEditorLanguage _originalLanguage;
        private bool _originalLanguageInitialized;
        private bool _originalOnboardingCompleted;
        private int _originalOnboardingVersion;
        private string _originalSkillRootDirectory;
        private bool _originalCodeIndexEnabled;
        private System.Collections.Generic.Dictionary<string, bool> _originalSelections;

        [SetUp]
        public void SetUp()
        {
            ProjectRoot = Path.Combine(Path.GetTempPath(), "AIBridgeRecommendedSkillTests_" + Path.GetRandomFileName());
            Directory.CreateDirectory(ProjectRoot);
            _originalGitExecutablePath = RecommendedSkillGitClient.GitExecutablePathForTests;
            RecommendedSkillGitClient.GitExecutablePathForTests = "git";
            CaptureProjectSettings();
            ResetProjectSettings();
        }

        [TearDown]
        public void TearDown()
        {
            ResetProjectSettings();
            RestoreProjectSettings();
            RecommendedSkillGitClient.GitExecutablePathForTests = _originalGitExecutablePath;

            if (Directory.Exists(ProjectRoot))
            {
                DeleteTemporaryDirectory(ProjectRoot);
            }
        }

        private void CaptureProjectSettings()
        {
            var settings = AIBridgeProjectSettings.Instance;
            _originalLanguage = settings.EditorLanguage;
            _originalLanguageInitialized = settings.EditorLanguageInitialized;
            _originalOnboardingCompleted = settings.OnboardingCompleted;
            _originalOnboardingVersion = settings.OnboardingVersion;
            _originalSkillRootDirectory = settings.SkillRootDirectory;
            _originalCodeIndexEnabled = settings.CodeIndex.EnableCodeIndex;
            _originalSelections = new System.Collections.Generic.Dictionary<string, bool>();
            foreach (var target in AssistantIntegrationRegistry.GetTargets())
            {
                bool selected;
                if (settings.TryGetAssistantSelection(target.Id, out selected))
                {
                    _originalSelections[target.Id] = selected;
                }
            }
        }

        private void RestoreProjectSettings()
        {
            var settings = AIBridgeProjectSettings.Instance;
            settings.EditorLanguage = _originalLanguage;
            settings.EditorLanguageInitialized = _originalLanguageInitialized;
            settings.OnboardingCompleted = _originalOnboardingCompleted;
            settings.OnboardingVersion = _originalOnboardingVersion;
            settings.SkillRootDirectory = _originalSkillRootDirectory;
            settings.CodeIndex.EnableCodeIndex = _originalCodeIndexEnabled;
            foreach (var target in AssistantIntegrationRegistry.GetTargets())
            {
                bool selected;
                if (_originalSelections.TryGetValue(target.Id, out selected))
                {
                    settings.SetAssistantSelection(target.Id, selected);
                }
                else
                {
                    settings.ClearAssistantSelection(target.Id);
                }
            }

            settings.SaveSettings();
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

        private static void DeleteTemporaryDirectory(string path)
        {
            ClearReadOnlyAttributes(path);
            Directory.Delete(path, true);
        }

        private static void ClearReadOnlyAttributes(string path)
        {
            // Git object files can be read-only on Windows, which makes Directory.Delete fail in Unity's test runner.
            foreach (var filePath in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(filePath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
                }
            }

            foreach (var directoryPath in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(directoryPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(directoryPath, attributes & ~FileAttributes.ReadOnly);
                }
            }

            var rootAttributes = File.GetAttributes(path);
            if ((rootAttributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, rootAttributes & ~FileAttributes.ReadOnly);
            }
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
