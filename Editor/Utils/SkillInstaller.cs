using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    [Serializable]
    internal sealed class PackagedSkillManifest
    {
        public int version = 1;
        public PackagedSkillTemplateEntry[] skills;
    }

    [Serializable]
    internal sealed class PackagedSkillTemplateEntry
    {
        public string id;
        public string displayName;
        public string source;
        public string summary;
        public string targetDirName;
        public bool entry;
        public bool installByDefault = true;

        public string GetTargetDirectoryName()
        {
            return string.IsNullOrEmpty(targetDirName) ? id : targetDirName;
        }
    }

    /// <summary>
    /// Automatically installs the AIBridge skill documentation to the project's .claude/skills directory.
    /// This allows Claude Code to discover and use the installed AIBridge skills.
    /// </summary>
    [InitializeOnLoad]
    public static class SkillInstaller
    {
        private const string SKILL_FILE_NAME = "SKILL.md";
        private const string SKILLS_MANIFEST_FILE_NAME = "manifest.json";
        private const string PACKAGE_NAME = "cn.lys.aibridge";
        private const string CLI_CACHE_FOLDER = "AIBridgeCache/CLI";
        private const string LEGACY_SKILL_FOLDER = "Skill~";
        private const string PACKAGED_SKILLS_FOLDER = "Skills~";
        private const string DEFAULT_PRIMARY_SKILL_ID = "aibridge";
        private const string CLAUDE_SKILLS_ROOT = "/.claude/skills";

        private static readonly string[] CLI_FILES = new[]
        {
            "AIBridgeCLI.dll",
            "AIBridgeCLI.deps.json",
            "AIBridgeCLI.runtimeconfig.json",
            "AIBridgeCLI.pdb",
            "Newtonsoft.Json.dll",
            "AIBridgeCLI"
        };

        static SkillInstaller()
        {
            EditorApplication.delayCall += InstallSkillIfNeeded;
        }

        private static string GetPlatformRID()
        {
#if UNITY_EDITOR_WIN
            return "win-x64";
#elif UNITY_EDITOR_OSX
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                   System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64";
#elif UNITY_EDITOR_LINUX
            return "linux-x64";
#else
            return "win-x64";
#endif
        }

        private static string GetCliExecutableName()
        {
#if UNITY_EDITOR_WIN
            return "AIBridgeCLI.exe";
#else
            return "AIBridgeCLI";
#endif
        }

        private static void InstallSkillIfNeeded()
        {
            try
            {
                var projectRoot = GetProjectRoot();
                CopyCliToCacheIfNeeded(projectRoot);
                var results = InstallAssistantIntegrations(projectRoot);
                LogResults(results);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"[SkillInstaller] Failed to install skill documentation: {ex.Message}");
            }
        }

        private static void CopyCliToCacheIfNeeded(string projectRoot)
        {
            var platformRID = GetPlatformRID();
            var cliExeName = GetCliExecutableName();
            var targetCliDir = Path.Combine(projectRoot, CLI_CACHE_FOLDER);
            var targetCliExe = Path.Combine(targetCliDir, cliExeName);

            var sourceCliDir = GetSourceCliDirectory(platformRID);
            if (string.IsNullOrEmpty(sourceCliDir))
            {
                sourceCliDir = GetSourceCliDirectory(null);
            }

            if (string.IsNullOrEmpty(sourceCliDir))
            {
                AIBridgeLogger.LogWarning("[SkillInstaller] Source CLI directory not found");
                return;
            }

            var sourceCliExe = Path.Combine(sourceCliDir, cliExeName);
            if (!File.Exists(sourceCliExe))
            {
                AIBridgeLogger.LogWarning($"[SkillInstaller] Source CLI executable not found: {sourceCliExe}");
                return;
            }

            var needsCopy = !File.Exists(targetCliExe);
            if (!needsCopy)
            {
                var sourceTime = File.GetLastWriteTimeUtc(sourceCliExe);
                var targetTime = File.GetLastWriteTimeUtc(targetCliExe);
                needsCopy = sourceTime > targetTime;
            }

            if (!needsCopy)
            {
                return;
            }

            if (!Directory.Exists(targetCliDir))
            {
                Directory.CreateDirectory(targetCliDir);
            }

            var copiedCount = 0;

            try
            {
                File.Copy(sourceCliExe, targetCliExe, true);
                copiedCount++;
#if !UNITY_EDITOR_WIN
                try
                {
                    var process = new System.Diagnostics.Process();
                    process.StartInfo.FileName = "chmod";
                    process.StartInfo.Arguments = $"+x \"{targetCliExe}\"";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    process.WaitForExit();
                }
                catch
                {
                }
#endif
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning($"[SkillInstaller] Failed to copy {cliExeName}: {ex.Message}");
            }

            foreach (var fileName in CLI_FILES)
            {
                var sourceFile = Path.Combine(sourceCliDir, fileName);
                var targetFile = Path.Combine(targetCliDir, fileName);

                if (!File.Exists(sourceFile))
                {
                    continue;
                }

                try
                {
                    File.Copy(sourceFile, targetFile, true);
                    copiedCount++;
                }
                catch (Exception ex)
                {
                    AIBridgeLogger.LogWarning($"[SkillInstaller] Failed to copy {fileName}: {ex.Message}");
                }
            }

            if (copiedCount > 0)
            {
                AIBridgeLogger.LogInfo($"[SkillInstaller] Copied {copiedCount} CLI files to: {targetCliDir}");
            }
        }

        private static string GetSourceCliDirectory(string platformRID)
        {
            var projectRoot = GetProjectRoot();
            var subPath = string.IsNullOrEmpty(platformRID) ? "CLI" : $"CLI/{platformRID}";

            var directPath = Path.Combine(projectRoot, "Packages", PACKAGE_NAME, "Tools~", subPath);
            if (Directory.Exists(directPath))
            {
                return directPath;
            }

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PACKAGE_NAME}");
            if (packageInfo != null)
            {
                var resolvedPath = Path.Combine(packageInfo.resolvedPath, "Tools~", subPath);
                if (Directory.Exists(resolvedPath))
                {
                    return resolvedPath;
                }
            }

            return null;
        }

        private static string GetProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        private static string ResolvePackageRelativePath(string projectRoot, string relativePath)
        {
            var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var directPath = Path.Combine(projectRoot, "Packages", PACKAGE_NAME, normalizedRelativePath);
            if (File.Exists(directPath) || Directory.Exists(directPath))
            {
                return directPath;
            }

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PACKAGE_NAME}");
            if (packageInfo != null)
            {
                var resolvedPath = Path.Combine(packageInfo.resolvedPath, normalizedRelativePath);
                if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
                {
                    return resolvedPath;
                }
            }

            return null;
        }

        private static string GetLegacySourceSkillPath(string projectRoot)
        {
            return ResolvePackageRelativePath(projectRoot, LEGACY_SKILL_FOLDER + "/" + SKILL_FILE_NAME);
        }

        private static string GetSourceSkillsManifestPath(string projectRoot)
        {
            return ResolvePackageRelativePath(projectRoot, PACKAGED_SKILLS_FOLDER + "/" + SKILLS_MANIFEST_FILE_NAME);
        }

        private static PackagedSkillManifest LoadSkillManifest(string projectRoot)
        {
            var manifestPath = GetSourceSkillsManifestPath(projectRoot);
            if (!string.IsNullOrEmpty(manifestPath) && File.Exists(manifestPath))
            {
                var manifestContent = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = JsonUtility.FromJson<PackagedSkillManifest>(manifestContent);
                ValidateManifest(manifest);
                return manifest;
            }

            var legacySkillPath = GetLegacySourceSkillPath(projectRoot);
            if (string.IsNullOrEmpty(legacySkillPath) || !File.Exists(legacySkillPath))
            {
                return null;
            }

            return new PackagedSkillManifest
            {
                version = 1,
                skills = new[]
                {
                    new PackagedSkillTemplateEntry
                    {
                        id = DEFAULT_PRIMARY_SKILL_ID,
                        displayName = "AIBridge",
                        source = LEGACY_SKILL_FOLDER + "/" + SKILL_FILE_NAME,
                        summary = "Core Unity Editor automation skill.",
                        entry = true,
                        installByDefault = true
                    }
                }
            };
        }

        private static void ValidateManifest(PackagedSkillManifest manifest)
        {
            if (manifest == null)
            {
                throw new InvalidOperationException("Skill manifest is empty or invalid JSON.");
            }

            if (manifest.skills == null || manifest.skills.Length == 0)
            {
                throw new InvalidOperationException("Skill manifest does not declare any installable skills.");
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targetDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasEntrySkill = false;
            var entryCount = 0;

            foreach (var skill in manifest.skills)
            {
                if (skill == null)
                {
                    throw new InvalidOperationException("Skill manifest contains an empty skill entry.");
                }

                if (string.IsNullOrWhiteSpace(skill.id))
                {
                    throw new InvalidOperationException("Skill manifest contains an entry without an id.");
                }

                if (string.IsNullOrWhiteSpace(skill.source))
                {
                    throw new InvalidOperationException($"Skill manifest entry '{skill.id}' is missing a source path.");
                }

                if (!ids.Add(skill.id))
                {
                    throw new InvalidOperationException($"Skill manifest contains a duplicate id: {skill.id}");
                }

                ValidateManifestPathSegment(skill.id, "id");

                var effectiveTargetDirectory = skill.GetTargetDirectoryName();
                ValidateManifestPathSegment(effectiveTargetDirectory, $"targetDirName for '{skill.id}'");
                if (!targetDirectories.Add(effectiveTargetDirectory))
                {
                    throw new InvalidOperationException($"Skill manifest contains a duplicate target directory: {effectiveTargetDirectory}");
                }

                ValidateManifestSourcePath(skill.source, skill.id);

                if (skill.entry)
                {
                    hasEntrySkill = true;
                    entryCount++;
                }
            }

            if (!hasEntrySkill || entryCount != 1)
            {
                throw new InvalidOperationException("Skill manifest must declare exactly one entry skill.");
            }
        }

        private static void ValidateManifestPathSegment(string value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Skill manifest {fieldName} cannot be empty.");
            }

            if (value.Contains("..") || value.Contains("/") || value.Contains("\\") || Path.IsPathRooted(value))
            {
                throw new InvalidOperationException($"Skill manifest {fieldName} must be a single safe directory segment: {value}");
            }
        }

        private static void ValidateManifestSourcePath(string source, string skillId)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new InvalidOperationException($"Skill manifest entry '{skillId}' is missing a source path.");
            }

            if (Path.IsPathRooted(source) || source.Contains(".."))
            {
                throw new InvalidOperationException($"Skill manifest entry '{skillId}' has an invalid source path: {source}");
            }
        }

        private static List<PackagedSkillTemplateEntry> GetInstallableSkills(PackagedSkillManifest manifest)
        {
            var installableSkills = new List<PackagedSkillTemplateEntry>();
            if (manifest == null || manifest.skills == null)
            {
                return installableSkills;
            }

            foreach (var skill in manifest.skills)
            {
                if (skill != null && skill.installByDefault)
                {
                    installableSkills.Add(skill);
                }
            }

            return installableSkills;
        }

        private static PackagedSkillTemplateEntry GetPrimarySkill(PackagedSkillManifest manifest, AssistantIntegrationTarget target)
        {
            if (manifest == null || manifest.skills == null)
            {
                return null;
            }

            foreach (var skill in manifest.skills)
            {
                if (skill != null && !string.IsNullOrEmpty(target.PrimarySkillId) && string.Equals(skill.id, target.PrimarySkillId, StringComparison.OrdinalIgnoreCase))
                {
                    return skill;
                }
            }

            foreach (var skill in manifest.skills)
            {
                if (skill != null && skill.entry)
                {
                    return skill;
                }
            }

            return null;
        }

        private static string ResolveSkillSourcePath(string projectRoot, PackagedSkillTemplateEntry skill)
        {
            return ResolvePackageRelativePath(projectRoot, skill.source);
        }

        private static string GetCanonicalSkillDocPath(string skillId)
        {
            return CLAUDE_SKILLS_ROOT + "/" + skillId + "/" + SKILL_FILE_NAME;
        }

        private static string BuildSkillMarkdownLine(AssistantIntegrationTarget target, PackagedSkillTemplateEntry skill)
        {
            var summary = string.IsNullOrWhiteSpace(skill.summary) ? string.Empty : " — " + skill.summary.Trim();

            if (!target.SupportsSkillDirectory)
            {
                return "- `" + skill.id + "`" + summary;
            }

            var docPath = "/" + target.GetSkillFileRelativePath(skill.GetTargetDirectoryName());
            return "- `" + skill.id + "` ([doc](" + docPath + "))" + summary;
        }

        private static string BuildSkillsMarkdown(AssistantIntegrationTarget target, List<PackagedSkillTemplateEntry> skills, bool includePrimary)
        {
            var lines = new List<string>();
            var primarySkillId = target.PrimarySkillId;

            foreach (var skill in skills)
            {
                if (skill == null)
                {
                    continue;
                }

                var isPrimary = !string.IsNullOrEmpty(primarySkillId) && string.Equals(skill.id, primarySkillId, StringComparison.OrdinalIgnoreCase);
                if (!includePrimary && isPrimary)
                {
                    continue;
                }

                lines.Add(BuildSkillMarkdownLine(target, skill));
            }

            return lines.Count == 0 ? "- None" : string.Join("\n", lines.ToArray());
        }

        private static IntegrationAction InstallSkillFile(string sourcePath, string targetFile)
        {
            var targetDir = Path.GetDirectoryName(targetFile);
            if (string.IsNullOrEmpty(targetDir))
            {
                throw new InvalidOperationException($"Unable to determine target directory for skill file: {targetFile}");
            }

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            if (File.Exists(targetFile))
            {
                var sourceTime = File.GetLastWriteTimeUtc(sourcePath);
                var targetTime = File.GetLastWriteTimeUtc(targetFile);
                if (sourceTime <= targetTime)
                {
                    return IntegrationAction.AlreadyUpToDate;
                }

                CopySkillFile(sourcePath, targetFile);
                return IntegrationAction.UpdatedBlock;
            }

            CopySkillFile(sourcePath, targetFile);
            return IntegrationAction.CreatedFile;
        }

        private static void CopySkillFile(string sourcePath, string targetFile)
        {
            var content = File.ReadAllText(sourcePath, Encoding.UTF8);
            var cliExeName = GetCliExecutableName();
            var hardcodedPath = $"Packages/{PACKAGE_NAME}/Tools~/CLI/AIBridgeCLI.exe";
            var fixedCliPath = "./" + CLI_CACHE_FOLDER + "/" + cliExeName;
            if (content.Contains(hardcodedPath))
            {
                content = content.Replace(hardcodedPath, fixedCliPath);
                AIBridgeLogger.LogInfo($"[SkillInstaller] Replaced CLI path: {hardcodedPath} -> {fixedCliPath}");
            }

            File.WriteAllText(targetFile, content, Encoding.UTF8);
        }

        private static IntegrationAction AggregateSkillActions(List<IntegrationAction> actions)
        {
            if (actions == null || actions.Count == 0)
            {
                return IntegrationAction.SkippedMissing;
            }

            if (actions.Contains(IntegrationAction.CreatedFile))
            {
                return IntegrationAction.CreatedFile;
            }

            if (actions.Contains(IntegrationAction.UpdatedBlock))
            {
                return IntegrationAction.UpdatedBlock;
            }

            if (actions.Contains(IntegrationAction.Failed))
            {
                return IntegrationAction.Failed;
            }

            return IntegrationAction.AlreadyUpToDate;
        }

        private static IntegrationAction InstallSkillFilesForTarget(
            string projectRoot,
            AssistantIntegrationTarget target,
            PackagedSkillManifest manifest,
            out List<string> skillFilePaths,
            out List<string> installedSkillIds)
        {
            skillFilePaths = new List<string>();
            installedSkillIds = new List<string>();

            if (!target.SupportsSkillDirectory)
            {
                return IntegrationAction.SkippedDisabled;
            }

            var installableSkills = GetInstallableSkills(manifest);
            if (installableSkills.Count == 0)
            {
                AIBridgeLogger.LogWarning("[SkillInstaller] No installable packaged skills were found.");
                return IntegrationAction.SkippedMissing;
            }

            var actions = new List<IntegrationAction>();
            foreach (var skill in installableSkills)
            {
                var sourcePath = ResolveSkillSourcePath(projectRoot, skill);
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    throw new FileNotFoundException($"Skill template not found for '{skill.id}'.", sourcePath ?? skill.source);
                }

                var targetDir = Path.Combine(
                    projectRoot,
                    target.SkillRootRelativePath.Replace('/', Path.DirectorySeparatorChar),
                    skill.GetTargetDirectoryName().Replace('/', Path.DirectorySeparatorChar));
                var targetFile = Path.Combine(targetDir, SKILL_FILE_NAME);
                var action = InstallSkillFile(sourcePath, targetFile);

                actions.Add(action);
                skillFilePaths.Add(targetFile);
                installedSkillIds.Add(skill.id);
            }

            return AggregateSkillActions(actions);
        }

        private static List<AssistantIntegrationResult> InstallAssistantIntegrations(string projectRoot)
        {
            var results = new List<AssistantIntegrationResult>();
            var manifest = LoadSkillManifest(projectRoot);

            foreach (var target in AssistantIntegrationRegistry.GetTargets())
            {
                var result = new AssistantIntegrationResult
                {
                    AssistantId = target.DisplayName,
                    RootRuleAction = IntegrationAction.None,
                    SkillFileAction = IntegrationAction.None,
                    SkillFilePaths = new List<string>(),
                    InstalledSkillIds = new List<string>()
                };

                try
                {
                    if (target.SupportsSkillDirectory)
                    {
                        result.SkillFileAction = InstallSkillFilesForTarget(projectRoot, target, manifest, out var skillFilePaths, out var installedSkillIds);
                        result.SkillFilePaths = skillFilePaths;
                        result.InstalledSkillIds = installedSkillIds;

                        var primarySkillPath = target.GetPrimarySkillFileRelativePath();
                        if (!string.IsNullOrEmpty(primarySkillPath))
                        {
                            result.SkillFilePath = Path.Combine(projectRoot, primarySkillPath.Replace('/', Path.DirectorySeparatorChar));
                        }
                    }
                    else
                    {
                        result.SkillFileAction = IntegrationAction.SkippedDisabled;
                    }

                    var template = RuleTemplateLoader.Load(projectRoot, target.RootRuleTemplateRelativePath);
                    var tokens = BuildTemplateTokens(projectRoot, target, manifest);
                    string rootRulePath;
                    result.RootRuleAction = RuleFileInstaller.Install(projectRoot, target, template, tokens, out rootRulePath);
                    result.RootRuleFilePath = rootRulePath;
                    result.Message = BuildResultMessage(target, result);
                }
                catch (Exception ex)
                {
                    result.RootRuleAction = IntegrationAction.Failed;
                    result.Message = ex.Message;
                    AIBridgeLogger.LogWarning($"[SkillInstaller] Failed to install {target.DisplayName} integration: {ex.Message}");
                }

                results.Add(result);
            }

            return results;
        }

        private static Dictionary<string, string> BuildTemplateTokens(string projectRoot, AssistantIntegrationTarget target, PackagedSkillManifest manifest)
        {
            var cliExeName = GetCliExecutableName();
            var installableSkills = GetInstallableSkills(manifest);
            var primarySkill = GetPrimarySkill(manifest, target);
            var primarySkillId = primarySkill != null ? primarySkill.id : DEFAULT_PRIMARY_SKILL_ID;
            var primarySkillDirectory = primarySkill != null ? primarySkill.GetTargetDirectoryName() : DEFAULT_PRIMARY_SKILL_ID;
            var primarySkillDocPath = target.SupportsSkillDirectory
                ? "/" + target.GetSkillFileRelativePath(primarySkillDirectory)
                : GetCanonicalSkillDocPath(primarySkillDirectory);

            return new Dictionary<string, string>
            {
                { "CLI_PATH", "./" + CLI_CACHE_FOLDER + "/" + cliExeName },
                { "CLI_EXE_NAME", cliExeName },
                { "CLI_CACHE_DIR", CLI_CACHE_FOLDER },
                { "SKILL_DOC_PATH", primarySkillDocPath },
                { "PRIMARY_SKILL_DOC_PATH", primarySkillDocPath },
                { "PRIMARY_SKILL_ID", primarySkillId },
                { "AVAILABLE_SKILLS_MARKDOWN", BuildSkillsMarkdown(target, installableSkills, true) },
                { "ADDITIONAL_SKILLS_MARKDOWN", BuildSkillsMarkdown(target, installableSkills, false) },
                { "PROJECT_ROOT_RULE_FILE", target.RootRuleFileName },
                { "ASSISTANT_NAME", target.DisplayName },
                { "PROJECT_ROOT", projectRoot }
            };
        }

        private static void LogResults(IEnumerable<AssistantIntegrationResult> results)
        {
            foreach (var result in results)
            {
                AIBridgeLogger.LogInfo($"[SkillInstaller] {BuildCompactResultMessage(result)}");
            }
        }

        private static string BuildCompactResultMessage(AssistantIntegrationResult result)
        {
            var skillCount = result.SkillFilePaths == null ? 0 : result.SkillFilePaths.Count;
            return $"{result.AssistantId}: skills={result.SkillFileAction} ({skillCount}), rule={result.RootRuleAction}";
        }

        private static string BuildResultMessage(AssistantIntegrationTarget target, AssistantIntegrationResult result)
        {
            var skillCount = result.SkillFilePaths == null ? 0 : result.SkillFilePaths.Count;
            return target.DisplayName + ": skills=" + result.SkillFileAction + " (" + skillCount + "), rule=" + result.RootRuleAction;
        }

        private static string BuildManualInstallSummary(IEnumerable<AssistantIntegrationResult> results)
        {
            var builder = new StringBuilder();
            builder.AppendLine("AIBridge integrations updated:");
            builder.AppendLine();

            foreach (var result in results)
            {
                builder.AppendLine(result.AssistantId + ":");
                builder.AppendLine("- Skills: " + result.SkillFileAction + FormatCountSuffix(result.SkillFilePaths));

                if (result.SkillFilePaths != null)
                {
                    foreach (var skillFilePath in result.SkillFilePaths)
                    {
                        builder.AppendLine("  - " + skillFilePath);
                    }
                }

                builder.AppendLine("- Rule: " + result.RootRuleAction + FormatPathSuffix(result.RootRuleFilePath));
                builder.AppendLine();
            }

            builder.Append("CLI copied to: ").Append(CLI_CACHE_FOLDER);
            return builder.ToString();
        }

        private static string FormatPathSuffix(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : " (" + path + ")";
        }

        private static string FormatCountSuffix(List<string> items)
        {
            var count = items == null ? 0 : items.Count;
            return " (" + count + ")";
        }

        [MenuItem("AIBridge/Install Assistant Skills")]
        public static void ManualInstall()
        {
            try
            {
                var projectRoot = GetProjectRoot();
                CopyCliToCacheIfNeeded(projectRoot);
                var results = InstallAssistantIntegrations(projectRoot);
                LogResults(results);
                EditorUtility.DisplayDialog("AIBridge", BuildManualInstallSummary(results), "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("AIBridge", $"Failed to install: {ex.Message}", "OK");
            }
        }
    }
}
