using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AIBridge.Editor.ScriptExecution;

namespace AIBridge.Editor
{
    public partial class AIBridgeSettingsWindow
    {
        private void DrawAssistantIntegrationSettings()
        {
            EditorGUILayout.LabelField("Skill Installation", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Select which supported AI tools should receive AIBridge skill installation. Detected tools are selected by default on first use.",
                MessageType.Info);

            // 自动安装开关
            EditorGUI.BeginChangeCheck();
            var autoInstall = EditorGUILayout.Toggle("自动安装 Skills", AIBridgeProjectSettings.Instance.AutoInstallSkills);
            if (EditorGUI.EndChangeCheck())
            {
                AIBridgeProjectSettings.Instance.AutoInstallSkills = autoInstall;
                AIBridgeProjectSettings.Instance.SaveSettings();
            }

            EditorGUILayout.Space(5);

            if (_assistantIntegrationSelections == null)
            {
                LoadAssistantIntegrationSelections();
            }

            foreach (var selection in _assistantIntegrationSelections)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                EditorGUI.BeginChangeCheck();
                var selected = EditorGUILayout.ToggleLeft(selection.Target.DisplayName, selection.IsSelected);
                if (EditorGUI.EndChangeCheck())
                {
                    selection.IsSelected = selected;
                    AssistantIntegrationSelectionSettings.SetSelected(selection.Target.Id, selected);
                }

                var status = selection.IsDetected ? "Detected" : "Not detected";
                EditorGUILayout.LabelField(status + ": " + selection.Detail, EditorStyles.miniLabel);
                DrawSkillRootDirectoryField(selection);

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Select Detected"))
            {
                SelectDetectedTools();
            }

            if (GUILayout.Button("Select All"))
            {
                SelectAllTools();
            }

            if (GUILayout.Button("Clear"))
            {
                ClearToolSelection();
            }

            EditorGUILayout.EndHorizontal();

            var selectedCount = _assistantIntegrationSelections.Count(selection => selection.IsSelected);
            EditorGUILayout.LabelField($"{selectedCount} tool(s) selected", EditorStyles.miniLabel);

            EditorGUI.BeginDisabledGroup(selectedCount == 0);
            if (GUILayout.Button("Install Selected Integrations", GUILayout.Height(30)))
            {
                InstallSelectedTools();
            }
            EditorGUI.EndDisabledGroup();

            if (selectedCount == 0)
            {
                EditorGUILayout.HelpBox("Select at least one tool to install AIBridge integrations.", MessageType.Warning);
            }

            EditorGUILayout.Space(10);

            // 安装 Unity 项目 AGENTS.md 模板按钮
            EditorGUILayout.LabelField("Unity 项目 AGENTS.md 模板", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "安装面向 Unity 项目的 AGENTS.md 模板到项目根目录，方便初次使用者更好地使用 AIBridge。\n安装后会自动执行一次 Skills 安装。",
                MessageType.Info);

            if (GUILayout.Button("安装 Unity 项目 AGENTS.md 模板", GUILayout.Height(30)))
            {
                InstallAgentsFile();
            }
        }

        private void LoadAssistantIntegrationSelections()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var targets = AssistantIntegrationRegistry.GetTargets();
            var selections = AssistantIntegrationSelectionSettings.LoadSelections(projectRoot, targets);

            _assistantIntegrationSelections = new List<AssistantIntegrationSelectionState>(targets.Count);
            foreach (var target in targets)
            {
                var detection = AssistantIntegrationDetector.Detect(projectRoot, target);
                _assistantIntegrationSelections.Add(new AssistantIntegrationSelectionState
                {
                    Target = target,
                    IsDetected = detection.IsDetected,
                    Detail = detection.Detail,
                    IsSelected = selections.TryGetValue(target.Id, out var isSelected) && isSelected,
                    SkillRootDirectory = target.GetResolvedSkillRootDirectoryRelativePath(projectRoot)
                });
            }
        }

        private void DrawSkillRootDirectoryField(AssistantIntegrationSelectionState selection)
        {
            if (!selection.Target.SupportsSkillDirectory)
            {
                return;
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginChangeCheck();
            var newDirectory = EditorGUILayout.DelayedTextField("Skills 根目录", selection.SkillRootDirectory);
            if (EditorGUI.EndChangeCheck())
            {
                SetSkillRootDirectory(selection, newDirectory);
            }

            if (GUILayout.Button("Browse", GUILayout.Width(64)))
            {
                BrowseSkillRootDirectory(selection);
            }

            if (GUILayout.Button("Open", GUILayout.Width(52)))
            {
                OpenSkillRootDirectory(selection);
            }

            if (GUILayout.Button("Reset", GUILayout.Width(52)))
            {
                ResetSkillRootDirectory(selection);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("将安装到: " + BuildSkillInstallPreview(selection), EditorStyles.miniLabel);
        }

        private void SetSkillRootDirectory(AssistantIntegrationSelectionState selection, string directory)
        {
            var normalized = NormalizeProjectRelativeDirectory(directory);
            if (string.IsNullOrEmpty(normalized))
            {
                ResetSkillRootDirectory(selection);
                return;
            }

            if (!IsValidProjectRelativeDirectory(normalized))
            {
                EditorUtility.DisplayDialog("无效目录", "Skills 根目录必须是项目内相对路径。", "确定");
                selection.SkillRootDirectory = selection.Target.GetResolvedSkillRootDirectoryRelativePath(GetProjectRoot());
                return;
            }

            if (AIBridgeProjectSettings.Instance.SetAssistantSkillRootDirectory(selection.Target.Id, normalized))
            {
                AIBridgeProjectSettings.Instance.SaveSettings();
            }

            selection.SkillRootDirectory = selection.Target.GetResolvedSkillRootDirectoryRelativePath(GetProjectRoot());
        }

        private void BrowseSkillRootDirectory(AssistantIntegrationSelectionState selection)
        {
            var projectRoot = GetProjectRoot();
            var currentDirectory = Path.Combine(projectRoot, selection.SkillRootDirectory.Replace('/', Path.DirectorySeparatorChar));
            var selectedDirectory = EditorUtility.OpenFolderPanel("选择 Skills 根目录", currentDirectory, string.Empty);
            if (string.IsNullOrEmpty(selectedDirectory))
            {
                return;
            }

            string relativeDirectory;
            if (!TryMakeProjectRelativeDirectory(projectRoot, selectedDirectory, out relativeDirectory))
            {
                EditorUtility.DisplayDialog("无效目录", "请选择项目根目录内的文件夹。", "确定");
                return;
            }

            SetSkillRootDirectory(selection, relativeDirectory);
        }

        private void OpenSkillRootDirectory(AssistantIntegrationSelectionState selection)
        {
            var projectRoot = GetProjectRoot();
            var directory = Path.Combine(projectRoot, selection.SkillRootDirectory.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            EditorUtility.RevealInFinder(directory);
        }

        private void ResetSkillRootDirectory(AssistantIntegrationSelectionState selection)
        {
            if (AIBridgeProjectSettings.Instance.ClearAssistantSkillRootDirectory(selection.Target.Id))
            {
                AIBridgeProjectSettings.Instance.SaveSettings();
            }

            selection.SkillRootDirectory = selection.Target.GetResolvedSkillRootDirectoryRelativePath(GetProjectRoot());
        }

        private string BuildSkillInstallPreview(AssistantIntegrationSelectionState selection)
        {
            var skillDirectoryName = selection.Target.GetSkillDirectoryName();
            if (string.IsNullOrEmpty(skillDirectoryName))
            {
                return selection.SkillRootDirectory;
            }

            // 面板选择的是 Skills 根目录，实际安装时每个 Skill 会落到根目录下的独立子目录。
            return selection.SkillRootDirectory.TrimEnd('/', '\\') + "/" + skillDirectoryName;
        }

        private static string GetProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        private static string NormalizeProjectRelativeDirectory(string directory)
        {
            return string.IsNullOrWhiteSpace(directory)
                ? string.Empty
                : directory.Trim().Replace('\\', '/').Trim('/');
        }

        private static bool IsValidProjectRelativeDirectory(string directory)
        {
            return !string.IsNullOrEmpty(directory)
                && !Path.IsPathRooted(directory)
                && !directory.Split('/').Any(part => part == "..");
        }

        private static bool TryMakeProjectRelativeDirectory(string projectRoot, string selectedDirectory, out string relativeDirectory)
        {
            relativeDirectory = null;
            var projectFullPath = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var selectedFullPath = Path.GetFullPath(selectedDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!selectedFullPath.StartsWith(projectFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(selectedFullPath, projectFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var relative = selectedFullPath.Length == projectFullPath.Length
                ? string.Empty
                : selectedFullPath.Substring(projectFullPath.Length + 1);
            relativeDirectory = NormalizeProjectRelativeDirectory(relative);
            return IsValidProjectRelativeDirectory(relativeDirectory);
        }

        private void SelectDetectedTools()
        {
            foreach (var selection in _assistantIntegrationSelections)
            {
                selection.IsSelected = selection.IsDetected;
                AssistantIntegrationSelectionSettings.SetSelected(selection.Target.Id, selection.IsSelected);
            }
        }

        private void SelectAllTools()
        {
            foreach (var selection in _assistantIntegrationSelections)
            {
                selection.IsSelected = true;
                AssistantIntegrationSelectionSettings.SetSelected(selection.Target.Id, true);
            }
        }

        private void ClearToolSelection()
        {
            foreach (var selection in _assistantIntegrationSelections)
            {
                selection.IsSelected = false;
                AssistantIntegrationSelectionSettings.SetSelected(selection.Target.Id, false);
            }
        }

        private void InstallSelectedTools()
        {
            var selectedTargetIds = _assistantIntegrationSelections
                .Where(selection => selection.IsSelected)
                .Select(selection => selection.Target.Id)
                .ToArray();

            SkillInstaller.ManualInstallSelected(selectedTargetIds);
            LoadAssistantIntegrationSelections();
        }

        private void ClearScreenshotCache()
        {
            var screenshotsDir = ScreenshotHelper.ScreenshotsDir;
            if (Directory.Exists(screenshotsDir))
            {
                var files = Directory.GetFiles(screenshotsDir);
                int count = 0;
                foreach (var file in files)
                {
                    if (Path.GetFileName(file) != ".gitignore")
                    {
                        try
                        {
                            File.Delete(file);
                            count++;
                        }
                        catch
                        {
                            // Ignore deletion errors
                        }
                    }
                }
                Debug.Log($"[AIBridge] Cleared {count} files from screenshot cache.");
            }
        }

        /// <summary>
        /// 获取 Unity 项目 AGENTS.md 模板路径（兼容 Packages 和 PackageCache）
        /// </summary>
        private static string GetSourceAgentsPath()
        {
            const string PACKAGE_NAME = "cn.lys.aibridge";
            const string AGENTS_FILE_NAME = "AGENTS.md";
            const string PROJECT_TEMPLATE_RELATIVE_PATH = "Templates~/ProjectRules/AGENTS.md";
            var projectRoot = Path.GetDirectoryName(Application.dataPath);

            // 方法 1: 直接从 Packages 目录查找（本地/嵌入式包）
            var directPath = Path.Combine(projectRoot, "Packages", PACKAGE_NAME, PROJECT_TEMPLATE_RELATIVE_PATH.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(directPath))
            {
                return directPath;
            }

            // 方法 2: 使用 PackageInfo 解析路径（git/registry 包）
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PACKAGE_NAME}");
            if (packageInfo != null)
            {
                var packagePath = Path.Combine(packageInfo.resolvedPath, PROJECT_TEMPLATE_RELATIVE_PATH.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(packagePath))
                {
                    return packagePath;
                }
            }

            // 兼容旧版本包结构：历史上项目模板曾放在包根 AGENTS.md。
            var legacyDirectPath = Path.Combine(projectRoot, "Packages", PACKAGE_NAME, AGENTS_FILE_NAME);
            if (IsLegacyProjectAgentsTemplate(legacyDirectPath))
            {
                return legacyDirectPath;
            }

            if (packageInfo != null)
            {
                var legacyPackagePath = Path.Combine(packageInfo.resolvedPath, AGENTS_FILE_NAME);
                if (IsLegacyProjectAgentsTemplate(legacyPackagePath))
                {
                    return legacyPackagePath;
                }
            }

            return null;
        }

        private static bool IsLegacyProjectAgentsTemplate(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                var content = File.ReadAllText(path);
                return !content.Contains("本文件只用于开发 `cn.lys.aibridge` 包自身");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 安装 AGENTS.md 到项目根目录
        /// </summary>
        private void InstallAgentsFile()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var targetPath = Path.Combine(projectRoot, "AGENTS.md");

            // 检查目标文件是否已存在
            if (File.Exists(targetPath))
            {
                if (!EditorUtility.DisplayDialog(
                    "确认覆盖",
                    "项目根目录已存在 AGENTS.md 文件，是否覆盖？",
                    "覆盖",
                    "取消"))
                {
                    return;
                }
            }

            // 获取源文件路径
            var sourcePath = GetSourceAgentsPath();
            if (string.IsNullOrEmpty(sourcePath))
            {
                EditorUtility.DisplayDialog(
                    "安装失败",
                    "未找到 Unity 项目 AGENTS.md 模板。\n预期位置：Packages/cn.lys.aibridge/Templates~/ProjectRules/AGENTS.md",
                    "确定");
                return;
            }

            try
            {
                // 拷贝文件
                File.Copy(sourcePath, targetPath, true);
                Debug.Log($"[AIBridge] AGENTS.md 已安装到: {targetPath}");

                // 自动执行 Skills 安装
                InstallSelectedTools();

                EditorUtility.DisplayDialog(
                    "安装成功",
                    "Unity 项目 AGENTS.md 模板已成功安装到项目根目录。\n\n已自动执行 Skills 安装。",
                    "确定");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    "安装失败",
                    $"拷贝 AGENTS.md 时发生错误：\n{ex.Message}",
                    "确定");
            }
        }
    }
}
