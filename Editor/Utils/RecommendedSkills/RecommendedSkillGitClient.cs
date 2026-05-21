using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AIBridge.Editor
{
    internal static class RecommendedSkillGitClient
    {
        private const int GitTimeoutMilliseconds = 120000;
        private const string CacheRootRelativePath = ".aibridge/skill-library/cache";
        private const string GitExecutableName = "git";
        private static string _gitExecutablePath = GitExecutableName;

        public static string GitExecutablePathForTests
        {
            get { return _gitExecutablePath; }
            set { _gitExecutablePath = string.IsNullOrWhiteSpace(value) ? GitExecutableName : value; }
        }

        public static string EnsureRepository(string projectRoot, RecommendedSkillRepository repository)
        {
            var cacheDirectory = GetRepositoryCacheDirectory(projectRoot, repository);
            if (Directory.Exists(Path.Combine(cacheDirectory, ".git")))
            {
                RunGit(cacheDirectory, "fetch --depth 1 origin " + Quote(repository.BranchOrTag));
                ConfigureSparseCheckout(cacheDirectory, repository);
                RunGit(cacheDirectory, "checkout --force FETCH_HEAD");
                return cacheDirectory;
            }

            var parent = Path.GetDirectoryName(cacheDirectory);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            RunGit(parent, "clone --depth 1 --sparse --branch " + Quote(repository.BranchOrTag) + " " + Quote(repository.RepositoryUrl) + " " + Quote(cacheDirectory));
            ConfigureSparseCheckout(cacheDirectory, repository);
            return cacheDirectory;
        }

        public static string GetCurrentCommit(string repositoryDirectory)
        {
            return RunGit(repositoryDirectory, "rev-parse HEAD").Trim();
        }

        private static string GetRepositoryCacheDirectory(string projectRoot, RecommendedSkillRepository repository)
        {
            var hash = ComputeHash(repository.RepositoryUrl + "#" + repository.BranchOrTag);
            return Path.Combine(projectRoot, CacheRootRelativePath.Replace('/', Path.DirectorySeparatorChar), repository.Id + "-" + hash);
        }

        private static void ConfigureSparseCheckout(string repositoryDirectory, RecommendedSkillRepository repository)
        {
            var sparsePaths = BuildSparseCheckoutPaths(repository);
            if (string.IsNullOrEmpty(sparsePaths))
            {
                return;
            }

            RunGit(repositoryDirectory, "sparse-checkout init --no-cone");
            RunGit(repositoryDirectory, "sparse-checkout set " + sparsePaths);
        }

        private static string BuildSparseCheckoutPaths(RecommendedSkillRepository repository)
        {
            var builder = new StringBuilder();
            AppendSparsePath(builder, repository.ManifestRelativePath);
            AppendSparsePath(builder, repository.ScanRootRelativePath);
            return builder.ToString().Trim();
        }

        private static void AppendSparsePath(StringBuilder builder, string path)
        {
            var normalized = NormalizeGitPath(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Quote(normalized));
        }

        private static string NormalizeGitPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var normalized = path.Trim().Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(2);
            }

            return normalized.Trim('/');
        }

        private static string ComputeHash(string value)
        {
            using (var sha1 = SHA1.Create())
            {
                var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder();
                for (var i = 0; i < 8 && i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static string RunGit(string workingDirectory, string arguments)
        {
            var process = new Process();
            process.StartInfo.FileName = _gitExecutablePath;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Directory.GetCurrentDirectory() : workingDirectory;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(BuildGitLaunchFailedMessage(ex), ex);
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(GitTimeoutMilliseconds))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                }

                throw new TimeoutException("Git command timed out: git " + arguments);
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Git command failed: git " + arguments + "\n" + error);
            }

            return output;
        }

        private static string BuildGitLaunchFailedMessage(Exception ex)
        {
            return AIBridgeEditorText.T(
                "Recommended Skill Library could not start Git. Please install Git and make sure the git command is available in PATH.\n\n" + ex.Message,
                "推荐 Skill 库无法启动 Git。请安装 Git，并确认 git 命令已加入 PATH。\n\n" + ex.Message);
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
