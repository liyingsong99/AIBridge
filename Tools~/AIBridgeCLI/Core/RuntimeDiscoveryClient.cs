using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AIBridge.Runtime.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Core
{
    public sealed class RuntimeDiscoveryClient
    {
        public const int DefaultDiscoveryPort = 27183;
        public const int DefaultDiscoveryTimeoutMs = 1500;
        public const int DefaultCacheSeconds = 300;
        public const int DefaultHttpPort = 27182;
        public const int DefaultPortScanCount = 50;

        private const int MinReceiveSleepMs = 10;
        private const int HealthCheckMinTimeoutMs = 500;
        private const int HealthCheckMaxTimeoutMs = 2000;
        private const int AutoDiscoveryTimeoutMs = 1000;
        private const int CacheLockTimeoutMs = 3000;
        private const int CacheLockRetryDelayMs = 25;
        private const int MaxDiscoveryCacheTargets = 64;
        private const string DiscoveryProtocol = "aibridge-runtime-discovery";
        private const string DiscoveryCacheFileName = "discovery-cache.json";
        private const string DiscoveryCacheLockSuffix = ".lock";
        private const string DiscoveryRefreshLockSuffix = ".refresh.lock";

        public RuntimeDiscoveryResult Discover(int timeoutMs, int udpPort, string projectHint = null)
        {
            return Discover(new RuntimeDiscoveryOptions
            {
                timeoutMs = timeoutMs,
                udpPort = udpPort,
                projectHint = projectHint,
                scanAllInterfaces = true
            });
        }

        public RuntimeDiscoveryResult Discover(RuntimeDiscoveryOptions options)
        {
            options = NormalizeOptions(options);
            var responses = new List<RuntimeDiscoveryTarget>();
            var scannedInterfaces = BuildInterfacePlan(options);
            var requestId = "disc_" + Guid.NewGuid().ToString("N");
            var payload = JsonConvert.SerializeObject(new
            {
                protocol = DiscoveryProtocol,
                version = 1,
                requestId = requestId,
                projectHint = options.projectHint
            }, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

            var bytes = Encoding.UTF8.GetBytes(payload);
            var sentPackets = 0;
            var receivedPackets = 0;
            var duplicateResponses = 0;
            var invalidResponses = 0;
            var ignoredByProjectHint = 0;
            var sockets = new List<RuntimeDiscoverySocket>();
            var startPort = options.udpPort <= 0 ? DefaultDiscoveryPort : options.udpPort;
            var endPort = Math.Min(65535, startPort + DefaultPortScanCount - 1);

            try
            {
                for (var i = 0; i < scannedInterfaces.Count; i++)
                {
                    var interfaceInfo = scannedInterfaces[i];
                    if (!interfaceInfo.scanned)
                    {
                        continue;
                    }

                    var socket = TryCreateSocket(interfaceInfo);
                    if (socket == null)
                    {
                        continue;
                    }

                    sockets.Add(socket);
                    sentPackets += SendDiscoveryPackets(socket, bytes, startPort, endPort);
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, options.timeoutMs));
                while (DateTime.UtcNow < deadline)
                {
                    var sawPacket = false;
                    for (var i = 0; i < sockets.Count; i++)
                    {
                        var socket = sockets[i];
                        while (socket.Client.Available > 0)
                        {
                            sawPacket = true;
                            receivedPackets++;
                            var remote = new IPEndPoint(IPAddress.Any, 0);
                            var responseBytes = socket.Client.Receive(ref remote);
                            var target = ParseResponse(responseBytes, requestId, remote, socket.Interface);
                            if (target == null)
                            {
                                invalidResponses++;
                                continue;
                            }

                            if (IsIgnoredByProjectHint(target, options.projectHint))
                            {
                                ignoredByProjectHint++;
                                continue;
                            }

                            if (responses.Any(existing => SameTarget(existing, target)))
                            {
                                duplicateResponses++;
                                continue;
                            }

                            responses.Add(target);
                        }
                    }

                    if (!sawPacket)
                    {
                        Thread.Sleep(MinReceiveSleepMs);
                    }
                }
            }
            finally
            {
                for (var i = 0; i < sockets.Count; i++)
                {
                    sockets[i].Dispose();
                }
            }

            ApplyHealthChecks(responses, options);
            responses.Sort(CompareTargets);

            var cacheTargets = responses
                .Where(target => target.reachable)
                .ToList();
            WriteCache(cacheTargets, options.cacheSeconds);

            var reachableCount = responses.Count(target => target.reachable);
            return new RuntimeDiscoveryResult
            {
                success = true,
                count = responses.Count,
                reachableCount = reachableCount,
                targets = responses,
                cachePath = GetCachePath(),
                diagnostics = new RuntimeDiscoveryDiagnostics
                {
                    scannedInterfaces = scannedInterfaces,
                    sentPackets = sentPackets,
                    receivedPackets = receivedPackets,
                    duplicateResponses = duplicateResponses,
                    invalidResponses = invalidResponses,
                    ignoredByProjectHint = ignoredByProjectHint,
                    healthPassed = reachableCount,
                    healthFailed = responses.Count - reachableCount,
                    startUdpPort = startPort,
                    endUdpPort = endPort,
                    timeoutMs = options.timeoutMs,
                    suggestions = BuildSuggestions(responses, scannedInterfaces, sentPackets)
                }
            };
        }

        public static List<RuntimeDiscoveryTarget> ReadFreshCache(int cacheSeconds)
        {
            var path = GetCachePath();
            if (!File.Exists(path))
            {
                return new List<RuntimeDiscoveryTarget>();
            }

            try
            {
                JObject json;
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    json = JObject.Load(reader);
                }
                var targetsToken = json["targets"] as JArray;
                if (targetsToken == null)
                {
                    return new List<RuntimeDiscoveryTarget>();
                }

                var maxAge = TimeSpan.FromSeconds(Math.Max(1, cacheSeconds <= 0 ? DefaultCacheSeconds : cacheSeconds));
                var now = DateTimeOffset.UtcNow;
                var results = new List<RuntimeDiscoveryTarget>();
                foreach (var token in targetsToken.OfType<JObject>())
                {
                    var target = ParseCachedTarget(token);
                    if (target == null || string.IsNullOrWhiteSpace(target.url))
                    {
                        continue;
                    }

                    if (target.reachable == false)
                    {
                        continue;
                    }

                    DateTimeOffset seen;
                    if (!DateTimeOffset.TryParse(target.lastSeenUtc, out seen)
                        || now - seen > maxAge
                        || seen - now > maxAge)
                    {
                        continue;
                    }

                    results.Add(target);
                }

                results.Sort(CompareTargets);
                return results;
            }
            catch
            {
                return new List<RuntimeDiscoveryTarget>();
            }
        }

        public static List<RuntimeDiscoveryTarget> ReadFreshCacheOrDiscover(
            int cacheSeconds,
            int udpPort,
            string target,
            string platform,
            string projectHint,
            string token)
        {
            var effectiveCacheSeconds = Math.Max(1, cacheSeconds <= 0 ? DefaultCacheSeconds : cacheSeconds);
            var effectiveUdpPort = udpPort <= 0 ? DefaultDiscoveryPort : udpPort;
            var cached = ReadFreshCache(effectiveCacheSeconds);
            if (SelectCachedTarget(cached, target, platform, projectHint) != null)
            {
                return cached;
            }

            var cachePath = GetCachePath();
            var refreshLockPath = cachePath + DiscoveryRefreshLockSuffix;
            using (var refreshLock = TryAcquireLock(refreshLockPath, CacheLockTimeoutMs))
            {
                if (refreshLock == null)
                {
                    return ReadFreshCache(effectiveCacheSeconds);
                }

                // 只有第一个 CLI 进程执行 LAN 扫描，其余并发请求复用它写入的 cache。
                cached = ReadFreshCache(effectiveCacheSeconds);
                if (SelectCachedTarget(cached, target, platform, projectHint) != null)
                {
                    return cached;
                }

                try
                {
                    new RuntimeDiscoveryClient().Discover(new RuntimeDiscoveryOptions
                    {
                        timeoutMs = AutoDiscoveryTimeoutMs,
                        udpPort = effectiveUdpPort,
                        cacheSeconds = effectiveCacheSeconds,
                        projectHint = projectHint,
                        token = token,
                        scanAllInterfaces = true
                    });
                }
                catch
                {
                    // 自动发现失败时保留已有新鲜缓存，避免瞬时网络异常把 --target latest 解析成空。
                    return ReadFreshCache(effectiveCacheSeconds);
                }

                return ReadFreshCache(effectiveCacheSeconds);
            }
        }

        public static RuntimeDiscoveryTarget SelectCachedTarget(
            List<RuntimeDiscoveryTarget> targets,
            string target,
            string platform,
            string projectHint)
        {
            if (targets == null || targets.Count == 0)
            {
                return null;
            }

            var filtered = targets
                .Where(candidate => MatchesTargetFilter(candidate, target)
                    && MatchesPlatformFilter(candidate, platform)
                    && MatchesProjectHint(candidate, projectHint))
                .ToList();
            if (filtered.Count == 0)
            {
                return null;
            }

            filtered.Sort(CompareTargets);
            return filtered[0];
        }

        public static bool MatchesRuntimeFilters(RuntimeDiscoveryTarget target, string platform, string projectHint)
        {
            return MatchesPlatformFilter(target, platform) && MatchesProjectHint(target, projectHint);
        }

        public static string GetCachePath()
        {
            return Path.Combine(PathHelper.GetExchangeDirectory(), RuntimePathHelper.RuntimeDirectoryName, DiscoveryCacheFileName);
        }

        private static RuntimeDiscoveryOptions NormalizeOptions(RuntimeDiscoveryOptions options)
        {
            options = options ?? new RuntimeDiscoveryOptions();
            options.timeoutMs = Math.Max(100, options.timeoutMs <= 0 ? DefaultDiscoveryTimeoutMs : options.timeoutMs);
            options.udpPort = options.udpPort <= 0 ? DefaultDiscoveryPort : options.udpPort;
            options.cacheSeconds = Math.Max(1, options.cacheSeconds <= 0 ? DefaultCacheSeconds : options.cacheSeconds);
            options.projectHint = NormalizeOption(options.projectHint);
            options.localIp = NormalizeOption(options.localIp);
            options.interfaceName = NormalizeOption(options.interfaceName);
            options.token = NormalizeOption(options.token);
            return options;
        }

        private static List<RuntimeDiscoveryInterfaceInfo> BuildInterfacePlan(RuntimeDiscoveryOptions options)
        {
            var interfaces = EnumerateInterfaces();
            var hasExplicitFilter = !string.IsNullOrWhiteSpace(options.localIp)
                || !string.IsNullOrWhiteSpace(options.interfaceName);
            var selectedCount = 0;

            for (var i = 0; i < interfaces.Count; i++)
            {
                var item = interfaces[i];
                var explicitMatch = hasExplicitFilter && MatchesExplicitInterfaceFilter(item, options);
                if (hasExplicitFilter && !explicitMatch)
                {
                    item.scanned = false;
                    item.skippedReason = "filtered_by_localIp_or_interface";
                    continue;
                }

                if (!string.Equals(item.status, OperationalStatus.Up.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    item.scanned = false;
                    item.skippedReason = "interface_down";
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.localIp))
                {
                    item.scanned = false;
                    item.skippedReason = "no_ipv4";
                    continue;
                }

                if (item.loopback)
                {
                    item.scanned = false;
                    item.skippedReason = "loopback";
                    continue;
                }

                if (item.apipa)
                {
                    item.scanned = false;
                    item.skippedReason = "apipa";
                    continue;
                }

                if (item.isVirtual && !options.includeVirtual && !explicitMatch)
                {
                    item.scanned = false;
                    item.skippedReason = "virtual_or_reserved";
                    continue;
                }

                if (!options.scanAllInterfaces && !hasExplicitFilter && selectedCount > 0)
                {
                    item.scanned = false;
                    item.skippedReason = "scanAllInterfaces_false";
                    continue;
                }

                item.scanned = true;
                selectedCount++;
            }

            interfaces.Sort(CompareInterfaces);
            return interfaces;
        }

        private static List<RuntimeDiscoveryInterfaceInfo> EnumerateInterfaces()
        {
            var results = new List<RuntimeDiscoveryInterfaceInfo>();
            try
            {
                var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
                for (var i = 0; i < networkInterfaces.Length; i++)
                {
                    var nic = networkInterfaces[i];
                    IPInterfaceProperties properties;
                    try
                    {
                        properties = nic.GetIPProperties();
                    }
                    catch
                    {
                        continue;
                    }

                    var unicast = properties == null ? null : properties.UnicastAddresses;
                    if (unicast == null || unicast.Count == 0)
                    {
                        results.Add(CreateInterfaceInfo(nic, null, null));
                        continue;
                    }

                    var added = false;
                    foreach (UnicastIPAddressInformation addressInfo in unicast)
                    {
                        if (addressInfo == null || addressInfo.Address == null)
                        {
                            continue;
                        }

                        if (addressInfo.Address.AddressFamily != AddressFamily.InterNetwork)
                        {
                            continue;
                        }

                        results.Add(CreateInterfaceInfo(nic, addressInfo.Address, addressInfo.IPv4Mask));
                        added = true;
                    }

                    if (!added)
                    {
                        results.Add(CreateInterfaceInfo(nic, null, null));
                    }
                }
            }
            catch
            {
            }

            return results;
        }

        private static RuntimeDiscoveryInterfaceInfo CreateInterfaceInfo(NetworkInterface nic, IPAddress address, IPAddress mask)
        {
            var localIp = address == null ? null : address.ToString();
            var subnetMask = mask == null ? null : mask.ToString();
            var broadcast = address == null || mask == null ? null : BuildBroadcastAddress(address, mask);
            return new RuntimeDiscoveryInterfaceInfo
            {
                name = nic == null ? null : nic.Name,
                description = nic == null ? null : nic.Description,
                id = nic == null ? null : nic.Id,
                type = nic == null ? null : nic.NetworkInterfaceType.ToString(),
                status = nic == null ? null : nic.OperationalStatus.ToString(),
                localIp = localIp,
                subnetMask = subnetMask,
                broadcastAddress = broadcast,
                isVirtual = IsVirtualInterface(nic, address),
                loopback = address != null && IPAddress.IsLoopback(address),
                apipa = IsApipaAddress(address)
            };
        }

        private static RuntimeDiscoverySocket TryCreateSocket(RuntimeDiscoveryInterfaceInfo interfaceInfo)
        {
            try
            {
                IPAddress localAddress;
                if (!IPAddress.TryParse(interfaceInfo.localIp, out localAddress))
                {
                    interfaceInfo.scanned = false;
                    interfaceInfo.skippedReason = "invalid_local_ip";
                    return null;
                }

                var client = new UdpClient(new IPEndPoint(localAddress, 0))
                {
                    EnableBroadcast = true
                };
                return new RuntimeDiscoverySocket(client, interfaceInfo);
            }
            catch (Exception ex)
            {
                interfaceInfo.scanned = false;
                interfaceInfo.error = ex.Message;
                interfaceInfo.skippedReason = "socket_bind_failed";
                return null;
            }
        }

        private static int SendDiscoveryPackets(RuntimeDiscoverySocket socket, byte[] bytes, int startPort, int endPort)
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
                        socket.Interface.sentPackets++;
                    }
                    catch (Exception ex)
                    {
                        socket.Interface.error = ex.Message;
                    }
                }
            }

            return sent;
        }

        private static List<IPAddress> BuildBroadcastEndPoints(RuntimeDiscoveryInterfaceInfo interfaceInfo)
        {
            var addresses = new List<IPAddress> { IPAddress.Broadcast };
            IPAddress subnetBroadcast;
            if (!string.IsNullOrWhiteSpace(interfaceInfo.broadcastAddress)
                && IPAddress.TryParse(interfaceInfo.broadcastAddress, out subnetBroadcast)
                && !addresses.Contains(subnetBroadcast))
            {
                addresses.Add(subnetBroadcast);
            }

            return addresses;
        }

        private static RuntimeDiscoveryTarget ParseResponse(
            byte[] bytes,
            string requestId,
            IPEndPoint remote,
            RuntimeDiscoveryInterfaceInfo sourceInterface)
        {
            try
            {
                var json = JObject.Parse(Encoding.UTF8.GetString(bytes));
                if (!string.Equals(ReadString(json, "protocol"), DiscoveryProtocol, StringComparison.Ordinal))
                {
                    return null;
                }

                var responseRequestId = ReadString(json, "requestId");
                if (!string.IsNullOrWhiteSpace(responseRequestId)
                    && !string.Equals(responseRequestId, requestId, StringComparison.Ordinal))
                {
                    return null;
                }

                var targetId = ReadString(json, "targetId");
                var transport = ReadString(json, "transport");
                var url = ReadString(json, "reachableUrl") ?? ReadString(json, "url");
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
                    url = BuildRemoteUrl(remote, DefaultHttpPort);
                }

                if (url.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0
                    || url.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    url = BuildRemoteUrl(remote, ReadPort(url, DefaultHttpPort));
                }

                var normalizedUrl = NormalizeUrl(url);
                var platform = ReadString(json, "platform");
                var isLocal = IsLocalTarget(remote, sourceInterface, platform);
                var isVirtual = sourceInterface != null && sourceInterface.isVirtual;
                var targetKind = ResolveTargetKind(platform, isLocal, isVirtual);

                return new RuntimeDiscoveryTarget
                {
                    targetId = targetId ?? "http",
                    source = "lan-discovery",
                    transport = "http",
                    url = normalizedUrl,
                    reachableUrl = normalizedUrl,
                    bindUrl = NormalizeUrl(ReadString(json, "bindUrl") ?? ReadString(json, "httpUrl")),
                    platform = platform,
                    projectName = ReadString(json, "projectName"),
                    applicationVersion = ReadString(json, "applicationVersion"),
                    deviceName = ReadString(json, "deviceName"),
                    requiresToken = ReadBool(json, "requiresToken") ?? false,
                    capabilities = json["capabilities"],
                    lastSeenUtc = DateTime.UtcNow.ToString("o"),
                    remoteEndPoint = remote == null ? null : remote.ToString(),
                    sourceInterface = sourceInterface == null ? null : sourceInterface.name,
                    sourceInterfaceDescription = sourceInterface == null ? null : sourceInterface.description,
                    sourceInterfaceAddress = sourceInterface == null ? null : sourceInterface.localIp,
                    sourceInterfaceBroadcast = sourceInterface == null ? null : sourceInterface.broadcastAddress,
                    isLocal = isLocal,
                    isVirtualInterface = isVirtual,
                    targetKind = targetKind
                };
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyHealthChecks(List<RuntimeDiscoveryTarget> targets, RuntimeDiscoveryOptions options)
        {
            var timeoutMs = Math.Min(HealthCheckMaxTimeoutMs, Math.Max(HealthCheckMinTimeoutMs, options.timeoutMs));
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                target.healthUrl = BuildUrl(target.url, "/aibridge/health");
                target.lastHealthCheckUtc = DateTime.UtcNow.ToString("o");
                JObject health;
                string error;
                if (!TryGetHealth(target.url, timeoutMs, options.token, out health, out error))
                {
                    target.reachable = false;
                    target.healthError = error;
                    continue;
                }

                target.reachable = true;
                target.health = health;
                target.healthError = null;
                target.lastSeenUtc = DateTime.UtcNow.ToString("o");
                if (!IsHealthReady(health))
                {
                    target.reachable = false;
                    target.healthError = BuildHealthNotReadyReason(health);
                    continue;
                }

                target.targetId = ReadString(health, "targetId") ?? target.targetId;
                target.platform = ReadString(health, "platform") ?? target.platform;
                target.projectName = ReadString(health, "productName") ?? target.projectName;
                target.applicationVersion = ReadString(health, "applicationVersion") ?? target.applicationVersion;
                target.deviceName = ReadString(health, "deviceName") ?? target.deviceName;
                target.bindUrl = NormalizeUrl(ReadString(health, "bindUrl") ?? ReadString(health, "httpUrl") ?? target.bindUrl);
                target.reachableUrl = target.url;
                target.capabilities = health["capabilities"] ?? target.capabilities;
                target.targetKind = ResolveTargetKind(target.platform, target.isLocal, target.isVirtualInterface);
            }
        }

        private static bool TryGetHealth(string baseUrl, int timeoutMs, string token, out JObject health, out string error)
        {
            health = null;
            error = null;
            try
            {
                using (var client = new HttpClient())
                using (var message = new HttpRequestMessage(HttpMethod.Get, BuildUrl(baseUrl, "/aibridge/health")))
                using (var cancellation = new CancellationTokenSource(Math.Max(100, timeoutMs)))
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }

                    var response = client.SendAsync(message, cancellation.Token).GetAwaiter().GetResult();
                    using (response)
                    {
                        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (!response.IsSuccessStatusCode)
                        {
                            error = "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase;
                            return false;
                        }

                        health = JObject.Parse(body);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static RuntimeDiscoveryTarget ParseCachedTarget(JObject json)
        {
            var target = new RuntimeDiscoveryTarget
            {
                targetId = ReadString(json, "targetId") ?? "http",
                source = ReadString(json, "source") ?? "lan-discovery",
                transport = ReadString(json, "transport") ?? "http",
                url = NormalizeUrl(ReadString(json, "url")),
                reachableUrl = NormalizeUrl(ReadString(json, "reachableUrl") ?? ReadString(json, "url")),
                bindUrl = NormalizeUrl(ReadString(json, "bindUrl") ?? ReadString(json, "httpUrl")),
                platform = ReadString(json, "platform"),
                projectName = ReadString(json, "projectName"),
                applicationVersion = ReadString(json, "applicationVersion"),
                deviceName = ReadString(json, "deviceName"),
                requiresToken = ReadBool(json, "requiresToken") ?? false,
                capabilities = json["capabilities"],
                lastSeenUtc = ReadString(json, "lastSeenUtc"),
                lastHealthCheckUtc = ReadString(json, "lastHealthCheckUtc"),
                reachable = ReadBool(json, "reachable") ?? true,
                healthUrl = ReadString(json, "healthUrl"),
                remoteEndPoint = ReadString(json, "remoteEndPoint"),
                sourceInterface = ReadString(json, "sourceInterface"),
                sourceInterfaceDescription = ReadString(json, "sourceInterfaceDescription"),
                sourceInterfaceAddress = ReadString(json, "sourceInterfaceAddress"),
                sourceInterfaceBroadcast = ReadString(json, "sourceInterfaceBroadcast"),
                isLocal = ReadBool(json, "isLocal") ?? false,
                isVirtualInterface = ReadBool(json, "isVirtualInterface") ?? false,
                targetKind = ReadString(json, "targetKind")
            };

            if (string.IsNullOrWhiteSpace(target.targetKind))
            {
                target.targetKind = ResolveTargetKind(target.platform, target.isLocal, target.isVirtualInterface);
            }

            return target;
        }

        private static void WriteCache(List<RuntimeDiscoveryTarget> targets, int cacheSeconds)
        {
            var path = GetCachePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var cacheLock = TryAcquireLock(path + DiscoveryCacheLockSuffix, CacheLockTimeoutMs))
            {
                if (cacheLock == null)
                {
                    return;
                }

                var mergedTargets = MergeCacheTargets(ReadFreshCache(cacheSeconds), targets);
                var json = JsonConvert.SerializeObject(new
                {
                    updatedAtUtc = DateTime.UtcNow.ToString("o"),
                    targets = mergedTargets
                }, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                AIBridgeAtomicFile.WriteTextAtomic(path, json, Encoding.UTF8);
            }
        }

        private static List<RuntimeDiscoveryTarget> MergeCacheTargets(
            List<RuntimeDiscoveryTarget> existing,
            List<RuntimeDiscoveryTarget> incoming)
        {
            var merged = existing == null
                ? new List<RuntimeDiscoveryTarget>()
                : new List<RuntimeDiscoveryTarget>(existing);
            if (incoming == null)
            {
                return merged;
            }

            for (var i = 0; i < incoming.Count; i++)
            {
                var target = incoming[i];
                if (target == null)
                {
                    continue;
                }

                var index = merged.FindIndex(existingTarget => SameTarget(existingTarget, target));
                if (index >= 0)
                {
                    merged[index] = SelectFreshestTarget(merged[index], target);
                }
                else
                {
                    merged.Add(target);
                }
            }

            merged.Sort(CompareTargets);
            if (merged.Count > MaxDiscoveryCacheTargets)
            {
                merged.RemoveRange(MaxDiscoveryCacheTargets, merged.Count - MaxDiscoveryCacheTargets);
            }

            return merged;
        }

        private static RuntimeDiscoveryTarget SelectFreshestTarget(
            RuntimeDiscoveryTarget existing,
            RuntimeDiscoveryTarget incoming)
        {
            if (existing == null)
            {
                return incoming;
            }

            if (incoming == null)
            {
                return existing;
            }

            DateTimeOffset existingSeen;
            DateTimeOffset incomingSeen;
            var existingHasSeen = DateTimeOffset.TryParse(existing.lastSeenUtc, out existingSeen);
            var incomingHasSeen = DateTimeOffset.TryParse(incoming.lastSeenUtc, out incomingSeen);
            if (existingHasSeen && (!incomingHasSeen || existingSeen > incomingSeen))
            {
                return existing;
            }

            return incoming;
        }

        private static FileStream TryAcquireLock(string path, int timeoutMs)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            while (true)
            {
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        return null;
                    }

                    Thread.Sleep(CacheLockRetryDelayMs);
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

        private static bool SameTarget(RuntimeDiscoveryTarget left, RuntimeDiscoveryTarget right)
        {
            return string.Equals(left.targetId, right.targetId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.url, right.url, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareTargets(RuntimeDiscoveryTarget left, RuntimeDiscoveryTarget right)
        {
            var reachableCompare = CompareBoolTrueFirst(left == null || left.reachable, right == null || right.reachable);
            if (reachableCompare != 0)
            {
                return reachableCompare;
            }

            var rankCompare = GetTargetPreferenceRank(left).CompareTo(GetTargetPreferenceRank(right));
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

        private static int CompareInterfaces(RuntimeDiscoveryInterfaceInfo left, RuntimeDiscoveryInterfaceInfo right)
        {
            var scannedCompare = CompareBoolTrueFirst(left != null && left.scanned, right != null && right.scanned);
            if (scannedCompare != 0)
            {
                return scannedCompare;
            }

            return string.Compare(left == null ? null : left.name, right == null ? null : right.name, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareBoolTrueFirst(bool left, bool right)
        {
            if (left == right)
            {
                return 0;
            }

            return left ? -1 : 1;
        }

        private static int GetTargetPreferenceRank(RuntimeDiscoveryTarget target)
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

        private static bool MatchesTargetFilter(RuntimeDiscoveryTarget target, string requestedTarget)
        {
            if (target == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(requestedTarget)
                || string.Equals(requestedTarget, RuntimeTransportOptions.DefaultTarget, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(target.targetId, requestedTarget, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesPlatformFilter(RuntimeDiscoveryTarget target, string platform)
        {
            if (target == null)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(platform)
                || string.Equals(target.platform, platform, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(target.platform)
                    && target.platform.IndexOf(platform, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool MatchesProjectHint(RuntimeDiscoveryTarget target, string projectHint)
        {
            if (target == null)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(projectHint)
                || string.Equals(target.projectName, projectHint, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIgnoredByProjectHint(RuntimeDiscoveryTarget target, string projectHint)
        {
            return target != null
                && !string.IsNullOrWhiteSpace(projectHint)
                && !MatchesProjectHint(target, projectHint);
        }

        private static bool MatchesExplicitInterfaceFilter(RuntimeDiscoveryInterfaceInfo item, RuntimeDiscoveryOptions options)
        {
            if (item == null)
            {
                return false;
            }

            var localIpMatches = string.IsNullOrWhiteSpace(options.localIp)
                || string.Equals(item.localIp, options.localIp, StringComparison.OrdinalIgnoreCase);
            var interfaceMatches = string.IsNullOrWhiteSpace(options.interfaceName)
                || ContainsIgnoreCase(item.name, options.interfaceName)
                || ContainsIgnoreCase(item.description, options.interfaceName)
                || ContainsIgnoreCase(item.id, options.interfaceName);
            return localIpMatches && interfaceMatches;
        }

        private static bool ContainsIgnoreCase(string value, string pattern)
        {
            return string.IsNullOrWhiteSpace(pattern)
                || (!string.IsNullOrWhiteSpace(value)
                    && value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsLocalTarget(IPEndPoint remote, RuntimeDiscoveryInterfaceInfo sourceInterface, string platform)
        {
            if (remote == null || remote.Address == null)
            {
                return false;
            }

            if (IPAddress.IsLoopback(remote.Address))
            {
                return true;
            }

            if (sourceInterface != null
                && string.Equals(remote.Address.ToString(), sourceInterface.localIp, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static string ResolveTargetKind(string platform, bool isLocal, bool isVirtualInterface)
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

        private static bool IsVirtualInterface(NetworkInterface nic, IPAddress address)
        {
            if (nic == null)
            {
                return false;
            }

            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                || nic.NetworkInterfaceType == NetworkInterfaceType.Unknown)
            {
                return true;
            }

            if (IsBenchmarkingRange(address))
            {
                return true;
            }

            var text = ((nic.Name ?? string.Empty) + " " + (nic.Description ?? string.Empty)).ToLowerInvariant();
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

        private static string BuildBroadcastAddress(IPAddress address, IPAddress mask)
        {
            var addressBytes = address.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            if (addressBytes.Length != 4 || maskBytes.Length != 4)
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
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0)
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

        private static string NormalizeOption(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string ReadString(JObject obj, string key)
        {
            return obj != null && obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var value)
                ? value.Value<string>()
                : null;
        }

        private static bool? ReadBool(JObject obj, string key)
        {
            if (obj == null || !obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var value))
            {
                return null;
            }

            if (value.Type == JTokenType.Boolean)
            {
                return value.Value<bool>();
            }

            bool parsed;
            return bool.TryParse(value.Value<string>(), out parsed) ? parsed : (bool?)null;
        }

        private static bool IsHealthReady(JObject health)
        {
            var ready = ReadBool(health, "ready");
            if (ready.HasValue && !ready.Value)
            {
                return false;
            }

            var commandPumpReady = ReadBool(health, "commandPumpReady");
            if (commandPumpReady.HasValue && !commandPumpReady.Value)
            {
                return false;
            }

            return true;
        }

        private static string BuildHealthNotReadyReason(JObject health)
        {
            var reason = ReadString(health, "commandPumpReason");
            if (!string.IsNullOrWhiteSpace(reason))
            {
                return reason;
            }

            var state = ReadString(health, "runtimeState");
            return string.IsNullOrWhiteSpace(state) ? "runtime_not_ready" : "runtime_not_ready: " + state;
        }

        private static List<string> BuildSuggestions(
            List<RuntimeDiscoveryTarget> responses,
            List<RuntimeDiscoveryInterfaceInfo> scannedInterfaces,
            int sentPackets)
        {
            var suggestions = new List<string>();
            if (responses != null && responses.Any(target => target.reachable))
            {
                suggestions.Add("HTTP/LAN discovery is reachable. Use `runtime status --target latest --platform Android` or the returned url.");
                return suggestions;
            }

            if (sentPackets <= 0)
            {
                suggestions.Add("No UDP discovery packet was sent. Check --localIp/--interface filters and skipped scannedInterfaces.");
            }

            suggestions.Add("Verify the Player log contains `[AIBridgeRuntime] HTTP transport listening` and `LAN discovery listening`.");
            suggestions.Add("For Release builds, verify AIBRIDGE_RUNTIME_ALLOW_RELEASE_BUILD is defined or runtimeSettings.allowInReleaseBuild is enabled.");
            suggestions.Add("Verify the phone and PC are on the same LAN subnet, and firewall/router rules allow UDP 27183-27232 and TCP 27182-27231.");
            suggestions.Add("If multiple adapters exist, retry with `--interface WLAN` or `--localIp <wifi-ip>`; use `--includeVirtual true` only for virtual targets.");
            return suggestions;
        }

        private sealed class RuntimeDiscoverySocket : IDisposable
        {
            public RuntimeDiscoverySocket(UdpClient client, RuntimeDiscoveryInterfaceInfo interfaceInfo)
            {
                Client = client;
                Interface = interfaceInfo;
            }

            public UdpClient Client { get; private set; }
            public RuntimeDiscoveryInterfaceInfo Interface { get; private set; }

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

    public sealed class RuntimeDiscoveryOptions
    {
        public int timeoutMs { get; set; } = RuntimeDiscoveryClient.DefaultDiscoveryTimeoutMs;
        public int udpPort { get; set; } = RuntimeDiscoveryClient.DefaultDiscoveryPort;
        public int cacheSeconds { get; set; } = RuntimeDiscoveryClient.DefaultCacheSeconds;
        public string projectHint { get; set; }
        public string localIp { get; set; }
        public string interfaceName { get; set; }
        public bool includeVirtual { get; set; }
        public bool scanAllInterfaces { get; set; } = true;
        public string token { get; set; }
    }

    public sealed class RuntimeDiscoveryResult
    {
        public bool success { get; set; }
        public int count { get; set; }
        public int reachableCount { get; set; }
        public List<RuntimeDiscoveryTarget> targets { get; set; }
        public string cachePath { get; set; }
        public RuntimeDiscoveryDiagnostics diagnostics { get; set; }
    }

    public sealed class RuntimeDiscoveryDiagnostics
    {
        public List<RuntimeDiscoveryInterfaceInfo> scannedInterfaces { get; set; }
        public int sentPackets { get; set; }
        public int receivedPackets { get; set; }
        public int duplicateResponses { get; set; }
        public int invalidResponses { get; set; }
        public int ignoredByProjectHint { get; set; }
        public int healthPassed { get; set; }
        public int healthFailed { get; set; }
        public int startUdpPort { get; set; }
        public int endUdpPort { get; set; }
        public int timeoutMs { get; set; }
        public List<string> suggestions { get; set; }
    }

    public sealed class RuntimeDiscoveryInterfaceInfo
    {
        public string name { get; set; }
        public string description { get; set; }
        public string id { get; set; }
        public string type { get; set; }
        public string status { get; set; }
        public string localIp { get; set; }
        public string subnetMask { get; set; }
        public string broadcastAddress { get; set; }
        public bool isVirtual { get; set; }
        public bool loopback { get; set; }
        public bool apipa { get; set; }
        public bool scanned { get; set; }
        public string skippedReason { get; set; }
        public int sentPackets { get; set; }
        public string error { get; set; }
    }

    public sealed class RuntimeDiscoveryTarget
    {
        public string targetId { get; set; }
        public string source { get; set; }
        public string transport { get; set; }
        public string url { get; set; }
        public string reachableUrl { get; set; }
        public string bindUrl { get; set; }
        public string platform { get; set; }
        public string projectName { get; set; }
        public string applicationVersion { get; set; }
        public string deviceName { get; set; }
        public bool requiresToken { get; set; }
        public JToken capabilities { get; set; }
        public string lastSeenUtc { get; set; }
        public string lastHealthCheckUtc { get; set; }
        public bool reachable { get; set; }
        public string healthUrl { get; set; }
        public string healthError { get; set; }
        public JToken health { get; set; }
        public string remoteEndPoint { get; set; }
        public string sourceInterface { get; set; }
        public string sourceInterfaceDescription { get; set; }
        public string sourceInterfaceAddress { get; set; }
        public string sourceInterfaceBroadcast { get; set; }
        public bool isLocal { get; set; }
        public bool isVirtualInterface { get; set; }
        public string targetKind { get; set; }
    }
}
