using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Core
{
    public sealed class RuntimeConfig
    {
        public const string FileName = "runtime-config.json";

        public string transport { get; set; }
        public string url { get; set; }
        public string target { get; set; }
        public string token { get; set; }
        public RuntimeDiscoveryConfig discovery { get; set; }

        public static RuntimeConfig Load()
        {
            var path = GetConfigPath();
            if (!File.Exists(path))
            {
                return new RuntimeConfig();
            }

            try
            {
                var json = JObject.Parse(File.ReadAllText(path));
                var discoveryToken = json["discovery"] as JObject;
                return new RuntimeConfig
                {
                    transport = ReadString(json, "transport"),
                    url = ReadString(json, "url"),
                    target = ReadString(json, "target"),
                    token = ReadString(json, "token"),
                    discovery = discoveryToken == null
                        ? null
                        : new RuntimeDiscoveryConfig
                        {
                            enabled = ReadBool(discoveryToken, "enabled") ?? true,
                            udpPort = ReadInt(discoveryToken, "udpPort") ?? RuntimeDiscoveryClient.DefaultDiscoveryPort,
                            cacheSeconds = Math.Max(
                                RuntimeDiscoveryClient.DefaultCacheSeconds,
                                ReadInt(discoveryToken, "cacheSeconds") ?? RuntimeDiscoveryClient.DefaultCacheSeconds)
                        }
                };
            }
            catch
            {
                return new RuntimeConfig();
            }
        }

        public static string GetConfigPath()
        {
            return Path.Combine(PathHelper.GetExchangeDirectory(), FileName);
        }

        private static string ReadString(JObject obj, string key)
        {
            return obj != null && obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var value)
                ? value.Value<string>()
                : null;
        }

        private static int? ReadInt(JObject obj, string key)
        {
            if (obj == null || !obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var value))
            {
                return null;
            }

            return value.Type == JTokenType.Integer ? value.Value<int>() : (int?)null;
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

    public sealed class RuntimeDiscoveryConfig
    {
        public bool enabled { get; set; } = true;
        public int udpPort { get; set; } = RuntimeDiscoveryClient.DefaultDiscoveryPort;
        public int cacheSeconds { get; set; } = RuntimeDiscoveryClient.DefaultCacheSeconds;
    }
}
