using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    public partial class AIBridgeSettingsWindow
    {
        private const float CodeIndexSettingsLabelWidthRatio = 0.3f;
        private const float CodeIndexSettingsMinLabelWidth = 220f;
        private const float CodeIndexSettingsMaxLabelWidth = 300f;

        private static readonly string[] CodeIndexCleanupModeLabels =
        {
            "Process Only",
            "Process + Temp",
            "Full Cleanup"
        };

        private static readonly string[] CodeIndexCleanupModeLabelsCn =
        {
            "仅进程状态",
            "进程和临时状态",
            "完整清理"
        };

        private void DrawCodeIndexSettingsTab()
        {
            AIBridgeCodeIndexEditorUtility.CleanupOrphanDaemonsFromSettingsPanel();
            var settings = AIBridgeProjectSettings.Instance.CodeIndex;
            var wasCodeIndexEnabled = settings.EnableCodeIndex;

            EditorGUILayout.LabelField(AIBridgeEditorText.T("Read-only Code Index", "只读 Code Index"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Code Index starts a project-local daemon for read-only C# declaration-name lookup and declaration location queries.",
                    "Code Index 会启动当前工程本地 daemon，用于只读的 C# 声明名查找和声明位置查询。"),
                MessageType.Info);

            var oldLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = GetCodeIndexSettingsLabelWidth();
            EditorGUI.BeginChangeCheck();

            settings.EnableCodeIndex = EditorGUILayout.Toggle(
                AIBridgeEditorText.T("Enable Code Index", "启用 Code Index"),
                settings.EnableCodeIndex);

            if (!settings.EnableCodeIndex)
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "Code Index is disabled. The aibridge-code-index Skill is removed from selected assistant integrations when Auto Install Skills is enabled; otherwise reinstall selected integrations manually.",
                        "Code Index 已关闭。启用自动安装 Skills 时，会从已选 AI 工具集成中移除 aibridge-code-index；否则请手动重新安装选中集成。"),
                    MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(!settings.EnableCodeIndex))
            {
                settings.PrewarmOnUnityStartup = EditorGUILayout.Toggle(
                    AIBridgeEditorText.T("Prewarm On Unity Startup", "Unity 启动后自动预热"),
                    settings.PrewarmOnUnityStartup);

                settings.WarmupDelaySeconds = EditorGUILayout.IntSlider(
                    AIBridgeEditorText.T("Warmup Delay Seconds", "预热延迟秒数"),
                    Mathf.Max(0, settings.WarmupDelaySeconds),
                    0,
                    60);

                using (new EditorGUI.DisabledScope(true))
                {
                    settings.WarmupMode = EditorGUILayout.TextField(
                        AIBridgeEditorText.T("Warmup Mode", "预热模式"),
                        string.IsNullOrEmpty(settings.WarmupMode) ? AIBridgeProjectSettings.DefaultCodeIndexWarmupMode : settings.WarmupMode);
                }

                settings.AutoRefreshOnFileChange = EditorGUILayout.Toggle(
                    AIBridgeEditorText.T("Auto Refresh On File Change", "文件变化后自动刷新"),
                    settings.AutoRefreshOnFileChange);

                settings.FallbackToTextSearch = EditorGUILayout.Toggle(
                    AIBridgeEditorText.T("Fallback To Text Search", "语义不可用时文本降级"),
                    settings.FallbackToTextSearch);

                settings.IncludePackageCacheSourceAssemblies = EditorGUILayout.Toggle(
                    AIBridgeEditorText.T("Include PackageCache Source", "包含 PackageCache 源码"),
                    settings.IncludePackageCacheSourceAssemblies);

                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "When disabled, PackageCache assemblies are excluded from source indexing but still kept as metadata references for snapshot generation compatibility.",
                        "关闭后，PackageCache 程序集不会作为源码索引项目，但仍会作为 metadata reference 保留，以兼容快照生成。"),
                    MessageType.None);

                settings.IgnoredAssemblyPatterns = DrawCodeIndexPatternTextArea(
                    AIBridgeEditorText.T("Ignored Assembly Patterns", "忽略程序集规则"),
                    settings.IgnoredAssemblyPatterns);

                settings.IgnoredSourcePathPatterns = DrawCodeIndexPatternTextArea(
                    AIBridgeEditorText.T("Ignored Source Path Patterns", "忽略源码路径规则"),
                    settings.IgnoredSourcePathPatterns);

                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "Patterns can be split by line, comma, or semicolon. Supports * and ?. Text without wildcards matches by contains.",
                        "规则可用换行、逗号或分号分隔。支持 * 和 ?；不含通配符时按包含匹配。"),
                    MessageType.None);

                var cleanupIndex = GetCodeIndexCleanupModeIndex(settings.CleanupModeOnQuit);
                cleanupIndex = EditorGUILayout.Popup(
                    AIBridgeEditorText.T("Cleanup Mode On Quit", "退出清理策略"),
                    cleanupIndex,
                    AIBridgeEditorText.Language == AIBridgeEditorLanguage.SimplifiedChinese
                        ? CodeIndexCleanupModeLabelsCn
                        : CodeIndexCleanupModeLabels);
                settings.CleanupModeOnQuit = AIBridgeProjectSettings.SupportedCodeIndexCleanupModes[Mathf.Clamp(cleanupIndex, 0, AIBridgeProjectSettings.SupportedCodeIndexCleanupModes.Length - 1)];
            }

            var settingsChanged = EditorGUI.EndChangeCheck();
            EditorGUIUtility.labelWidth = oldLabelWidth;

            if (settingsChanged)
            {
                settings.WarmupDelaySeconds = Mathf.Max(0, settings.WarmupDelaySeconds);
                settings.WarmupMode = AIBridgeProjectSettings.DefaultCodeIndexWarmupMode;
                settings.CleanupModeOnQuit = AIBridgeProjectSettings.NormalizeCodeIndexCleanupMode(settings.CleanupModeOnQuit);
                settings.IgnoredAssemblyPatterns = settings.IgnoredAssemblyPatterns ?? AIBridgeProjectSettings.DefaultCodeIndexIgnoredAssemblyPatterns;
                settings.IgnoredSourcePathPatterns = settings.IgnoredSourcePathPatterns ?? AIBridgeProjectSettings.DefaultCodeIndexIgnoredSourcePathPatterns;
                AIBridgeProjectSettings.Instance.SaveSettings();
                AIBridgeCodeIndexEditorUtility.WriteCodeIndexConfig();

                if (wasCodeIndexEnabled != settings.EnableCodeIndex)
                {
                    if (!settings.EnableCodeIndex)
                    {
                        AIBridgeCodeIndexEditorUtility.ShutdownDaemon(settings.CleanupModeOnQuit, 3000);
                    }

                    if (AIBridgeProjectSettings.Instance.AutoInstallSkills)
                    {
                        SkillInstaller.RefreshInstalledIntegrationsNoDialog();
                    }
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Resolved Paths", "解析路径"), EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(
                AIBridgeCodeIndexEditorUtility.ResolveCliPath() ?? AIBridgeEditorText.T("AIBridgeCLI not found", "未找到 AIBridgeCLI"),
                EditorStyles.wordWrappedMiniLabel,
                GUILayout.Height(20));
            EditorGUILayout.SelectableLabel(
                AIBridgeCodeIndexEditorUtility.GetIndexDirectory(),
                EditorStyles.wordWrappedMiniLabel,
                GUILayout.Height(20));
            EditorGUILayout.SelectableLabel(
                AIBridgeCodeIndexEditorUtility.GetSnapshotDirectory(),
                EditorStyles.wordWrappedMiniLabel,
                GUILayout.Height(20));

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!settings.EnableCodeIndex))
            {
                if (GUILayout.Button(AIBridgeEditorText.T("Generate Snapshot", "生成快照"), GUILayout.Height(24)))
                {
                    var success = AIBridgeCodeIndexEditorUtility.ScheduleSnapshotRefresh(manual: true);
                    Debug.Log(AIBridgeEditorText.T(
                        success ? "[AIBridge] Code Index snapshot refresh scheduled." : "[AIBridge] Code Index snapshot refresh was not scheduled.",
                        success ? "[AIBridge] Code Index 快照刷新已排队。" : "[AIBridge] Code Index 快照刷新未排队。"));
                }

                if (GUILayout.Button(AIBridgeEditorText.T("Warmup Now", "立即预热"), GUILayout.Height(24)))
                {
                    var started = AIBridgeCodeIndexEditorUtility.StartWarmupNoWait(manual: true);
                    Debug.Log(AIBridgeEditorText.T(
                        started ? "[AIBridge] Code Index warmup scheduled." : "[AIBridge] Code Index warmup was not scheduled.",
                        started ? "[AIBridge] Code Index 预热已排队。" : "[AIBridge] Code Index 预热未排队。"));
                }
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Shutdown", "关闭索引"), GUILayout.Height(24)))
            {
                AIBridgeCodeIndexEditorUtility.ShutdownDaemon(settings.CleanupModeOnQuit, 3000);
                Debug.Log(AIBridgeEditorText.T("[AIBridge] Code Index daemon shutdown requested.", "[AIBridge] 已请求关闭 Code Index daemon。"));
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Open State Directory", "打开状态目录"), GUILayout.Height(24)))
            {
                AIBridgeCodeIndexEditorUtility.OpenIndexDirectory();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Status CLI", "复制状态命令"), GUILayout.Height(24)))
            {
                EditorGUIUtility.systemCopyBuffer = AIBridgeCodeIndexEditorUtility.BuildCliCommand("code_index status");
                Debug.Log(AIBridgeEditorText.T("[AIBridge] Code Index status CLI command copied.", "[AIBridge] Code Index 状态命令已复制。"));
            }
            EditorGUILayout.EndHorizontal();
        }

        private static int GetCodeIndexCleanupModeIndex(string cleanupMode)
        {
            var normalized = AIBridgeProjectSettings.NormalizeCodeIndexCleanupMode(cleanupMode);
            for (var i = 0; i < AIBridgeProjectSettings.SupportedCodeIndexCleanupModes.Length; i++)
            {
                if (AIBridgeProjectSettings.SupportedCodeIndexCleanupModes[i] == normalized)
                {
                    return i;
                }
            }

            return 0;
        }

        private static string DrawCodeIndexPatternTextArea(string label, string value)
        {
            EditorGUILayout.LabelField(label);
            return EditorGUILayout.TextArea(value ?? string.Empty, GUILayout.MinHeight(42));
        }

        private float GetCodeIndexSettingsLabelWidth()
        {
            return Mathf.Clamp(
                position.width * CodeIndexSettingsLabelWidthRatio,
                CodeIndexSettingsMinLabelWidth,
                CodeIndexSettingsMaxLabelWidth);
        }
    }
}
