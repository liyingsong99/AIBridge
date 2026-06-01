using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Automatically installs the AIBridge skill documentation to each selected tool's skills directory.
    /// This allows AI assistants to discover and use the skill for Unity Editor operations.
    /// </summary>
    [InitializeOnLoad]
    public static class SkillInstaller
    {
        private const string SKILL_FILE_NAME = "SKILL.md";
        private const string SKILL_INSTALL_MANIFEST_FILE_NAME = "aibridge-skill.json";
        private const string PACKAGE_NAME = "cn.lys.aibridge";
        private const string CLI_CACHE_FOLDER = ".aibridge/cli";
        private const string CODE_INDEX_FOLDER = "CodeIndex";
        private static readonly string[] CLI_FILES = new[]
        {
            "AIBridgeCLI.dll",
            "AIBridgeCLI.deps.json",
            "AIBridgeCLI.runtimeconfig.json",
            "AIBridgeCLI.pdb",
            "Newtonsoft.Json.dll",
            "AIBridgeCLI"  // macOS/Linux executable (no extension)
        };
        
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

        static SkillInstaller()
        {
            // Delay execution to ensure Unity is fully initialized
            EditorApplication.delayCall += InstallSkillIfNeeded;
        }

        /// <summary>
        /// Check if skill documentation needs to be installed and install it
        /// </summary>
        private static void InstallSkillIfNeeded()
        {
            try
            {
                if (IsAssetImportWorker())
                {
                    return;
                }

                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    EditorApplication.delayCall += InstallSkillIfNeeded;
                    return;
                }

                var projectRoot = GetProjectRoot();
                CopyCliToCacheIfNeeded(projectRoot);

                if (!EnsureEditorLanguageInitialized())
                {
                    return;
                }

                var targets = GetSelectedTargets(projectRoot);
                HarnessCapabilitySnapshot.WriteNoThrow(projectRoot, targets);

                // 检查是否启用自动安装
                if (!AIBridgeProjectSettings.Instance.AutoInstallSkills)
                {
                    return;
                }
                
                // 清理未勾选目标的注入内容
                CleanupUnselectedTargets(projectRoot, targets);
                
                if (targets.Count == 0)
                {
                    return;
                }

                var results = InstallAssistantIntegrations(projectRoot, targets);
                SkillPluginAdapter.GenerateForTargets(projectRoot, targets);
                LogResults(results);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"[SkillInstaller] Failed to install skill documentation: {ex.Message}");
            }
        }

        internal static void RefreshInstalledIntegrationsNoDialog()
        {
            try
            {
                if (IsAssetImportWorker())
                {
                    return;
                }

                var projectRoot = GetProjectRoot();
                CopyCliToCacheIfNeeded(projectRoot);

                var targets = GetSelectedTargets(projectRoot);
                HarnessCapabilitySnapshot.WriteNoThrow(projectRoot, targets);
                CleanupUnselectedTargets(projectRoot, targets);
                if (targets.Count == 0)
                {
                    return;
                }

                var results = InstallAssistantIntegrations(projectRoot, targets);
                SkillPluginAdapter.GenerateForTargets(projectRoot, targets);
                LogResults(results);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("[SkillInstaller] Failed to refresh installed integrations: " + ex.Message);
            }
        }

        private static bool EnsureEditorLanguageInitialized()
        {
            var settings = AIBridgeProjectSettings.Instance;
            if (settings.EditorLanguageInitialized)
            {
                return true;
            }

            if (Application.isBatchMode)
            {
                settings.EditorLanguage = AIBridgeEditorLanguage.English;
                settings.EditorLanguageInitialized = true;
                settings.SaveSettings();
                return true;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += InstallSkillIfNeeded;
                return false;
            }

            settings.EditorLanguage = ShowInitialLanguageDialog();
            settings.EditorLanguageInitialized = true;
            settings.SaveSettings();
            return true;
        }

        private static bool IsAssetImportWorker()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-name", StringComparison.OrdinalIgnoreCase)
                    && i + 1 < args.Length
                    && args[i + 1].StartsWith("AssetImportWorker", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static AIBridgeEditorLanguage ShowInitialLanguageDialog()
        {
            var result = EditorUtility.DisplayDialog(
                "AIBridge Language / 语言",
                "Choose the language for AIBridge editor UI and project AGENTS.md template.\n\n请选择 AIBridge 编辑器界面和项目 AGENTS.md 模板使用的语言。",
                "English",
                "简体中文");

            return result ? AIBridgeEditorLanguage.English : AIBridgeEditorLanguage.SimplifiedChinese;
        }
        
        /// <summary>
        /// Copy CLI files to .aibridge/cli directory.
        /// This provides a fixed, stable path for AI assistants to use.
        /// </summary>
        private static void CopyCliToCacheIfNeeded(string projectRoot)
        {
            var platformRID = GetPlatformRID();
            var cliExeName = GetCliExecutableName();
            var targetCliDir = Path.Combine(projectRoot, CLI_CACHE_FOLDER);
            
            // Find source CLI directory (platform-specific)
            var sourceCliDir = GetSourceCliDirectory(platformRID);
            if (string.IsNullOrEmpty(sourceCliDir))
            {
                // Fallback to legacy non-platform directory
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
            
            // 包管理器更新时文件时间戳不一定递增，CLI 缓存必须按内容差异判断是否刷新。
            var needsCopy = IsCliCopyNeeded(sourceCliDir, targetCliDir, cliExeName)
                || IsCodeIndexCopyNeeded(sourceCliDir, targetCliDir);
            
            if (!needsCopy)
            {
                return;
            }
            
            // Create target directory
            if (!Directory.Exists(targetCliDir))
            {
                Directory.CreateDirectory(targetCliDir);
            }
            
            var copiedCount = 0;
            foreach (var fileName in GetCliFilesToCopy(cliExeName))
            {
                var sourceFile = Path.Combine(sourceCliDir, fileName);
                var targetFile = Path.Combine(targetCliDir, fileName);
                
                if (File.Exists(sourceFile))
                {
                    var makeExecutable = string.Equals(fileName, cliExeName, StringComparison.OrdinalIgnoreCase);
                    if (CopyFileToCache(sourceFile, targetFile, fileName, makeExecutable))
                    {
                        copiedCount++;
                    }
                }
            }

            copiedCount += CopyCodeIndexToCache(sourceCliDir, targetCliDir);
            
            if (copiedCount > 0)
            {
                AIBridgeLogger.LogInfo($"[SkillInstaller] Copied {copiedCount} CLI files to: {targetCliDir}");
            }
        }

        private static bool IsCliCopyNeeded(string sourceCliDir, string targetCliDir, string cliExeName)
        {
            foreach (var fileName in GetCliFilesToCopy(cliExeName))
            {
                var sourceFile = Path.Combine(sourceCliDir, fileName);
                if (!File.Exists(sourceFile))
                {
                    continue;
                }

                var targetFile = Path.Combine(targetCliDir, fileName);
                if (IsFileCopyNeeded(sourceFile, targetFile))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> GetCliFilesToCopy(string cliExeName)
        {
            yield return cliExeName;

            foreach (var fileName in CLI_FILES)
            {
                if (!string.Equals(fileName, cliExeName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return fileName;
                }
            }
        }

        private static bool CopyFileToCache(string sourceFile, string targetFile, string displayName, bool makeExecutable)
        {
            try
            {
                File.Copy(sourceFile, targetFile, true);
                if (makeExecutable)
                {
                    EnsureExecutablePermission(targetFile);
                }

                return true;
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning($"[SkillInstaller] Failed to copy {displayName}: {ex.Message}");
                return false;
            }
        }

        private static void EnsureExecutablePermission(string targetFile)
        {
#if !UNITY_EDITOR_WIN
            try
            {
                var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "chmod";
                process.StartInfo.Arguments = $"+x \"{targetFile}\"";
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

        internal static bool IsFileCopyNeeded(string sourceFile, string targetFile)
        {
            if (!File.Exists(sourceFile))
            {
                return false;
            }

            if (!File.Exists(targetFile))
            {
                return true;
            }

            try
            {
                var sourceInfo = new FileInfo(sourceFile);
                var targetInfo = new FileInfo(targetFile);
                if (sourceInfo.Length != targetInfo.Length)
                {
                    return true;
                }

                if (sourceInfo.LastWriteTimeUtc > targetInfo.LastWriteTimeUtc)
                {
                    return true;
                }

                return !FilesHaveSameHash(sourceFile, targetFile);
            }
            catch
            {
                return true;
            }
        }

        private static bool FilesHaveSameHash(string sourceFile, string targetFile)
        {
            using (var sha256 = SHA256.Create())
            {
                var sourceHash = ComputeFileHash(sha256, sourceFile);
                var targetHash = ComputeFileHash(sha256, targetFile);
                return sourceHash.SequenceEqual(targetHash);
            }
        }

        private static byte[] ComputeFileHash(HashAlgorithm hashAlgorithm, string path)
        {
            using (var stream = File.OpenRead(path))
            {
                return hashAlgorithm.ComputeHash(stream);
            }
        }

        private static bool IsCodeIndexCopyNeeded(string sourceCliDir, string targetCliDir)
        {
            var sourceDir = Path.Combine(sourceCliDir, CODE_INDEX_FOLDER);
            if (!Directory.Exists(sourceDir))
            {
                return false;
            }

            var targetDir = Path.Combine(targetCliDir, CODE_INDEX_FOLDER);
            if (!Directory.Exists(targetDir))
            {
                return true;
            }

            return IsDirectoryCopyNeeded(sourceDir, targetDir);
        }

        private static bool IsDirectoryCopyNeeded(string sourceDir, string targetDir)
        {
            var sourceFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            var sourceRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sourceFile in sourceFiles)
            {
                var relativePath = GetRelativeFilePath(sourceDir, sourceFile);
                sourceRelativePaths.Add(relativePath);

                var targetFile = Path.Combine(targetDir, relativePath);
                if (IsFileCopyNeeded(sourceFile, targetFile))
                {
                    return true;
                }
            }

            foreach (var targetFile in Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = GetRelativeFilePath(targetDir, targetFile);
                if (!sourceRelativePaths.Contains(relativePath))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetRelativeFilePath(string rootDirectory, string filePath)
        {
            var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(filePath);
            var prefix = root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(prefix.Length);
            }

            return Path.GetFileName(filePath);
        }

        private static int CopyCodeIndexToCache(string sourceCliDir, string targetCliDir)
        {
            var sourceDir = Path.Combine(sourceCliDir, CODE_INDEX_FOLDER);
            if (!Directory.Exists(sourceDir))
            {
                return 0;
            }

            var targetDir = Path.Combine(targetCliDir, CODE_INDEX_FOLDER);
            try
            {
                if (Directory.Exists(targetDir))
                {
                    Directory.Delete(targetDir, true);
                }

                return CopyDirectoryContents(sourceDir, targetDir);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning($"[SkillInstaller] Failed to copy {CODE_INDEX_FOLDER}: {ex.Message}");
                return 0;
            }
        }

        private static int CopyDirectoryContents(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            var copied = 0;
            foreach (var filePath in Directory.GetFiles(sourceDir))
            {
                var targetFile = Path.Combine(targetDir, Path.GetFileName(filePath));
                File.Copy(filePath, targetFile, true);
                copied++;
            }

            foreach (var childDir in Directory.GetDirectories(sourceDir))
            {
                copied += CopyDirectoryContents(childDir, Path.Combine(targetDir, Path.GetFileName(childDir)));
            }

            return copied;
        }

        /// <summary>
        /// Get the source CLI directory from the package.
        /// </summary>
        /// <param name="platformRID">Platform RID (e.g., win-x64, osx-arm64) or null for legacy path</param>
        private static string GetSourceCliDirectory(string platformRID)
        {
            var projectRoot = GetProjectRoot();
            var subPath = string.IsNullOrEmpty(platformRID) ? "CLI" : $"CLI/{platformRID}";
            
            // Method 1: Direct package path (for local/embedded packages)
            var directPath = Path.Combine(projectRoot, "Packages", PACKAGE_NAME, "Tools~", subPath);
            if (Directory.Exists(directPath))
            {
                return directPath;
            }
            
            // Method 2: Use PackageInfo for resolved path (for git/registry packages)
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

        /// <summary>
        /// Get the Unity project root directory
        /// </summary>
        private static string GetProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        /// <summary>
        /// Get the source skill file path from the package
        /// </summary>
        private static string GetSourceSkillPath()
        {
            var sourceSkillRoot = GetSourceSkillRootPath();
            if (string.IsNullOrEmpty(sourceSkillRoot))
            {
                return null;
            }

            var sourceSkillPath = Path.Combine(sourceSkillRoot, SKILL_FILE_NAME);
            return File.Exists(sourceSkillPath) ? sourceSkillPath : null;
        }

        /// <summary>
        /// Get the source Skill~ directory from the package.
        /// </summary>
        private static string GetSourceSkillRootPath()
        {
            var projectRoot = GetProjectRoot();

            var directPath = Path.Combine(projectRoot, "Packages", PACKAGE_NAME, "Skill~");
            if (Directory.Exists(directPath))
            {
                return directPath;
            }

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PACKAGE_NAME}");
            if (packageInfo != null)
            {
                var packagePath = Path.Combine(packageInfo.resolvedPath, "Skill~");
                if (Directory.Exists(packagePath))
                {
                    return packagePath;
                }
            }

            return null;
        }

        /// <summary>
        /// Generate and write skill file to target location.
        /// Reads the template and applies CLI path replacement.
        /// Command references are generated into Skill reference files separately.
        /// </summary>
        private static void GenerateAndWriteSkillFile(string sourcePath, string targetDir, string targetFile)
        {
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var content = File.ReadAllText(sourcePath, System.Text.Encoding.UTF8);
            var cliExeName = GetCliExecutableName();
            var hardcodedPath = $"Packages/{PACKAGE_NAME}/Tools~/CLI/AIBridgeCLI.exe";
            var fixedCliPath = "./" + CLI_CACHE_FOLDER + "/" + cliExeName;
            if (content.Contains(hardcodedPath))
            {
                content = content.Replace(hardcodedPath, fixedCliPath);
            }

            content = ApplyProjectVersionTokens(content);
            File.WriteAllText(targetFile, content, System.Text.Encoding.UTF8);
        }

        private static List<AssistantIntegrationResult> InstallAssistantIntegrations(string projectRoot)
        {
            return InstallAssistantIntegrations(projectRoot, AssistantIntegrationRegistry.GetTargets());
        }

        internal static List<AssistantIntegrationResult> InstallAssistantIntegrations(string projectRoot, IEnumerable<AssistantIntegrationTarget> targets)
        {
            var results = new List<AssistantIntegrationResult>();
            var sourceSkillPath = GetSourceSkillPath();
            
            // 去重逻辑：如果同时存在 CLAUDE.md 和 AGENTS.md，优先使用 AGENTS.md
            var targetsList = targets.ToList();
            var hasClaudeTarget = targetsList.Any(t => t.Id == "claude");
            var hasCodexTarget = targetsList.Any(t => t.Id == "codex");
            
            if (hasClaudeTarget && hasCodexTarget)
            {
                var claudeMdPath = Path.Combine(projectRoot, "CLAUDE.md");
                var agentsMdPath = Path.Combine(projectRoot, "AGENTS.md");
                
                // 如果两个文件都存在，优先使用 AGENTS.md，跳过 CLAUDE.md
                if (File.Exists(claudeMdPath) && File.Exists(agentsMdPath))
                {
                    targetsList = targetsList.Where(t => t.Id != "claude").ToList();
                    AIBridgeLogger.LogInfo("[SkillInstaller] 检测到 CLAUDE.md 和 AGENTS.md 同时存在，优先使用 AGENTS.md");
                }
            }
            
            foreach (var target in targetsList)
            {
                var result = new AssistantIntegrationResult
                {
                    AssistantId = target.DisplayName,
                    RootRuleAction = IntegrationAction.None,
                    SkillFileAction = IntegrationAction.None
                };

                try
                {
                    if (target.SupportsSkillDirectory)
                    {
                        string skillFilePath;
                        result.SkillFileAction = InstallSkillFileForTarget(projectRoot, target, sourceSkillPath, out skillFilePath);
                        result.SkillFilePath = skillFilePath;
                        result.AdditionalSkillFilePaths.AddRange(InstallAdditionalSkillDirectoriesForTarget(projectRoot, target));
                        GenerateSkillReferenceFilesForTarget(projectRoot, target);
                    }

                    var template = RuleTemplateLoader.Load(projectRoot, target.RootRuleTemplateRelativePath);
                    var tokens = BuildTemplateTokens(projectRoot, target, targetsList);
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

            HarnessCapabilitySnapshot.WriteNoThrow(projectRoot, targetsList);
            return results;
        }

        private static IntegrationAction InstallSkillFileForTarget(string projectRoot, AssistantIntegrationTarget target, string sourceSkillPath, out string skillFilePath)
        {
            skillFilePath = null;
            if (string.IsNullOrEmpty(sourceSkillPath) || !File.Exists(sourceSkillPath))
            {
                AIBridgeLogger.LogWarning($"[SkillInstaller] Source skill file not found. Expected at: Packages/{PACKAGE_NAME}/Skill~/{SKILL_FILE_NAME}");
                return IntegrationAction.SkippedMissing;
            }

            var resolvedSkillDirectory = target.GetResolvedSkillDirectoryRelativePath(projectRoot);
            var targetDir = Path.Combine(projectRoot, resolvedSkillDirectory.Replace('/', Path.DirectorySeparatorChar));
            skillFilePath = Path.Combine(targetDir, target.SkillFileName);
            var sourceSkillRoot = Path.GetDirectoryName(sourceSkillPath);
            if (IsUnsafeSkillInstallTarget(sourceSkillRoot, targetDir))
            {
                AIBridgeLogger.LogWarning("[SkillInstaller] Refused to install Skill into package source Skill~ directory: " + targetDir);
                return IntegrationAction.SkippedUnsafePath;
            }

            var existed = File.Exists(skillFilePath);

            GenerateAndWriteSkillFile(sourceSkillPath, targetDir, skillFilePath);
            return existed ? IntegrationAction.UpdatedBlock : IntegrationAction.CreatedFile;
        }

        private static List<string> InstallAdditionalSkillDirectoriesForTarget(string projectRoot, AssistantIntegrationTarget target)
        {
            var installedSkillFiles = new List<string>();
            var sourceSkillRoot = GetSourceSkillRootPath();
            if (string.IsNullOrEmpty(sourceSkillRoot) || !Directory.Exists(sourceSkillRoot))
            {
                return installedSkillFiles;
            }

            var targetSkillRoot = GetTargetSkillRootDirectory(projectRoot, target);
            if (string.IsNullOrEmpty(targetSkillRoot))
            {
                return installedSkillFiles;
            }

            if (IsUnsafeSkillInstallTarget(sourceSkillRoot, targetSkillRoot))
            {
                AIBridgeLogger.LogWarning("[SkillInstaller] Refused to install additional Skills into package source Skill~ directory: " + targetSkillRoot);
                return installedSkillFiles;
            }

            foreach (var sourceSkillDir in Directory.GetDirectories(sourceSkillRoot))
            {
                var sourceSkillFile = Path.Combine(sourceSkillDir, SKILL_FILE_NAME);
                if (!File.Exists(sourceSkillFile))
                {
                    continue;
                }

                var skillName = Path.GetFileName(sourceSkillDir);
                var targetSkillDir = Path.Combine(targetSkillRoot, skillName);
                var targetSkillFile = Path.Combine(targetSkillDir, SKILL_FILE_NAME);

                if (!ShouldInstallSkillDirectory(sourceSkillDir))
                {
                    if (Directory.Exists(targetSkillDir))
                    {
                        Directory.Delete(targetSkillDir, true);
                        AIBridgeLogger.LogInfo("[SkillInstaller] Removed disabled Skill directory: " + targetSkillDir);
                    }

                    continue;
                }

                // 子目录 Skill 独立安装，先清空目标目录可避免删除源文件后残留旧资源。
                if (Directory.Exists(targetSkillDir))
                {
                    Directory.Delete(targetSkillDir, true);
                }

                CopyDirectory(sourceSkillDir, targetSkillDir);
                installedSkillFiles.Add(targetSkillFile);
            }

            return installedSkillFiles;
        }

        private static void GenerateSkillReferenceFilesForTarget(string projectRoot, AssistantIntegrationTarget target)
        {
            var targetSkillDirectory = GetTargetSkillDirectory(projectRoot, target);
            if (string.IsNullOrEmpty(targetSkillDirectory))
            {
                return;
            }

            var commands = CommandRegistry.GetAllCommands();
            SkillDocumentGenerator.GenerateReferenceFiles(targetSkillDirectory, commands);
        }

        private static string GetTargetSkillDirectory(string projectRoot, AssistantIntegrationTarget target)
        {
            if (!target.SupportsSkillDirectory)
            {
                return null;
            }

            var resolvedSkillDirectory = target.GetResolvedSkillDirectoryRelativePath(projectRoot);
            return string.IsNullOrEmpty(resolvedSkillDirectory)
                ? null
                : Path.Combine(projectRoot, resolvedSkillDirectory.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string GetTargetSkillRootDirectory(string projectRoot, AssistantIntegrationTarget target)
        {
            if (!target.SupportsSkillDirectory || string.IsNullOrEmpty(target.SkillDirectoryRelativePath))
            {
                return null;
            }

            var skillRootDirectory = target.GetResolvedSkillRootDirectoryRelativePath(projectRoot);
            return string.IsNullOrEmpty(skillRootDirectory)
                ? null
                : Path.Combine(projectRoot, skillRootDirectory.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var filePath in Directory.GetFiles(sourceDir))
            {
                if (filePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(Path.GetFileName(filePath), SKILL_INSTALL_MANIFEST_FILE_NAME, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var targetFile = Path.Combine(targetDir, Path.GetFileName(filePath));
                File.Copy(filePath, targetFile, true);
            }

            foreach (var childDir in Directory.GetDirectories(sourceDir))
            {
                var targetChildDir = Path.Combine(targetDir, Path.GetFileName(childDir));
                CopyDirectory(childDir, targetChildDir);
            }
        }

        internal static bool IsUnsafeSkillInstallTarget(string sourceSkillRoot, string targetDirectory)
        {
            if (string.IsNullOrEmpty(sourceSkillRoot) || string.IsNullOrEmpty(targetDirectory))
            {
                return false;
            }

            // 安装目录不能落在包内 Skill~ 源目录下，否则刷新时会先删目标目录并误删源 Skill。
            return IsSameOrChildDirectory(sourceSkillRoot, targetDirectory);
        }

        private static bool IsSameOrChildDirectory(string parentDirectory, string candidateDirectory)
        {
            var parent = NormalizeFullDirectoryPath(parentDirectory);
            var candidate = NormalizeFullDirectoryPath(candidateDirectory);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            if (string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFullDirectoryPath(string path)
        {
            try
            {
                return Path.GetFullPath(path)
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                    .TrimEnd(Path.DirectorySeparatorChar);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, string> BuildTemplateTokens(string projectRoot, AssistantIntegrationTarget target, IEnumerable<AssistantIntegrationTarget> selectedTargets)
        {
            var cliExeName = GetCliExecutableName();
            var language = AIBridgeProjectSettings.Instance.EditorLanguage;
            var unityVersion = GetCurrentUnityVersionText();
            var csharpLanguageVersion = GetCurrentCSharpLanguageVersionText();
            var workflowSkillDocPath = GetSkillDocumentPath(projectRoot, target, "aibridge-development-workflow");
            var skillRootPath = GetSkillRootDocumentPath(projectRoot, target);
            var harnessCapabilityRule = HarnessCapabilitySnapshot.BuildRootRuleSummary(projectRoot, selectedTargets, language);
            return new Dictionary<string, string>
            {
                { "CLI_PATH", "./" + CLI_CACHE_FOLDER + "/" + cliExeName },
                { "COMMON_COMMANDS_TITLE", AIBridgeEditorText.For(language, "Common Commands", "常用命令") },
                { "PROJECT_VERSION_TITLE", AIBridgeEditorText.For(language, "Project Version", "项目版本") },
                { "UNITY_VERSION", unityVersion },
                { "CSHARP_LANGUAGE_VERSION", csharpLanguageVersion },
                { "UNITY_VERSION_RULE", AIBridgeEditorText.For(language, "Current Unity version: " + unityVersion, "当前项目 Unity 版本：" + unityVersion) },
                { "CSHARP_VERSION_RULE", AIBridgeEditorText.For(language, "Current C# language requirement: compatible with " + csharpLanguageVersion + "; do not use newer syntax.", "当前项目 C# 语言版本要求：兼容 " + csharpLanguageVersion + "，禁止使用更高版本语法。") },
                { "CAPABILITIES_TITLE", AIBridgeEditorText.For(language, "Current Capabilities", "当前能力状态") },
                { "HARNESS_CAPABILITY_RULE", harnessCapabilityRule },
                { "CODE_INDEX_CAPABILITY_RULE", BuildCodeIndexCapabilityRule(language) },
                { "ROUTING_TITLE", AIBridgeEditorText.For(language, "Routing Rules", "路由原则") },
                { "QUICK_TASK_RULE", AIBridgeEditorText.For(language, "Quick tasks: answer or execute directly for pure Q&A, code explanation, search/display, or tasks with no code or asset changes.", "快速任务：纯问答、代码解释、查找、显示、无代码或资源修改，直接回答或执行。") },
                { "DEVELOPMENT_TASK_RULE", AIBridgeEditorText.For(language, "Development, debugging, review, and validation tasks for C# code, Unity assets, Prefabs, Editor tools, package structure, tests, AGENTS.md, Skills, Runtime behavior, or logs must load `aibridge-development-workflow` first.", "开发、调试、审查和验证任务：涉及 C# 代码、Unity 资源、Prefab、Editor 工具、包结构、测试、AGENTS.md、Skills、Runtime 行为或日志时，必须优先加载 `aibridge-development-workflow`。") },
                { "WORKFLOW_SKILL_RULE", AIBridgeEditorText.For(language, "After entering the workflow, `aibridge-development-workflow` probes harness readiness, chooses the task branch, and decides whether to load additional Skills.", "进入工作流后，由 `aibridge-development-workflow` 探测 harness 能力、选择任务分支，并决定是否继续加载其它 Skill。") },
                { "SKILL_LOADING_TITLE", AIBridgeEditorText.For(language, "Skill Loading", "Skill 加载") },
                { "WORKFLOW_SKILL_ENTRY", AIBridgeEditorText.For(language, "Load `aibridge-development-workflow` from `" + workflowSkillDocPath + "` before development tasks.", "开发任务先加载 `" + workflowSkillDocPath + "` 中的 `aibridge-development-workflow`。") },
                { "SKILL_ROOT_RULE", AIBridgeEditorText.For(language, "AIBridge Skills are installed under `" + skillRootPath + "/<skill-name>/SKILL.md`; load sibling Skills from that directory when this root rule or the workflow requires them.", "AIBridge Skills 安装在 `" + skillRootPath + "/<skill-name>/SKILL.md`；当本根规则或工作流要求时，从该目录加载同级 Skill。") }
            };
        }

        private static string BuildCodeIndexCapabilityRule(AIBridgeEditorLanguage language)
        {
            if (AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex)
            {
                return AIBridgeEditorText.For(
                    language,
                    "Code Index: enabled. For C# code lookup or source navigation, load `aibridge-code-index` first when the query can be expressed as symbol, definition, reference, implementation, derived type, caller, or diagnostic lookup. Use `rg` for literal, fuzzy, non-C# searches, or when Code Index is unavailable.",
                    "Code Index：已启用。C# 代码查找或源码导航中，只要查询可表达为符号、定义、引用、实现、派生类型、调用者或诊断查询，应优先加载 `aibridge-code-index`。字面量、模糊文本、非 C# 搜索或 Code Index 不可用时使用 `rg`。");
            }

            return AIBridgeEditorText.For(
                language,
                "Code Index: disabled. Do not call `code_index`; use `rg`, file reads, or regular AIBridge commands for code search.",
                "Code Index：已关闭。不要调用 `code_index`；请使用 `rg`、文件读取或常规 AIBridge 命令搜索代码。");
        }

        private static bool ShouldInstallSkillDirectory(string sourceSkillDir)
        {
            string requiredFeature;
            if (!TryReadRequiredFeature(sourceSkillDir, out requiredFeature))
            {
                return false;
            }

            return IsRequiredFeatureEnabled(requiredFeature);
        }

        private static bool TryReadRequiredFeature(string sourceSkillDir, out string requiredFeature)
        {
            requiredFeature = null;
            var manifestPath = Path.Combine(sourceSkillDir, SKILL_INSTALL_MANIFEST_FILE_NAME);
            if (!File.Exists(manifestPath))
            {
                return true;
            }

            try
            {
                var manifest = JsonUtility.FromJson<SkillInstallManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
                requiredFeature = manifest == null ? null : manifest.requiredFeature;
                return true;
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("[SkillInstaller] Failed to read Skill install manifest: " + ex.Message);
                return false;
            }
        }

        private static bool IsRequiredFeatureEnabled(string requiredFeature)
        {
            if (string.IsNullOrWhiteSpace(requiredFeature))
            {
                return true;
            }

            if (string.Equals(requiredFeature, "code-index", StringComparison.OrdinalIgnoreCase))
            {
                return AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex;
            }

            AIBridgeLogger.LogWarning("[SkillInstaller] Unknown required Skill feature: " + requiredFeature);
            return false;
        }

        internal static string ApplyProjectVersionTokens(string content)
        {
            return content
                .Replace("{{UNITY_VERSION}}", GetCurrentUnityVersionText())
                .Replace("{{CSHARP_LANGUAGE_VERSION}}", GetCurrentCSharpLanguageVersionText());
        }

        private static string GetCurrentUnityVersionText()
        {
            return string.IsNullOrEmpty(Application.unityVersion)
                ? "Unknown"
                : Application.unityVersion;
        }

        private static string GetCurrentCSharpLanguageVersionText()
        {
            return "C# " + CodeCommand.GetSupportedCSharpLanguageVersion();
        }

        private static string GetSkillDocumentPath(string projectRoot, AssistantIntegrationTarget target, string skillName)
        {
            return target.SupportsSkillDirectory
                ? "/" + target.GetResolvedSiblingSkillFileRelativePath(projectRoot, skillName)
                : "/Packages/" + PACKAGE_NAME + "/Skill~/" + skillName + "/" + SKILL_FILE_NAME;
        }

        private static string GetSkillRootDocumentPath(string projectRoot, AssistantIntegrationTarget target)
        {
            if (target.SupportsSkillDirectory)
            {
                var skillRoot = target.GetResolvedSkillRootDirectoryRelativePath(projectRoot);
                if (!string.IsNullOrEmpty(skillRoot))
                {
                    return "/" + skillRoot.Replace('\\', '/').Trim('/');
                }
            }

            return "/Packages/" + PACKAGE_NAME + "/Skill~";
        }

        private static List<AssistantIntegrationTarget> GetSelectedTargets(string projectRoot)
        {
            var allTargets = AssistantIntegrationRegistry.GetTargets();
            var selections = AssistantIntegrationSelectionSettings.LoadSelections(projectRoot, allTargets);
            return allTargets.Where(target => selections.TryGetValue(target.Id, out var selected) && selected).ToList();
        }

        internal static List<AssistantIntegrationTarget> GetSelectedTargetsForPluginGeneration(string projectRoot)
        {
            return GetSelectedTargets(projectRoot);
        }

        private static List<AssistantIntegrationTarget> GetTargetsByIds(IEnumerable<string> targetIds)
        {
            var selectedIds = new HashSet<string>(targetIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return AssistantIntegrationRegistry.GetTargets()
                .Where(target => selectedIds.Contains(target.Id))
                .ToList();
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
            return $"{result.AssistantId}: skill={result.SkillFileAction}, rule={result.RootRuleAction}";
        }

        private static string BuildResultMessage(AssistantIntegrationTarget target, AssistantIntegrationResult result)
        {
            return target.DisplayName + ": skill=" + result.SkillFileAction + ", rule=" + result.RootRuleAction;
        }

        private static string BuildManualInstallSummary(IEnumerable<AssistantIntegrationResult> results)
        {
            var language = AIBridgeProjectSettings.Instance.EditorLanguage;
            var builder = new StringBuilder();
            builder.AppendLine(AIBridgeEditorText.For(language, "AIBridge integrations updated:", "AIBridge 集成已更新："));
            builder.AppendLine();

            foreach (var result in results)
            {
                builder.AppendLine(result.AssistantId + ":");
                if (!string.IsNullOrEmpty(result.SkillFilePath))
                {
                    builder.AppendLine("- Skill: " + result.SkillFileAction + " (" + result.SkillFilePath + ")");
                }
                foreach (var additionalSkillPath in result.AdditionalSkillFilePaths)
                {
                    builder.AppendLine(AIBridgeEditorText.For(language, "- Additional Skill: ", "- 附加 Skill：") + additionalSkillPath);
                }
                builder.AppendLine(AIBridgeEditorText.For(language, "- Rule: ", "- 规则：") + result.RootRuleAction + FormatPathSuffix(result.RootRuleFilePath));
                builder.AppendLine();
            }

            builder.Append(AIBridgeEditorText.For(language, "CLI copied to: ", "CLI 已复制到：")).Append(CLI_CACHE_FOLDER);
            return builder.ToString();
        }

        private static string FormatPathSuffix(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : " (" + path + ")";
        }

        /// <summary>
        /// 清理未勾选目标的 AIBridge 注入内容
        /// </summary>
        private static void CleanupUnselectedTargets(string projectRoot, List<AssistantIntegrationTarget> selectedTargets)
        {
            var allTargets = AssistantIntegrationRegistry.GetTargets();
            var selectedIds = new HashSet<string>(selectedTargets.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);

            foreach (var target in allTargets)
            {
                // 跳过已勾选的目标
                if (selectedIds.Contains(target.Id))
                {
                    continue;
                }

                try
                {
                    // 清理 RootRule 文件中的注入块
                    var template = RuleTemplateLoader.Load(projectRoot, target.RootRuleTemplateRelativePath);
                    if (RuleFileInstaller.RemoveBlock(projectRoot, target, template))
                    {
                        var ruleFilePath = Path.Combine(projectRoot, target.RootRuleFileName);
                        AIBridgeLogger.LogInfo($"[SkillInstaller] Removed AIBridge block from {target.DisplayName}: {ruleFilePath}");
                    }

                    // 自定义 skills 根目录可能被多个工具复用，只有没有任何已选工具仍引用时才清理。
                    if (target.SupportsSkillDirectory
                        && !string.IsNullOrEmpty(target.SkillDirectoryRelativePath)
                        && !IsSkillRootUsedByAnySelectedTarget(projectRoot, target, selectedTargets))
                    {
                        CleanupSkillDirectoriesForTarget(projectRoot, target);
                    }
                }
                catch (Exception ex)
                {
                    AIBridgeLogger.LogWarning($"[SkillInstaller] Failed to cleanup {target.DisplayName}: {ex.Message}");
                }
            }

            SkillPluginAdapter.CleanupForTargets(projectRoot, allTargets.Where(target => !selectedIds.Contains(target.Id)));
        }

        private static void CleanupSkillDirectoriesForTarget(string projectRoot, AssistantIntegrationTarget target)
        {
            var skillDirs = new List<string>();
            var sourceSkillRoot = GetSourceSkillRootPath();
            var resolvedSkillDirectory = target.GetResolvedSkillDirectoryRelativePath(projectRoot);
            if (!string.IsNullOrEmpty(resolvedSkillDirectory))
            {
                var resolvedSkillPath = Path.Combine(projectRoot, resolvedSkillDirectory.Replace('/', Path.DirectorySeparatorChar));
                if (IsUnsafeSkillInstallTarget(sourceSkillRoot, resolvedSkillPath))
                {
                    AIBridgeLogger.LogWarning("[SkillInstaller] Refused to cleanup package source Skill~ directory: " + resolvedSkillPath);
                    return;
                }

                skillDirs.Add(resolvedSkillPath);
            }

            var targetSkillRoot = GetTargetSkillRootDirectory(projectRoot, target);
            if (!string.IsNullOrEmpty(targetSkillRoot) && !string.IsNullOrEmpty(sourceSkillRoot) && Directory.Exists(sourceSkillRoot))
            {
                if (IsUnsafeSkillInstallTarget(sourceSkillRoot, targetSkillRoot))
                {
                    AIBridgeLogger.LogWarning("[SkillInstaller] Refused to cleanup additional Skills from package source Skill~ directory: " + targetSkillRoot);
                    return;
                }

                foreach (var sourceSkillDir in Directory.GetDirectories(sourceSkillRoot))
                {
                    if (File.Exists(Path.Combine(sourceSkillDir, SKILL_FILE_NAME)))
                    {
                        skillDirs.Add(Path.Combine(targetSkillRoot, Path.GetFileName(sourceSkillDir)));
                    }
                }
            }

            foreach (var skillDir in skillDirs.Distinct())
            {
                if (Directory.Exists(skillDir))
                {
                    Directory.Delete(skillDir, true);
                    AIBridgeLogger.LogInfo($"[SkillInstaller] Removed Skill directory for {target.DisplayName}: {skillDir}");
                }
            }
        }

        private static bool IsSkillRootUsedByAnySelectedTarget(string projectRoot, AssistantIntegrationTarget target, IEnumerable<AssistantIntegrationTarget> selectedTargets)
        {
            var targetRoot = target.GetResolvedSkillRootDirectoryRelativePath(projectRoot);
            if (string.IsNullOrEmpty(targetRoot))
            {
                return false;
            }

            foreach (var selectedTarget in selectedTargets)
            {
                if (!selectedTarget.SupportsSkillDirectory)
                {
                    continue;
                }

                var selectedRoot = selectedTarget.GetResolvedSkillRootDirectoryRelativePath(projectRoot);
                if (string.Equals(targetRoot, selectedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        [Serializable]
        private sealed class SkillInstallManifest
        {
            public string requiredFeature;
        }

        /// <summary>
        /// Manually trigger skill installation.
        /// </summary>
        public static void ManualInstall()
        {
            try
            {
                var projectRoot = GetProjectRoot();
                var targets = GetSelectedTargets(projectRoot);
                if (targets.Count == 0)
                {
                    EditorUtility.DisplayDialog(
                        "AIBridge",
                        AIBridgeEditorText.T("No assistant tools selected for installation. Open AIBridge/Settings and choose at least one tool.", "未选择要安装的 AI 工具。请打开 AIBridge/Settings 并至少选择一个工具。"),
                        AIBridgeEditorText.T("OK", "确定"));
                    return;
                }

                CopyCliToCacheIfNeeded(projectRoot);
                var results = InstallAssistantIntegrations(projectRoot, targets);
                SkillPluginAdapter.GenerateForTargets(projectRoot, targets);
                LogResults(results);
                EditorUtility.DisplayDialog("AIBridge", BuildManualInstallSummary(results), AIBridgeEditorText.T("OK", "确定"));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("AIBridge", AIBridgeEditorText.T($"Failed to install: {ex.Message}", $"安装失败：{ex.Message}"), AIBridgeEditorText.T("OK", "确定"));
            }
        }

        public static void ManualInstallSelected(IEnumerable<string> targetIds)
        {
            try
            {
                var projectRoot = GetProjectRoot();
                var targets = GetTargetsByIds(targetIds);
                if (targets.Count == 0)
                {
                    EditorUtility.DisplayDialog(
                        "AIBridge",
                        AIBridgeEditorText.T("No assistant tools selected for installation.", "未选择要安装的 AI 工具。"),
                        AIBridgeEditorText.T("OK", "确定"));
                    return;
                }

                var selectedIds = new HashSet<string>(targets.Select(target => target.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var target in AssistantIntegrationRegistry.GetTargets())
                {
                    AssistantIntegrationSelectionSettings.SetSelected(target.Id, selectedIds.Contains(target.Id));
                }

                CleanupUnselectedTargets(projectRoot, targets);
                CopyCliToCacheIfNeeded(projectRoot);
                var results = InstallAssistantIntegrations(projectRoot, targets);
                SkillPluginAdapter.GenerateForTargets(projectRoot, targets);
                LogResults(results);
                EditorUtility.DisplayDialog("AIBridge", BuildManualInstallSummary(results), AIBridgeEditorText.T("OK", "确定"));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("AIBridge", AIBridgeEditorText.T($"Failed to install: {ex.Message}", $"安装失败：{ex.Message}"), AIBridgeEditorText.T("OK", "确定"));
            }
        }
    }
}
