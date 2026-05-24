using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIBridge.Editor
{
    internal static class RecommendedSkillInstaller
    {
        private const string SkillFileName = "SKILL.md";

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

                var skillRootDirectory = AIBridgeProjectSettings.Instance.SkillRootDirectory;
                var targetDirectory = Path.Combine(projectRoot, skillRootDirectory, skill.Name);
                if (Directory.Exists(targetDirectory) && !overwrite)
                {
                    return new RecommendedSkillInstallResult
                    {
                        Success = false,
                        InstalledDirectory = targetDirectory,
                        Message = AIBridgeEditorText.T("Target skill already exists.", "目标 Skill 已存在。")
                    };
                }

                if (Directory.Exists(targetDirectory))
                {
                    Directory.Delete(targetDirectory, true);
                }

                CopyDirectory(sourceDirectory, targetDirectory);
                RecommendedSkillInstallRegistry.Upsert(projectRoot, new InstalledSkillRecord
                {
                    Name = skill.Name,
                    RepositoryId = repository.Id,
                    RepositoryUrl = repository.RepositoryUrl,
                    SourceRelativePath = skill.SourceRelativePath,
                    BranchOrTag = repository.BranchOrTag,
                    Commit = commit,
                    InstalledAtUtcTicks = DateTime.UtcNow.Ticks
                });

                SkillPluginAdapter.GenerateSelected(projectRoot);
                return new RecommendedSkillInstallResult
                {
                    Success = true,
                    InstalledDirectory = targetDirectory,
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

                var targetDirectory = Path.Combine(projectRoot, AIBridgeProjectSettings.Instance.SkillRootDirectory, skill.Name);
                if (!IsInsideDirectory(Path.Combine(projectRoot, AIBridgeProjectSettings.Instance.SkillRootDirectory), targetDirectory))
                {
                    return new RecommendedSkillInstallResult
                    {
                        Success = false,
                        Message = AIBridgeEditorText.T("Invalid install path.", "安装路径无效。")
                    };
                }

                if (Directory.Exists(targetDirectory))
                {
                    Directory.Delete(targetDirectory, true);
                }

                RecommendedSkillInstallRegistry.Remove(projectRoot, skill.Name);
                SkillPluginAdapter.GenerateSelected(projectRoot);
                return new RecommendedSkillInstallResult
                {
                    Success = true,
                    InstalledDirectory = targetDirectory,
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
            foreach (var skill in skills)
            {
                var targetDirectory = Path.Combine(projectRoot, AIBridgeProjectSettings.Instance.SkillRootDirectory, skill.Name);
                if (!Directory.Exists(targetDirectory))
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
