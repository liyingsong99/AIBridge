using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Core
{
    public sealed class RuntimeDiscoveryClient
    {
        public const int DefaultDiscoveryPort = 27183;
        public const int DefaultDiscoveryTimeoutMs = 1500;
        public const int DefaultCacheSeconds = 30;
        private const int DefaultHttpPort = 27182;
        private const string DiscoveryProtocol = "aibridge-runtime-discovery";
        private const string DiscoveryCacheFileName = "discovery-cache.json";

        public RuntimeDiscoveryResult Discover(int timeoutMs, int udpPort, string projectHint = null)
        {
            var responses = new List<RuntimeDiscoveryTarget>();
            var requestId = "disc_" + Guid.NewGuid().ToString("N");
            var payload = JsonConvert.SerializeObject(new
            {
                protocol = DiscoveryProtocol,
                version = 1,
                requestId = requestId,
                projectHint = projectHint
            }, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

            var bytes = Encoding.UTF8.GetBytes(payload);
            using (var udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                udp.Client.ReceiveTimeout = Math.Max(100, timeoutMs);
                udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, udpPort <= 0 ? DefaultDiscoveryPort : udpPort));

                var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, timeoutMs));
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var remote = new IPEndPoint(IPAddress.Any, 0);
                        var responseBytes = udp.Receive(ref remote);
                        var target = ParseResponse(responseBytes, requestId, remote);
                        if (target != null && !responses.Any(existing => SameTarget(existing, target)))
                        {
                            responses.Add(target);
                        }
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                }
            }

            responses.Sort(CompareTargets);
            if (responses.Count > 0)
            {
                WriteCache(responses);
            }

            return new RuntimeDiscoveryResult
            {
                success = true,
                count = responses.Count,
                targets = responses,
                cachePath = GetCachePath()
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
                var json = JObject.Parse(File.ReadAllText(path));
                var targetsToken = json["targets"] as JArray;
                if (targetsToken == null)
                {
                    return new List<RuntimeDiscoveryTarget>();
                }

                var maxAge = TimeSpan.FromSeconds(Math.Max(1, cacheSeconds <= 0 ? DefaultCacheSeconds : cacheSeconds));
                var results = new List<RuntimeDiscoveryTarget>();
                foreach (var token in targetsToken.OfType<JObject>())
                {
                    var target = ParseCachedTarget(token);
                    if (target == null || string.IsNullOrWhiteSpace(target.url))
                    {
                        continue;
                    }

                    if (DateTimeOffset.TryParse(target.lastSeenUtc, out var seen)
                        && DateTimeOffset.UtcNow - seen > maxAge)
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

        public static string GetCachePath()
        {
            return Path.Combine(PathHelper.GetExchangeDirectory(), RuntimePathHelper.RuntimeDirectoryName, DiscoveryCacheFileName);
        }

        private static RuntimeDiscoveryTarget ParseResponse(byte[] bytes, string requestId, IPEndPoint remote)
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
                var url = ReadString(json, "url");
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

                return new RuntimeDiscoveryTarget
                {
                    targetId = targetId ?? "http",
                    source = "lan-discovery",
                    transport = "http",
                    url = NormalizeUrl(url),
                    platform = ReadString(json, "platform"),
                    projectName = ReadString(json, "projectName"),
                    applicationVersion = ReadString(json, "applicationVersion"),
                    deviceName = ReadString(json, "deviceName"),
                    requiresToken = ReadBool(json, "requiresToken") ?? false,
                    capabilities = json["capabilities"],
                    lastSeenUtc = DateTime.UtcNow.ToString("o"),
                    remoteEndPoint = remote.ToString()
                };
            }
            catch
            {
                return null;
            }
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

        private static RuntimeDiscoveryTarget ParseCachedTarget(JObject json)
        {
            return new RuntimeDiscoveryTarget
            {
                targetId = ReadString(json, "targetId") ?? "http",
                source = ReadString(json, "source") ?? "lan-discovery",
                transport = ReadString(json, "transport") ?? "http",
                url = NormalizeUrl(ReadString(json, "url")),
                platform = ReadString(json, "platform"),
                projectName = ReadString(json, "projectName"),
                applicationVersion = ReadString(json, "applicationVersion"),
                deviceName = ReadString(json, "deviceName"),
                requiresToken = ReadBool(json, "requiresToken") ?? false,
                capabilities = json["capabilities"],
                lastSeenUtc = ReadString(json, "lastSeenUtc"),
                remoteEndPoint = ReadString(json, "remoteEndPoint")
            };
        }

        private static void WriteCache(List<RuntimeDiscoveryTarget> targets)
        {
            var path = GetCachePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonConvert.SerializeObject(new
            {
                updatedAtUtc = DateTime.UtcNow.ToString("o"),
                targets = targets
            }, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
        }

        private static bool SameTarget(RuntimeDiscoveryTarget left, RuntimeDiscoveryTarget right)
        {
            return string.Equals(left.targetId, right.targetId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.url, right.url, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareTargets(RuntimeDiscoveryTarget left, RuntimeDiscoveryTarget right)
        {
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

        private static string NormalizeUrl(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');
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

            return value.Type == JTokenType.Boolean ? value.Value<bool>() : (bool?)null;
        }
    }

    public sealed class RuntimeDiscoveryResult
    {
        public bool success { get; set; }
        public int count { get; set; }
        public List<RuntimeDiscoveryTarget> targets { get; set; }
        public string cachePath { get; set; }
    }

    public sealed class RuntimeDiscoveryTarget
    {
        public string targetId { get; set; }
        public string source { get; set; }
        public string transport { get; set; }
        public string url { get; set; }
        public string platform { get; set; }
        public string projectName { get; set; }
        public string applicationVersion { get; set; }
        public string deviceName { get; set; }
        public bool requiresToken { get; set; }
        public JToken capabilities { get; set; }
        public string lastSeenUtc { get; set; }
        public string remoteEndPoint { get; set; }
    }
}
