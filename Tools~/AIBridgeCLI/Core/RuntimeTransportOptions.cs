using System;

namespace AIBridgeCLI.Core
{
    public enum RuntimeTransportKind
    {
        File,
        Http
    }

    public sealed class RuntimeTransportOptions
    {
        public const string DefaultTarget = "latest";
        public const string TransportEnvironment = "AIBRIDGE_RUNTIME_TRANSPORT";
        public const string DefaultHttpUrl = "http://127.0.0.1:27182";
        public const string HttpUrlEnvironment = "AIBRIDGE_RUNTIME_URL";
        public const string TokenEnvironment = "AIBRIDGE_RUNTIME_TOKEN";
        public const int MinimumDiscoveryCacheSeconds = 300;

        public RuntimeTransportKind Kind { get; private set; }
        public string RuntimeDirectory { get; private set; }
        public string Target { get; private set; }
        public int TimeoutMs { get; private set; }
        public int PollIntervalMs { get; private set; }
        public string HttpUrl { get; private set; }
        public string Token { get; private set; }
        public bool HttpUrlExplicit { get; private set; }
        public int DiscoveryCacheSeconds { get; private set; }
        public string PreferredPlatform { get; private set; }
        public string PreferredProjectHint { get; private set; }

        private RuntimeTransportOptions()
        {
        }

        public static RuntimeTransportOptions Create(
            string transport,
            string runtimeDirectoryOverride,
            string target,
            int timeoutMs,
            int pollIntervalMs)
        {
            var commandLineOptions = ReadCommandLineOptions();
            var config = RuntimeConfig.Load();
            var resolvedTransport = ResolveTransportName(transport, config);
            var resolvedTarget = string.IsNullOrWhiteSpace(target) ? (string.IsNullOrWhiteSpace(config.target) ? DefaultTarget : config.target) : target;
            var runtimeDirectory = RuntimePathHelper.ResolveRuntimeDirectory(runtimeDirectoryOverride);
            var preferredPlatform = ResolveOption(commandLineOptions, "platform");
            var preferredProjectHint = ResolveOption(commandLineOptions, "projectHint");
            var token = ResolveOption(commandLineOptions, "token", TokenEnvironment, config.token);
            var httpUrlExplicit = HasExplicitHttpUrl(commandLineOptions);
            var httpUrl = ResolveHttpUrl(commandLineOptions, config, resolvedTarget, runtimeDirectory, preferredPlatform, preferredProjectHint, token);
            return new RuntimeTransportOptions
            {
                Kind = ParseTransportKind(resolvedTransport),
                RuntimeDirectory = runtimeDirectory,
                Target = resolvedTarget,
                TimeoutMs = timeoutMs,
                PollIntervalMs = pollIntervalMs,
                HttpUrl = NormalizeHttpUrl(httpUrl),
                HttpUrlExplicit = httpUrlExplicit,
                DiscoveryCacheSeconds = ResolveDiscoveryCacheSeconds(config),
                Token = token,
                PreferredPlatform = preferredPlatform,
                PreferredProjectHint = preferredProjectHint
            };
        }

        private static bool HasExplicitHttpUrl(System.Collections.Generic.Dictionary<string, string> options)
        {
            if (options.TryGetValue("url", out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HttpUrlEnvironment));
        }

        private static string ResolveTransportName(string transport, RuntimeConfig config)
        {
            if (!string.IsNullOrWhiteSpace(transport))
            {
                return transport;
            }

            var envTransport = Environment.GetEnvironmentVariable(TransportEnvironment);
            if (!string.IsNullOrWhiteSpace(envTransport))
            {
                return envTransport;
            }

            return string.IsNullOrWhiteSpace(config?.transport) ? "http" : config.transport;
        }

        private static RuntimeTransportKind ParseTransportKind(string transport)
        {
            if (string.Equals(transport, "file", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeTransportKind.File;
            }

            if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeTransportKind.Http;
            }

            if (string.Equals(transport, "adb", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Runtime adb transport has been removed from the core transport path. Use Android USB port forwarding instead: adb reverse tcp:27182 tcp:27182, then run runtime commands with --transport http --url http://127.0.0.1:27182.");
            }

            throw new ArgumentException($"Unsupported runtime transport: {transport}. Supported transports: file, http.");
        }

        private static string ResolveHttpUrl(
            System.Collections.Generic.Dictionary<string, string> options,
            RuntimeConfig config,
            string target,
            string runtimeDirectory,
            string preferredPlatform,
            string preferredProjectHint,
            string token)
        {
            var explicitUrl = ResolveOption(options, "url", HttpUrlEnvironment, null);
            if (!string.IsNullOrWhiteSpace(explicitUrl))
            {
                return explicitUrl;
            }

            var discoveryEnabled = config?.discovery == null || config.discovery.enabled;
            var cacheSeconds = ResolveDiscoveryCacheSeconds(config);
            var discoveryUdpPort = config?.discovery == null
                ? RuntimeDiscoveryClient.DefaultDiscoveryPort
                : config.discovery.udpPort;
            var cachedTargets = discoveryEnabled
                ? RuntimeDiscoveryClient.ReadFreshCacheOrDiscover(
                    cacheSeconds,
                    discoveryUdpPort,
                    target,
                    preferredPlatform,
                    preferredProjectHint,
                    token)
                : null;

            // latest 默认走 discovery cache 的 Android/远端优先排序，避免本机 Player heartbeat 抢占局域网目标。
            if (string.Equals(target, DefaultTarget, StringComparison.OrdinalIgnoreCase))
            {
                var latestCachedTarget = RuntimeDiscoveryClient.SelectCachedTarget(cachedTargets, target, preferredPlatform, preferredProjectHint);
                if (latestCachedTarget != null && !string.IsNullOrWhiteSpace(latestCachedTarget.url))
                {
                    return latestCachedTarget.reachableUrl ?? latestCachedTarget.url;
                }
            }

            // 当前工程 fresh heartbeat 是 Runtime 实际端口的权威来源，优先于可能过期的配置文件。
            if (RuntimePathHelper.TryResolveFreshHttpUrl(runtimeDirectory, target, preferredPlatform, preferredProjectHint, out var heartbeatUrl))
            {
                return heartbeatUrl;
            }

            if (cachedTargets != null)
            {
                var cachedTarget = RuntimeDiscoveryClient.SelectCachedTarget(cachedTargets, target, preferredPlatform, preferredProjectHint);
                if (cachedTarget != null && !string.IsNullOrWhiteSpace(cachedTarget.url))
                {
                    return cachedTarget.reachableUrl ?? cachedTarget.url;
                }
            }

            if (!string.IsNullOrWhiteSpace(config?.url))
            {
                return config.url;
            }

            return DefaultHttpUrl;
        }

        private static int ResolveDiscoveryCacheSeconds(RuntimeConfig config)
        {
            var configured = config == null || config.discovery == null
                ? RuntimeDiscoveryClient.DefaultCacheSeconds
                : config.discovery.cacheSeconds;
            return Math.Max(MinimumDiscoveryCacheSeconds, configured);
        }

        private static string ResolveOption(System.Collections.Generic.Dictionary<string, string> options, string key, string environmentName, string configValue)
        {
            if (options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var envValue = Environment.GetEnvironmentVariable(environmentName);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                return envValue;
            }

            return string.IsNullOrWhiteSpace(configValue) ? null : configValue;
        }

        private static string ResolveOption(System.Collections.Generic.Dictionary<string, string> options, string key)
        {
            if (options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            return null;
        }

        private static System.Collections.Generic.Dictionary<string, string> ReadCommandLineOptions()
        {
            var options = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var args = Environment.GetCommandLineArgs();
            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.IsNullOrEmpty(arg) || !arg.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var key = arg.Substring(2);
                var value = "true";
                var equalsIndex = key.IndexOf('=');
                if (equalsIndex >= 0)
                {
                    value = key.Substring(equalsIndex + 1);
                    key = key.Substring(0, equalsIndex);
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[i + 1];
                    i++;
                }

                options[key] = value;
            }

            return options;
        }

        private static string NormalizeHttpUrl(string value)
        {
            var url = string.IsNullOrWhiteSpace(value) ? DefaultHttpUrl : value.Trim();
            return url.TrimEnd('/');
        }
    }
}
