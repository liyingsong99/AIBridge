using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// 首次启动设置向导：语言 + AI 工具 + 安装选项 + 结果回显。
    /// 只在用户确认后写入规则与 Skills；关闭窗口等价于“稍后配置”。
    /// </summary>
    public sealed class AIBridgeSetupWindow : EditorWindow
    {
        private sealed class ToolSelectionState
        {
            public AssistantIntegrationTarget Target;
            public bool Selected;
            public bool Detected;
            public bool SpecificallyDetected;
            public string DetectionDetail;
        }

        private const float MinWindowWidth = 640f;
        private const float MinWindowHeight = 660f;

        private Vector2 _scrollPosition;
        private List<ToolSelectionState> _tools;
        private AIBridgeEditorLanguage _language;
        private bool _autoInstallSkills;
        private bool _installAgentsTemplate;
        private bool _showMoreTools;
        private bool _showAdvanced;
        private string _customSkillRoot;
        private bool _installing;
        private bool _finished;
        private bool _isRerun;
        private List<AssistantIntegrationResult> _results;
        private string _errorMessage;
        private static bool _assemblyReloading;

        [MenuItem("AIBridge/Setup", false, 0)]
        public static void OpenWindow()
        {
            var window = GetWindow<AIBridgeSetupWindow>();
            window.titleContent = new GUIContent(AIBridgeEditorText.T("AIBridge Setup", "AIBridge 设置向导"));
            window.minSize = new Vector2(MinWindowWidth, MinWindowHeight);
            window._isRerun = AIBridgeProjectSettings.Instance.OnboardingCompleted;
            window._finished = window._isRerun;
            window.Show();
            window.Focus();
            window.ReloadState();
        }

        /// <summary>
        /// 首次流程自动打开；同一 Editor 会话只弹一次，避免域重载反复打扰。
        /// </summary>
        internal static bool OpenForOnboarding()
        {
            if (Application.isBatchMode
                || EditorApplication.isPlayingOrWillChangePlaymode
                || AIBridgeOnboardingState.WasAutoOpenedThisSession())
            {
                return false;
            }

            AIBridgeOnboardingState.MarkAutoOpenedThisSession();
            OpenWindow();
            return true;
        }

        private void OnEnable()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            _isRerun = AIBridgeProjectSettings.Instance.OnboardingCompleted;
            _finished = _isRerun;
            ReloadState();
        }

        private void OnDisable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
        }

        private static void OnBeforeAssemblyReload()
        {
            // 域重载会销毁窗口，不能把“重载”误判成用户关闭窗口而跳过首次引导。
            _assemblyReloading = true;
        }

        private static void OnAfterAssemblyReload()
        {
            _assemblyReloading = false;
        }

        private void OnDestroy()
        {
            if (_assemblyReloading || _finished || _installing || _isRerun)
            {
                return;
            }

            // 直接关闭窗口视为“稍后配置”：先落完成标记，再按默认选择安装一次，保持旧版开箱可用。
            _finished = true;
            AIBridgeOnboardingState.MarkCompleted();
            EditorApplication.delayCall += RunDefaultInstall;
        }

        private static void RunDefaultInstall()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            SkillInstaller.RefreshInstalledIntegrationsNoDialog();
        }

        private void ReloadState()
        {
            var projectRoot = GetProjectRoot();
            var settings = AIBridgeProjectSettings.Instance;
            _language = settings.EditorLanguageInitialized
                ? settings.EditorLanguage
                : AIBridgeOnboardingState.ResolveInitialLanguage();
            _autoInstallSkills = settings.AutoInstallSkills;
            _customSkillRoot = settings.SkillRootDirectory;
            _errorMessage = null;
            _results = null;

            var initialSelection = AIBridgeOnboardingState.ResolveInitialSelection(projectRoot);
            var initialIds = new HashSet<string>(initialSelection.Select(target => target.Id), StringComparer.OrdinalIgnoreCase);
            var tools = new List<ToolSelectionState>();
            foreach (var target in AssistantIntegrationRegistry.GetTargets().OrderBy(target => target.SortOrder))
            {
                var specificDirectory = AssistantIntegrationDetector.GetFirstDetectedDirectory(projectRoot, target);
                var detection = AssistantIntegrationDetector.Detect(projectRoot, target);
                var selected = initialIds.Contains(target.Id);
                if (_isRerun)
                {
                    bool savedSelection;
                    if (settings.TryGetAssistantSelection(target.Id, out savedSelection))
                    {
                        selected = savedSelection;
                    }
                }

                tools.Add(new ToolSelectionState
                {
                    Target = target,
                    Selected = selected,
                    Detected = detection.IsDetected,
                    SpecificallyDetected = !string.IsNullOrEmpty(specificDirectory),
                    DetectionDetail = !string.IsNullOrEmpty(specificDirectory) ? specificDirectory : detection.Detail
                });
            }

            if (_isRerun && tools.All(tool => !tool.Selected))
            {
                foreach (var tool in tools)
                {
                    tool.Selected = initialIds.Contains(tool.Target.Id);
                }
            }

            _tools = tools;
            UpdateAgentsTemplateDefault();
        }

        private void OnGUI()
        {
            titleContent = new GUIContent(AIBridgeEditorText.T("AIBridge Setup", "AIBridge 设置向导"));
            if (_tools == null)
            {
                ReloadState();
            }

            if (_results != null)
            {
                DrawResultView();
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            DrawHeader();
            DrawLanguageSection();
            DrawToolSection();
            DrawOptionsSection();
            EditorGUILayout.EndScrollView();
            DrawFooter();
        }

        private static string GetProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Welcome to AIBridge", "欢迎使用 AIBridge"),
                EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Choose the language and AI tools you use. AIBridge will write the matching rule files and Skills after you confirm.",
                    "选择界面语言和正在使用的 AI 工具。确认后 AIBridge 会写入对应的规则文件与 Skills。"),
                MessageType.Info);

            if (!string.IsNullOrEmpty(_errorMessage))
            {
                EditorGUILayout.HelpBox(_errorMessage, MessageType.Error);
            }
        }

        private void DrawLanguageSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Language", "语言"), EditorStyles.boldLabel);
            var languageIndex = _language == AIBridgeEditorLanguage.SimplifiedChinese ? 1 : 0;
            var nextIndex = GUILayout.Toolbar(languageIndex, AIBridgeEditorText.LanguageLabels, GUILayout.Height(28f));
            if (nextIndex == languageIndex)
            {
                return;
            }

            _language = nextIndex == 1 ? AIBridgeEditorLanguage.SimplifiedChinese : AIBridgeEditorLanguage.English;
            // 语言决定规则文件文本；这里先落盘，安装阶段直接使用，不需要用户再点一次。
            AIBridgeProjectSettings.Instance.EditorLanguage = _language;
            AIBridgeProjectSettings.Instance.EditorLanguageInitialized = true;
            AIBridgeProjectSettings.Instance.SaveSettings();
            Repaint();
        }

        private void DrawToolSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("AI Tools", "AI 工具"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Tools that share a rule file and Skill directory are written once. You can change this later in AIBridge/Workflows > Skills.",
                    "共用同一个规则文件与 Skill 目录的工具只会写入一次。之后可在 AIBridge/Workflows > Skills 中修改。"),
                MessageType.None);

            var projectRoot = GetProjectRoot();
            foreach (var tool in _tools.Where(tool => tool.Target.IsCommon))
            {
                DrawToolCard(tool, projectRoot);
            }

            _showMoreTools = EditorGUILayout.Foldout(
                _showMoreTools,
                AIBridgeEditorText.T("More tools", "更多工具"),
                true);
            if (_showMoreTools)
            {
                foreach (var tool in _tools.Where(tool => !tool.Target.IsCommon))
                {
                    DrawToolCard(tool, projectRoot);
                }
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Select Detected", "选择已检测到")))
            {
                foreach (var tool in _tools)
                {
                    tool.Selected = tool.Detected;
                }

                UpdateAgentsTemplateDefault();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Select All", "全选")))
            {
                foreach (var tool in _tools)
                {
                    tool.Selected = true;
                }

                UpdateAgentsTemplateDefault();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Clear", "清空")))
            {
                foreach (var tool in _tools)
                {
                    tool.Selected = false;
                }

                UpdateAgentsTemplateDefault();
            }

            EditorGUILayout.EndHorizontal();

            var selectedCount = _tools.Count(tool => tool.Selected);
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T($"{selectedCount} tool(s) selected", $"已选择 {selectedCount} 个工具"),
                EditorStyles.miniLabel);
        }

        private void DrawToolCard(ToolSelectionState tool, string projectRoot)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            var selected = EditorGUILayout.ToggleLeft(tool.Target.DisplayName, tool.Selected, EditorStyles.boldLabel);
            if (selected != tool.Selected)
            {
                tool.Selected = selected;
                UpdateAgentsTemplateDefault();
            }

            if (!string.IsNullOrEmpty(tool.Target.ShortDescription))
            {
                EditorGUILayout.LabelField(tool.Target.ShortDescription, EditorStyles.miniLabel);
            }

            var status = tool.SpecificallyDetected
                ? AIBridgeEditorText.T("Detected: ", "已检测到：") + tool.DetectionDetail
                : tool.Detected
                    ? AIBridgeEditorText.T("Rule file detected: ", "检测到规则文件：") + tool.DetectionDetail
                    : AIBridgeEditorText.T("Not detected", "未检测到");
            EditorGUILayout.LabelField(status, EditorStyles.miniLabel);

            var skillDirectory = tool.Target.GetResolvedSkillDirectoryRelativePath(projectRoot);
            var preview = tool.Target.RootRuleFileName;
            if (!string.IsNullOrEmpty(skillDirectory))
            {
                preview += "  +  " + skillDirectory;
            }

            EditorGUILayout.LabelField(AIBridgeEditorText.T("Will write: ", "将写入：") + preview, EditorStyles.wordWrappedMiniLabel);

            if (AssistantIntegrationGroupBuilder.IsRootRuleShared(tool.Target.RootRuleFileName))
            {
                EditorGUILayout.LabelField(
                    AIBridgeEditorText.T("Shared with: ", "共用工具：")
                    + AssistantIntegrationGroupBuilder.GetSharedRootRuleOwners(tool.Target.RootRuleFileName),
                    EditorStyles.wordWrappedMiniLabel);
            }

            if (!string.IsNullOrEmpty(tool.Target.DocsUrl) && GUILayout.Button(AIBridgeEditorText.T("Docs", "文档"), EditorStyles.miniButton, GUILayout.Width(60f)))
            {
                Application.OpenURL(tool.Target.DocsUrl);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawOptionsSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Options", "选项"), EditorStyles.boldLabel);

            _autoInstallSkills = EditorGUILayout.ToggleLeft(
                AIBridgeEditorText.T("Auto install / refresh Skills", "自动安装/刷新 Skills"),
                _autoInstallSkills);

            var selectedUsesAgents = GetSelectedTools().Any(tool =>
                string.Equals(tool.Target.RootRuleFileName, "AGENTS.md", StringComparison.OrdinalIgnoreCase));
            var agentsPath = Path.Combine(GetProjectRoot(), "AGENTS.md");
            if (selectedUsesAgents && !File.Exists(agentsPath))
            {
                _installAgentsTemplate = EditorGUILayout.ToggleLeft(
                    AIBridgeEditorText.T("Install Unity project AGENTS.md template", "安装 Unity 项目 AGENTS.md 模板"),
                    _installAgentsTemplate);
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "The template adds Unity/C# versions and CLI usage. The AIBridge block is appended after it.",
                        "模板会补充 Unity/C# 版本与 CLI 用法，随后再追加 AIBridge 区块。"),
                    MessageType.None);
            }
            else if (selectedUsesAgents)
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "AGENTS.md already exists. AIBridge will only append its own block and keep your content.",
                        "AGENTS.md 已存在。AIBridge 只追加自己的区块，不会覆盖现有内容。"),
                    MessageType.None);
            }

            _showAdvanced = EditorGUILayout.Foldout(
                _showAdvanced,
                AIBridgeEditorText.T("Advanced", "高级"),
                true);
            if (!_showAdvanced)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Leave empty to use each tool's native Skills directory. A custom directory applies to all selected tools and may not be discovered automatically.",
                    "留空时使用各工具自己的原生 Skills 目录。自定义目录会应用到所有已选工具，且可能无法被工具自动发现。"),
                MessageType.Warning);
            _customSkillRoot = EditorGUILayout.DelayedTextField(
                AIBridgeEditorText.T("Custom Skills directory", "自定义 Skills 目录"),
                _customSkillRoot ?? string.Empty);
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Current mode: ", "当前模式：")
                + (string.IsNullOrEmpty(NormalizeDirectory(_customSkillRoot))
                    ? AIBridgeEditorText.T("Automatic per tool default directories", "自动使用各工具默认目录")
                    : AIBridgeEditorText.T("Custom: ", "自定义：") + NormalizeDirectory(_customSkillRoot)),
                EditorStyles.miniLabel);
        }

        private void DrawFooter()
        {
            var selected = GetSelectedTools();
            var rootRuleCount = selected
                .Select(tool => AssistantIntegrationGroupBuilder.NormalizeRelativePath(tool.Target.RootRuleFileName))
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var skillRootCount = selected
                .Select(tool => tool.Target.GetResolvedSkillRootDirectoryRelativePath(GetProjectRoot()))
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T(
                    $"{selected.Count} tool(s), {rootRuleCount} rule file(s), {skillRootCount} Skill dir(s)",
                    $"{selected.Count} 个工具，{rootRuleCount} 个规则文件，{skillRootCount} 个 Skill 目录"),
                EditorStyles.miniLabel);

            if (selected.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "Select at least one tool, or choose Configure Later.",
                        "请至少选择一个工具，或选择稍后配置。"),
                    MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Configure Later", "稍后配置"), GUILayout.Height(34f)))
            {
                SkipAndClose();
            }

            GUI.enabled = selected.Count > 0 && !_installing;
            var installLabel = _isRerun
                ? AIBridgeEditorText.T("Apply and Install", "应用并安装")
                : AIBridgeEditorText.T("Install Now", "完成并安装");
            if (GUILayout.Button(installLabel, GUILayout.Height(34f)))
            {
                BeginInstall(selected);
            }

            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        private void BeginInstall(List<ToolSelectionState> selected)
        {
            if (_installing || selected.Count == 0)
            {
                return;
            }

            var projectRoot = GetProjectRoot();
            var normalizedSkillRoot = NormalizeDirectory(_customSkillRoot);
            if (!string.IsNullOrEmpty(normalizedSkillRoot) && !IsValidProjectRelativeDirectory(normalizedSkillRoot))
            {
                _errorMessage = AIBridgeEditorText.T(
                    "The Skills directory must be a project-relative path.",
                    "Skills 目录必须是项目内相对路径。");
                return;
            }

            var settings = AIBridgeProjectSettings.Instance;
            settings.EditorLanguage = _language;
            settings.EditorLanguageInitialized = true;
            settings.AutoInstallSkills = _autoInstallSkills;
            settings.SkillRootDirectory = normalizedSkillRoot;
            settings.SaveSettings();
            AIBridgeOnboardingState.MarkCompleted();

            _installing = true;
            _errorMessage = null;
            Repaint();

            try
            {
                EditorUtility.DisplayProgressBar(
                    AIBridgeEditorText.T("AIBridge Setup", "AIBridge 设置向导"),
                    AIBridgeEditorText.T("Writing project rules...", "正在写入项目规则..."),
                    0.2f);
                if (_installAgentsTemplate
                    && selected.Any(tool => string.Equals(tool.Target.RootRuleFileName, "AGENTS.md", StringComparison.OrdinalIgnoreCase))
                    && !File.Exists(Path.Combine(projectRoot, "AGENTS.md")))
                {
                    string templateError;
                    if (!SkillInstaller.TryInstallProjectAgentsTemplate(projectRoot, out templateError))
                    {
                        _errorMessage = templateError;
                        return;
                    }
                }

                EditorUtility.DisplayProgressBar(
                    AIBridgeEditorText.T("AIBridge Setup", "AIBridge 设置向导"),
                    AIBridgeEditorText.T("Installing Skills and integrations...", "正在安装 Skills 与集成..."),
                    0.6f);
                _results = SkillInstaller.InstallTargets(
                    projectRoot,
                    selected.Select(tool => tool.Target.Id),
                    true);
                _finished = true;
            }
            catch (Exception ex)
            {
                _errorMessage = ex.Message;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _installing = false;
                Repaint();
            }
        }

        private void SkipAndClose()
        {
            _finished = true;
            if (!_isRerun)
            {
                AIBridgeOnboardingState.MarkCompleted();
                EditorApplication.delayCall += RunDefaultInstall;
            }

            Close();
        }

        private void DrawResultView()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Setup complete", "设置完成"),
                EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "AIBridge wrote the following files. You can change the selection at any time in AIBridge/Workflows > Skills.",
                    "AIBridge 已写入以下文件。之后可随时在 AIBridge/Workflows > Skills 中修改选择。"),
                MessageType.Info);

            if (!string.IsNullOrEmpty(_errorMessage))
            {
                EditorGUILayout.HelpBox(_errorMessage, MessageType.Error);
            }

            foreach (var result in _results ?? new List<AssistantIntegrationResult>())
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(result.AssistantId, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    AIBridgeEditorText.T("Rule: ", "规则：")
                    + GetActionLabel(result.RootRuleAction)
                    + "  " + FormatPath(result.RootRuleFilePath),
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField(
                    AIBridgeEditorText.T("Skill: ", "Skill：")
                    + GetActionLabel(result.SkillFileAction)
                    + "  " + FormatPath(result.SkillFilePath),
                    EditorStyles.wordWrappedMiniLabel);
                if (result.RootRuleAction == IntegrationAction.Failed
                    || result.SkillFileAction == IntegrationAction.Failed)
                {
                    EditorGUILayout.HelpBox(result.Message ?? string.Empty, MessageType.Error);
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Open Project Root", "打开项目根目录"), GUILayout.Height(32f)))
            {
                EditorUtility.RevealInFinder(GetProjectRoot());
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open Workflows", "打开 Workflows"), GUILayout.Height(32f)))
            {
                AIBridgeWorkflowsWindow.OpenWindow();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Done", "完成"), GUILayout.Height(32f)))
            {
                Close();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void UpdateAgentsTemplateDefault()
        {
            if (GetSelectedTools().Count > 0)
            {
                _installAgentsTemplate = !File.Exists(Path.Combine(GetProjectRoot(), "AGENTS.md"));
            }
        }

        private List<ToolSelectionState> GetSelectedTools()
        {
            return _tools == null
                ? new List<ToolSelectionState>()
                : _tools.Where(tool => tool.Selected).ToList();
        }

        private static string GetActionLabel(IntegrationAction action)
        {
            switch (action)
            {
                case IntegrationAction.CreatedFile:
                    return AIBridgeEditorText.T("created", "已新建");
                case IntegrationAction.InsertedBlock:
                    return AIBridgeEditorText.T("block appended", "已追加区块");
                case IntegrationAction.UpdatedBlock:
                    return AIBridgeEditorText.T("updated", "已更新");
                case IntegrationAction.MigratedLegacyBlock:
                    return AIBridgeEditorText.T("migrated", "已迁移");
                case IntegrationAction.AlreadyUpToDate:
                    return AIBridgeEditorText.T("up to date", "已是最新");
                case IntegrationAction.SkippedMissing:
                    return AIBridgeEditorText.T("skipped (missing source)", "已跳过（源缺失）");
                case IntegrationAction.SkippedDisabled:
                    return AIBridgeEditorText.T("skipped (disabled)", "已跳过（未启用）");
                case IntegrationAction.SkippedUnsafePath:
                    return AIBridgeEditorText.T("skipped (unsafe path)", "已跳过（路径不安全）");
                case IntegrationAction.Failed:
                    return AIBridgeEditorText.T("failed", "失败");
                default:
                    return AIBridgeEditorText.T("no change", "无变化");
            }
        }

        private static string FormatPath(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : "(" + path + ")";
        }

        private static string NormalizeDirectory(string directory)
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
    }
}
