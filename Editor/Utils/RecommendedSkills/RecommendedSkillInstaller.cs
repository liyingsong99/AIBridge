using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIBridge.Editor
{
    internal static class RecommendedSkillInstaller
    {
        private const string SkillFileName = "SKILL.md";

        public static string GetPrimaryInstallRootDirectory(string projectRoot)
        {
            var roots = GetSelectedInstallRootDirectories(projectRoot);
            return roots.Count > 0 ? roots[0] : GetFallbackInstallRootDirectory(projectRoot);
        }

        public static List<string> GetSelectedInstallRootDirectories(string projectRoot)
        {
            var result = new List<string>();
            var targets = SkillInstaller.GetSelectedTargetsForPluginGeneration(projectRoot);
            foreach (var target in targets)
            {
                if (target == null || !target.SupportsSkillDirectory)
                {
                    continue;
                }

                var root = target.GetResolvedSkillRootDirectoryRelativePath(projectRoot);
                AddUniqueRoot(result, root);
            }

            if (result.Count == 0)
            {
                AddUniqueRoot(result, GetFallbackInstallRootDirectory(projectRoot));
            }

            return result;
        }

        public static List<RecommendedSkillInfo> RefreshRepository(string projectRoot, RecommendedSkillRepository repository)
        {
            var repositoryDirectory = RecommendedSkillGitClient.EnsureRepository(projectRoot, repository);
            var commit = RecommendedSkillGitClient.GetCurrentCommit(repositoryDirectory);
            var skills = RecommendedSkillManifestParser.LoadSkills(repository, repositoryDirectory, commit);
            ApplyInstallStates(projectRoot, skills);
            return skills;
        }

        public static RecommendedSkillInstallResult Install(string projectRoot, RecommendedSkillRepository repository, RecommendedSkillInfo skill, bool overwrite)
        {
            try
            {
                if (string.IsNullOrEmpty(skill.Name) || skill.Name.Contains("/") || skill.Name.Contains("\\") || skill.Name.Contains(".."))
                {
                    return new RecommendedSkillInstallResult
                    {
                        Success = false,
                        Message = AIBridgeEditorText.T("Invalid skill name.", "Skill 名称无效。")
                    };
                }

                var repositoryDirectory = RecommendedSkillGitClient.EnsureRepository(projectRoot, repository);
                var commit = RecommendedSkillGitClient.GetCurrentCommit(repositoryDirectory);
                var sourceDirectory = Path.Combine(repositoryDirectory, skill.SourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!IsInsideDirectory(repositoryDirectory, sourceDirectory) || !File.Exists(Path.Combine(sourceDirectory, SkillFileName)))
                {
                    return new RecommendedSkillInstallResult
                    {
                        Success = false,
                        Message = AIBridgeEditorText.T("Skill source is missing SKILL.md.", "Skill 源目录缺少 SKILL.md。")
                    };
                }

                var installRootDirectories = GetSelectedInstallRootDirectories(projectRoot);
                var primaryTargetDirectory = Path.Combine(projectRoot, installRootDirectories[0], skill.Name);
                if (installRootDirectories.Exists(root => Directory.Exists(Path.Combine(projectRoot, root, skill.Name))) && !overwrite)
                {
                    return new RecommendedSkillInstallResult
                    {
                        Success = false,
                        InstalledDirectory = primaryTargetDirectory,
                        Message = AIBridgeEditorText.T("Target skill already exists.", "目标 Skill 已存在。")
                    };
                }

                foreach (var installRootDirectory in installRootDirectories)
                {
                    var targetDirectory = Path.Combine(projectRoot, installRootDirectory, skill.Name);
                    if (Directory.Exists(targetDirectory))
                    {
                        Directory.Delete(targetDirectory, true);
                    }

                    CopyDirectory(sourceDirectory, targetDirectory);
                }

                RecommendedSkillInstallRegistry.Upsert(projectRoot, new InstalledSkillRecord
                {
                    Name = skill.Name,
                    RepositoryId = repository.Id,
                    RepositoryUrl = repository.RepositoryUrl,
                    SourceRelativePath = skill.SourceRelativePath,
                    BranchOrTag = repository.BranchOrTag,
                    Commit = commit,
                    InstallRootDirectory = string.Join(";", installRootDirectories.ToArray()),
                    InstalledAtUtcTicks = DateTime.UtcNow.Ticks
                });

                SkillPluginAdapter.GenerateSelected(projectRoot);
                return new RecommendedSkillInstallResult
                {
                    Success = true,
                    InstalledDirectory = string.Join(";", installRootDirectories.ConvertAll(root => Path.Combine(projectRoot, root, skill.Name)).ToArray()),
                    Message = AIBridgeEditorText.T("Skill installed.", "Skill 已安装。")
                };
            }
            catch (Exception ex)
            {
                return new RecommendedSkillInstallResult
                {
                    Success = false,
                    Message = ex.Message
                };
            }
        }

        public static RecommendedSkillInstallResult Remove(string projectRoot, RecommendedSkillInfo skill)
        {
            try
            {
                if (string.IsNullOrEmpty(skill.Name) || skill.Name.Contains("/") || skill.Name.Contains("\\") || skill.Name.Contains(".."))
                {
                    return new RecommendedSkillInstallResult
                    {
                        Success = false,
                        Message = AIBridgeEditorText.T("Invalid skill name.", "Skill 名称无效。")
                    };
                }

                var installRootDirectories = GetInstallRootDirectoriesForRemoval(projectRoot, skill.Name);
                var primaryTargetDirectory = Path.Combine(projectRoot, installRootDirectories[0], skill.Name);
                if (installRootDirectories.Exists(root => !IsInsideDirectory(Path.Combine(projectRoot, root), Path.Combine(projectRoot, root, skill.Name))))
                {
                    return new RecommendedSkillInstallResult
                    {
                        Success = false,
                        Message = AIBridgeEditorText.T("Invalid install path.", "安装路径无效。")
                    };
                }

                foreach (var installRootDirectory in installRootDirectories)
                {
                    var targetDirectory = Path.Combine(projectRoot, installRootDirectory, skill.Name);
                    if (Directory.Exists(targetDirectory))
                    {
                        Directory.Delete(targetDirectory, true);
                    }
                }

                RecommendedSkillInstallRegistry.Remove(projectRoot, skill.Name);
                SkillPluginAdapter.GenerateSelected(projectRoot);
                return new RecommendedSkillInstallResult
                {
                    Success = true,
                    InstalledDirectory = primaryTargetDirectory,
                    Message = AIBridgeEditorText.T("Skill removed.", "Skill 已移除。")
                };
            }
            catch (Exception ex)
            {
                return new RecommendedSkillInstallResult
                {
                    Success = false,
                    Message = ex.Message
                };
            }
        }

        public static void ApplyInstallStates(string projectRoot, IEnumerable<RecommendedSkillInfo> skills)
        {
            var installRootDirectories = GetSelectedInstallRootDirectories(projectRoot);
            foreach (var skill in skills)
            {
                if (!installRootDirectories.Exists(root => Directory.Exists(Path.Combine(projectRoot, root, skill.Name))))
                {
                    skill.InstallState = RecommendedSkillInstallState.NotInstalled;
                    continue;
                }

                var record = RecommendedSkillInstallRegistry.Find(projectRoot, skill.Name);
                skill.InstallState = record != null
                    && string.Equals(record.RepositoryUrl, skill.RepositoryUrl, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(record.Commit, skill.Commit, StringComparison.OrdinalIgnoreCase)
                        ? RecommendedSkillInstallState.Installed
                        : RecommendedSkillInstallState.UpdateAvailable;
            }
        }

        private static List<string> GetInstallRootDirectoriesForRemoval(string projectRoot, string skillName)
        {
            var result = new List<string>();
            var record = RecommendedSkillInstallRegistry.Find(projectRoot, skillName);
            if (record != null && !string.IsNullOrEmpty(record.InstallRootDirectory))
            {
                foreach (var root in record.InstallRootDirectory.Split(';'))
                {
                    AddUniqueRoot(result, root);
                }
            }

            foreach (var root in GetSelectedInstallRootDirectories(projectRoot))
            {
                AddUniqueRoot(result, root);
            }

            AddUniqueRoot(result, AIBridgeProjectSettings.LegacySharedSkillRootDirectory);
            return result;
        }

        private static string GetFallbackInstallRootDirectory(string projectRoot)
        {
            var customSkillRootDirectory = AIBridgeProjectSettings.Instance.SkillRootDirectory;
            if (!string.IsNullOrEmpty(customSkillRootDirectory))
            {
                return customSkillRootDirectory;
            }

            foreach (var target in AssistantIntegrationRegistry.GetTargets())
            {
                if (target != null
                    && string.Equals(target.Id, "codex", StringComparison.OrdinalIgnoreCase)
                    && target.SupportsSkillDirectory)
                {
                    return target.GetResolvedSkillRootDirectoryRelativePath(projectRoot);
                }
            }

            foreach (var target in AssistantIntegrationRegistry.GetTargets())
            {
                if (target != null && target.SupportsSkillDirectory)
                {
                    return target.GetResolvedSkillRootDirectoryRelativePath(projectRoot);
                }
            }

            return AIBridgeProjectSettings.LegacySharedSkillRootDirectory;
        }

        private static void AddUniqueRoot(List<string> roots, string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var normalized = root.Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(normalized))
            {
                return;
            }

            if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part == ".."))
            {
                return;
            }

            for (var i = 0; i < roots.Count; i++)
            {
                if (string.Equals(roots[i], normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            roots.Add(normalized);
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var filePath in Directory.GetFiles(sourceDir))
            {
                if (ShouldSkipFile(filePath))
                {
                    continue;
                }

                var targetFile = Path.Combine(targetDir, Path.GetFileName(filePath));
                File.Copy(filePath, targetFile, true);
            }

            foreach (var childDir in Directory.GetDirectories(sourceDir))
            {
                if (ShouldSkipDirectory(childDir))
                {
                    continue;
                }

                var targetChildDir = Path.Combine(targetDir, Path.GetFileName(childDir));
                CopyDirectory(childDir, targetChildDir);
            }
        }

        private static bool ShouldSkipFile(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            return fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldSkipDirectory(string directoryPath)
        {
            var name = Path.GetFileName(directoryPath);
            return string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInsideDirectory(string rootDirectory, string fullPath)
        {
            var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var path = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
