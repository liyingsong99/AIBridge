using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private const string PACKAGE_NAME = "cn.lys.aibridge";
        private const string CLI_CACHE_FOLDER = ".aibridge/cli";
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

                if (!EnsureEditorLanguageInitialized())
                {
                    return;
                }

                // 检查是否启用自动安装
                if (!AIBridgeProjectSettings.Instance.AutoInstallSkills)
                {
                    return;
                }

                var projectRoot = GetProjectRoot();
                var targets = GetSelectedTargets(projectRoot);
                
                // 清理未勾选目标的注入内容
                CleanupUnselectedTargets(projectRoot, targets);
                
                if (targets.Count == 0)
                {
                    return;
                }

                CleanupUnselectedTargets(projectRoot, targets);
                CopyCliToCacheIfNeeded(projectRoot);
                var results = InstallAssistantIntegrations(projectRoot, targets);
                SkillPluginAdapter.GenerateForTargets(projectRoot, targets);
                LogResults(results);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"[SkillInstaller] Failed to install skill documentation: {ex.Message}");
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

            File.WriteAllText(targetFile, content, System.Text.Encoding.UTF8);
        }

        private static List<AssistantIntegrationResult> InstallAssistantIntegrations(string projectRoot)
        {
            return InstallAssistantIntegrations(projectRoot, AssistantIntegrationRegistry.GetTargets());
        }

        private static List<AssistantIntegrationResult> InstallAssistantIntegrations(string projectRoot, IEnumerable<AssistantIntegrationTarget> targets)
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

            var resolvedSkillDirectory = target.GetResolvedSkillDirectoryRelativePath(projectRoot);
            var targetDir = Path.Combine(projectRoot, resolvedSkillDirectory.Replace('/', Path.DirectorySeparatorChar));
            skillFilePath = Path.Combine(targetDir, target.SkillFileName);
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

                var targetFile = Path.Combine(targetDir, Path.GetFileName(filePath));
                File.Copy(filePath, targetFile, true);
            }

            foreach (var childDir in Directory.GetDirectories(sourceDir))
            {
                var targetChildDir = Path.Combine(targetDir, Path.GetFileName(childDir));
                CopyDirectory(childDir, targetChildDir);
            }
        }

        private static Dictionary<string, string> BuildTemplateTokens(string projectRoot, AssistantIntegrationTarget target)
        {
            var cliExeName = GetCliExecutableName();
            var language = AIBridgeProjectSettings.Instance.EditorLanguage;
            var workflowSkillDocPath = GetSkillDocumentPath(projectRoot, target, "aibridge-development-workflow");
            var skillRootPath = GetSkillRootDocumentPath(projectRoot, target);
            return new Dictionary<string, string>
            {
                { "CLI_PATH", "./" + CLI_CACHE_FOLDER + "/" + cliExeName },
                { "COMMON_COMMANDS_TITLE", AIBridgeEditorText.For(language, "Common Commands", "常用命令") },
                { "ROUTING_TITLE", AIBridgeEditorText.For(language, "Routing Rules", "路由原则") },
                { "QUICK_TASK_RULE", AIBridgeEditorText.For(language, "Quick tasks: answer or execute directly for pure Q&A, code explanation, search/display, or tasks with no code or asset changes.", "快速任务：纯问答、代码解释、查找、显示、无代码或资源修改，直接回答或执行。") },
                { "DEVELOPMENT_TASK_RULE", AIBridgeEditorText.For(language, "Development tasks: creating, modifying, fixing, refactoring C# code, Unity assets, Prefabs, Editor tools, package structure, tests, AGENTS.md, or Skills must load `aibridge-development-workflow` first.", "开发任务：创建、修改、修复、重构 C# 代码、Unity 资源、Prefab、Editor 工具、包结构、测试、AGENTS.md 或 Skills，必须优先加载 `aibridge-development-workflow`。") },
                { "WORKFLOW_SKILL_RULE", AIBridgeEditorText.For(language, "After entering the standard development workflow, `aibridge-development-workflow` decides whether to load additional Skills in Skills matching mode.", "进入标准开发工作流后，由 `aibridge-development-workflow` 在 `【Skills 匹配模式】` 决定是否继续加载其它 Skill。") },
                { "SKILL_LOADING_TITLE", AIBridgeEditorText.For(language, "Skill Loading", "Skill 加载") },
                { "WORKFLOW_SKILL_ENTRY", AIBridgeEditorText.For(language, "Load `aibridge-development-workflow` from `" + workflowSkillDocPath + "` before development tasks.", "开发任务先加载 `" + workflowSkillDocPath + "` 中的 `aibridge-development-workflow`。") },
                { "SKILL_ROOT_RULE", AIBridgeEditorText.For(language, "AIBridge Skills are installed under `" + skillRootPath + "/<skill-name>/SKILL.md`; load sibling Skills from that directory only when the workflow requires them.", "AIBridge Skills 安装在 `" + skillRootPath + "/<skill-name>/SKILL.md`；仅在工作流要求时从该目录加载同级 Skill。") }
            };
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
            var resolvedSkillDirectory = target.GetResolvedSkillDirectoryRelativePath(projectRoot);
            if (!string.IsNullOrEmpty(resolvedSkillDirectory))
            {
                skillDirs.Add(Path.Combine(projectRoot, resolvedSkillDirectory.Replace('/', Path.DirectorySeparatorChar)));
            }

            var targetSkillRoot = GetTargetSkillRootDirectory(projectRoot, target);
            var sourceSkillRoot = GetSourceSkillRootPath();
            if (!string.IsNullOrEmpty(targetSkillRoot) && !string.IsNullOrEmpty(sourceSkillRoot) && Directory.Exists(sourceSkillRoot))
            {
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
