using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    internal static class RecommendedSkillManifestParser
    {
        private const string SkillFileName = "SKILL.md";
        private const int DescriptionPreviewLength = 160;
        private static readonly Regex InvalidSkillNameChars = new Regex("[^a-z0-9-]+", RegexOptions.Compiled);

        public static List<RecommendedSkillInfo> LoadSkills(RecommendedSkillRepository repository, string repositoryDirectory, string commit)
        {
            var skills = LoadSkillsFromManifest(repository, repositoryDirectory, commit);
            if (skills.Count > 0)
            {
                return skills;
            }

            return ScanSkills(repository, repositoryDirectory, commit);
        }

        private static List<RecommendedSkillInfo> LoadSkillsFromManifest(RecommendedSkillRepository repository, string repositoryDirectory, string commit)
        {
            var result = new List<RecommendedSkillInfo>();
            var manifestPath = Path.Combine(repositoryDirectory, NormalizePath(repository.ManifestRelativePath));
            if (string.IsNullOrEmpty(repository.ManifestRelativePath) || !File.Exists(manifestPath))
            {
                return result;
            }

            var manifest = AIBridgeJson.DeserializeObject(File.ReadAllText(manifestPath));
            if (manifest == null)
            {
                return result;
            }

            object skillsValue;
            if (!manifest.TryGetValue("skills", out skillsValue))
            {
                return result;
            }

            foreach (var skillPath in ExtractSkillPaths(skillsValue))
            {
                AddSkillIfValid(result, repository, repositoryDirectory, commit, skillPath);
            }

            return result
                .GroupBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> ExtractSkillPaths(object skillsValue)
        {
            if (skillsValue is string skillRoot)
            {
                yield return skillRoot;
                yield break;
            }

            var list = skillsValue as List<object>;
            if (list == null)
            {
                yield break;
            }

            foreach (var item in list)
            {
                var path = item as string;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return path;
                    continue;
                }

                var itemMap = item as Dictionary<string, object>;
                if (itemMap == null)
                {
                    continue;
                }

                object pathValue;
                if (itemMap.TryGetValue("path", out pathValue) || itemMap.TryGetValue("source", out pathValue))
                {
                    var pathText = pathValue as string;
                    if (!string.IsNullOrWhiteSpace(pathText))
                    {
                        yield return pathText;
                    }
                }
            }
        }

        private static List<RecommendedSkillInfo> ScanSkills(RecommendedSkillRepository repository, string repositoryDirectory, string commit)
        {
            var result = new List<RecommendedSkillInfo>();
            var scanRoot = string.IsNullOrEmpty(repository.ScanRootRelativePath)
                ? repositoryDirectory
                : Path.Combine(repositoryDirectory, NormalizePath(repository.ScanRootRelativePath));

            if (!Directory.Exists(scanRoot))
            {
                return result;
            }

            foreach (var skillFile in Directory.GetFiles(scanRoot, SkillFileName, SearchOption.AllDirectories))
            {
                var skillDirectory = Path.GetDirectoryName(skillFile);
                if (string.IsNullOrEmpty(skillDirectory))
                {
                    continue;
                }

                var relativePath = MakeRelativePath(repositoryDirectory, skillDirectory);
                AddSkillIfValid(result, repository, repositoryDirectory, commit, relativePath);
            }

            return result
                .GroupBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddSkillIfValid(List<RecommendedSkillInfo> result, RecommendedSkillRepository repository, string repositoryDirectory, string commit, string sourceRelativePath)
        {
            var normalizedPath = NormalizeManifestPath(sourceRelativePath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return;
            }

            var sourceDirectory = Path.Combine(repositoryDirectory, NormalizePath(normalizedPath));
            if (File.Exists(sourceDirectory) && string.Equals(Path.GetFileName(sourceDirectory), SkillFileName, StringComparison.OrdinalIgnoreCase))
            {
                sourceDirectory = Path.GetDirectoryName(sourceDirectory);
            }

            if (string.IsNullOrEmpty(sourceDirectory) || !IsInsideDirectory(repositoryDirectory, sourceDirectory))
            {
                return;
            }

            var skillFilePath = Path.Combine(sourceDirectory, SkillFileName);
            if (string.IsNullOrEmpty(sourceDirectory) || !File.Exists(skillFilePath))
            {
                return;
            }

            var frontmatter = ReadFrontmatter(skillFilePath);
            var name = GetString(frontmatter, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Path.GetFileName(sourceDirectory);
            }

            result.Add(new RecommendedSkillInfo
            {
                Name = NormalizeSkillName(name),
                DisplayName = name,
                Description = TrimDescription(GetString(frontmatter, "description")),
                SourceRelativePath = MakeRelativePath(repositoryDirectory, sourceDirectory),
                RepositoryId = repository.Id,
                RepositoryUrl = repository.RepositoryUrl,
                BranchOrTag = repository.BranchOrTag,
                Commit = commit
            });
        }

        private static Dictionary<string, string> ReadFrontmatter(string skillFilePath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = File.ReadAllLines(skillFilePath);
            if (lines.Length == 0 || lines[0].Trim() != "---")
            {
                return result;
            }

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Trim() == "---")
                {
                    break;
                }

                var separatorIndex = line.IndexOf(':');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separatorIndex).Trim();
                var value = line.Substring(separatorIndex + 1).Trim().Trim('"');
                result[key] = value;
            }

            return result;
        }

        private static string GetString(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static string TrimDescription(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return normalized.Length <= DescriptionPreviewLength
                ? normalized
                : normalized.Substring(0, DescriptionPreviewLength) + "...";
        }

        private static string NormalizeSkillName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var normalized = name.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
            normalized = InvalidSkillNameChars.Replace(normalized, "-").Trim('-');
            while (normalized.Contains("--"))
            {
                normalized = normalized.Replace("--", "-");
            }

            return normalized;
        }

        private static string NormalizeManifestPath(string path)
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

        private static string NormalizePath(string path)
        {
            return NormalizeManifestPath(path).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string MakeRelativePath(string rootDirectory, string fullPath)
        {
            var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var path = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (path.Length <= root.Length)
            {
                return string.Empty;
            }

            return path.Substring(root.Length + 1).Replace('\\', '/');
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
