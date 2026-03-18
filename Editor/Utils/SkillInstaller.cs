using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Automatically installs the AIBridge skill documentation to the project's .claude/skills directory.
    /// This allows Claude Code to discover and use the skill for Unity Editor operations.
    /// </summary>
    [InitializeOnLoad]
    public static class SkillInstaller
    {
        private const string SKILL_FILE_NAME = "SKILL.md";
        private const string PACKAGE_NAME = "cn.lys.aibridge";
        private const string CLI_CACHE_FOLDER = "AIBridgeCache/CLI";
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
        
        /// <summary>
        /// Copy CLI files to AIBridgeCache/CLI directory.
        /// This provides a fixed, stable path for AI assistants to use.
        /// </summary>
        private static void CopyCliToCacheIfNeeded(string projectRoot)
        {
            var platformRID = GetPlatformRID();
            var cliExeName = GetCliExecutableName();
            var targetCliDir = Path.Combine(projectRoot, CLI_CACHE_FOLDER);
            var targetCliExe = Path.Combine(targetCliDir, cliExeName);
            
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
            
            // Check if we need to copy (target doesn't exist or source is newer)
            bool needsCopy = !File.Exists(targetCliExe);
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
            
            // Create target directory
            if (!Directory.Exists(targetCliDir))
            {
                Directory.CreateDirectory(targetCliDir);
            }
            
            // Copy all CLI files
            int copiedCount = 0;
            
            // Copy executable first
            try
            {
                File.Copy(sourceCliExe, targetCliExe, true);
                copiedCount++;
#if !UNITY_EDITOR_WIN
                // Set executable permission on Unix platforms
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
                catch { }
#endif
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning($"[SkillInstaller] Failed to copy {cliExeName}: {ex.Message}");
            }
            
            // Copy other files
            foreach (var fileName in CLI_FILES)
            {
                var sourceFile = Path.Combine(sourceCliDir, fileName);
                var targetFile = Path.Combine(targetCliDir, fileName);
                
                if (File.Exists(sourceFile))
                {
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
            }
            
            if (copiedCount > 0)
            {
                AIBridgeLogger.LogInfo($"[SkillInstaller] Copied {copiedCount} CLI files to: {targetCliDir}");
            }
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
            // Try to find the package in Packages folder
            var projectRoot = GetProjectRoot();

            // Method 1: Direct package path (for local/embedded packages)
            var directPath = Path.Combine(projectRoot, "Packages", PACKAGE_NAME, "Skill~", SKILL_FILE_NAME);
            if (File.Exists(directPath))
            {
                return directPath;
            }

            // Method 2: Use PackageInfo to resolve package path (for git/registry packages)
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PACKAGE_NAME}");
            if (packageInfo != null)
            {
                var packagePath = Path.Combine(packageInfo.resolvedPath, "Skill~", SKILL_FILE_NAME);
                if (File.Exists(packagePath))
                {
                    return packagePath;
                }
            }

            return null;
        }

        /// <summary>
        /// Copy skill file to target location with CLI path replacement.
        /// This ensures the CLI path in SKILL.md uses the fixed ./AIBridgeCache/CLI location for AI-facing docs.
        /// </summary>
        private static void CopySkillFile(string sourcePath, string targetDir, string targetFile)
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
                AIBridgeLogger.LogInfo($"[SkillInstaller] Replaced CLI path: {hardcodedPath} -> {fixedCliPath}");
            }

            File.WriteAllText(targetFile, content, System.Text.Encoding.UTF8);
        }

        private static List<AssistantIntegrationResult> InstallAssistantIntegrations(string projectRoot)
        {
            var results = new List<AssistantIntegrationResult>();
            var sourceSkillPath = GetSourceSkillPath();
            foreach (var target in AssistantIntegrationRegistry.GetTargets())
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
                    }

                    var template = RuleTemplateLoader.Load(projectRoot, target.RootRuleTemplateRelativePath);
                    var tokens = BuildTemplateTokens(projectRoot, target);
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

        private static IntegrationAction InstallSkillFileForTarget(string projectRoot, AssistantIntegrationTarget target, string sourceSkillPath, out string skillFilePath)
        {
            skillFilePath = null;
            if (string.IsNullOrEmpty(sourceSkillPath) || !File.Exists(sourceSkillPath))
            {
                AIBridgeLogger.LogWarning($"[SkillInstaller] Source skill file not found. Expected at: Packages/{PACKAGE_NAME}/Skill~/{SKILL_FILE_NAME}");
                return IntegrationAction.SkippedMissing;
            }

            var targetDir = Path.Combine(projectRoot, target.SkillDirectoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
            skillFilePath = Path.Combine(targetDir, target.SkillFileName);
            if (File.Exists(skillFilePath))
            {
                var sourceTime = File.GetLastWriteTimeUtc(sourceSkillPath);
                var targetTime = File.GetLastWriteTimeUtc(skillFilePath);
                if (sourceTime <= targetTime)
                {
                    return IntegrationAction.AlreadyUpToDate;
                }

                CopySkillFile(sourceSkillPath, targetDir, skillFilePath);
                return IntegrationAction.UpdatedBlock;
            }

            CopySkillFile(sourceSkillPath, targetDir, skillFilePath);
            return IntegrationAction.CreatedFile;
        }

        private static Dictionary<string, string> BuildTemplateTokens(string projectRoot, AssistantIntegrationTarget target)
        {
            var cliExeName = GetCliExecutableName();
            return new Dictionary<string, string>
            {
                { "CLI_PATH", "./" + CLI_CACHE_FOLDER + "/" + cliExeName },
                { "CLI_EXE_NAME", cliExeName },
                { "CLI_CACHE_DIR", CLI_CACHE_FOLDER },
                { "SKILL_DOC_PATH", target.SupportsSkillDirectory ? "/" + target.GetSkillFileRelativePath() : "/.claude/skills/aibridge/SKILL.md" },
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
            return $"{result.AssistantId}: skill={result.SkillFileAction}, rule={result.RootRuleAction}";
        }

        private static string BuildResultMessage(AssistantIntegrationTarget target, AssistantIntegrationResult result)
        {
            return target.DisplayName + ": skill=" + result.SkillFileAction + ", rule=" + result.RootRuleAction;
        }

        private static string BuildManualInstallSummary(IEnumerable<AssistantIntegrationResult> results)
        {
            var builder = new StringBuilder();
            builder.AppendLine("AIBridge integrations updated:");
            builder.AppendLine();

            foreach (var result in results)
            {
                builder.AppendLine(result.AssistantId + ":");
                if (!string.IsNullOrEmpty(result.SkillFilePath))
                {
                    builder.AppendLine("- Skill: " + result.SkillFileAction + " (" + result.SkillFilePath + ")");
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

        /// <summary>
        /// Manually trigger skill installation (for menu item)
        /// </summary>
        [MenuItem("AIBridge/Install Skill Documentation")]
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
