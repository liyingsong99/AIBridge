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
        private const string CODE_INDEX_DAEMON_FILE_NAME = "AIBridgeCodeIndex";
        private const string CODE_INDEX_TEMP_PREFIX = CODE_INDEX_FOLDER + ".tmp.";
        private const string CODE_INDEX_BACKUP_PREFIX = CODE_INDEX_FOLDER + ".old.";
        private const string CODE_INDEX_DAEMON_SHUTDOWN_CLEANUP_MODE = "processOnly";
        private const int CODE_INDEX_DAEMON_SHUTDOWN_TIMEOUT_MS = 3000;
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly string[] CLI_FILES = new[]
        {
            "AIBridgeCLI.dll",
            "AIBridgeCLI.deps.json",
            "AIBridgeCLI.runtimeconfig.json",
            "AIBridgeCLI.pdb",
            "Newtonsoft.Json.dll",
            "AIBridgeCLI"  // macOS/Linux executable (no extension)
        };
        private static readonly string[] CODE_INDEX_REQUIRED_MANAGED_FILES = new[]
        {
            CODE_INDEX_DAEMON_FILE_NAME + ".dll",
            CODE_INDEX_DAEMON_FILE_NAME + ".deps.json",
            CODE_INDEX_DAEMON_FILE_NAME + ".runtimeconfig.json",
            "Newtonsoft.Json.dll",
            "Microsoft.CodeAnalysis.dll",
            "Microsoft.CodeAnalysis.CSharp.dll",
            "Microsoft.CodeAnalysis.Workspaces.dll",
            "Microsoft.CodeAnalysis.CSharp.Workspaces.dll",
            "System.Composition.AttributedModel.dll",
            "System.Composition.Convention.dll",
            "System.Composition.Hosting.dll",
            "System.Composition.Runtime.dll",
            "System.Composition.TypedParts.dll"
        };
        private static Action<string, int> _codeIndexDaemonShutdown = AIBridgeCodeIndexEditorUtility.ShutdownDaemon;
        
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

        private static string GetCodeIndexExecutableName()
        {
#if UNITY_EDITOR_WIN
            return CODE_INDEX_DAEMON_FILE_NAME + ".exe";
#else
            return CODE_INDEX_DAEMON_FILE_NAME;
#endif
        }

        static SkillInstaller()
        {
            ScheduleAutomaticInstall();
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

                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    return;
                }

                if (!ShouldRunAutomaticInstall(EditorApplication.isCompiling, EditorApplication.isUpdating, false))
                {
                    ScheduleAutomaticInstall();
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

        private static void ScheduleAutomaticInstall()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            // Delay execution to ensure Unity is fully initialized.
            EditorApplication.delayCall += InstallSkillIfNeeded;
        }

        internal static bool ShouldRunAutomaticInstall(bool isCompiling, bool isUpdating, bool isPlayingOrWillChangePlaymode)
        {
            return !isCompiling
                   && !isUpdating
                   && !isPlayingOrWillChangePlaymode;
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
            var cliNeedsCopy = IsCliCopyNeeded(sourceCliDir, targetCliDir, cliExeName);
            var codeIndexNeedsCopy = IsCodeIndexCopyNeeded(sourceCliDir, targetCliDir);
            
            if (!cliNeedsCopy && !codeIndexNeedsCopy)
            {
                return;
            }
            
            // Create target directory
            if (!Directory.Exists(targetCliDir))
            {
                Directory.CreateDirectory(targetCliDir);
            }
            
            var copiedCount = 0;
            if (cliNeedsCopy)
            {
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
            }

            if (codeIndexNeedsCopy)
            {
                copiedCount += RefreshCodeIndexCache(sourceCliDir, targetCliDir);
            }
            
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

        internal static bool IsCodeIndexCopyNeeded(string sourceCliDir, string targetCliDir)
        {
            var sourceDir = Path.Combine(sourceCliDir, CODE_INDEX_FOLDER);
            if (!Directory.Exists(sourceDir))
            {
                return false;
            }

            string[] sourceMissingFiles;
            if (!IsCodeIndexDirectoryComplete(sourceDir, out sourceMissingFiles))
            {
                AIBridgeLogger.LogWarning("[SkillInstaller] Source CodeIndex directory is incomplete and will not be copied. Missing: " + FormatMissingFiles(sourceMissingFiles));
                return false;
            }

            var targetDir = Path.Combine(targetCliDir, CODE_INDEX_FOLDER);
            if (!Directory.Exists(targetDir))
            {
                return true;
            }

            string[] targetMissingFiles;
            if (!IsCodeIndexDirectoryComplete(targetDir, out targetMissingFiles))
            {
                return true;
            }

            // CodeIndex 发布目录与项目缓存目录必须严格同步；哪怕完整，也不能接受静默漂移。
            return IsDirectoryCopyNeeded(sourceDir, targetDir);
        }

        internal static bool IsCodeIndexDirectoryComplete(string directory, out string[] missingFiles)
        {
            var missing = new List<string>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                missing.Add(CODE_INDEX_FOLDER);
                missingFiles = missing.ToArray();
                return false;
            }

            foreach (var fileName in GetRequiredCodeIndexFiles())
            {
                if (!File.Exists(Path.Combine(directory, fileName)))
                {
                    missing.Add(fileName);
                }
            }

            missingFiles = missing.ToArray();
            return missing.Count == 0;
        }

        private static IEnumerable<string> GetRequiredCodeIndexFiles()
        {
            yield return GetCodeIndexExecutableName();

            foreach (var fileName in CODE_INDEX_REQUIRED_MANAGED_FILES)
            {
                yield return fileName;
            }
        }

        private static string FormatMissingFiles(IEnumerable<string> missingFiles)
        {
            return string.Join(", ", missingFiles ?? Enumerable.Empty<string>());
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

        internal static int CopyCodeIndexToCache(string sourceCliDir, string targetCliDir)
        {
            var sourceDir = Path.Combine(sourceCliDir, CODE_INDEX_FOLDER);
            if (!Directory.Exists(sourceDir))
            {
                return 0;
            }

            var targetDir = Path.Combine(targetCliDir, CODE_INDEX_FOLDER);
            var tempDir = Path.Combine(targetCliDir, CODE_INDEX_TEMP_PREFIX + Guid.NewGuid().ToString("N"));
            var backupDir = Path.Combine(targetCliDir, CODE_INDEX_BACKUP_PREFIX + Guid.NewGuid().ToString("N"));
            try
            {
                string[] sourceMissingFiles;
                if (!IsCodeIndexDirectoryComplete(sourceDir, out sourceMissingFiles))
                {
                    AIBridgeLogger.LogWarning("[SkillInstaller] Refused to copy incomplete CodeIndex source. Missing: " + FormatMissingFiles(sourceMissingFiles));
                    return 0;
                }

                DeleteDirectoryIfExists(tempDir);
                DeleteDirectoryIfExists(backupDir);
                var copied = CopyDirectoryContents(sourceDir, tempDir);

                string[] copiedMissingFiles;
                if (!IsCodeIndexDirectoryComplete(tempDir, out copiedMissingFiles))
                {
                    DeleteDirectoryIfExists(tempDir);
                    AIBridgeLogger.LogWarning("[SkillInstaller] Refused to install incomplete CodeIndex cache. Missing: " + FormatMissingFiles(copiedMissingFiles));
                    return 0;
                }

                if (Directory.Exists(targetDir))
                {
                    Directory.Move(targetDir, backupDir);
                }

                Directory.Move(tempDir, targetDir);
                DeleteDirectoryIfExists(backupDir);
                return copied;
            }
            catch (Exception ex)
            {
                RestoreCodeIndexBackup(targetDir, backupDir);
                DeleteDirectoryIfExists(tempDir);
                AIBridgeLogger.LogWarning($"[SkillInstaller] Failed to copy {CODE_INDEX_FOLDER}: {ex.Message}");
                return 0;
            }
        }

        internal static int RefreshCodeIndexCache(string sourceCliDir, string targetCliDir)
        {
            ShutdownCodeIndexDaemonBeforeCacheRefresh();
            return CopyCodeIndexToCache(sourceCliDir, targetCliDir);
        }

        private static int CopyDirectoryContents(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            var copied = 0;
            foreach (var filePath in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(filePath);
                var targetFile = Path.Combine(targetDir, fileName);
                File.Copy(filePath, targetFile, true);
                if (string.Equals(fileName, GetCodeIndexExecutableName(), StringComparison.OrdinalIgnoreCase))
                {
                    EnsureExecutablePermission(targetFile);
                }

                copied++;
            }

            foreach (var childDir in Directory.GetDirectories(sourceDir))
            {
                copied += CopyDirectoryContents(childDir, Path.Combine(targetDir, Path.GetFileName(childDir)));
            }

            return copied;
        }

        private static void ShutdownCodeIndexDaemonBeforeCacheRefresh()
        {
            try
            {
                _codeIndexDaemonShutdown(CODE_INDEX_DAEMON_SHUTDOWN_CLEANUP_MODE, CODE_INDEX_DAEMON_SHUTDOWN_TIMEOUT_MS);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("[SkillInstaller] Failed to shutdown CodeIndex daemon before cache refresh: " + ex.Message);
            }
        }

        private static void RestoreCodeIndexBackup(string targetDir, string backupDir)
        {
            if (string.IsNullOrEmpty(backupDir) || !Directory.Exists(backupDir) || Directory.Exists(targetDir))
            {
                return;
            }

            try
            {
                Directory.Move(backupDir, targetDir);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("[SkillInstaller] Failed to restore previous CodeIndex cache: " + ex.Message);
            }
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        internal static void SetCodeIndexDaemonShutdownForTests(Action<string, int> shutdownHandler)
        {
            _codeIndexDaemonShutdown = shutdownHandler ?? AIBridgeCodeIndexEditorUtility.ShutdownDaemon;
        }

        internal static void ResetCodeIndexDaemonShutdownForTests()
        {
            _codeIndexDaemonShutdown = AIBridgeCodeIndexEditorUtility.ShutdownDaemon;
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
            var hardcodedPath = $"Packages/{PACKAGE_NAME}/Tools~/CLI/AIBridgeCLI.exe";
            var fixedCliPath = GetProjectRelativeCliPath();
            if (content.Contains(hardcodedPath))
            {
                content = content.Replace(hardcodedPath, fixedCliPath);
            }

            content = ApplyProjectTemplateTokens(content);
            WriteTextIfChanged(targetFile, content, Utf8NoBom);
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
                        GenerateWorkflowPreferenceFilesForTarget(projectRoot, target);
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

            var changed = GenerateAndWriteSkillFileIfChanged(sourceSkillPath, targetDir, skillFilePath);
            if (!existed)
            {
                return IntegrationAction.CreatedFile;
            }

            return changed ? IntegrationAction.UpdatedBlock : IntegrationAction.AlreadyUpToDate;
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

                // 子目录 Skill 独立安装，并在无内容变化时保持目标目录时间戳稳定。
                var generatedFiles = string.Equals(skillName, WorkflowPreferenceRenderer.DevelopmentWorkflowSkillName, StringComparison.OrdinalIgnoreCase)
                    ? GetWorkflowGeneratedRelativePaths()
                    : null;
                SyncDirectory(sourceSkillDir, targetSkillDir, true, generatedFiles);
                installedSkillFiles.Add(targetSkillFile);
            }

            return installedSkillFiles;
        }

        internal static List<string> GenerateWorkflowPreferenceFilesForSelectedTargets(string projectRoot)
        {
            var generatedFiles = new List<string>();
            foreach (var target in GetSelectedTargets(projectRoot))
            {
                generatedFiles.AddRange(GenerateWorkflowPreferenceFilesForTarget(projectRoot, target));
            }

            return generatedFiles;
        }

        private static List<string> GenerateWorkflowPreferenceFilesForTarget(string projectRoot, AssistantIntegrationTarget target)
        {
            var generatedFiles = new List<string>();
            var sourceSkillRoot = GetSourceSkillRootPath();
            var targetSkillRoot = GetTargetSkillRootDirectory(projectRoot, target);
            if (string.IsNullOrEmpty(sourceSkillRoot) || string.IsNullOrEmpty(targetSkillRoot))
            {
                return generatedFiles;
            }

            if (IsUnsafeSkillInstallTarget(sourceSkillRoot, targetSkillRoot))
            {
                AIBridgeLogger.LogWarning("[SkillInstaller] Refused to generate workflow preference files into package source Skill~ directory: " + targetSkillRoot);
                return generatedFiles;
            }

            var workflowSkillDirectory = Path.Combine(targetSkillRoot, WorkflowPreferenceRenderer.DevelopmentWorkflowSkillName);
            if (!Directory.Exists(workflowSkillDirectory))
            {
                return generatedFiles;
            }

            SyncWorkflowSkillEntryFile(sourceSkillRoot, workflowSkillDirectory);
            SyncWorkflowBranchDocuments(sourceSkillRoot, workflowSkillDirectory);
            var preferencesPath = Path.Combine(workflowSkillDirectory, WorkflowPreferenceRenderer.PreferencesRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var branchSelectionPath = Path.Combine(workflowSkillDirectory, WorkflowPreferenceRenderer.BranchSelectionRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var graphManifestPath = Path.Combine(workflowSkillDirectory, WorkflowPreferenceRenderer.GraphManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var implementationBranchManifestPath = Path.Combine(workflowSkillDirectory, WorkflowPreferenceRenderer.ImplementationBranchManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
            EnsureParentDirectory(preferencesPath);
            EnsureParentDirectory(branchSelectionPath);
            EnsureParentDirectory(graphManifestPath);
            EnsureParentDirectory(implementationBranchManifestPath);
            WriteTextIfChanged(preferencesPath, WorkflowPreferenceRenderer.RenderPreferences(projectRoot, target), Utf8NoBom);
            WriteTextIfChanged(branchSelectionPath, WorkflowPreferenceRenderer.RenderBranchSelection(projectRoot, target), Utf8NoBom);
            WriteTextIfChanged(graphManifestPath, WorkflowPreferenceRenderer.RenderGraphManifest(projectRoot, target), Utf8NoBom);
            WriteTextIfChanged(implementationBranchManifestPath, WorkflowPreferenceRenderer.RenderImplementationBranchManifest(), Utf8NoBom);
            generatedFiles.Add(preferencesPath);
            generatedFiles.Add(branchSelectionPath);
            generatedFiles.Add(graphManifestPath);
            generatedFiles.Add(implementationBranchManifestPath);
            return generatedFiles;
        }

        private static void SyncWorkflowSkillEntryFile(string sourceSkillRoot, string workflowSkillDirectory)
        {
            var sourceSkillPath = Path.Combine(
                sourceSkillRoot,
                WorkflowPreferenceRenderer.DevelopmentWorkflowSkillName,
                SKILL_FILE_NAME);
            if (!File.Exists(sourceSkillPath))
            {
                return;
            }

            var targetSkillPath = Path.Combine(workflowSkillDirectory, SKILL_FILE_NAME);
            GenerateAndWriteSkillFile(sourceSkillPath, workflowSkillDirectory, targetSkillPath);
        }

        private static void SyncWorkflowBranchDocuments(string sourceSkillRoot, string workflowSkillDirectory)
        {
            var sourceBranchesDirectory = Path.Combine(
                sourceSkillRoot,
                WorkflowPreferenceRenderer.DevelopmentWorkflowSkillName,
                "references",
                "branches");
            if (!Directory.Exists(sourceBranchesDirectory))
            {
                return;
            }

            var targetBranchesDirectory = Path.Combine(workflowSkillDirectory, "references", "branches");
            SyncDirectory(sourceBranchesDirectory, targetBranchesDirectory, true, null);
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

        private static void EnsureParentDirectory(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
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

        private static bool GenerateAndWriteSkillFileIfChanged(string sourcePath, string targetDir, string targetFile)
        {
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var content = File.ReadAllText(sourcePath, System.Text.Encoding.UTF8);
            var hardcodedPath = $"Packages/{PACKAGE_NAME}/Tools~/CLI/AIBridgeCLI.exe";
            var fixedCliPath = GetProjectRelativeCliPath();
            if (content.Contains(hardcodedPath))
            {
                content = content.Replace(hardcodedPath, fixedCliPath);
            }

            content = ApplyProjectTemplateTokens(content);
            return WriteTextIfChanged(targetFile, content, Utf8NoBom);
        }

        private static HashSet<string> GetWorkflowGeneratedRelativePaths()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                NormalizeRelativePath(WorkflowPreferenceRenderer.PreferencesRelativePath),
                NormalizeRelativePath(WorkflowPreferenceRenderer.BranchSelectionRelativePath),
                NormalizeRelativePath(WorkflowPreferenceRenderer.GraphManifestRelativePath),
                NormalizeRelativePath(WorkflowPreferenceRenderer.ImplementationBranchManifestRelativePath)
            };
        }

        private static void SyncDirectory(string sourceDir, string targetDir, bool removeExtraEntries, HashSet<string> skippedRelativePaths)
        {
            SyncDirectory(sourceDir, targetDir, sourceDir, removeExtraEntries, skippedRelativePaths);
        }

        private static void SyncDirectory(
            string sourceRoot,
            string targetDir,
            string currentSourceDir,
            bool removeExtraEntries,
            HashSet<string> skippedRelativePaths)
        {
            Directory.CreateDirectory(targetDir);
            var expectedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var filePath in Directory.GetFiles(currentSourceDir))
            {
                if (filePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(Path.GetFileName(filePath), SKILL_INSTALL_MANIFEST_FILE_NAME, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = Path.GetFileName(filePath);
                if (ShouldSkipSyncFile(sourceRoot, filePath, skippedRelativePaths))
                {
                    expectedEntries.Add(fileName);
                    continue;
                }

                expectedEntries.Add(fileName);
                var targetFile = Path.Combine(targetDir, fileName);
                CopyFileIfChanged(filePath, targetFile);
            }

            foreach (var childDir in Directory.GetDirectories(currentSourceDir))
            {
                var dirName = Path.GetFileName(childDir);
                expectedEntries.Add(dirName);
                SyncDirectory(sourceRoot, Path.Combine(targetDir, dirName), childDir, removeExtraEntries, skippedRelativePaths);
            }

            if (!removeExtraEntries)
            {
                return;
            }

            foreach (var targetEntry in Directory.GetFileSystemEntries(targetDir))
            {
                if (!expectedEntries.Contains(Path.GetFileName(targetEntry)) && !ShouldKeepExtraSyncEntry(targetEntry))
                {
                    DeleteFileSystemEntry(targetEntry);
                }
            }
        }

        private static bool ShouldKeepExtraSyncEntry(string targetEntry)
        {
            if (Directory.Exists(targetEntry))
            {
                return DirectoryContainsGeneratedFiles(targetEntry);
            }

            return IsGeneratedFile(targetEntry);
        }

        private static bool DirectoryContainsGeneratedFiles(string directory)
        {
            foreach (var filePath in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (IsGeneratedFile(filePath))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsGeneratedFile(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            if (string.Equals(fileName, Path.GetFileName(WorkflowPreferenceRenderer.PreferencesRelativePath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, Path.GetFileName(WorkflowPreferenceRenderer.BranchSelectionRelativePath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, Path.GetFileName(WorkflowPreferenceRenderer.GraphManifestRelativePath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, Path.GetFileName(WorkflowPreferenceRenderer.ImplementationBranchManifestRelativePath), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                return File.Exists(filePath)
                       && File.ReadLines(filePath).FirstOrDefault() == SkillDocumentGenerator.GeneratedHeader;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldSkipSyncFile(string sourceRoot, string filePath, HashSet<string> skippedRelativePaths)
        {
            if (skippedRelativePaths == null || skippedRelativePaths.Count == 0)
            {
                return false;
            }

            return skippedRelativePaths.Contains(NormalizeRelativePath(GetRelativeFilePath(sourceRoot, filePath)));
        }

        private static void CopyFileIfChanged(string sourceFile, string targetFile)
        {
            if (!IsFileCopyNeeded(sourceFile, targetFile))
            {
                return;
            }

            var targetDir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Copy(sourceFile, targetFile, true);
        }

        internal static bool WriteTextIfChanged(string path, string content, Encoding encoding)
        {
            if (File.Exists(path))
            {
                try
                {
                    if (File.ReadAllBytes(path).SequenceEqual(GetEncodedBytes(content, encoding)))
                    {
                        return false;
                    }
                }
                catch
                {
                    // Fall through and rewrite unreadable or invalid files.
                }
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, content, encoding);
            return true;
        }

        private static byte[] GetEncodedBytes(string content, Encoding encoding)
        {
            var preamble = encoding.GetPreamble();
            var body = encoding.GetBytes(content);
            if (preamble == null || preamble.Length == 0)
            {
                return body;
            }

            var bytes = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
            return bytes;
        }

        private static string NormalizeRelativePath(string path)
        {
            return string.IsNullOrEmpty(path)
                ? string.Empty
                : path.Replace('\\', '/').Trim('/');
        }

        private static void DeleteFileSystemEntry(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                return;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
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
            var cliPath = GetProjectRelativeCliPath();
            var language = AIBridgeProjectSettings.Instance.EditorLanguage;
            var unityVersion = GetCurrentUnityVersionText();
            var csharpLanguageVersion = GetCurrentCSharpLanguageVersionText();
            var workflowSkillDocPath = GetSkillDocumentPath(projectRoot, target, "aibridge-development-workflow");
            var skillRootPath = GetSkillRootDocumentPath(projectRoot, target);
            var harnessCapabilityRule = HarnessCapabilitySnapshot.BuildRootRuleSummary(projectRoot, selectedTargets, language);
            return new Dictionary<string, string>
            {
                { "CLI_PATH", cliPath },
                { "CLI_PATH_RULE", AIBridgeEditorText.For(
                    language,
                    "`$CLI` points to the project-local AIBridge CLI. In PowerShell, assign `$CLI = \"" + cliPath + "\"`, then run `& $CLI <command> [action] [options]`.",
                    "`$CLI` 指向项目本地 AIBridge CLI。PowerShell 中可先设 `$CLI = \"" + cliPath + "\"`，再用 `& $CLI <command> [action] [options]` 调用。") },
                { "COMMON_COMMANDS_TITLE", AIBridgeEditorText.For(language, "Common Commands", "常用命令") },
                { "HOST_EXEC_TITLE", AIBridgeEditorText.For(language, "Host Exec", "Host Exec") },
                { "HOST_EXEC_RULE", AIBridgeEditorText.For(
                    language,
                    "Use `$CLI exec run --stdin` when external host tool arguments are non-trivial, include regex/globs/JSON/spaces, require stdin, need timeout/output limits, or run multiple jobs. AIBridge commands such as `harness status`, `compile unity`, `get_logs`, `asset`, `inspector`, `runtime`, `workflow`, `multi`, and `code execute` run directly. `exec run --stdin` reads JSON from stdin; use `command`, not `cmd`, and do not append raw shell commands after `--stdin`. When the payload contains quotes, backslashes, or regex, build a PowerShell object and pipe `ConvertTo-Json` output, or use `--request-file`. Use `$CLI exec batch --stdin` only for multiple external host jobs.",
                    "当外部 host 工具参数不简单、包含正则/通配符/JSON/空格、需要 stdin、需要超时/输出限制或多个 jobs 时，优先用 `$CLI exec run --stdin`。`harness status`、`compile unity`、`get_logs`、`asset`、`inspector`、`runtime`、`workflow`、`multi`、`code execute` 这类 AIBridge 命令直接调用。`exec run --stdin` 从 stdin 读取 JSON；使用 `command`，不是 `cmd`，也不要在 `--stdin` 后面追加裸 shell 命令。请求里如果包含引号、反斜杠或正则，先用 PowerShell 对象再 `ConvertTo-Json`，或者改用 `--request-file`。`$CLI exec batch --stdin` 只用于多个外部 host 任务。") },
                { "PROJECT_VERSION_TITLE", AIBridgeEditorText.For(language, "Project Version", "项目版本") },
                { "UNITY_VERSION", unityVersion },
                { "CSHARP_LANGUAGE_VERSION", csharpLanguageVersion },
                { "UNITY_VERSION_RULE", AIBridgeEditorText.For(language, "Current Unity version: " + unityVersion, "当前项目 Unity 版本：" + unityVersion) },
                { "CSHARP_VERSION_RULE", AIBridgeEditorText.For(language, "Current C# language requirement: compatible with " + csharpLanguageVersion + "; do not use newer syntax.", "当前项目 C# 语言版本要求：兼容 " + csharpLanguageVersion + "，禁止使用更高版本语法。") },
                { "CAPABILITIES_TITLE", AIBridgeEditorText.For(language, "Current Capabilities", "当前能力状态") },
                { "HARNESS_CAPABILITY_RULE", harnessCapabilityRule },
                { "CODE_INDEX_CAPABILITY_RULE", BuildCodeIndexCapabilityRule(language) },
                { "ROUTING_TITLE", AIBridgeEditorText.For(language, "Routing Rules", "路由原则") },
                { "QUICK_TASK_RULE", AIBridgeEditorText.For(language, "Quick tasks: answer or execute directly without loading `aibridge-development-workflow` for pure Q&A, code explanation, simple search/display, or tasks with no code or Unity asset changes and no review, validation, or root-cause verdict.", "快速任务：纯问答、代码解释、简单查找/显示，且不需要修改代码或 Unity 资源、不输出审查/验证/根因结论时，直接回答或执行，不加载 `aibridge-development-workflow`。") },
                { "DEVELOPMENT_TASK_RULE", AIBridgeEditorText.For(language, "Workflow tasks: load `aibridge-development-workflow` first when the task requires code or Unity asset changes, persistent AGENTS/Skill/workflow rule changes, root-cause debugging, Runtime/log evidence, or a risk review/validation verdict.", "工作流任务：当任务需要修改代码或 Unity 资源、修改持久化 AGENTS/Skill/workflow 规则、调试根因、采集 Runtime/日志证据，或输出风险审查/验证结论时，必须优先加载 `aibridge-development-workflow`。") },
                { "WORKFLOW_SKILL_RULE", AIBridgeEditorText.For(language, "After entering the workflow, `aibridge-development-workflow` probes harness readiness, chooses the task branch, and decides whether to load additional Skills.", "进入工作流后，由 `aibridge-development-workflow` 探测 harness 能力、选择任务分支，并决定是否继续加载其它 Skill。") },
                { "SKILL_LOADING_TITLE", AIBridgeEditorText.For(language, "Skill Loading", "Skill 加载") },
                { "WORKFLOW_SKILL_ENTRY", AIBridgeEditorText.For(language, "Load `aibridge-development-workflow` from `" + workflowSkillDocPath + "` before workflow tasks.", "工作流任务先加载 `" + workflowSkillDocPath + "` 中的 `aibridge-development-workflow`。") },
                { "SKILL_ROOT_RULE", AIBridgeEditorText.For(language, "AIBridge Skills are installed under `" + skillRootPath + "/<skill-name>/SKILL.md`; load sibling Skills from that directory when this root rule or the workflow requires them.", "AIBridge Skills 安装在 `" + skillRootPath + "/<skill-name>/SKILL.md`；当本根规则或工作流要求时，从该目录加载同级 Skill。") }
            };
        }

        private static string BuildCodeIndexCapabilityRule(AIBridgeEditorLanguage language)
        {
            if (AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex)
            {
                return AIBridgeEditorText.For(
                    language,
                    "Code Index: enabled. Use `aibridge-code-index` only for fast C# declaration-name lookup when the query can be expressed as `symbol` or `definition`. Treat it as a path and declaration locator for class, interface, enum, field, property, method, constructor, or delegate names, then read the returned `.cs` files yourself for follow-up analysis. For Unity imported asset or script asset name/type lookup, use `asset search/find --format paths` when AIBridge and the Editor are available.",
                    "Code Index：已启用。`aibridge-code-index` 只用于快速 C# 声明名检索，查询面仅限 `symbol` 和 `definition`。它的职责是把类、接口、枚举、字段、属性、方法、构造器、delegate 等声明名快速定位到声明位置和 `.cs` 文件；拿到路径后继续由 AI 自己读取文件分析。Unity 已导入资源或脚本资源的名称/类型查找中，当 AIBridge 和 Editor 可用时使用 `asset search/find --format paths`。");
            }

            return AIBridgeEditorText.For(
                language,
                "Code Index: disabled. Do not call `code_index`; use `asset search/find --format paths` for Unity imported asset name/type lookup when AIBridge and the Editor are available.",
                "Code Index：已关闭。不要调用 `code_index`；当 AIBridge 和 Editor 可用时，Unity 已导入资源的名称/类型查找使用 `asset search/find --format paths`。");
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

        internal static string ApplyProjectTemplateTokens(string content)
        {
            return content
                .Replace("{{UNITY_VERSION}}", GetCurrentUnityVersionText())
                .Replace("{{CSHARP_LANGUAGE_VERSION}}", GetCurrentCSharpLanguageVersionText())
                .Replace("{{AIBRIDGE_CLI_PATH}}", GetProjectRelativeCliPath());
        }

        private static string GetProjectRelativeCliPath()
        {
            return "./" + CLI_CACHE_FOLDER + "/" + GetCliExecutableName();
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
                if (!ShouldLogResult(result))
                {
                    continue;
                }

                AIBridgeLogger.LogInfo($"[SkillInstaller] {BuildCompactResultMessage(result)}");
            }
        }

        private static bool ShouldLogResult(AssistantIntegrationResult result)
        {
            if (result == null)
            {
                return false;
            }

            return IsVisibleAction(result.SkillFileAction) || IsVisibleAction(result.RootRuleAction);
        }

        private static bool IsVisibleAction(IntegrationAction action)
        {
            return action != IntegrationAction.None && action != IntegrationAction.AlreadyUpToDate;
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
                        AIBridgeEditorText.T("No assistant tools selected for installation. Open AIBridge/Workflows and choose at least one tool.", "未选择要安装的 AI 工具。请打开 AIBridge/Workflows 并至少选择一个工具。"),
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
