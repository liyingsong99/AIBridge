using System;
using System.IO;
using AIBridge.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIBridge.Editor
{
    public partial class AIBridgeSettingsWindow
    {
        private const float RuntimeBridgeSettingsLabelWidthRatio = 0.28f;
        private const float RuntimeBridgeSettingsMinLabelWidth = 220f;
        private const float RuntimeBridgeSettingsMaxLabelWidth = 280f;

        private void DrawRuntimeBridgeSettingsTab()
        {
            var settings = AIBridgeProjectSettings.Instance.RuntimeBridge;

            EditorGUILayout.LabelField(AIBridgeEditorText.T("Runtime Bridge", "Runtime Bridge"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Runtime Bridge lets AIBridgeCLI connect to AIBridgeRuntime inside Play Mode or a built Player. Release builds remain disabled unless explicitly allowed.",
                    "Runtime Bridge 允许 AIBridgeCLI 连接 Play Mode 或已编译 Player 内的 AIBridgeRuntime。Release Build 默认关闭，除非显式允许。"),
                MessageType.Info);

            var oldLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = GetRuntimeBridgeSettingsLabelWidth();

            EditorGUI.BeginChangeCheck();

            settings.EnableRuntimeBridge = EditorGUILayout.Toggle(
                AIBridgeEditorText.T("Enable Runtime Bridge", "启用 Runtime Bridge"),
                settings.EnableRuntimeBridge);

            settings.AutoInjectRuntimeBridgeInEditorPlayMode = EditorGUILayout.Toggle(
                AIBridgeEditorText.T("Auto Inject In Editor Play Mode", "Editor Play Mode 自动注入"),
                settings.AutoInjectRuntimeBridgeInEditorPlayMode);

            settings.AutoInjectRuntimeBridgeInDevelopmentBuild = EditorGUILayout.Toggle(
                AIBridgeEditorText.T("Auto Inject In Development Build", "Development Build 自动注入"),
                settings.AutoInjectRuntimeBridgeInDevelopmentBuild);

            settings.KeepRunningInBackground = EditorGUILayout.Toggle(
                AIBridgeEditorText.T("Keep Running In Background", "后台保持运行"),
                settings.KeepRunningInBackground);

            var hybridClrInstalled = AIBridgeHybridClrUtility.IsHybridClrInstalled();
            using (new EditorGUI.DisabledScope(!hybridClrInstalled))
            {
                settings.EnableRuntimeCodeExecution = EditorGUILayout.Toggle(
                    AIBridgeEditorText.T("Enable Runtime Code Execution", "启用 Runtime 代码执行"),
                    settings.EnableRuntimeCodeExecution);
            }

            if (!hybridClrInstalled)
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "HybridCLR package is not installed. Runtime code execution will stay disabled to avoid IL2CPP Assembly.Load failures.",
                        "当前未安装 HybridCLR 包。Runtime 代码执行会保持关闭，避免 IL2CPP 下 Assembly.Load 失败。"),
                    MessageType.Info);
            }
            else if (settings.EnableRuntimeCodeExecution)
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "Runtime code execution loads Roslyn-compiled DLLs in Player by Assembly.Load. Keep it for trusted debugging builds only.",
                        "Runtime 代码执行会在 Player 中通过 Assembly.Load 加载 Roslyn 编译的 DLL。仅用于可信调试构建。"),
                    MessageType.Warning);
            }

            settings.EnableHttpTransport = EditorGUILayout.Toggle(
                AIBridgeEditorText.T("Enable HTTP Transport", "启用 HTTP Transport"),
                settings.EnableHttpTransport);

            using (new EditorGUI.DisabledScope(!settings.EnableHttpTransport))
            {
                settings.HttpBindAddress = EditorGUILayout.DelayedTextField(
                    AIBridgeEditorText.T("HTTP Bind Address", "HTTP 监听地址"),
                    settings.HttpBindAddress ?? string.Empty);

                settings.HttpPort = EditorGUILayout.IntField(
                    AIBridgeEditorText.T("HTTP Port", "HTTP 端口"),
                    settings.HttpPort);

                settings.EnableLanDiscovery = EditorGUILayout.Toggle(
                    AIBridgeEditorText.T("Enable LAN Discovery", "启用局域网自动发现"),
                    settings.EnableLanDiscovery);

                using (new EditorGUI.DisabledScope(!settings.EnableLanDiscovery))
                {
                    settings.DiscoveryUdpPort = EditorGUILayout.IntField(
                        AIBridgeEditorText.T("Discovery UDP Port", "发现 UDP 端口"),
                        settings.DiscoveryUdpPort);
                }
            }

            settings.AllowRuntimeBridgeInReleaseBuild = EditorGUILayout.Toggle(
                AIBridgeEditorText.T("Allow Runtime Bridge In Release Build", "允许 Release Build 启用 Runtime Bridge"),
                settings.AllowRuntimeBridgeInReleaseBuild);

            if (settings.AllowRuntimeBridgeInReleaseBuild)
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "Release Build Runtime Bridge is a debugging backdoor. Use an auth token and restrict Allowed Actions before shipping.",
                        "Release Build Runtime Bridge 是调试入口。发布前请设置鉴权 Token 并限制 Allowed Actions。"),
                    MessageType.Warning);
            }

            settings.ExchangeDirectory = EditorGUILayout.DelayedTextField(
                AIBridgeEditorText.T("Runtime Directory", "Runtime 目录"),
                settings.ExchangeDirectory ?? string.Empty);

            settings.TargetId = EditorGUILayout.DelayedTextField(
                AIBridgeEditorText.T("Default Target Id", "默认 Target Id"),
                settings.TargetId ?? string.Empty);

            settings.AuthToken = EditorGUILayout.DelayedTextField(
                AIBridgeEditorText.T("Auth Token", "鉴权 Token"),
                settings.AuthToken ?? string.Empty);

            settings.AllowedActions = EditorGUILayout.DelayedTextField(
                AIBridgeEditorText.T("Allowed Actions", "允许的 Actions"),
                settings.AllowedActions ?? string.Empty);

            settings.HeartbeatIntervalSeconds = EditorGUILayout.Slider(
                AIBridgeEditorText.T("Heartbeat Interval", "Heartbeat 间隔"),
                settings.HeartbeatIntervalSeconds,
                0.1f,
                10f);

            settings.LogBufferSize = EditorGUILayout.IntSlider(
                AIBridgeEditorText.T("Log Buffer Size", "日志缓存数量"),
                settings.LogBufferSize,
                50,
                5000);

            settings.MaxResultBytes = EditorGUILayout.IntField(
                AIBridgeEditorText.T("Max Result Bytes", "最大结果字节数"),
                settings.MaxResultBytes);

            var settingsChanged = EditorGUI.EndChangeCheck();
            EditorGUIUtility.labelWidth = oldLabelWidth;

            if (settingsChanged)
            {
                settings.MaxResultBytes = Math.Max(1024, settings.MaxResultBytes);
                settings.HttpBindAddress = string.IsNullOrWhiteSpace(settings.HttpBindAddress)
                    ? AIBridgeProjectSettings.DefaultRuntimeBridgeHttpBindAddress
                    : settings.HttpBindAddress.Trim();
                settings.HttpPort = Math.Max(1, settings.HttpPort);
                settings.DiscoveryUdpPort = Math.Max(1, settings.DiscoveryUdpPort);
                AIBridgeProjectSettings.Instance.SaveSettings();
                AIBridgeRuntimeBridgeEditorUtility.WriteRuntimeConfig();
                AIBridgeRuntimeBuildProcessor.SyncRuntimeBootstrapDefinesForActiveTarget();
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Resolved Runtime Directory", "解析后的 Runtime 目录"), EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(
                AIBridgeRuntimeBridgeEditorUtility.GetRuntimeDirectory(),
                EditorStyles.wordWrappedMiniLabel,
                GUILayout.Height(34));

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Runtime HTTP Entry", "Runtime HTTP 入口"), EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(
                AIBridgeRuntimeBridgeEditorUtility.BuildLocalHttpUrl(),
                EditorStyles.wordWrappedMiniLabel,
                GUILayout.Height(20));

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Runtime Config", "Runtime 配置"), EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(
                AIBridgeRuntimeBridgeEditorUtility.GetRuntimeConfigPath(),
                EditorStyles.wordWrappedMiniLabel,
                GUILayout.Height(20));

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Create Runtime Object", "创建 Runtime 对象"), GUILayout.Height(28)))
            {
                CreateOrSelectRuntimeObject();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Apply To Scene Runtime", "应用到场景 Runtime"), GUILayout.Height(28)))
            {
                ApplySettingsToSceneRuntimes(showDialog: true);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Open Players Panel", "打开 Players 面板"), GUILayout.Height(24)))
            {
                AIBridgePlayersWindow.OpenWindow();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open Runtime Directory", "打开 Runtime 目录"), GUILayout.Height(24)))
            {
                OpenRuntimeDirectory();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Launch Args", "复制启动参数"), GUILayout.Height(24)))
            {
                CopyLaunchArguments();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Write Runtime Config", "写入 Runtime 配置"), GUILayout.Height(24)))
            {
                WriteRuntimeConfig();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy HTTP Status CLI", "复制 HTTP 状态命令"), GUILayout.Height(24)))
            {
                CopyHttpStatusCommand();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Discover CLI", "复制发现命令"), GUILayout.Height(24)))
            {
                CopyDiscoverCommand();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Allowed Actions accepts comma, semicolon, or newline separated runtime handler action names. Empty means custom actions are allowed in Editor/Development Build and blocked in Release Build.",
                    "Allowed Actions 支持用逗号、分号或换行分隔 Runtime handler action。为空时 Editor/Development Build 允许自定义 action，Release Build 阻止自定义 action。"),
                MessageType.None);
        }

        private static void CreateOrSelectRuntimeObject()
        {
            var runtime = AIBridgeRuntimeBridgeEditorUtility.FindSceneRuntime();
            if (runtime == null)
            {
                runtime = AIBridgeRuntimeBridgeEditorUtility.CreateConfiguredRuntimeObject(
                    "AIBridgeRuntime",
                    HideFlags.None,
                    useUndo: true);
            }

            AIBridgeRuntimeBridgeEditorUtility.ApplyProjectSettingsToRuntime(runtime);
            EditorUtility.SetDirty(runtime);
            Selection.activeGameObject = runtime.gameObject;
            EditorGUIUtility.PingObject(runtime.gameObject);
            EditorSceneManager.MarkSceneDirty(runtime.gameObject.scene);
        }

        private static void ApplySettingsToSceneRuntimes(bool showDialog)
        {
            var runtimes = AIBridgeRuntimeBridgeEditorUtility.FindSceneRuntimes();

            for (var i = 0; i < runtimes.Length; i++)
            {
                AIBridgeRuntimeBridgeEditorUtility.ApplyProjectSettingsToRuntime(runtimes[i]);
                EditorUtility.SetDirty(runtimes[i]);
                EditorSceneManager.MarkSceneDirty(runtimes[i].gameObject.scene);
            }

            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "AIBridge",
                    AIBridgeEditorText.T(
                        $"Applied Runtime Bridge settings to {runtimes.Length} scene runtime object(s).",
                        $"已将 Runtime Bridge 设置应用到 {runtimes.Length} 个场景 Runtime 对象。"),
                    AIBridgeEditorText.T("OK", "确定"));
            }
        }

        private static void OpenRuntimeDirectory()
        {
            var path = AIBridgeRuntimeBridgeEditorUtility.GetRuntimeDirectory();
            Directory.CreateDirectory(path);
            EditorUtility.RevealInFinder(path);
        }

        private static void CopyLaunchArguments()
        {
            var settings = AIBridgeProjectSettings.Instance.RuntimeBridge;
            var runtimeDirectory = AIBridgeRuntimeBridgeEditorUtility.GetRuntimeDirectory();
            var targetId = string.IsNullOrWhiteSpace(settings.TargetId) ? "player1" : settings.TargetId.Trim();
            EditorGUIUtility.systemCopyBuffer =
                "--aibridge-runtime-dir " + AIBridgeRuntimeBridgeEditorUtility.Quote(runtimeDirectory)
                + " --aibridge-target-id " + AIBridgeRuntimeBridgeEditorUtility.Quote(targetId);
            Debug.Log(AIBridgeEditorText.T("[AIBridge] Runtime launch arguments copied.", "[AIBridge] Runtime 启动参数已复制。"));
        }

        private static void WriteRuntimeConfig()
        {
            var path = AIBridgeRuntimeBridgeEditorUtility.WriteRuntimeConfig();
            Debug.Log(AIBridgeEditorText.T(
                "[AIBridge] Runtime config written: " + path,
                "[AIBridge] Runtime 配置已写入：" + path));
        }

        private static void CopyHttpStatusCommand()
        {
            EditorGUIUtility.systemCopyBuffer = AIBridgeRuntimeBridgeEditorUtility.BuildCliCommand(
                "runtime status --transport http --url " + AIBridgeRuntimeBridgeEditorUtility.Quote(AIBridgeRuntimeBridgeEditorUtility.BuildLocalHttpUrl()) + " --target latest",
                includeRuntimeDirectory: false);
            Debug.Log(AIBridgeEditorText.T("[AIBridge] Runtime HTTP CLI command copied.", "[AIBridge] Runtime HTTP CLI 命令已复制。"));
        }

        private static void CopyDiscoverCommand()
        {
            var settings = AIBridgeProjectSettings.Instance.RuntimeBridge;
            EditorGUIUtility.systemCopyBuffer = AIBridgeRuntimeBridgeEditorUtility.BuildCliCommand(
                "runtime discover --udpPort " + Math.Max(1, settings.DiscoveryUdpPort),
                includeRuntimeDirectory: false);
            Debug.Log(AIBridgeEditorText.T("[AIBridge] Runtime discovery CLI command copied.", "[AIBridge] Runtime 自动发现 CLI 命令已复制。"));
        }

        private float GetRuntimeBridgeSettingsLabelWidth()
        {
            return Mathf.Clamp(
                position.width * RuntimeBridgeSettingsLabelWidthRatio,
                RuntimeBridgeSettingsMinLabelWidth,
                RuntimeBridgeSettingsMaxLabelWidth);
        }
    }
}
