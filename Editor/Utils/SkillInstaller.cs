using System;
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
        private const string SKILL_FOLDER_NAME = "aibridge";
        private const string SKILL_FILE_NAME = "SKILL.md";
        private const string PACKAGE_NAME = "cn.lys.aibridge";
        private const string CLAUDE_MD_FILE = "CLAUDE.md";
        private const string AIBRIDGE_SECTION_MARKER = "## AIBridge Unity Integration";
        
        // Fixed CLI path in AIBridgeCache directory
        private const string CLI_CACHE_FOLDER = "AIBridgeCache/CLI";
        private const string CLI_EXE_NAME = "AIBridgeCLI.exe";
        private static readonly string[] CLI_FILES = new[]
        {
            "AIBridgeCLI.exe",
            "AIBridgeCLI.dll",
            "AIBridgeCLI.deps.json",
            "AIBridgeCLI.runtimeconfig.json",
            "AIBridgeCLI.pdb",
            "Newtonsoft.Json.dll"
        };

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
                
                // Step 1: Copy CLI to AIBridgeCache/CLI (fixed location)
                CopyCliToCacheIfNeeded(projectRoot);
                
                var targetDir = Path.Combine(projectRoot, ".claude", "skills", SKILL_FOLDER_NAME);
                var targetFile = Path.Combine(targetDir, SKILL_FILE_NAME);

                // Check if skill file already exists
                if (File.Exists(targetFile))
                {
                    // Check if source is newer and update if needed
                    var sourceFile = GetSourceSkillPath();
                    if (!string.IsNullOrEmpty(sourceFile) && File.Exists(sourceFile))
                    {
                        var sourceTime = File.GetLastWriteTimeUtc(sourceFile);
                        var targetTime = File.GetLastWriteTimeUtc(targetFile);

                        if (sourceTime > targetTime)
                        {
                            CopySkillFile(sourceFile, targetDir, targetFile);
                            AIBridgeLogger.LogInfo($"[SkillInstaller] Updated skill documentation: {targetFile}");
                        }
                    }
                }
                else
                {
                    // Install skill documentation
                    var sourcePath = GetSourceSkillPath();
                    if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                    {
                        AIBridgeLogger.LogWarning($"[SkillInstaller] Source skill file not found. Expected at: Packages/{PACKAGE_NAME}/Skill~/{SKILL_FILE_NAME}");
                        return;
                    }

                    CopySkillFile(sourcePath, targetDir, targetFile);
                    AIBridgeLogger.LogInfo($"[SkillInstaller] Installed skill documentation to: {targetFile}");
                }

                // Update CLAUDE.md with skill index
                UpdateClaudeMdIfNeeded(projectRoot);
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
            var targetCliDir = Path.Combine(projectRoot, CLI_CACHE_FOLDER);
            var targetCliExe = Path.Combine(targetCliDir, CLI_EXE_NAME);
            
            // Find source CLI directory
            var sourceCliDir = GetSourceCliDirectory();
            if (string.IsNullOrEmpty(sourceCliDir))
            {
                AIBridgeLogger.LogWarning("[SkillInstaller] Source CLI directory not found");
                return;
            }
            
            var sourceCliExe = Path.Combine(sourceCliDir, CLI_EXE_NAME);
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
        private static string GetSourceCliDirectory()
        {
            var projectRoot = GetProjectRoot();
            
            // Method 1: Direct package path (for local/embedded packages)
            var directPath = Path.Combine(projectRoot, "Packages", PACKAGE_NAME, "Tools~", "CLI");
            if (Directory.Exists(directPath))
            {
                return directPath;
            }
            
            // Method 2: Use PackageInfo for resolved path (for git/registry packages)
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PACKAGE_NAME}");
            if (packageInfo != null)
            {
                var resolvedPath = Path.Combine(packageInfo.resolvedPath, "Tools~", "CLI");
                if (Directory.Exists(resolvedPath))
                {
                    return resolvedPath;
                }
            }
            
            return null;
        }

        /// <summary>
        /// Update CLAUDE.md with AIBridge skill index if not already present
        /// </summary>
        private static void UpdateClaudeMdIfNeeded(string projectRoot)
        {
            var claudeMdPath = Path.Combine(projectRoot, CLAUDE_MD_FILE);

            // Check if CLAUDE.md exists
            if (!File.Exists(claudeMdPath))
            {
                AIBridgeLogger.LogInfo($"[SkillInstaller] CLAUDE.md not found, skipping skill index update");
                return;
            }

            try
            {
                var content = File.ReadAllText(claudeMdPath, Encoding.UTF8);

                // Check if AIBridge section already exists
                if (content.Contains(AIBRIDGE_SECTION_MARKER))
                {
                    return;
                }

                // Append AIBridge skill index
                var skillIndex = GetAIBridgeSkillIndex();
                content = content.TrimEnd() + "\n\n" + skillIndex;

                File.WriteAllText(claudeMdPath, content, Encoding.UTF8);
                AIBridgeLogger.LogInfo($"[SkillInstaller] Added AIBridge skill index to CLAUDE.md");
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning($"[SkillInstaller] Failed to update CLAUDE.md: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the AIBridge skill index content for CLAUDE.md
        /// </summary>
        private static string GetAIBridgeSkillIndex()
        {
            // Use fixed CLI path in AIBridgeCache directory
            var cliPath = CLI_CACHE_FOLDER + "/" + CLI_EXE_NAME;
            var q = "\""; // Quote character for bash commands

            return $@"{AIBRIDGE_SECTION_MARKER}

**Skill**: `aibridge`

**Activation Keywords**: Unity log, compile Unity, modify asset, query asset, GameObject, Transform, Component, Scene, Prefab, screenshot, GIF

**When to Activate**:
- Get Unity console logs or compilation errors
- Compile Unity project and check results
- Create/modify/delete GameObjects in scene
- Manipulate Transform (position/rotation/scale)
- Add/remove/modify Components
- Load/save scenes, query scene hierarchy
- Instantiate or modify Prefabs
- Search assets in AssetDatabase
- Capture screenshots or record GIFs (Play Mode)

**Quick Reference**:
```bash
# CLI Path
{cliPath}

# Common Commands
AIBridgeCLI.exe compile unity --raw          # Compile and get errors
AIBridgeCLI.exe get_logs --logType Error     # Get error logs
AIBridgeCLI.exe asset search --mode script --keyword {q}Player{q}  # Search scripts
AIBridgeCLI.exe gameobject create --name {q}Cube{q} --primitiveType Cube
AIBridgeCLI.exe transform set_position --path {q}Player{q} --x 0 --y 1 --z 0
```

**Skill Documentation**: [AIBridge Skill](/.claude/skills/aibridge/SKILL.md)
";
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
        /// This ensures the CLI path in SKILL.md uses the fixed AIBridgeCache/CLI location.
        /// </summary>
        private static void CopySkillFile(string sourcePath, string targetDir, string targetFile)
        {
            // Create directory if not exists
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // Read source content
            var content = File.ReadAllText(sourcePath, System.Text.Encoding.UTF8);

            // Replace hardcoded path with fixed CLI cache path
            var hardcodedPath = $"Packages/{PACKAGE_NAME}/Tools~/CLI/AIBridgeCLI.exe";
            var fixedCliPath = CLI_CACHE_FOLDER + "/" + CLI_EXE_NAME;
            if (content.Contains(hardcodedPath))
            {
                content = content.Replace(hardcodedPath, fixedCliPath);
                AIBridgeLogger.LogInfo($"[SkillInstaller] Replaced CLI path: {hardcodedPath} -> {fixedCliPath}");
            }

            // Write to target with UTF-8 encoding
            File.WriteAllText(targetFile, content, System.Text.Encoding.UTF8);
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
                
                // Copy CLI to cache directory first
                CopyCliToCacheIfNeeded(projectRoot);
                
                var targetDir = Path.Combine(projectRoot, ".claude", "skills", SKILL_FOLDER_NAME);
                var targetFile = Path.Combine(targetDir, SKILL_FILE_NAME);

                var sourcePath = GetSourceSkillPath();
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    EditorUtility.DisplayDialog("AIBridge", "Source skill file not found.", "OK");
                    return;
                }

                CopySkillFile(sourcePath, targetDir, targetFile);

                // Also update CLAUDE.md
                UpdateClaudeMdIfNeeded(projectRoot);

                EditorUtility.DisplayDialog("AIBridge", $"Skill documentation installed to:\n{targetFile}\n\nCLI copied to: {CLI_CACHE_FOLDER}\n\nCLAUDE.md has been updated with skill index.", "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("AIBridge", $"Failed to install: {ex.Message}", "OK");
            }
        }
    }
}
