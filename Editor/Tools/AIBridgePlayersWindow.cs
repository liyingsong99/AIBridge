using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    public sealed class AIBridgePlayersWindow : EditorWindow
    {
        private readonly List<AIBridgeRuntimePlayerInfo> _players = new List<AIBridgeRuntimePlayerInfo>();
        private readonly List<AIBridgeRuntimeDiscoveredTargetInfo> _discoveredTargets = new List<AIBridgeRuntimeDiscoveredTargetInfo>();
        private Vector2 _scrollPosition;
        private string _runtimeDirectory;
        private string _localHttpUrl;
        private string _discoveryCachePath;
        private bool _scanLanOnRefresh = true;
        private bool _lanScanRunning;
        private string _lanScanStatus;
        private int _lanScanGeneration;
        private double _lastRefreshTime;

        [MenuItem("AIBridge/Players")]
        public static void OpenWindow()
        {
            var window = GetWindow<AIBridgePlayersWindow>();
            window.titleContent = new GUIContent(AIBridgeEditorText.T("AIBridge Players", "AIBridge Players"));
            window.minSize = new Vector2(820, 420);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshPlayers();
        }

        private void OnGUI()
        {
            DrawToolbar();
            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Runtime Directory", "Runtime 目录"),
                _runtimeDirectory ?? string.Empty,
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("HTTP Entry", "HTTP 入口"),
                _localHttpUrl ?? string.Empty,
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Discovery Cache", "发现缓存"),
                _discoveryCachePath ?? string.Empty,
                EditorStyles.wordWrappedMiniLabel);
            if (_lanScanRunning || !string.IsNullOrEmpty(_lanScanStatus))
            {
                EditorGUILayout.LabelField(
                    AIBridgeEditorText.T("LAN Scan", "局域网扫描"),
                    _lanScanStatus ?? string.Empty,
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.Space(6);
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            DrawDiscoveredTargets();

            if (_players.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "No file transport Runtime targets found. Start Play Mode or a built Player with AIBridgeRuntime enabled, or run LAN discovery for phone targets.",
                        "未找到 File transport Runtime 目标。请启动挂有 AIBridgeRuntime 的 Play Mode/Player，或对手机目标执行局域网发现。"),
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.LabelField(AIBridgeEditorText.T("File Transport Targets", "File Transport 目标"), EditorStyles.boldLabel);
                for (var i = 0; i < _players.Count; i++)
                {
                    DrawPlayer(_players[i]);
                    EditorGUILayout.Space(5);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button(AIBridgeEditorText.T("Refresh", "刷新"), EditorStyles.toolbarButton, GUILayout.Width(72)))
            {
                RefreshPlayers();
            }

            var previousScanLanOnRefresh = _scanLanOnRefresh;
            _scanLanOnRefresh = EditorGUILayout.ToggleLeft(
                AIBridgeEditorText.T("Scan LAN", "扫描局域网"),
                _scanLanOnRefresh,
                GUILayout.Width(96));
            if (_scanLanOnRefresh && !previousScanLanOnRefresh)
            {
                EditorApplication.delayCall += () =>
                {
                    if (this != null)
                    {
                        RefreshPlayers();
                    }
                };
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open Directory", "打开目录"), EditorStyles.toolbarButton, GUILayout.Width(110)))
            {
                OpenRuntimeDirectory();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy List CLI", "复制列表命令"), EditorStyles.toolbarButton, GUILayout.Width(110)))
            {
                CopyFileCommand("runtime list_targets");
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy HTTP CLI", "复制 HTTP 命令"), EditorStyles.toolbarButton, GUILayout.Width(112)))
            {
                CopyHttpCommand("runtime status --transport http --url " + Quote(_localHttpUrl) + " --target latest");
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Discover CLI", "复制发现命令"), EditorStyles.toolbarButton, GUILayout.Width(128)))
            {
                var settings = AIBridgeProjectSettings.Instance.RuntimeBridge;
                CopyHttpCommand("runtime discover --udpPort " + Math.Max(1, settings.DiscoveryUdpPort));
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T($"Targets: {_players.Count + _discoveredTargets.Count}", $"目标数：{_players.Count + _discoveredTargets.Count}"),
                EditorStyles.miniLabel,
                GUILayout.Width(90));
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T($"Refreshed: {FormatRefreshAge()}", $"刷新：{FormatRefreshAge()}"),
                EditorStyles.miniLabel,
                GUILayout.Width(130));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPlayer(AIBridgeRuntimePlayerInfo player)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(player.TargetId, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            var statusText = player.Stale
                ? AIBridgeEditorText.T("STALE", "已过期")
                : AIBridgeEditorText.T("ONLINE", "在线");
            var previousColor = GUI.color;
            GUI.color = player.Stale ? new Color(1f, 0.72f, 0.25f) : new Color(0.55f, 1f, 0.55f);
            GUILayout.Label(statusText, EditorStyles.boldLabel, GUILayout.Width(72));
            GUI.color = previousColor;
            if (player.Stale
                && GUILayout.Button(AIBridgeEditorText.T("Delete Cache", "删除缓存"), GUILayout.Width(92)))
            {
                DeletePlayerCache(player);
            }
            EditorGUILayout.EndHorizontal();

            DrawInfoLine(AIBridgeEditorText.T("Product", "产品"), JoinNonEmpty(player.ProductName, player.ApplicationVersion));
            DrawInfoLine(AIBridgeEditorText.T("Transport", "传输"), string.IsNullOrEmpty(player.Transport) ? "file" : player.Transport);
            DrawInfoLine(AIBridgeEditorText.T("HTTP URL", "HTTP URL"), player.HttpUrl);
            DrawInfoLine(AIBridgeEditorText.T("Scene", "场景"), player.ActiveScene);
            DrawInfoLine(AIBridgeEditorText.T("Platform", "平台"), player.Platform);
            DrawInfoLine(AIBridgeEditorText.T("Runtime", "Runtime"), player.RuntimeVersion);
            DrawInfoLine(AIBridgeEditorText.T("Process", "进程"), player.ProcessId > 0 ? player.ProcessId.ToString() : "-");
            DrawInfoLine(AIBridgeEditorText.T("Heartbeat", "Heartbeat"), FormatHeartbeat(player));
            DrawInfoLine(AIBridgeEditorText.T("Path", "路径"), player.TargetPath);

            EditorGUILayout.BeginHorizontal();
            if (!string.IsNullOrWhiteSpace(player.HttpUrl)
                && GUILayout.Button(AIBridgeEditorText.T("Copy HTTP Status", "复制 HTTP 状态")))
            {
                CopyHttpCommand("runtime status --transport http --url " + Quote(player.HttpUrl) + " --target " + QuoteTarget(player.TargetId));
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Status CLI", "复制状态命令")))
            {
                CopyFileCommand("runtime status --transport file --target " + QuoteTarget(player.TargetId));
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Logs CLI", "复制日志命令")))
            {
                CopyFileCommand("runtime logs --transport file --target " + QuoteTarget(player.TargetId) + " --logType Error --count 100");
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Screenshot CLI", "复制截图命令")))
            {
                CopyFileCommand("runtime screenshot --transport file --target " + QuoteTarget(player.TargetId));
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open", "打开"), GUILayout.Width(58)))
            {
                if (Directory.Exists(player.TargetPath))
                {
                    EditorUtility.RevealInFinder(player.TargetPath);
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawDiscoveredTargets()
        {
            if (_discoveredTargets.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "No LAN-discovered HTTP targets found. Keep Scan LAN checked and refresh after the Player is running on the same network.",
                        "未发现局域网 HTTP 目标。请保持“扫描局域网”勾选，并在同一网络中的 Player 运行后刷新。"),
                    MessageType.None);
                return;
            }

            EditorGUILayout.LabelField(AIBridgeEditorText.T("HTTP / LAN Discovered Targets", "HTTP / 局域网发现目标"), EditorStyles.boldLabel);
            for (var i = 0; i < _discoveredTargets.Count; i++)
            {
                DrawDiscoveredTarget(_discoveredTargets[i]);
                EditorGUILayout.Space(5);
            }
        }

        private void DrawDiscoveredTarget(AIBridgeRuntimeDiscoveredTargetInfo target)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(target.TargetId, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            var statusText = target.Stale
                ? AIBridgeEditorText.T("CACHE", "缓存")
                : target.Reachable
                    ? AIBridgeEditorText.T("REACHABLE", "可达")
                    : AIBridgeEditorText.T("DISCOVERED", "已发现");
            var previousColor = GUI.color;
            GUI.color = target.Stale ? new Color(1f, 0.72f, 0.25f) : target.Reachable ? new Color(0.55f, 1f, 0.55f) : new Color(0.65f, 0.8f, 1f);
            GUILayout.Label(statusText, EditorStyles.boldLabel, GUILayout.Width(96));
            GUI.color = previousColor;
            if (target.Stale
                && GUILayout.Button(AIBridgeEditorText.T("Delete Cache", "删除缓存"), GUILayout.Width(92)))
            {
                DeleteDiscoveredTargetCache(target);
            }
            EditorGUILayout.EndHorizontal();

            DrawInfoLine(AIBridgeEditorText.T("URL", "URL"), target.Url);
            DrawInfoLine(AIBridgeEditorText.T("Bind URL", "监听 URL"), target.BindUrl);
            DrawInfoLine(AIBridgeEditorText.T("Project", "项目"), JoinNonEmpty(target.ProjectName, target.ApplicationVersion));
            DrawInfoLine(AIBridgeEditorText.T("Device", "设备"), target.DeviceName);
            DrawInfoLine(AIBridgeEditorText.T("Platform", "平台"), target.Platform);
            DrawInfoLine(AIBridgeEditorText.T("Kind", "类型"), target.TargetKind);
            DrawInfoLine(AIBridgeEditorText.T("Auth", "鉴权"), target.RequiresToken ? AIBridgeEditorText.T("Token required", "需要 Token") : AIBridgeEditorText.T("No token", "无 Token"));
            DrawInfoLine(AIBridgeEditorText.T("Last Seen", "最后发现"), FormatDiscoveryAge(target));
            DrawInfoLine(AIBridgeEditorText.T("Health", "Health"), target.Reachable ? target.LastHealthCheckUtc : AIBridgeEditorText.T("unreachable", "不可达"));
            DrawInfoLine(AIBridgeEditorText.T("Remote", "远端"), target.RemoteEndPoint);
            DrawInfoLine(AIBridgeEditorText.T("Source NIC", "来源网卡"), JoinNonEmpty(target.SourceInterface, target.SourceInterfaceAddress));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Copy Status CLI", "复制状态命令")))
            {
                CopyDiscoveredCommand(target, "status");
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Logs CLI", "复制日志命令")))
            {
                CopyDiscoveredCommand(target, "logs --logType Error --count 100");
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Screenshot CLI", "复制截图命令")))
            {
                CopyDiscoveredCommand(target, "screenshot");
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private static void DrawInfoLine(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel, GUILayout.Width(88));
            EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(value) ? "-" : value, EditorStyles.miniLabel, GUILayout.Height(16));
            EditorGUILayout.EndHorizontal();
        }

        private void RefreshPlayers()
        {
            LoadPlayersFromCache();
            if (_scanLanOnRefresh)
            {
                BeginLanScan();
            }
        }

        private void LoadPlayersFromCache()
        {
            _runtimeDirectory = AIBridgeRuntimeBridgeEditorUtility.GetRuntimeDirectory();
            _localHttpUrl = AIBridgeRuntimeBridgeEditorUtility.BuildLocalHttpUrl();
            _discoveryCachePath = AIBridgeRuntimeBridgeEditorUtility.GetDiscoveryCachePath();
            _players.Clear();
            _players.AddRange(AIBridgeRuntimeBridgeEditorUtility.ListPlayers());
            _discoveredTargets.Clear();
            _discoveredTargets.AddRange(AIBridgeRuntimeBridgeEditorUtility.ListDiscoveredTargets());
            _lastRefreshTime = EditorApplication.timeSinceStartup;
            Repaint();
        }

        private void BeginLanScan()
        {
            if (_lanScanRunning)
            {
                return;
            }

            var settings = AIBridgeProjectSettings.Instance.RuntimeBridge;
            var udpPort = Math.Max(1, settings.DiscoveryUdpPort);
            var authToken = settings.AuthToken;
            var generation = ++_lanScanGeneration;
            var synchronizationContext = SynchronizationContext.Current;
            _lanScanRunning = true;
            _lanScanStatus = AIBridgeEditorText.T(
                "Scanning UDP " + udpPort + "...",
                "正在扫描 UDP " + udpPort + "...");
            Repaint();

            Task.Run(() =>
            {
                try
                {
                    return AIBridgeRuntimeBridgeEditorUtility.DiscoverLanTargets(
                        AIBridgeRuntimeBridgeEditorUtility.DefaultLanDiscoveryTimeoutMs,
                        udpPort,
                        authToken);
                }
                catch (Exception exception)
                {
                    return new AIBridgeRuntimeLanDiscoveryResult
                    {
                        Success = false,
                        Error = exception.Message
                    };
                }
            }).ContinueWith(task =>
            {
                var result = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : new AIBridgeRuntimeLanDiscoveryResult
                    {
                        Success = false,
                        Error = task.Exception == null ? "task canceled" : task.Exception.GetBaseException().Message
                    };
                if (synchronizationContext != null)
                {
                    synchronizationContext.Post(_ => CompleteLanScan(generation, result), null);
                }
            }, TaskScheduler.Default);
        }

        private void CompleteLanScan(int generation, AIBridgeRuntimeLanDiscoveryResult result)
        {
            if (this == null || generation != _lanScanGeneration)
            {
                return;
            }

            _lanScanRunning = false;
            if (result == null || !result.Success)
            {
                var error = result == null ? "unknown error" : result.Error;
                _lanScanStatus = AIBridgeEditorText.T(
                    "Scan failed: " + error,
                    "扫描失败：" + error);
            }
            else
            {
                _lanScanStatus = AIBridgeEditorText.T(
                    "Found " + result.ReachableCount + " reachable / " + result.Count + " discovered",
                    "发现 " + result.ReachableCount + " 个可达 / " + result.Count + " 个响应");
            }

            LoadPlayersFromCache();
        }

        private static void OpenRuntimeDirectory()
        {
            var path = AIBridgeRuntimeBridgeEditorUtility.GetRuntimeDirectory();
            Directory.CreateDirectory(path);
            EditorUtility.RevealInFinder(path);
        }

        private static void CopyFileCommand(string commandBody)
        {
            EditorGUIUtility.systemCopyBuffer = AIBridgeRuntimeBridgeEditorUtility.BuildCliCommand(commandBody);
            Debug.Log(AIBridgeEditorText.T("[AIBridge] Runtime CLI command copied.", "[AIBridge] Runtime CLI 命令已复制。"));
        }

        private static void CopyHttpCommand(string commandBody)
        {
            EditorGUIUtility.systemCopyBuffer = AIBridgeRuntimeBridgeEditorUtility.BuildCliCommand(commandBody, includeRuntimeDirectory: false);
            Debug.Log(AIBridgeEditorText.T("[AIBridge] Runtime HTTP CLI command copied.", "[AIBridge] Runtime HTTP CLI 命令已复制。"));
        }

        private static void CopyDiscoveredCommand(AIBridgeRuntimeDiscoveredTargetInfo target, string action)
        {
            if (target == null)
            {
                return;
            }

            CopyHttpCommand("runtime " + action
                + " --transport http --url " + Quote(target.Url)
                + " --target " + QuoteTarget(target.TargetId));
        }

        private void DeletePlayerCache(AIBridgeRuntimePlayerInfo player)
        {
            if (player == null)
            {
                return;
            }

            if (!EditorUtility.DisplayDialog(
                AIBridgeEditorText.T("Delete Runtime Target Cache", "删除 Runtime 目标缓存"),
                AIBridgeEditorText.T(
                    $"Delete stale Runtime target cache for '{player.TargetId}'?",
                    $"删除已过期 Runtime 目标 '{player.TargetId}' 的缓存？"),
                AIBridgeEditorText.T("Delete", "删除"),
                AIBridgeEditorText.T("Cancel", "取消")))
            {
                return;
            }

            if (!AIBridgeRuntimeBridgeEditorUtility.TryDeletePlayerCache(player, out var error))
            {
                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Delete Failed", "删除失败"),
                    error,
                    AIBridgeEditorText.T("OK", "确定"));
                return;
            }

            Debug.Log(AIBridgeEditorText.T(
                "[AIBridge] Stale Runtime target cache deleted: " + player.TargetId,
                "[AIBridge] 已删除过期 Runtime 目标缓存：" + player.TargetId));
            RefreshPlayers();
            GUIUtility.ExitGUI();
        }

        private void DeleteDiscoveredTargetCache(AIBridgeRuntimeDiscoveredTargetInfo target)
        {
            if (target == null)
            {
                return;
            }

            if (!EditorUtility.DisplayDialog(
                AIBridgeEditorText.T("Delete Discovery Cache", "删除发现缓存"),
                AIBridgeEditorText.T(
                    $"Delete stale discovered target cache for '{target.TargetId}'?",
                    $"删除已过期发现目标 '{target.TargetId}' 的缓存？"),
                AIBridgeEditorText.T("Delete", "删除"),
                AIBridgeEditorText.T("Cancel", "取消")))
            {
                return;
            }

            if (!AIBridgeRuntimeBridgeEditorUtility.TryDeleteDiscoveredTargetCache(target, out var error))
            {
                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Delete Failed", "删除失败"),
                    error,
                    AIBridgeEditorText.T("OK", "确定"));
                return;
            }

            Debug.Log(AIBridgeEditorText.T(
                "[AIBridge] Stale Runtime discovery cache deleted: " + target.TargetId,
                "[AIBridge] 已删除过期 Runtime 发现缓存：" + target.TargetId));
            RefreshPlayers();
            GUIUtility.ExitGUI();
        }

        private string FormatRefreshAge()
        {
            var age = Math.Max(0, EditorApplication.timeSinceStartup - _lastRefreshTime);
            return age < 1 ? AIBridgeEditorText.T("now", "刚刚") : age.ToString("F0") + "s";
        }

        private static string FormatHeartbeat(AIBridgeRuntimePlayerInfo player)
        {
            if (!player.AgeSeconds.HasValue)
            {
                return "-";
            }

            return player.AgeSeconds.Value.ToString("F1") + "s ago / " + player.LastHeartbeatUtc;
        }

        private static string FormatDiscoveryAge(AIBridgeRuntimeDiscoveredTargetInfo target)
        {
            if (target == null || !target.AgeSeconds.HasValue)
            {
                return "-";
            }

            return target.AgeSeconds.Value.ToString("F1") + "s ago / " + target.LastSeenUtc;
        }

        private static string JoinNonEmpty(string left, string right)
        {
            if (string.IsNullOrEmpty(left))
            {
                return right;
            }

            return string.IsNullOrEmpty(right) ? left : left + " " + right;
        }

        private static string QuoteTarget(string targetId)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return "latest";
            }

            return targetId.IndexOf(' ') >= 0 ? "\"" + targetId.Replace("\"", "\\\"") + "\"" : targetId;
        }

        private static string Quote(string value)
        {
            return AIBridgeRuntimeBridgeEditorUtility.Quote(value ?? string.Empty);
        }
    }
}
