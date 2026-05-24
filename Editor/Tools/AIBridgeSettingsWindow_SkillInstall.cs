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
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Skill Installation", "Skills 安装"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Select which supported AI tool should receive the AIBridge integration. On first use, AIBridge selects one recommended tool based on existing project signals.",
                    "选择要安装 AIBridge 适配层的 AI 工具。AIBridge Skill 默认统一安装到项目根目录 .skills。首次使用时会根据项目已有信号默认选择一个推荐工具。"),
                MessageType.Info);

            // 自动安装开关
            EditorGUI.BeginChangeCheck();
            var autoInstall = EditorGUILayout.Toggle(AIBridgeEditorText.T("Auto Install Skills", "自动安装 Skills"), AIBridgeProjectSettings.Instance.AutoInstallSkills);
            if (EditorGUI.EndChangeCheck())
            {
                AIBridgeProjectSettings.Instance.AutoInstallSkills = autoInstall;
                AIBridgeProjectSettings.Instance.SaveSettings();
            }

            DrawSharedSkillRootDirectoryField();
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

                var status = selection.IsDetected
                    ? AIBridgeEditorText.T("Detected", "已检测到")
                    : AIBridgeEditorText.T("Not detected", "未检测到");
                EditorGUILayout.LabelField(status + ": " + selection.Detail, EditorStyles.miniLabel);
                DrawSkillInstallPreview(selection);

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(AIBridgeEditorText.T("Select Detected", "选择已检测到")))
            {
                SelectDetectedTools();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Select All", "全选")))
            {
                SelectAllTools();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Clear", "清空")))
            {
                ClearToolSelection();
            }

            EditorGUILayout.EndHorizontal();

            var selectedCount = _assistantIntegrationSelections.Count(selection => selection.IsSelected);
            EditorGUILayout.LabelField(AIBridgeEditorText.T($"{selectedCount} tool(s) selected", $"已选择 {selectedCount} 个工具"), EditorStyles.miniLabel);

            EditorGUI.BeginDisabledGroup(selectedCount == 0);
            if (GUILayout.Button(AIBridgeEditorText.T("Install Selected Integrations", "安装选中集成"), GUILayout.Height(30)))
            {
                InstallSelectedTools();
            }
            EditorGUI.EndDisabledGroup();

            if (selectedCount == 0)
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "Select at least one tool to install AIBridge integrations.",
                        "至少选择一个工具才能安装 AIBridge 集成。"),
                    MessageType.Warning);
            }

            EditorGUILayout.Space(10);

            // 安装 Unity 项目 AGENTS.md 模板按钮
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Unity Project AGENTS.md Template", "Unity 项目 AGENTS.md 模板"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Install the Unity project AGENTS.md template to the project root. This also installs the Codex integration once.",
                    "安装面向 Unity 项目的 AGENTS.md 模板到项目根目录，方便初次使用者更好地使用 AIBridge。\n安装后会自动执行一次 Codex 集成安装。"),
                MessageType.Info);

            if (GUILayout.Button(AIBridgeEditorText.T("Install Unity Project AGENTS.md Template", "安装 Unity 项目 AGENTS.md 模板"), GUILayout.Height(30)))
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

        private void DrawSharedSkillRootDirectoryField()
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Shared Skills Directory", "共享 Skills 目录"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "AIBridge and recommended third-party Skills are installed here as the shared source. Tool-specific plugin adapters may mirror them to standard skills/ discovery directories.",
                    "AIBridge 和推荐第三方 Skill 会安装到这里作为共享源目录；不同工具的插件适配层可能会镜像到标准 skills/ 扫描目录。"),
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            var newDirectory = EditorGUILayout.DelayedTextField(
                AIBridgeEditorText.T("Skills Directory", "Skills 目录"),
                AIBridgeProjectSettings.Instance.SkillRootDirectory);
            if (EditorGUI.EndChangeCheck())
            {
                SetSharedSkillRootDirectory(newDirectory);
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Browse", "浏览"), GUILayout.Width(64)))
            {
                BrowseSharedSkillRootDirectory();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open", "打开"), GUILayout.Width(52)))
            {
                OpenSharedSkillRootDirectoryFromInstallTab();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Reset", "重置"), GUILayout.Width(52)))
            {
                ResetSharedSkillRootDirectory();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Current root: ", "当前根目录：") + AIBridgeProjectSettings.Instance.SkillRootDirectory, EditorStyles.miniLabel);
        }

        private void DrawSkillInstallPreview(AssistantIntegrationSelectionState selection)
        {
            if (!selection.Target.SupportsSkillDirectory)
            {
                return;
            }

            EditorGUILayout.LabelField(AIBridgeEditorText.T("AIBridge Skill path: ", "AIBridge Skill 路径：") + BuildSkillInstallPreview(selection), EditorStyles.miniLabel);
        }

        private void SetSharedSkillRootDirectory(string directory)
        {
            var normalized = NormalizeProjectRelativeDirectory(directory);
            if (string.IsNullOrEmpty(normalized))
            {
                normalized = AIBridgeProjectSettings.DefaultSkillRootDirectory;
            }

            if (!IsValidProjectRelativeDirectory(normalized))
            {
                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Invalid Directory", "无效目录"),
                    AIBridgeEditorText.T("The Skills directory must be a project-relative path.", "Skills 目录必须是项目内相对路径。"),
                    AIBridgeEditorText.T("OK", "确定"));
                return;
            }

            if (AIBridgeProjectSettings.Instance.SkillRootDirectory != normalized)
            {
                AIBridgeProjectSettings.Instance.SkillRootDirectory = normalized;
                AIBridgeProjectSettings.Instance.SaveSettings();
                RefreshAssistantIntegrationSkillRoots();
            }
        }

        private void BrowseSharedSkillRootDirectory()
        {
            var projectRoot = GetProjectRoot();
            var currentDirectory = Path.Combine(projectRoot, AIBridgeProjectSettings.Instance.SkillRootDirectory.Replace('/', Path.DirectorySeparatorChar));
            var selectedDirectory = EditorUtility.OpenFolderPanel(AIBridgeEditorText.T("Select Skills Directory", "选择 Skills 目录"), currentDirectory, string.Empty);
            if (string.IsNullOrEmpty(selectedDirectory))
            {
                return;
            }

            string relativeDirectory;
            if (!TryMakeProjectRelativeDirectory(projectRoot, selectedDirectory, out relativeDirectory))
            {
                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Invalid Directory", "无效目录"),
                    AIBridgeEditorText.T("Select a folder inside the project root.", "请选择项目根目录内的文件夹。"),
                    AIBridgeEditorText.T("OK", "确定"));
                return;
            }

            SetSharedSkillRootDirectory(relativeDirectory);
        }

        private void OpenSharedSkillRootDirectoryFromInstallTab()
        {
            var projectRoot = GetProjectRoot();
            var directory = Path.Combine(projectRoot, AIBridgeProjectSettings.Instance.SkillRootDirectory.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            EditorUtility.RevealInFinder(directory);
        }

        private void ResetSharedSkillRootDirectory()
        {
            SetSharedSkillRootDirectory(AIBridgeProjectSettings.DefaultSkillRootDirectory);
        }

        private void RefreshAssistantIntegrationSkillRoots()
        {
            if (_assistantIntegrationSelections == null)
            {
                return;
            }

            var projectRoot = GetProjectRoot();
            foreach (var selection in _assistantIntegrationSelections)
            {
                selection.SkillRootDirectory = selection.Target.GetResolvedSkillRootDirectoryRelativePath(projectRoot);
            }
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

        private void SelectSingleTool(string targetId)
        {
            foreach (var selection in _assistantIntegrationSelections)
            {
                var selected = string.Equals(selection.Target.Id, targetId, StringComparison.OrdinalIgnoreCase);
                selection.IsSelected = selected;
                AssistantIntegrationSelectionSettings.SetSelected(selection.Target.Id, selected);
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
        internal static string GetSourceAgentsPath()
        {
            const string PACKAGE_NAME = "cn.lys.aibridge";
            const string AGENTS_FILE_NAME = "AGENTS.md";
            var projectTemplateRelativePath = GetProjectAgentsTemplateRelativePath(AIBridgeProjectSettings.Instance.EditorLanguage);
            var projectRoot = Path.GetDirectoryName(Application.dataPath);

            // 方法 1: 直接从 Packages 目录查找（本地/嵌入式包）
            var directPath = Path.Combine(projectRoot, "Packages", PACKAGE_NAME, projectTemplateRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(directPath))
            {
                return directPath;
            }

            // 方法 2: 使用 PackageInfo 解析路径（git/registry 包）
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PACKAGE_NAME}");
            if (packageInfo != null)
            {
                var packagePath = Path.Combine(packageInfo.resolvedPath, projectTemplateRelativePath.Replace('/', Path.DirectorySeparatorChar));
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

        internal static string GetProjectAgentsTemplateRelativePath(AIBridgeEditorLanguage language)
        {
            return language == AIBridgeEditorLanguage.SimplifiedChinese
                ? "Templates~/ProjectRules/AGENTS.zh-CN.md"
                : "Templates~/ProjectRules/AGENTS.en-US.md";
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
                    AIBridgeEditorText.T("Confirm Overwrite", "确认覆盖"),
                    AIBridgeEditorText.T("AGENTS.md already exists in the project root. Overwrite it?", "项目根目录已存在 AGENTS.md 文件，是否覆盖？"),
                    AIBridgeEditorText.T("Overwrite", "覆盖"),
                    AIBridgeEditorText.T("Cancel", "取消")))
                {
                    return;
                }
            }

            // 获取源文件路径
            var sourcePath = GetSourceAgentsPath();
            if (string.IsNullOrEmpty(sourcePath))
            {
                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Install Failed", "安装失败"),
                    AIBridgeEditorText.T(
                        "Unity project AGENTS.md template was not found.\nExpected location: Packages/cn.lys.aibridge/" + GetProjectAgentsTemplateRelativePath(AIBridgeProjectSettings.Instance.EditorLanguage),
                        "未找到 Unity 项目 AGENTS.md 模板。\n预期位置：Packages/cn.lys.aibridge/" + GetProjectAgentsTemplateRelativePath(AIBridgeProjectSettings.Instance.EditorLanguage)),
                    AIBridgeEditorText.T("OK", "确定"));
                return;
            }

            try
            {
                // 拷贝文件
                File.Copy(sourcePath, targetPath, true);
                Debug.Log(AIBridgeEditorText.T($"[AIBridge] AGENTS.md installed to: {targetPath}", $"[AIBridge] AGENTS.md 已安装到: {targetPath}"));

                // AGENTS.md 是 Codex 规则入口，模板安装时只启用 Codex，避免新工程同时生成多个工具适配目录。
                SelectSingleTool("codex");
                InstallSelectedTools();

                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Install Complete", "安装成功"),
                    AIBridgeEditorText.T(
                        "Unity project AGENTS.md template was installed to the project root.\n\nCodex integration has also run once.",
                        "Unity 项目 AGENTS.md 模板已成功安装到项目根目录。\n\n已自动执行 Codex 集成安装。"),
                    AIBridgeEditorText.T("OK", "确定"));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Install Failed", "安装失败"),
                    AIBridgeEditorText.T($"Failed to copy AGENTS.md:\n{ex.Message}", $"拷贝 AGENTS.md 时发生错误：\n{ex.Message}"),
                    AIBridgeEditorText.T("OK", "确定"));
            }
        }
    }
}
