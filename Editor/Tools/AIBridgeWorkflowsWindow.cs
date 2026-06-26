using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    public sealed class AIBridgeWorkflowsWindow : EditorWindow
    {
        private const string GitRepositorySuffix = ".git";

        private enum TabType
        {
            Skills,
            RecommendedLibrary,
            WorkflowOptions
        }

        private sealed class AssistantIntegrationSelectionState
        {
            public AssistantIntegrationTarget Target { get; set; }
            public bool IsDetected { get; set; }
            public string Detail { get; set; }
            public bool IsSelected { get; set; }
            public string SkillRootDirectory { get; set; }
        }

        private Vector2 _scrollPosition;
        private TabType _currentTab;
        private List<AssistantIntegrationSelectionState> _assistantIntegrationSelections;
        private List<RecommendedSkillInfo> _recommendedSkills;
        private int _selectedRecommendedRepositoryIndex;
        private string _loadedRecommendedRepositoryId;
        private string _workflowOptionsApplyMessage;

        [MenuItem("Tools/AIBridge/Workflows")]
        public static void OpenWindow()
        {
            var window = GetWindow<AIBridgeWorkflowsWindow>();
            window.titleContent = new GUIContent(AIBridgeEditorText.T("AIBridge Workflows", "AIBridge Workflows"));
            window.minSize = new Vector2(760f, 520f);
            window.Show();
        }

        [MenuItem("AIBridge/Workflows")]
        private static void OpenLegacyMenuWindow()
        {
            OpenWindow();
        }

        private void OnEnable()
        {
            LoadAssistantIntegrationSelections();
            Repaint();
        }

        private void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space(6f);
            DrawToolbar();
            EditorGUILayout.Space(6f);
            DrawTabToolbar();
            EditorGUILayout.Space(8f);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            switch (_currentTab)
            {
                case TabType.Skills:
                    DrawSkillsTab();
                    break;
                case TabType.RecommendedLibrary:
                    DrawRecommendedLibraryTab();
                    break;
                case TabType.WorkflowOptions:
                    DrawWorkflowOptionsTab();
                    break;
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField(AIBridgeEditorText.T("AIBridge Workflows", "AIBridge Workflows"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Use this panel to install workflow Skills, manage the recommended Skill library, and configure project-level workflow preferences for Codex, Claude, Cursor, and similar tools.",
                    "这个面板用于安装工作流 Skills、管理推荐 Skill 库，并配置面向 Codex、Claude、Cursor 等工具的项目级工作流偏好。"),
                MessageType.Info);
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button(AIBridgeEditorText.T("Refresh", "刷新"), EditorStyles.toolbarButton, GUILayout.Width(76f)))
            {
                RefreshWindowState();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open Settings", "打开 Settings"), EditorStyles.toolbarButton, GUILayout.Width(112f)))
            {
                EditorApplication.ExecuteMenuItem("AIBridge/Settings");
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(BuildToolbarSummary(), EditorStyles.miniLabel, GUILayout.Width(320f));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTabToolbar()
        {
            var tabNames = new[]
            {
                AIBridgeEditorText.T("Skills", "Skills"),
                AIBridgeEditorText.T("Recommended Library", "推荐库"),
                AIBridgeEditorText.T("Workflow Options", "Workflow 选项")
            };
            _currentTab = (TabType)GUILayout.Toolbar((int)_currentTab, tabNames);
        }

        private void DrawSkillsTab()
        {
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Skills", "Skills"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Select the AI tools you use, install the AIBridge workflow integration, and keep project root rules aligned with the current Unity project.",
                    "选择正在使用的 AI 工具，安装 AIBridge 工作流集成，并让项目根规则与当前 Unity 项目保持一致。"),
                MessageType.None);

            EditorGUI.BeginChangeCheck();
            var autoInstall = EditorGUILayout.Toggle(
                AIBridgeEditorText.T("Auto Install Skills", "自动安装 Skills"),
                AIBridgeProjectSettings.Instance.AutoInstallSkills);
            if (EditorGUI.EndChangeCheck())
            {
                AIBridgeProjectSettings.Instance.AutoInstallSkills = autoInstall;
                AIBridgeProjectSettings.Instance.SaveSettings();
            }

            DrawCustomSkillRootDirectoryField();
            EditorGUILayout.Space(8f);

            if (_assistantIntegrationSelections == null)
            {
                LoadAssistantIntegrationSelections();
            }

            foreach (var selection in _assistantIntegrationSelections)
            {
                DrawAssistantIntegrationCard(selection);
            }

            EditorGUILayout.Space(6f);
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
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T($"{selectedCount} tool(s) selected", $"已选择 {selectedCount} 个工具"),
                EditorStyles.miniLabel);

            EditorGUI.BeginDisabledGroup(selectedCount == 0);
            if (GUILayout.Button(AIBridgeEditorText.T("Install Selected Integrations", "安装选中集成"), GUILayout.Height(30f)))
            {
                InstallSelectedTools();
            }
            EditorGUI.EndDisabledGroup();

            if (selectedCount == 0)
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "Select at least one tool before installing workflow integrations.",
                        "安装工作流集成前，至少选择一个工具。"),
                    MessageType.Warning);
            }

            EditorGUILayout.Space(12f);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Project Rule Template", "项目规则模板"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Install the Unity project AGENTS.md template into the project root. This is mainly useful for Codex-based workflows and also refreshes the Codex integration once.",
                    "将 Unity 项目 AGENTS.md 模板安装到项目根目录。它主要服务于基于 Codex 的工作流，并会顺带刷新一次 Codex 集成。"),
                MessageType.Info);

            if (GUILayout.Button(AIBridgeEditorText.T("Install Unity Project AGENTS.md Template", "安装 Unity 项目 AGENTS.md 模板"), GUILayout.Height(28f)))
            {
                InstallAgentsFile();
            }
        }

        private void DrawRecommendedLibraryTab()
        {
            var repositories = RecommendedSkillRepositories.GetDefaultRepositories();
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Recommended Skill Library", "推荐 Skill 库"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Browse recommended third-party Skills and install them into the currently selected tool directories. Review third-party Skill content before enabling it in your workflow.",
                    "浏览推荐的第三方 Skills，并把它们安装到当前选中工具的目录。启用到工作流前，请先自行确认第三方 Skill 内容。"),
                MessageType.None);

            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Install Root: ", "安装根目录：") + GetRecommendedSkillInstallRootSummary(),
                EditorStyles.miniLabel);

            var repositoryNames = repositories.Select(item => item.DisplayName).ToArray();
            _selectedRecommendedRepositoryIndex = Mathf.Clamp(_selectedRecommendedRepositoryIndex, 0, repositoryNames.Length - 1);
            _selectedRecommendedRepositoryIndex = EditorGUILayout.Popup(
                AIBridgeEditorText.T("Repository", "仓库"),
                _selectedRecommendedRepositoryIndex,
                repositoryNames);

            var selectedRepository = repositories[_selectedRecommendedRepositoryIndex];
            if (!string.Equals(_loadedRecommendedRepositoryId, selectedRepository.Id, StringComparison.OrdinalIgnoreCase))
            {
                _recommendedSkills = null;
            }

            EditorGUILayout.LabelField(selectedRepository.RepositoryUrl + "#" + selectedRepository.BranchOrTag, EditorStyles.miniLabel);
            EditorGUILayout.LabelField(selectedRepository.Description, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(5f);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Refresh Skill List", "刷新 Skill 列表"), GUILayout.Height(28f)))
            {
                RefreshRecommendedSkillList(selectedRepository);
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open Repository", "前往仓库"), GUILayout.Height(28f)))
            {
                OpenRepositoryWebPage(selectedRepository);
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open Install Root", "打开安装目录"), GUILayout.Height(28f)))
            {
                OpenRecommendedSkillRootDirectory();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6f);
            if (_recommendedSkills == null || _recommendedSkills.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "Click Refresh Skill List to clone the repository and scan available Skills for this workflow panel.",
                        "点击“刷新 Skill 列表”，拉取仓库并扫描可用于当前工作流面板的 Skills。"),
                    MessageType.None);
                return;
            }

            foreach (var skill in _recommendedSkills)
            {
                DrawRecommendedSkillItem(selectedRepository, skill);
            }
        }

        private void DrawWorkflowOptionsTab()
        {
            var workflowUi = AIBridgeProjectSettings.Instance.WorkflowUi;

            EditorGUILayout.LabelField(AIBridgeEditorText.T("Workflow Options", "Workflow 选项"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "These options store project-level workflow preferences. They are meant for user-facing workflow setup, not low-level recipe or run debugging.",
                    "这些选项保存项目级工作流偏好。它们面向用户配置，而不是面向底层 recipe 或 run 调试。"),
                MessageType.None);
            DrawWorkflowOptionsApplyMessage();

            EditorGUILayout.LabelField(AIBridgeEditorText.T("Enabled Branches", "启用分支"), EditorStyles.boldLabel);
            DrawWorkflowBranchToggle(
                AIBridgeEditorText.T("Implementation", "实施"),
                AIBridgeEditorText.T("Allow change-oriented workflow guidance.", "允许以修改实现为主的工作流引导。"),
                workflowUi.EnableImplementationBranch,
                value => workflowUi.EnableImplementationBranch = value);
            DrawWorkflowBranchToggle(
                AIBridgeEditorText.T("Debug", "调试"),
                AIBridgeEditorText.T("Allow diagnosis-oriented workflow guidance.", "允许以问题诊断为主的工作流引导。"),
                workflowUi.EnableDebugBranch,
                value => workflowUi.EnableDebugBranch = value);
            DrawWorkflowBranchToggle(
                AIBridgeEditorText.T("Review", "审查"),
                AIBridgeEditorText.T("Allow review-only workflow guidance.", "允许以只读审查为主的工作流引导。"),
                workflowUi.EnableReviewBranch,
                value => workflowUi.EnableReviewBranch = value);
            DrawWorkflowBranchToggle(
                AIBridgeEditorText.T("Validation", "验证"),
                AIBridgeEditorText.T("Allow compile, log, and runtime validation workflow guidance.", "允许编译、日志和运行时验证类工作流引导。"),
                workflowUi.EnableValidationBranch,
                value => workflowUi.EnableValidationBranch = value);
            DrawWorkflowBranchToggle(
                AIBridgeEditorText.T("Orchestration", "编排"),
                AIBridgeEditorText.T("Allow multi-agent or multi-step orchestration workflow guidance.", "允许多代理或多步骤编排类工作流引导。"),
                workflowUi.EnableOrchestrationBranch,
                value => workflowUi.EnableOrchestrationBranch = value);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Defaults", "默认行为"), EditorStyles.boldLabel);
            DrawWorkflowValidationLevelField(workflowUi);

            EditorGUI.BeginChangeCheck();
            var preferRuntimeEvidence = EditorGUILayout.Toggle(
                AIBridgeEditorText.T("Prefer Runtime Evidence", "优先收集 Runtime 证据"),
                workflowUi.PreferRuntimeEvidence);
            if (EditorGUI.EndChangeCheck())
            {
                workflowUi.PreferRuntimeEvidence = preferRuntimeEvidence;
                SaveAndApplyWorkflowOptions();
            }

            EditorGUI.BeginChangeCheck();
            var preferCodeIndexGuidance = EditorGUILayout.Toggle(
                AIBridgeEditorText.T("Prefer Code Index Guidance", "优先使用 Code Index 指引"),
                workflowUi.PreferCodeIndexGuidance);
            if (EditorGUI.EndChangeCheck())
            {
                workflowUi.PreferCodeIndexGuidance = preferCodeIndexGuidance;
                SaveAndApplyWorkflowOptions();
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Prompt Prefixes", "提示词前缀"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Use short prompt prefixes to bias workflow behavior for a specific assistant. Leave them empty when you want the default routing only.",
                    "使用简短的提示词前缀为特定 assistant 增加工作流偏好；如果只想使用默认路由，可以留空。"),
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            var sharedPromptPrefix = EditorGUILayout.DelayedTextField(
                AIBridgeEditorText.T("Shared Prefix", "通用前缀"),
                workflowUi.SharedPromptPrefix ?? string.Empty);
            if (EditorGUI.EndChangeCheck())
            {
                workflowUi.SharedPromptPrefix = sharedPromptPrefix ?? string.Empty;
                SaveAndApplyWorkflowOptions();
            }

            foreach (var target in AssistantIntegrationRegistry.GetTargets())
            {
                AIBridgeProjectSettings.Instance.TryGetWorkflowAssistantPromptPrefix(target.Id, out var promptPrefix);
                EditorGUI.BeginChangeCheck();
                var updatedPrefix = EditorGUILayout.DelayedTextField(
                    target.DisplayName,
                    promptPrefix ?? string.Empty);
                if (EditorGUI.EndChangeCheck())
                {
                    AIBridgeProjectSettings.Instance.SetWorkflowAssistantPromptPrefix(target.Id, updatedPrefix);
                    SaveAndApplyWorkflowOptions();
                }
            }

            EditorGUILayout.Space(10f);
            if (GUILayout.Button(AIBridgeEditorText.T("Apply Workflow Options", "应用 Workflow 选项"), GUILayout.Height(28f)))
            {
                SaveAndApplyWorkflowOptions();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Reset Workflow Options", "重置 Workflow 选项"), GUILayout.Height(28f)))
            {
                if (EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Reset Workflow Options", "重置 Workflow 选项"),
                    AIBridgeEditorText.T(
                        "Reset workflow options to the package defaults for this project?",
                        "是否将当前项目的 Workflow 选项重置为包默认值？"),
                    AIBridgeEditorText.T("Reset", "重置"),
                    AIBridgeEditorText.T("Cancel", "取消")))
                {
                    ResetWorkflowOptions();
                }
            }
        }

        private void DrawAssistantIntegrationCard(AssistantIntegrationSelectionState selection)
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

            if (selection.Target.SupportsSkillDirectory)
            {
                EditorGUILayout.LabelField(
                    AIBridgeEditorText.T("AIBridge Skill path: ", "AIBridge Skill 路径：") + BuildSkillInstallPreview(selection),
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawCustomSkillRootDirectoryField()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Custom Skills Directory", "自定义 Skills 目录"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Leave this empty to install Skills into each selected tool's default directory, such as .codex/skills. Custom directories may not be discovered automatically by the AI tool.",
                    "留空时会安装到各已选工具的默认目录，例如 .codex/skills。自定义目录可能无法被 AI 工具自动发现。"),
                MessageType.Warning);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            var newDirectory = EditorGUILayout.DelayedTextField(
                AIBridgeEditorText.T("Custom Directory", "自定义目录"),
                AIBridgeProjectSettings.Instance.SkillRootDirectory);
            if (EditorGUI.EndChangeCheck())
            {
                SetCustomSkillRootDirectory(newDirectory);
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Browse", "浏览"), GUILayout.Width(64f)))
            {
                BrowseCustomSkillRootDirectory();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open", "打开"), GUILayout.Width(52f)))
            {
                OpenCustomSkillRootDirectory();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Reset", "重置"), GUILayout.Width(52f)))
            {
                ResetCustomSkillRootDirectory();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Current mode: ", "当前模式：") + GetSkillRootModeText(),
                EditorStyles.miniLabel);
        }

        private void DrawRecommendedSkillItem(RecommendedSkillRepository repository, RecommendedSkillInfo skill)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(skill.DisplayName, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(GetInstallStateText(skill.InstallState), EditorStyles.miniLabel, GUILayout.Width(92f));
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(skill.Description))
            {
                EditorGUILayout.LabelField(skill.Description, EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.LabelField(skill.SourceRelativePath, EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var buttonText = skill.InstallState == RecommendedSkillInstallState.NotInstalled
                ? AIBridgeEditorText.T("Install", "安装")
                : AIBridgeEditorText.T("Update", "更新");
            if (GUILayout.Button(buttonText, GUILayout.Width(96f)))
            {
                InstallRecommendedSkill(repository, skill);
            }

            if (skill.InstallState != RecommendedSkillInstallState.NotInstalled
                && GUILayout.Button(AIBridgeEditorText.T("Remove", "移除"), GUILayout.Width(96f)))
            {
                RemoveRecommendedSkill(repository, skill);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawWorkflowBranchToggle(string label, string description, bool value, Action<bool> onChanged)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.BeginChangeCheck();
            var updatedValue = EditorGUILayout.ToggleLeft(label, value);
            if (EditorGUI.EndChangeCheck())
            {
                if (value && !updatedValue && WorkflowPreferenceRenderer.CountEnabledBranches(AIBridgeProjectSettings.Instance.WorkflowUi) <= 1)
                {
                    EditorUtility.DisplayDialog(
                        AIBridgeEditorText.T("Workflow Branch Required", "需要至少一个 Workflow 分支"),
                        AIBridgeEditorText.T(
                            "At least one workflow branch must remain enabled.",
                            "至少需要保留一个启用的 Workflow 分支。"),
                        AIBridgeEditorText.T("OK", "确定"));
                    EditorGUILayout.EndVertical();
                    return;
                }

                onChanged(updatedValue);
                SaveAndApplyWorkflowOptions();
            }

            EditorGUILayout.LabelField(description, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        private void DrawWorkflowValidationLevelField(AIBridgeProjectSettings.WorkflowUiSettingsData workflowUi)
        {
            var validationLevels = AIBridgeProjectSettings.SupportedWorkflowValidationLevels;
            var labels = new[]
            {
                AIBridgeEditorText.T("Compile + Logs", "编译 + 日志"),
                AIBridgeEditorText.T("Compile Only", "仅编译"),
                AIBridgeEditorText.T("Compile + Logs + Runtime", "编译 + 日志 + Runtime")
            };

            var currentValue = AIBridgeProjectSettings.NormalizeWorkflowValidationLevel(workflowUi.DefaultValidationLevel);
            var currentIndex = Array.IndexOf(validationLevels, currentValue);
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            EditorGUI.BeginChangeCheck();
            var selectedIndex = EditorGUILayout.Popup(
                AIBridgeEditorText.T("Default Validation Level", "默认验证级别"),
                currentIndex,
                labels);
            if (EditorGUI.EndChangeCheck())
            {
                workflowUi.DefaultValidationLevel = validationLevels[Mathf.Clamp(selectedIndex, 0, validationLevels.Length - 1)];
                SaveAndApplyWorkflowOptions();
            }
        }

        private void DrawWorkflowOptionsApplyMessage()
        {
            if (string.IsNullOrEmpty(_workflowOptionsApplyMessage))
            {
                return;
            }

            EditorGUILayout.HelpBox(_workflowOptionsApplyMessage, MessageType.Info);
        }

        private void RefreshWindowState()
        {
            LoadAssistantIntegrationSelections();
            _recommendedSkills = null;
            _loadedRecommendedRepositoryId = null;
            Repaint();
        }

        private string BuildToolbarSummary()
        {
            var selectedToolCount = _assistantIntegrationSelections == null
                ? 0
                : _assistantIntegrationSelections.Count(selection => selection.IsSelected);
            var installRootSummary = GetSkillRootModeText();
            return AIBridgeEditorText.T("Selected tools: ", "已选工具：") + selectedToolCount + "    "
                + AIBridgeEditorText.T("Skill root: ", "Skill 根目录：") + installRootSummary;
        }

        private void LoadAssistantIntegrationSelections()
        {
            var projectRoot = GetProjectRoot();
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

        private void SetCustomSkillRootDirectory(string directory)
        {
            var previousDirectory = AIBridgeProjectSettings.Instance.SkillRootDirectory;
            var normalized = NormalizeProjectRelativeDirectory(directory);
            if (!string.IsNullOrEmpty(normalized) && !IsValidProjectRelativeDirectory(normalized))
            {
                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Invalid Directory", "无效目录"),
                    AIBridgeEditorText.T("The Skills directory must be a project-relative path.", "Skills 目录必须是项目内相对路径。"),
                    AIBridgeEditorText.T("OK", "确定"));
                return;
            }

            if (AIBridgeProjectSettings.Instance.SkillRootDirectory == normalized)
            {
                return;
            }

            AIBridgeProjectSettings.Instance.SkillRootDirectory = normalized;
            SaveProjectSettings();
            RefreshAssistantIntegrationSkillRoots();
            SkillPluginAdapter.CleanupSkillRootForTargets(GetProjectRoot(), AssistantIntegrationRegistry.GetTargets(), previousDirectory);
            SkillPluginAdapter.GenerateSelected(GetProjectRoot());
        }

        private void BrowseCustomSkillRootDirectory()
        {
            var projectRoot = GetProjectRoot();
            var currentRoot = AIBridgeProjectSettings.Instance.SkillRootDirectory;
            if (string.IsNullOrEmpty(currentRoot))
            {
                currentRoot = AIBridgeProjectSettings.LegacySharedSkillRootDirectory;
            }

            var currentDirectory = Path.Combine(projectRoot, currentRoot.Replace('/', Path.DirectorySeparatorChar));
            var selectedDirectory = EditorUtility.OpenFolderPanel(
                AIBridgeEditorText.T("Select Skills Directory", "选择 Skills 目录"),
                currentDirectory,
                string.Empty);
            if (string.IsNullOrEmpty(selectedDirectory))
            {
                return;
            }

            if (!TryMakeProjectRelativeDirectory(projectRoot, selectedDirectory, out var relativeDirectory))
            {
                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Invalid Directory", "无效目录"),
                    AIBridgeEditorText.T("Select a folder inside the project root.", "请选择项目根目录内的文件夹。"),
                    AIBridgeEditorText.T("OK", "确定"));
                return;
            }

            SetCustomSkillRootDirectory(relativeDirectory);
        }

        private void OpenCustomSkillRootDirectory()
        {
            var projectRoot = GetProjectRoot();
            var root = AIBridgeProjectSettings.Instance.SkillRootDirectory;
            if (string.IsNullOrEmpty(root))
            {
                root = RecommendedSkillInstaller.GetPrimaryInstallRootDirectory(projectRoot);
            }

            var directory = Path.Combine(projectRoot, root.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);
            EditorUtility.RevealInFinder(directory);
        }

        private void ResetCustomSkillRootDirectory()
        {
            SetCustomSkillRootDirectory(AIBridgeProjectSettings.DefaultSkillRootDirectory);
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

            return selection.SkillRootDirectory.TrimEnd('/', '\\') + "/" + skillDirectoryName;
        }

        private string GetSkillRootModeText()
        {
            var customRoot = AIBridgeProjectSettings.Instance.SkillRootDirectory;
            if (!string.IsNullOrEmpty(customRoot))
            {
                return AIBridgeEditorText.T("Custom: ", "自定义：") + customRoot;
            }

            return AIBridgeEditorText.T("Automatic per tool default directories", "自动使用各工具默认目录");
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

        private void InstallAgentsFile()
        {
            var projectRoot = GetProjectRoot();
            var targetPath = Path.Combine(projectRoot, "AGENTS.md");

            if (File.Exists(targetPath)
                && !EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Confirm Overwrite", "确认覆盖"),
                    AIBridgeEditorText.T("AGENTS.md already exists in the project root. Overwrite it?", "项目根目录已存在 AGENTS.md 文件，是否覆盖？"),
                    AIBridgeEditorText.T("Overwrite", "覆盖"),
                    AIBridgeEditorText.T("Cancel", "取消")))
            {
                return;
            }

            var sourcePath = AIBridgeSettingsWindow.GetSourceAgentsPath();
            if (string.IsNullOrEmpty(sourcePath))
            {
                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Install Failed", "安装失败"),
                    AIBridgeEditorText.T(
                        "Unity project AGENTS.md template was not found.\nExpected location: Packages/cn.lys.aibridge/" + AIBridgeSettingsWindow.GetProjectAgentsTemplateRelativePath(AIBridgeProjectSettings.Instance.EditorLanguage),
                        "未找到 Unity 项目 AGENTS.md 模板。\n预期位置：Packages/cn.lys.aibridge/" + AIBridgeSettingsWindow.GetProjectAgentsTemplateRelativePath(AIBridgeProjectSettings.Instance.EditorLanguage)),
                    AIBridgeEditorText.T("OK", "确定"));
                return;
            }

            try
            {
                var content = File.ReadAllText(sourcePath, System.Text.Encoding.UTF8);
                content = SkillInstaller.ApplyProjectTemplateTokens(content);
                File.WriteAllText(targetPath, content, System.Text.Encoding.UTF8);

                SelectSingleTool("codex");
                InstallSelectedTools();

                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Install Complete", "安装成功"),
                    AIBridgeEditorText.T(
                        "Unity project AGENTS.md template was installed to the project root.\n\nCodex integration has also run once.",
                        "Unity 项目 AGENTS.md 模板已成功安装到项目根目录。\n\n已自动执行一次 Codex 集成安装。"),
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

        private void RefreshRecommendedSkillList(RecommendedSkillRepository repository)
        {
            try
            {
                _recommendedSkills = RecommendedSkillInstaller.RefreshRepository(GetProjectRoot(), repository);
                _loadedRecommendedRepositoryId = repository.Id;
                Repaint();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Refresh Failed", "刷新失败"),
                    ex.Message,
                    AIBridgeEditorText.T("OK", "确定"));
            }
        }

        private void InstallRecommendedSkill(RecommendedSkillRepository repository, RecommendedSkillInfo skill)
        {
            var targetDirectory = Path.Combine(GetProjectRoot(), RecommendedSkillInstaller.GetPrimaryInstallRootDirectory(GetProjectRoot()), skill.Name);
            var overwrite = true;
            if (Directory.Exists(targetDirectory))
            {
                overwrite = EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Confirm Install", "确认安装"),
                    AIBridgeEditorText.T(
                        "Target skill already exists. Overwrite it?\n\n" + targetDirectory,
                        "目标 Skill 已存在，是否覆盖？\n\n" + targetDirectory),
                    AIBridgeEditorText.T("Overwrite", "覆盖"),
                    AIBridgeEditorText.T("Cancel", "取消"));
            }

            if (!overwrite)
            {
                return;
            }

            var result = RecommendedSkillInstaller.Install(GetProjectRoot(), repository, skill, true);
            if (result.Success)
            {
                RefreshRecommendedSkillList(repository);
            }

            EditorUtility.DisplayDialog(
                result.Success
                    ? AIBridgeEditorText.T("Install Complete", "安装成功")
                    : AIBridgeEditorText.T("Install Failed", "安装失败"),
                result.Success
                    ? result.Message + "\n" + result.InstalledDirectory
                    : result.Message,
                AIBridgeEditorText.T("OK", "确定"));
        }

        private void RemoveRecommendedSkill(RecommendedSkillRepository repository, RecommendedSkillInfo skill)
        {
            var targetDirectory = Path.Combine(GetProjectRoot(), RecommendedSkillInstaller.GetPrimaryInstallRootDirectory(GetProjectRoot()), skill.Name);
            if (!EditorUtility.DisplayDialog(
                AIBridgeEditorText.T("Confirm Remove", "确认移除"),
                AIBridgeEditorText.T(
                    "Remove this installed Skill?\n\n" + targetDirectory,
                    "是否移除这个已安装 Skill？\n\n" + targetDirectory),
                AIBridgeEditorText.T("Remove", "移除"),
                AIBridgeEditorText.T("Cancel", "取消")))
            {
                return;
            }

            var result = RecommendedSkillInstaller.Remove(GetProjectRoot(), skill);
            if (result.Success)
            {
                RefreshRecommendedSkillList(repository);
            }

            EditorUtility.DisplayDialog(
                result.Success
                    ? AIBridgeEditorText.T("Remove Complete", "移除成功")
                    : AIBridgeEditorText.T("Remove Failed", "移除失败"),
                result.Success
                    ? result.Message + "\n" + result.InstalledDirectory
                    : result.Message,
                AIBridgeEditorText.T("OK", "确定"));
        }

        private void OpenRecommendedSkillRootDirectory()
        {
            var directory = Path.Combine(GetProjectRoot(), RecommendedSkillInstaller.GetPrimaryInstallRootDirectory(GetProjectRoot()));
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            EditorUtility.RevealInFinder(directory);
        }

        private string GetRecommendedSkillInstallRootSummary()
        {
            var roots = RecommendedSkillInstaller.GetSelectedInstallRootDirectories(GetProjectRoot());
            return string.Join("; ", roots.ToArray());
        }

        private static void OpenRepositoryWebPage(RecommendedSkillRepository repository)
        {
            if (repository == null || string.IsNullOrEmpty(repository.RepositoryUrl))
            {
                return;
            }

            Application.OpenURL(GetRepositoryWebUrl(repository.RepositoryUrl));
        }

        private static string GetRepositoryWebUrl(string repositoryUrl)
        {
            return repositoryUrl.EndsWith(GitRepositorySuffix, StringComparison.OrdinalIgnoreCase)
                ? repositoryUrl.Substring(0, repositoryUrl.Length - GitRepositorySuffix.Length)
                : repositoryUrl;
        }

        private static string GetInstallStateText(RecommendedSkillInstallState state)
        {
            switch (state)
            {
                case RecommendedSkillInstallState.Installed:
                    return AIBridgeEditorText.T("Installed", "已安装");
                case RecommendedSkillInstallState.UpdateAvailable:
                    return AIBridgeEditorText.T("Update", "可更新");
                default:
                    return AIBridgeEditorText.T("Not installed", "未安装");
            }
        }

        private void ResetWorkflowOptions()
        {
            var workflowUi = AIBridgeProjectSettings.Instance.WorkflowUi;
            workflowUi.EnableImplementationBranch = AIBridgeProjectSettings.DefaultWorkflowImplementationBranchEnabled;
            workflowUi.EnableDebugBranch = AIBridgeProjectSettings.DefaultWorkflowDebugBranchEnabled;
            workflowUi.EnableReviewBranch = AIBridgeProjectSettings.DefaultWorkflowReviewBranchEnabled;
            workflowUi.EnableValidationBranch = AIBridgeProjectSettings.DefaultWorkflowValidationBranchEnabled;
            workflowUi.EnableOrchestrationBranch = AIBridgeProjectSettings.DefaultWorkflowOrchestrationBranchEnabled;
            workflowUi.DefaultValidationLevel = AIBridgeProjectSettings.DefaultWorkflowValidationLevel;
            workflowUi.PreferRuntimeEvidence = AIBridgeProjectSettings.DefaultWorkflowPreferRuntimeEvidence;
            workflowUi.PreferCodeIndexGuidance = AIBridgeProjectSettings.DefaultWorkflowPreferCodeIndexGuidance;
            workflowUi.SharedPromptPrefix = AIBridgeProjectSettings.DefaultWorkflowSharedPromptPrefix;
            workflowUi.AssistantPromptPrefixes.Clear();
            SaveAndApplyWorkflowOptions();
            Repaint();
        }

        private static void SaveProjectSettings()
        {
            AIBridgeProjectSettings.Instance.SaveSettings();
        }

        private void SaveAndApplyWorkflowOptions()
        {
            AIBridgeProjectSettings.Instance.SaveSettings();
            var generatedFiles = SkillInstaller.GenerateWorkflowPreferenceFilesForSelectedTargets(GetProjectRoot());
            _workflowOptionsApplyMessage = generatedFiles.Count > 0
                ? AIBridgeEditorText.T(
                    "Workflow options saved and applied to installed Skills: " + generatedFiles.Count + " generated file(s).",
                    "Workflow 选项已保存，并已应用到已安装 Skills：" + generatedFiles.Count + " 个生成文件。")
                : AIBridgeEditorText.T(
                    "Workflow options saved. Install selected integrations before these preferences can affect assistant Skills.",
                    "Workflow 选项已保存。需要先安装选中集成，这些偏好才会影响 assistant Skills。");
            Repaint();
        }
    }
}
