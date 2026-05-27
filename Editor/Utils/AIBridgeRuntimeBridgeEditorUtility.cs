using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIBridge.Internal.Json;
using AIBridge.Runtime;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    internal sealed class AIBridgeRuntimePlayerInfo
    {
        public string TargetId;
        public string Transport;
        public string ProductName;
        public string ApplicationVersion;
        public string RuntimeVersion;
        public string Platform;
        public string ActiveScene;
        public string TargetPath;
        public string CommandsPath;
        public string ResultsPath;
        public string HttpUrl;
        public string LastHeartbeatUtc;
        public int ProcessId;
        public int HttpPort;
        public int LanDiscoveryUdpPort;
        public bool Stale;
        public double? AgeSeconds;
    }

    internal sealed class AIBridgeRuntimeDiscoveredTargetInfo
    {
        public string TargetId;
        public string Url;
        public string Platform;
        public string ProjectName;
        public string ApplicationVersion;
        public string DeviceName;
        public string LastSeenUtc;
        public string RemoteEndPoint;
        public bool RequiresToken;
        public bool Stale;
        public double? AgeSeconds;
    }

    internal static class AIBridgeRuntimeBridgeEditorUtility
    {
        public const string RuntimeDirectoryName = "runtime";
        public const string TargetsDirectoryName = "targets";
        public const string HeartbeatFileName = "heartbeat.json";
        public const string CommandsDirectoryName = "commands";
        public const string ResultsDirectoryName = "results";
        public const string RuntimeConfigFileName = "runtime-config.json";
        public const string DiscoveryCacheFileName = "discovery-cache.json";
        public const int DiscoveryCacheFreshSeconds = 30;
        private static readonly TimeSpan StaleHeartbeatTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DiscoveryCacheStaleTimeout = TimeSpan.FromSeconds(DiscoveryCacheFreshSeconds);

        public static string GetRuntimeDirectory()
        {
            var configured = AIBridgeProjectSettings.Instance.RuntimeBridge.ExchangeDirectory;
            if (string.IsNullOrWhiteSpace(configured))
            {
                return Path.Combine(AIBridge.BridgeDirectory, RuntimeDirectoryName);
            }

            return Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configured));
        }

        public static List<AIBridgeRuntimePlayerInfo> ListPlayers()
        {
            var players = new List<AIBridgeRuntimePlayerInfo>();
            var targetsRoot = Path.Combine(GetRuntimeDirectory(), TargetsDirectoryName);
            if (!Directory.Exists(targetsRoot))
            {
                return players;
            }

            var targetDirectories = Directory.GetDirectories(targetsRoot);
            for (var i = 0; i < targetDirectories.Length; i++)
            {
                var targetPath = targetDirectories[i];
                var heartbeatPath = Path.Combine(targetPath, HeartbeatFileName);
                var heartbeat = ReadHeartbeat(heartbeatPath);
                var targetId = GetString(heartbeat, "targetId");
                if (string.IsNullOrEmpty(targetId))
                {
                    targetId = Path.GetFileName(targetPath);
                }

                var lastHeartbeat = ParseHeartbeatTime(GetString(heartbeat, "lastHeartbeatUtc"));
                var ageSeconds = lastHeartbeat.HasValue
                    ? (double?)(DateTime.UtcNow - lastHeartbeat.Value).TotalSeconds
                    : null;

                players.Add(new AIBridgeRuntimePlayerInfo
                {
                    TargetId = targetId,
                    Transport = "file",
                    ProductName = GetString(heartbeat, "productName"),
                    ApplicationVersion = GetString(heartbeat, "applicationVersion"),
                    RuntimeVersion = GetString(heartbeat, "runtimeVersion"),
                    Platform = GetString(heartbeat, "platform"),
                    ActiveScene = GetString(heartbeat, "activeScene"),
                    TargetPath = targetPath,
                    CommandsPath = GetString(heartbeat, "commandsPath") ?? Path.Combine(targetPath, CommandsDirectoryName),
                    ResultsPath = GetString(heartbeat, "resultsPath") ?? Path.Combine(targetPath, ResultsDirectoryName),
                    HttpUrl = GetString(heartbeat, "httpUrl"),
                    LastHeartbeatUtc = lastHeartbeat.HasValue ? lastHeartbeat.Value.ToString("o") : null,
                    ProcessId = GetInt(heartbeat, "processId"),
                    HttpPort = GetInt(heartbeat, "httpPort"),
                    LanDiscoveryUdpPort = GetInt(heartbeat, "lanDiscoveryUdpPort"),
                    Stale = !lastHeartbeat.HasValue || DateTime.UtcNow - lastHeartbeat.Value > StaleHeartbeatTimeout,
                    AgeSeconds = ageSeconds
                });
            }

            players.Sort(ComparePlayers);
            return players;
        }

        public static string BuildCliCommand(string commandBody)
        {
            return BuildCliCommand(commandBody, includeRuntimeDirectory: true);
        }

        public static string BuildCliCommand(string commandBody, bool includeRuntimeDirectory)
        {
            if (!includeRuntimeDirectory)
            {
                return "$CLI " + commandBody;
            }

            var runtimeDirectory = GetRuntimeDirectory();
            return "$CLI " + commandBody + " --runtime-dir " + Quote(runtimeDirectory);
        }

        public static string BuildLocalHttpUrl()
        {
            var settings = AIBridgeProjectSettings.Instance.RuntimeBridge;
            var port = Math.Max(1, settings.HttpPort);
            var host = settings.HttpBindAddress;
            if (string.IsNullOrWhiteSpace(host) || host == "*" || host == "+" || host == "0.0.0.0")
            {
                host = "127.0.0.1";
            }

            return "http://" + host.Trim() + ":" + port;
        }

        public static string GetRuntimeConfigPath()
        {
            return Path.Combine(AIBridge.BridgeDirectory, RuntimeConfigFileName);
        }

        public static string GetDiscoveryCachePath()
        {
            return Path.Combine(AIBridge.BridgeDirectory, RuntimeDirectoryName, DiscoveryCacheFileName);
        }

        public static string WriteRuntimeConfig()
        {
            var settings = AIBridgeProjectSettings.Instance.RuntimeBridge;
            var target = string.IsNullOrWhiteSpace(settings.TargetId) ? "latest" : settings.TargetId.Trim();
            var config = new Dictionary<string, object>
            {
                ["transport"] = "http",
                ["url"] = BuildLocalHttpUrl(),
                ["target"] = target,
                ["token"] = settings.AuthToken ?? string.Empty,
                ["discovery"] = new Dictionary<string, object>
                {
                    ["enabled"] = settings.EnableLanDiscovery,
                    ["udpPort"] = Math.Max(1, settings.DiscoveryUdpPort),
                    ["cacheSeconds"] = DiscoveryCacheFreshSeconds
                }
            };

            var path = GetRuntimeConfigPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, AIBridgeJson.Serialize(config, pretty: true));
            return path;
        }

        public static List<AIBridgeRuntimeDiscoveredTargetInfo> ListDiscoveredTargets()
        {
            var targets = new List<AIBridgeRuntimeDiscoveredTargetInfo>();
            var cache = ReadDiscoveryCache();
            var rawTargets = GetList(cache, "targets");
            if (rawTargets == null)
            {
                return targets;
            }

            for (var i = 0; i < rawTargets.Count; i++)
            {
                var item = rawTargets[i] as Dictionary<string, object>;
                if (item == null)
                {
                    continue;
                }

                var url = GetString(item, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var lastSeen = ParseHeartbeatTime(GetString(item, "lastSeenUtc"));
                var ageSeconds = lastSeen.HasValue
                    ? (double?)(DateTime.UtcNow - lastSeen.Value).TotalSeconds
                    : null;

                targets.Add(new AIBridgeRuntimeDiscoveredTargetInfo
                {
                    TargetId = GetString(item, "targetId") ?? "http",
                    Url = url.TrimEnd('/'),
                    Platform = GetString(item, "platform"),
                    ProjectName = GetString(item, "projectName"),
                    ApplicationVersion = GetString(item, "applicationVersion"),
                    DeviceName = GetString(item, "deviceName"),
                    LastSeenUtc = lastSeen.HasValue ? lastSeen.Value.ToString("o") : null,
                    RemoteEndPoint = GetString(item, "remoteEndPoint"),
                    RequiresToken = GetBool(item, "requiresToken"),
                    Stale = !lastSeen.HasValue || DateTime.UtcNow - lastSeen.Value > DiscoveryCacheStaleTimeout,
                    AgeSeconds = ageSeconds
                });
            }

            targets.Sort(CompareDiscoveredTargets);
            return targets;
        }

        public static bool TryDeletePlayerCache(AIBridgeRuntimePlayerInfo player, out string error)
        {
            error = null;
            if (player == null)
            {
                error = "Runtime target is null.";
                return false;
            }

            if (!player.Stale)
            {
                error = "Only stale Runtime target cache can be deleted.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(player.TargetPath))
            {
                error = "Runtime target path is empty.";
                return false;
            }

            try
            {
                var targetPath = Path.GetFullPath(player.TargetPath);
                var targetsRoot = Path.GetFullPath(Path.Combine(GetRuntimeDirectory(), TargetsDirectoryName));
                // 删除缓存目录前限制在 targets 根目录内，避免误删外部路径。
                if (!IsChildPath(targetPath, targetsRoot))
                {
                    error = "Runtime target path is outside the targets cache directory.";
                    return false;
                }

                if (Directory.Exists(targetPath))
                {
                    Directory.Delete(targetPath, recursive: true);
                }

                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static bool TryDeleteDiscoveredTargetCache(AIBridgeRuntimeDiscoveredTargetInfo target, out string error)
        {
            error = null;
            if (target == null)
            {
                error = "Discovered target is null.";
                return false;
            }

            if (!target.Stale)
            {
                error = "Only stale discovered target cache can be deleted.";
                return false;
            }

            try
            {
                var cache = ReadDiscoveryCache();
                var rawTargets = GetList(cache, "targets");
                if (cache == null || rawTargets == null)
                {
                    return true;
                }

                var keptTargets = new List<object>();
                var removed = false;
                for (var i = 0; i < rawTargets.Count; i++)
                {
                    var item = rawTargets[i] as Dictionary<string, object>;
                    if (item != null && IsSameDiscoveredTarget(item, target))
                    {
                        removed = true;
                        continue;
                    }

                    keptTargets.Add(rawTargets[i]);
                }

                if (!removed)
                {
                    return true;
                }

                cache["targets"] = keptTargets;
                var path = GetDiscoveryCachePath();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, AIBridgeJson.Serialize(cache, pretty: true));
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        public static AIBridgeRuntime FindSceneRuntime()
        {
            return FindSceneRuntimes().FirstOrDefault();
        }

        public static AIBridgeRuntime[] FindSceneRuntimes()
        {
            return Resources.FindObjectsOfTypeAll<AIBridgeRuntime>()
                .Where(runtime => runtime != null
                    && runtime.gameObject != null
                    && runtime.gameObject.scene.IsValid()
                    && !EditorUtility.IsPersistent(runtime))
                .ToArray();
        }

        public static AIBridgeRuntime CreateConfiguredRuntimeObject(string objectName, HideFlags hideFlags, bool useUndo)
        {
            var gameObject = new GameObject(objectName);
            gameObject.SetActive(false);
            gameObject.hideFlags = hideFlags;

            if (useUndo)
            {
                Undo.RegisterCreatedObjectUndo(gameObject, "Create AIBridgeRuntime");
            }

            var runtime = gameObject.AddComponent<AIBridgeRuntime>();
            ApplyProjectSettingsToRuntime(runtime);
            gameObject.SetActive(true);
            return runtime;
        }

        public static void ApplyProjectSettingsToRuntime(AIBridgeRuntime runtime)
        {
            if (runtime == null)
            {
                return;
            }

            var source = AIBridgeProjectSettings.Instance.RuntimeBridge;
            if (runtime.runtimeSettings == null)
            {
                runtime.runtimeSettings = new AIBridgeRuntimeSettings();
            }

            runtime.runtimeSettings.enableRuntimeBridge = source.EnableRuntimeBridge;
            runtime.runtimeSettings.allowInReleaseBuild = source.AllowInReleaseBuild;
            runtime.runtimeSettings.exchangeDirectory = source.ExchangeDirectory ?? string.Empty;
            runtime.runtimeSettings.targetId = source.TargetId ?? string.Empty;
            runtime.runtimeSettings.authToken = source.AuthToken ?? string.Empty;
            runtime.runtimeSettings.allowedActions = ParseAllowedActions(source.AllowedActions);
            runtime.runtimeSettings.enableRuntimeCodeExecution =
                source.EnableRuntimeCodeExecution && AIBridgeHybridClrUtility.IsHybridClrInstalled();
            runtime.runtimeSettings.heartbeatIntervalSeconds = source.HeartbeatIntervalSeconds;
            runtime.runtimeSettings.logBufferSize = Math.Max(1, source.LogBufferSize);
            runtime.runtimeSettings.maxResultBytes = Math.Max(1024, source.MaxResultBytes);
            runtime.runtimeSettings.keepRunningInBackground = source.KeepRunningInBackground;
            runtime.runtimeSettings.enableHttpTransport = source.EnableHttpTransport;
            runtime.runtimeSettings.httpBindAddress = string.IsNullOrWhiteSpace(source.HttpBindAddress)
                ? AIBridgeProjectSettings.DefaultRuntimeBridgeHttpBindAddress
                : source.HttpBindAddress.Trim();
            runtime.runtimeSettings.httpPort = Math.Max(1, source.HttpPort);
            runtime.runtimeSettings.enableLanDiscovery = source.EnableLanDiscovery;
            runtime.runtimeSettings.discoveryUdpPort = Math.Max(1, source.DiscoveryUdpPort);
        }

        private static Dictionary<string, object> ReadHeartbeat(string heartbeatPath)
        {
            if (!File.Exists(heartbeatPath))
            {
                return null;
            }

            try
            {
                return AIBridgeJson.DeserializeObject(File.ReadAllText(heartbeatPath));
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, object> ReadDiscoveryCache()
        {
            var path = GetDiscoveryCachePath();
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return AIBridgeJson.DeserializeObject(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static DateTime? ParseHeartbeatTime(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(value, out var parsed))
            {
                return parsed.UtcDateTime;
            }

            return null;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value.ToString();
        }

        private static int GetInt(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return 0;
            }

            if (value is long longValue)
            {
                return (int)longValue;
            }

            if (value is double doubleValue)
            {
                return (int)doubleValue;
            }

            return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
        }

        private static bool GetBool(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return false;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            return bool.TryParse(value.ToString(), out var parsed) && parsed;
        }

        private static List<object> GetList(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value as List<object>;
        }

        private static string[] ParseAllowedActions(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new string[0];
            }

            return value
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(action => action.Trim())
                .Where(action => !string.IsNullOrEmpty(action))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool IsChildPath(string childPath, string parentPath)
        {
            if (string.IsNullOrEmpty(childPath) || string.IsNullOrEmpty(parentPath))
            {
                return false;
            }

            var normalizedParent = parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return childPath.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameDiscoveredTarget(Dictionary<string, object> item, AIBridgeRuntimeDiscoveredTargetInfo target)
        {
            var itemTargetId = GetString(item, "targetId") ?? "http";
            var itemUrl = (GetString(item, "url") ?? string.Empty).TrimEnd('/');
            return StringEquals(itemTargetId, target.TargetId)
                && StringEquals(itemUrl, target.Url)
                && StringEquals(GetString(item, "remoteEndPoint"), target.RemoteEndPoint);
        }

        private static bool StringEquals(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static int ComparePlayers(AIBridgeRuntimePlayerInfo left, AIBridgeRuntimePlayerInfo right)
        {
            if (left.Stale != right.Stale)
            {
                return left.Stale ? 1 : -1;
            }

            var leftAge = left.AgeSeconds ?? double.MaxValue;
            var rightAge = right.AgeSeconds ?? double.MaxValue;
            var ageCompare = leftAge.CompareTo(rightAge);
            return ageCompare != 0
                ? ageCompare
                : string.Compare(left.TargetId, right.TargetId, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareDiscoveredTargets(AIBridgeRuntimeDiscoveredTargetInfo left, AIBridgeRuntimeDiscoveredTargetInfo right)
        {
            if (left.Stale != right.Stale)
            {
                return left.Stale ? 1 : -1;
            }

            var leftAge = left.AgeSeconds ?? double.MaxValue;
            var rightAge = right.AgeSeconds ?? double.MaxValue;
            var ageCompare = leftAge.CompareTo(rightAge);
            return ageCompare != 0
                ? ageCompare
                : string.Compare(left.TargetId, right.TargetId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
