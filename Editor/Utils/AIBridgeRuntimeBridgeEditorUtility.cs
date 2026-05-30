using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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
        public string ReachableUrl;
        public string BindUrl;
        public string Platform;
        public string ProjectName;
        public string ApplicationVersion;
        public string DeviceName;
        public string LastSeenUtc;
        public string LastHealthCheckUtc;
        public string RemoteEndPoint;
        public string SourceInterface;
        public string SourceInterfaceAddress;
        public string SourceInterfaceDescription;
        public string TargetKind;
        public bool RequiresToken;
        public bool Reachable;
        public bool Stale;
        public double? AgeSeconds;
    }

    internal sealed class AIBridgeRuntimeLanDiscoveryResult
    {
        public bool Success;
        public int Count;
        public int ReachableCount;
        public int SentPackets;
        public int ScannedInterfaces;
        public string Error;
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
        public const int DefaultLanDiscoveryTimeoutMs = 1500;
        private const int DefaultRuntimeHttpPort = 27182;
        private const int LanDiscoveryPortScanCount = 50;
        private const int MaxPort = 65535;
        private const int MinReceiveSleepMs = 10;
        private const int HealthCheckMinTimeoutMs = 500;
        private const int HealthCheckMaxTimeoutMs = 2000;
        private const string DiscoveryProtocol = "aibridge-runtime-discovery";
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

                var url = GetString(item, "reachableUrl") ?? GetString(item, "url");
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
                    ReachableUrl = (GetString(item, "reachableUrl") ?? url).TrimEnd('/'),
                    BindUrl = GetString(item, "bindUrl"),
                    Platform = GetString(item, "platform"),
                    ProjectName = GetString(item, "projectName"),
                    ApplicationVersion = GetString(item, "applicationVersion"),
                    DeviceName = GetString(item, "deviceName"),
                    LastSeenUtc = lastSeen.HasValue ? lastSeen.Value.ToString("o") : null,
                    LastHealthCheckUtc = GetString(item, "lastHealthCheckUtc"),
                    RemoteEndPoint = GetString(item, "remoteEndPoint"),
                    SourceInterface = GetString(item, "sourceInterface"),
                    SourceInterfaceAddress = GetString(item, "sourceInterfaceAddress"),
                    SourceInterfaceDescription = GetString(item, "sourceInterfaceDescription"),
                    TargetKind = GetString(item, "targetKind"),
                    RequiresToken = GetBool(item, "requiresToken"),
                    Reachable = !item.ContainsKey("reachable") || GetBool(item, "reachable"),
                    Stale = !lastSeen.HasValue || DateTime.UtcNow - lastSeen.Value > DiscoveryCacheStaleTimeout,
                    AgeSeconds = ageSeconds
                });
            }

            targets.Sort(CompareDiscoveredTargets);
            return targets;
        }

        public static AIBridgeRuntimeLanDiscoveryResult DiscoverLanTargets(int timeoutMs, int udpPort, string authToken)
        {
            var result = new AIBridgeRuntimeLanDiscoveryResult();
            var targets = new List<AIBridgeRuntimeLanDiscoveryTarget>();
            var sockets = new List<AIBridgeRuntimeLanDiscoverySocket>();
            try
            {
                // Players 面板的默认扫描直接复用 Runtime UDP 协议，避免依赖外部 CLI 进程。
                timeoutMs = Math.Max(100, timeoutMs <= 0 ? DefaultLanDiscoveryTimeoutMs : timeoutMs);
                var startPort = Math.Max(1, Math.Min(MaxPort, udpPort <= 0 ? AIBridgeProjectSettings.DefaultRuntimeBridgeDiscoveryUdpPort : udpPort));
                var endPort = Math.Min(MaxPort, startPort + LanDiscoveryPortScanCount - 1);
                var interfaces = BuildLanDiscoveryInterfacePlan();
                var requestId = "disc_" + Guid.NewGuid().ToString("N");
                var payload = new Dictionary<string, object>
                {
                    ["protocol"] = DiscoveryProtocol,
                    ["version"] = 1,
                    ["requestId"] = requestId
                };
                var bytes = Encoding.UTF8.GetBytes(AIBridgeJson.Serialize(payload, pretty: false));

                for (var i = 0; i < interfaces.Count; i++)
                {
                    var interfaceInfo = interfaces[i];
                    if (!interfaceInfo.Scanned)
                    {
                        continue;
                    }

                    var socket = TryCreateLanDiscoverySocket(interfaceInfo);
                    if (socket == null)
                    {
                        continue;
                    }

                    sockets.Add(socket);
                    result.ScannedInterfaces++;
                    result.SentPackets += SendLanDiscoveryPackets(socket, bytes, startPort, endPort);
                }

                ReceiveLanDiscoveryResponses(sockets, targets, requestId, timeoutMs);
                ApplyLanDiscoveryHealthChecks(targets, timeoutMs, authToken);
                targets.Sort(CompareLanDiscoveryTargets);

                var reachableTargets = targets
                    .Where(target => target.reachable)
                    .ToList();
                WriteDiscoveryCache(reachableTargets);

                result.Success = true;
                result.Count = targets.Count;
                result.ReachableCount = reachableTargets.Count;
                return result;
            }
            catch (Exception exception)
            {
                result.Success = false;
                result.Error = exception.Message;
                return result;
            }
            finally
            {
                for (var i = 0; i < sockets.Count; i++)
                {
                    sockets[i].Dispose();
                }
            }
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

            if (runtime.runtimeSettings == null)
            {
                runtime.runtimeSettings = new AIBridgeRuntimeSettings();
            }

            runtime.runtimeSettings.CopyFrom(CreateRuntimeSettingsFromProjectSettings());
        }

        public static AIBridgeRuntimeSettings CreateRuntimeSettingsFromProjectSettings()
        {
            var source = AIBridgeProjectSettings.Instance.RuntimeBridge;
            return new AIBridgeRuntimeSettings
            {
                enableRuntimeBridge = source.EnableRuntimeBridge,
                allowInReleaseBuild = source.AllowInReleaseBuild,
                exchangeDirectory = source.ExchangeDirectory ?? string.Empty,
                targetId = source.TargetId ?? string.Empty,
                authToken = source.AuthToken ?? string.Empty,
                allowedActions = ParseAllowedActions(source.AllowedActions),
                enableRuntimeCodeExecution = source.EnableRuntimeCodeExecution && AIBridgeHybridClrUtility.IsHybridClrInstalled(),
                heartbeatIntervalSeconds = source.HeartbeatIntervalSeconds,
                logBufferSize = Math.Max(1, source.LogBufferSize),
                maxResultBytes = Math.Max(1024, source.MaxResultBytes),
                keepRunningInBackground = source.KeepRunningInBackground,
                enableHttpTransport = source.EnableHttpTransport,
                httpBindAddress = string.IsNullOrWhiteSpace(source.HttpBindAddress)
                    ? AIBridgeProjectSettings.DefaultRuntimeBridgeHttpBindAddress
                    : source.HttpBindAddress.Trim(),
                httpPort = Math.Max(1, source.HttpPort),
                enableLanDiscovery = source.EnableLanDiscovery,
                discoveryUdpPort = Math.Max(1, source.DiscoveryUdpPort)
            };
        }

        private static List<AIBridgeRuntimeLanDiscoveryInterfaceInfo> BuildLanDiscoveryInterfacePlan()
        {
            var results = new List<AIBridgeRuntimeLanDiscoveryInterfaceInfo>();
            try
            {
                var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
                for (var i = 0; i < networkInterfaces.Length; i++)
                {
                    var networkInterface = networkInterfaces[i];
                    IPInterfaceProperties properties;
                    try
                    {
                        properties = networkInterface.GetIPProperties();
                    }
                    catch
                    {
                        continue;
                    }

                    var unicastAddresses = properties == null ? null : properties.UnicastAddresses;
                    if (unicastAddresses == null)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation addressInfo in unicastAddresses)
                    {
                        if (addressInfo == null
                            || addressInfo.Address == null
                            || addressInfo.Address.AddressFamily != AddressFamily.InterNetwork)
                        {
                            continue;
                        }

                        IPAddress mask = null;
                        try
                        {
                            mask = addressInfo.IPv4Mask;
                        }
                        catch
                        {
                        }

                        var item = CreateLanDiscoveryInterfaceInfo(networkInterface, addressInfo.Address, mask);
                        item.Scanned = IsScannableLanDiscoveryInterface(item);
                        results.Add(item);
                    }
                }
            }
            catch
            {
            }

            results.Sort(CompareLanDiscoveryInterfaces);
            return results;
        }

        private static AIBridgeRuntimeLanDiscoveryInterfaceInfo CreateLanDiscoveryInterfaceInfo(
            NetworkInterface networkInterface,
            IPAddress address,
            IPAddress mask)
        {
            return new AIBridgeRuntimeLanDiscoveryInterfaceInfo
            {
                Name = networkInterface == null ? null : networkInterface.Name,
                Description = networkInterface == null ? null : networkInterface.Description,
                Type = networkInterface == null ? null : networkInterface.NetworkInterfaceType.ToString(),
                Status = networkInterface == null ? null : networkInterface.OperationalStatus.ToString(),
                LocalIp = address == null ? null : address.ToString(),
                BroadcastAddress = address == null || mask == null ? null : BuildBroadcastAddress(address, mask),
                IsVirtual = IsVirtualInterface(networkInterface, address),
                IsLoopback = address != null && IPAddress.IsLoopback(address),
                IsApipa = IsApipaAddress(address)
            };
        }

        private static bool IsScannableLanDiscoveryInterface(AIBridgeRuntimeLanDiscoveryInterfaceInfo item)
        {
            // 默认只扫真实局域网网卡，避免 VPN/虚拟网卡把扫描范围放大。
            return item != null
                && string.Equals(item.Status, OperationalStatus.Up.ToString(), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.LocalIp)
                && !item.IsLoopback
                && !item.IsApipa
                && !item.IsVirtual;
        }

        private static AIBridgeRuntimeLanDiscoverySocket TryCreateLanDiscoverySocket(AIBridgeRuntimeLanDiscoveryInterfaceInfo interfaceInfo)
        {
            try
            {
                IPAddress localAddress;
                if (interfaceInfo == null || !IPAddress.TryParse(interfaceInfo.LocalIp, out localAddress))
                {
                    return null;
                }

                var client = new UdpClient(new IPEndPoint(localAddress, 0))
                {
                    EnableBroadcast = true
                };
                return new AIBridgeRuntimeLanDiscoverySocket(client, interfaceInfo);
            }
            catch
            {
                return null;
            }
        }

        private static int SendLanDiscoveryPackets(
            AIBridgeRuntimeLanDiscoverySocket socket,
            byte[] bytes,
            int startPort,
            int endPort)
        {
            var sent = 0;
            var endpoints = BuildBroadcastEndPoints(socket.Interface);
            for (var port = startPort; port <= endPort; port++)
            {
                for (var i = 0; i < endpoints.Count; i++)
                {
                    try
                    {
                        socket.Client.Send(bytes, bytes.Length, new IPEndPoint(endpoints[i], port));
                        sent++;
                    }
                    catch
                    {
                    }
                }
            }

            return sent;
        }

        private static void ReceiveLanDiscoveryResponses(
            List<AIBridgeRuntimeLanDiscoverySocket> sockets,
            List<AIBridgeRuntimeLanDiscoveryTarget> targets,
            string requestId,
            int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, timeoutMs));
            while (DateTime.UtcNow < deadline)
            {
                var sawPacket = false;
                for (var i = 0; i < sockets.Count; i++)
                {
                    var socket = sockets[i];
                    while (HasPendingUdpPacket(socket.Client))
                    {
                        sawPacket = true;
                        var remote = new IPEndPoint(IPAddress.Any, 0);
                        var responseBytes = socket.Client.Receive(ref remote);
                        var target = ParseLanDiscoveryResponse(responseBytes, requestId, remote, socket.Interface);
                        if (target == null || targets.Any(existing => IsSameLanDiscoveryTarget(existing, target)))
                        {
                            continue;
                        }

                        targets.Add(target);
                    }
                }

                if (!sawPacket)
                {
                    Thread.Sleep(MinReceiveSleepMs);
                }
            }
        }

        private static bool HasPendingUdpPacket(UdpClient client)
        {
            try
            {
                return client != null && client.Available > 0;
            }
            catch
            {
                return false;
            }
        }

        private static AIBridgeRuntimeLanDiscoveryTarget ParseLanDiscoveryResponse(
            byte[] bytes,
            string requestId,
            IPEndPoint remote,
            AIBridgeRuntimeLanDiscoveryInterfaceInfo sourceInterface)
        {
            try
            {
                var json = AIBridgeJson.DeserializeObject(Encoding.UTF8.GetString(bytes));
                if (!string.Equals(GetString(json, "protocol"), DiscoveryProtocol, StringComparison.Ordinal))
                {
                    return null;
                }

                var responseRequestId = GetString(json, "requestId");
                if (!string.IsNullOrWhiteSpace(responseRequestId)
                    && !string.Equals(responseRequestId, requestId, StringComparison.Ordinal))
                {
                    return null;
                }

                var targetId = GetString(json, "targetId");
                var transport = GetString(json, "transport");
                var url = GetString(json, "reachableUrl") ?? GetString(json, "url");
                if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(targetId))
                {
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(transport)
                    && !string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    url = BuildRemoteUrl(remote, DefaultRuntimeHttpPort);
                }

                if (url.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0
                    || url.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    url = BuildRemoteUrl(remote, ReadPort(url, DefaultRuntimeHttpPort));
                }

                var platform = GetString(json, "platform");
                var isLocal = IsLocalTarget(remote, sourceInterface);
                var isVirtual = sourceInterface != null && sourceInterface.IsVirtual;
                var normalizedUrl = NormalizeUrl(url);
                return new AIBridgeRuntimeLanDiscoveryTarget
                {
                    targetId = targetId ?? "http",
                    source = "lan-discovery",
                    transport = "http",
                    url = normalizedUrl,
                    reachableUrl = normalizedUrl,
                    bindUrl = NormalizeUrl(GetString(json, "bindUrl") ?? GetString(json, "httpUrl")),
                    platform = platform,
                    projectName = GetString(json, "projectName"),
                    applicationVersion = GetString(json, "applicationVersion"),
                    deviceName = GetString(json, "deviceName"),
                    requiresToken = GetBool(json, "requiresToken"),
                    capabilities = GetValue(json, "capabilities"),
                    lastSeenUtc = DateTime.UtcNow.ToString("o"),
                    remoteEndPoint = remote == null ? null : remote.ToString(),
                    sourceInterface = sourceInterface == null ? null : sourceInterface.Name,
                    sourceInterfaceDescription = sourceInterface == null ? null : sourceInterface.Description,
                    sourceInterfaceAddress = sourceInterface == null ? null : sourceInterface.LocalIp,
                    sourceInterfaceBroadcast = sourceInterface == null ? null : sourceInterface.BroadcastAddress,
                    isLocal = isLocal,
                    isVirtualInterface = isVirtual,
                    targetKind = ResolveLanDiscoveryTargetKind(platform, isLocal, isVirtual)
                };
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyLanDiscoveryHealthChecks(
            List<AIBridgeRuntimeLanDiscoveryTarget> targets,
            int timeoutMs,
            string authToken)
        {
            var healthTimeoutMs = Math.Min(HealthCheckMaxTimeoutMs, Math.Max(HealthCheckMinTimeoutMs, timeoutMs));
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                target.healthUrl = BuildUrl(target.url, "/aibridge/health");
                target.lastHealthCheckUtc = DateTime.UtcNow.ToString("o");

                Dictionary<string, object> health;
                string error;
                if (!TryGetLanDiscoveryHealth(target.url, healthTimeoutMs, authToken, out health, out error))
                {
                    target.reachable = false;
                    target.healthError = error;
                    continue;
                }

                // 以 health 返回的主线程缓存信息为准，保证缓存目标可直接被 Runtime CLI 使用。
                target.reachable = true;
                target.healthError = null;
                target.lastSeenUtc = DateTime.UtcNow.ToString("o");
                target.targetId = GetString(health, "targetId") ?? target.targetId;
                target.platform = GetString(health, "platform") ?? target.platform;
                target.projectName = GetString(health, "productName") ?? target.projectName;
                target.applicationVersion = GetString(health, "applicationVersion") ?? target.applicationVersion;
                target.deviceName = GetString(health, "deviceName") ?? target.deviceName;
                target.bindUrl = NormalizeUrl(GetString(health, "bindUrl") ?? GetString(health, "httpUrl") ?? target.bindUrl);
                target.reachableUrl = target.url;
                target.capabilities = GetValue(health, "capabilities") ?? target.capabilities;
                target.targetKind = ResolveLanDiscoveryTargetKind(target.platform, target.isLocal, target.isVirtualInterface);
            }
        }

        private static bool TryGetLanDiscoveryHealth(
            string baseUrl,
            int timeoutMs,
            string authToken,
            out Dictionary<string, object> health,
            out string error)
        {
            health = null;
            error = null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(BuildUrl(baseUrl, "/aibridge/health"));
                request.Method = "GET";
                request.Timeout = Math.Max(100, timeoutMs);
                request.ReadWriteTimeout = Math.Max(100, timeoutMs);
                request.Accept = "application/json";
                if (!string.IsNullOrWhiteSpace(authToken))
                {
                    request.Headers[HttpRequestHeader.Authorization] = "Bearer " + authToken;
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    var statusCode = (int)response.StatusCode;
                    if (statusCode < 200 || statusCode >= 300)
                    {
                        error = "HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + response.StatusDescription;
                        return false;
                    }

                    using (var stream = response.GetResponseStream())
                    {
                        if (stream == null)
                        {
                            error = "Empty HTTP response.";
                            return false;
                        }

                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            health = AIBridgeJson.DeserializeObject(reader.ReadToEnd());
                        }
                    }
                }

                if (health == null)
                {
                    error = "Invalid HTTP health response.";
                    return false;
                }

                return true;
            }
            catch (WebException exception)
            {
                var response = exception.Response as HttpWebResponse;
                if (response == null)
                {
                    error = exception.Message;
                }
                else
                {
                    error = "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.StatusDescription;
                    response.Dispose();
                }

                return false;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static void WriteDiscoveryCache(List<AIBridgeRuntimeLanDiscoveryTarget> targets)
        {
            var path = GetDiscoveryCachePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var cache = new Dictionary<string, object>
            {
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["targets"] = targets ?? new List<AIBridgeRuntimeLanDiscoveryTarget>()
            };
            File.WriteAllText(path, AIBridgeJson.Serialize(cache, pretty: true));
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

        private static object GetValue(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value))
            {
                return null;
            }

            return value;
        }

        private static string BuildBroadcastAddress(IPAddress address, IPAddress mask)
        {
            var addressBytes = address == null ? null : address.GetAddressBytes();
            var maskBytes = mask == null ? null : mask.GetAddressBytes();
            if (addressBytes == null || maskBytes == null || addressBytes.Length != 4 || maskBytes.Length != 4)
            {
                return null;
            }

            var broadcastBytes = new byte[4];
            for (var i = 0; i < 4; i++)
            {
                broadcastBytes[i] = (byte)(addressBytes[i] | ~maskBytes[i]);
            }

            return new IPAddress(broadcastBytes).ToString();
        }

        private static List<IPAddress> BuildBroadcastEndPoints(AIBridgeRuntimeLanDiscoveryInterfaceInfo interfaceInfo)
        {
            var addresses = new List<IPAddress> { IPAddress.Broadcast };
            IPAddress subnetBroadcast;
            if (interfaceInfo != null
                && !string.IsNullOrWhiteSpace(interfaceInfo.BroadcastAddress)
                && IPAddress.TryParse(interfaceInfo.BroadcastAddress, out subnetBroadcast)
                && !addresses.Contains(subnetBroadcast))
            {
                addresses.Add(subnetBroadcast);
            }

            return addresses;
        }

        private static bool IsVirtualInterface(NetworkInterface networkInterface, IPAddress address)
        {
            if (networkInterface == null)
            {
                return false;
            }

            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Unknown)
            {
                return true;
            }

            if (IsBenchmarkingRange(address))
            {
                return true;
            }

            var text = ((networkInterface.Name ?? string.Empty) + " " + (networkInterface.Description ?? string.Empty)).ToLowerInvariant();
            var markers = new[]
            {
                "virtual",
                "vmware",
                "hyper-v",
                "virtualbox",
                "docker",
                "wsl",
                "tap",
                "tun",
                "vpn",
                "tailscale",
                "zerotier",
                "hamachi",
                "loopback",
                "bluetooth"
            };
            for (var i = 0; i < markers.Length; i++)
            {
                if (text.IndexOf(markers[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsApipaAddress(IPAddress address)
        {
            var bytes = address == null ? null : address.GetAddressBytes();
            return bytes != null && bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
        }

        private static bool IsBenchmarkingRange(IPAddress address)
        {
            var bytes = address == null ? null : address.GetAddressBytes();
            return bytes != null && bytes.Length == 4 && bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
        }

        private static bool IsLocalTarget(IPEndPoint remote, AIBridgeRuntimeLanDiscoveryInterfaceInfo sourceInterface)
        {
            if (remote == null || remote.Address == null)
            {
                return false;
            }

            if (IPAddress.IsLoopback(remote.Address))
            {
                return true;
            }

            return sourceInterface != null
                && string.Equals(remote.Address.ToString(), sourceInterface.LocalIp, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildRemoteUrl(IPEndPoint remote, int port)
        {
            var address = remote == null || remote.Address == null ? IPAddress.Loopback : remote.Address;
            return "http://" + FormatHost(address) + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatHost(IPAddress address)
        {
            if (address != null && address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return "[" + address + "]";
            }

            return address == null ? IPAddress.Loopback.ToString() : address.ToString();
        }

        private static int ReadPort(string url, int defaultPort)
        {
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Port > 0)
            {
                return uri.Port;
            }

            return defaultPort;
        }

        private static string BuildUrl(string baseUrl, string path)
        {
            return (baseUrl ?? string.Empty).TrimEnd('/') + "/" + (path ?? string.Empty).TrimStart('/');
        }

        private static string NormalizeUrl(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');
        }

        private static string ResolveLanDiscoveryTargetKind(string platform, bool isLocal, bool isVirtualInterface)
        {
            if (isVirtualInterface)
            {
                return "virtual-interface-target";
            }

            if (IsAndroidPlatform(platform))
            {
                return "android-player";
            }

            return isLocal ? "local-player" : "remote-player";
        }

        private static bool IsAndroidPlatform(string platform)
        {
            return !string.IsNullOrWhiteSpace(platform)
                && platform.IndexOf("Android", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsSameLanDiscoveryTarget(AIBridgeRuntimeLanDiscoveryTarget left, AIBridgeRuntimeLanDiscoveryTarget right)
        {
            return left != null
                && right != null
                && string.Equals(left.targetId, right.targetId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.url, right.url, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareLanDiscoveryTargets(AIBridgeRuntimeLanDiscoveryTarget left, AIBridgeRuntimeLanDiscoveryTarget right)
        {
            var reachableCompare = CompareBoolTrueFirst(left == null || left.reachable, right == null || right.reachable);
            if (reachableCompare != 0)
            {
                return reachableCompare;
            }

            var rankCompare = GetLanDiscoveryTargetPreferenceRank(left).CompareTo(GetLanDiscoveryTargetPreferenceRank(right));
            if (rankCompare != 0)
            {
                return rankCompare;
            }

            DateTimeOffset leftSeen;
            DateTimeOffset rightSeen;
            var leftHasSeen = DateTimeOffset.TryParse(left == null ? null : left.lastSeenUtc, out leftSeen);
            var rightHasSeen = DateTimeOffset.TryParse(right == null ? null : right.lastSeenUtc, out rightSeen);
            if (leftHasSeen && rightHasSeen)
            {
                return rightSeen.CompareTo(leftSeen);
            }

            if (leftHasSeen != rightHasSeen)
            {
                return leftHasSeen ? -1 : 1;
            }

            return string.Compare(left == null ? null : left.targetId, right == null ? null : right.targetId, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetLanDiscoveryTargetPreferenceRank(AIBridgeRuntimeLanDiscoveryTarget target)
        {
            if (target == null)
            {
                return 100;
            }

            if (IsAndroidPlatform(target.platform))
            {
                return 0;
            }

            if (!target.isLocal && !target.isVirtualInterface)
            {
                return 1;
            }

            if (target.isLocal)
            {
                return 2;
            }

            return target.isVirtualInterface ? 3 : 4;
        }

        private static int CompareLanDiscoveryInterfaces(
            AIBridgeRuntimeLanDiscoveryInterfaceInfo left,
            AIBridgeRuntimeLanDiscoveryInterfaceInfo right)
        {
            var scannedCompare = CompareBoolTrueFirst(left != null && left.Scanned, right != null && right.Scanned);
            if (scannedCompare != 0)
            {
                return scannedCompare;
            }

            return string.Compare(left == null ? null : left.Name, right == null ? null : right.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareBoolTrueFirst(bool left, bool right)
        {
            if (left == right)
            {
                return 0;
            }

            return left ? -1 : 1;
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

        private sealed class AIBridgeRuntimeLanDiscoveryInterfaceInfo
        {
            public string Name;
            public string Description;
            public string Type;
            public string Status;
            public string LocalIp;
            public string BroadcastAddress;
            public bool IsVirtual;
            public bool IsLoopback;
            public bool IsApipa;
            public bool Scanned;
        }

        private sealed class AIBridgeRuntimeLanDiscoveryTarget
        {
            public string targetId;
            public string source;
            public string transport;
            public string url;
            public string reachableUrl;
            public string bindUrl;
            public string platform;
            public string projectName;
            public string applicationVersion;
            public string deviceName;
            public bool requiresToken;
            public object capabilities;
            public string lastSeenUtc;
            public string lastHealthCheckUtc;
            public bool reachable;
            public string healthUrl;
            public string healthError;
            public string remoteEndPoint;
            public string sourceInterface;
            public string sourceInterfaceDescription;
            public string sourceInterfaceAddress;
            public string sourceInterfaceBroadcast;
            public bool isLocal;
            public bool isVirtualInterface;
            public string targetKind;
        }

        private sealed class AIBridgeRuntimeLanDiscoverySocket : IDisposable
        {
            public AIBridgeRuntimeLanDiscoverySocket(
                UdpClient client,
                AIBridgeRuntimeLanDiscoveryInterfaceInfo interfaceInfo)
            {
                Client = client;
                Interface = interfaceInfo;
            }

            public UdpClient Client { get; private set; }
            public AIBridgeRuntimeLanDiscoveryInterfaceInfo Interface { get; private set; }

            public void Dispose()
            {
                if (Client != null)
                {
                    Client.Close();
                    Client = null;
                }
            }
        }
    }
}
