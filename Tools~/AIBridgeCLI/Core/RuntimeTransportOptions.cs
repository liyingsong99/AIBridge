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

        public RuntimeTransportKind Kind { get; private set; }
        public string RuntimeDirectory { get; private set; }
        public string Target { get; private set; }
        public int TimeoutMs { get; private set; }
        public int PollIntervalMs { get; private set; }
        public string HttpUrl { get; private set; }
        public string Token { get; private set; }

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
            var httpUrl = ResolveHttpUrl(commandLineOptions, config, resolvedTarget, runtimeDirectory);
            return new RuntimeTransportOptions
            {
                Kind = ParseTransportKind(resolvedTransport),
                RuntimeDirectory = runtimeDirectory,
                Target = resolvedTarget,
                TimeoutMs = timeoutMs,
                PollIntervalMs = pollIntervalMs,
                HttpUrl = NormalizeHttpUrl(httpUrl),
                Token = ResolveOption(commandLineOptions, "token", TokenEnvironment, config.token)
            };
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

        private static string ResolveHttpUrl(System.Collections.Generic.Dictionary<string, string> options, RuntimeConfig config, string target, string runtimeDirectory)
        {
            var explicitUrl = ResolveOption(options, "url", HttpUrlEnvironment, null);
            if (!string.IsNullOrWhiteSpace(explicitUrl))
            {
                return explicitUrl;
            }

            // 当前工程 fresh heartbeat 是 Runtime 实际端口的权威来源，优先于可能过期的配置文件。
            if (RuntimePathHelper.TryResolveFreshHttpUrl(runtimeDirectory, target, out var heartbeatUrl))
            {
                return heartbeatUrl;
            }

            if (!string.IsNullOrWhiteSpace(config?.url))
            {
                return config.url;
            }

            var discoveryEnabled = config?.discovery == null || config.discovery.enabled;
            if (discoveryEnabled)
            {
                var cacheSeconds = config?.discovery == null ? RuntimeDiscoveryClient.DefaultCacheSeconds : config.discovery.cacheSeconds;
                var cachedTargets = RuntimeDiscoveryClient.ReadFreshCache(cacheSeconds);
                var cachedTarget = ResolveCachedTarget(cachedTargets, target);
                if (cachedTarget != null && !string.IsNullOrWhiteSpace(cachedTarget.url))
                {
                    return cachedTarget.url;
                }
            }

            return DefaultHttpUrl;
        }

        private static RuntimeDiscoveryTarget ResolveCachedTarget(System.Collections.Generic.List<RuntimeDiscoveryTarget> targets, string target)
        {
            if (targets == null || targets.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(target) && !string.Equals(target, DefaultTarget, StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 0; i < targets.Count; i++)
                {
                    if (string.Equals(targets[i].targetId, target, StringComparison.OrdinalIgnoreCase))
                    {
                        return targets[i];
                    }
                }

                return null;
            }

            return targets[0];
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
