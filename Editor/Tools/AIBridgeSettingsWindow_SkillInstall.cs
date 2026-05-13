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

            // 安装 AGENTS.md 按钮
            EditorGUILayout.LabelField("AGENTS 工作流规范", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "安装 AGENTS.md 工作流规范文档到项目根目录，方便初次使用者更好地使用 AIBridge。\n安装后会自动执行一次 Skills 安装。",
                MessageType.Info);

            if (GUILayout.Button("安装 AGENTS.md 示例到项目", GUILayout.Height(30)))
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
                    IsSelected = selections.TryGetValue(target.Id, out var isSelected) && isSelected
                });
            }
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
        /// 获取 AGENTS.md 源文件路径（兼容 Packages 和 PackageCache）
        /// </summary>
        private static string GetSourceAgentsPath()
        {
            const string PACKAGE_NAME = "cn.lys.aibridge";
            const string AGENTS_FILE_NAME = "AGENTS.md";
            var projectRoot = Path.GetDirectoryName(Application.dataPath);

            // 方法 1: 直接从 Packages 目录查找（本地/嵌入式包）
            var directPath = Path.Combine(projectRoot, "Packages", PACKAGE_NAME, AGENTS_FILE_NAME);
            if (File.Exists(directPath))
            {
                return directPath;
            }

            // 方法 2: 使用 PackageInfo 解析路径（git/registry 包）
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PACKAGE_NAME}");
            if (packageInfo != null)
            {
                var packagePath = Path.Combine(packageInfo.resolvedPath, AGENTS_FILE_NAME);
                if (File.Exists(packagePath))
                {
                    return packagePath;
                }
            }

            return null;
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
                    "未找到 AGENTS.md 源文件。\n预期位置：Packages/cn.lys.aibridge/AGENTS.md",
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
                    $"AGENTS.md 已成功安装到项目根目录。\n\n已自动执行 Skills 安装。",
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
