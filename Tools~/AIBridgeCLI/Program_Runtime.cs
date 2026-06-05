using System;
using System.Collections.Generic;
using System.Globalization;
using AIBridgeCLI.Core;
using Newtonsoft.Json;

namespace AIBridgeCLI
{
    partial class Program
    {
        private const int RuntimePerfTimeoutPaddingMs = 3000;

        static int HandleRuntimeCommand(ParsedArgs parsed, CommandRequest request, int timeout, bool noWait, OutputMode outputMode)
        {
            var runtimeAction = RuntimePathHelper.GetRuntimeAction(request);
            if (string.Equals(runtimeAction, "runtime.discover", StringComparison.OrdinalIgnoreCase))
            {
                return HandleRuntimeDiscover(parsed, outputMode);
            }

            parsed.Options.TryGetValue("runtime-dir", out var runtimeDirectory);
            parsed.Options.TryGetValue("target", out var target);
            parsed.Options.TryGetValue("transport", out var transport);

            var actualTimeout = ResolveRuntimeTimeout(parsed, request, timeout);
            var probeTargets = ShouldProbeRuntimeTargets(parsed, runtimeAction);
            var diagnoseTargetNotFound = parsed.GetBool("diagnose") || parsed.GetBool("deep");
            var sender = new RuntimeCommandSender(
                runtimeDirectory,
                target,
                actualTimeout,
                transport: transport,
                probeTargets: probeTargets,
                diagnoseTargetNotFound: diagnoseTargetNotFound);
            CommandResult result;

            if (noWait)
            {
                result = sender.TrySendCommandNoWait(request);
                TryAttachWorkflowResult(parsed, result, result.success ? 0 : 1);
                if (outputMode == OutputMode.Pretty)
                {
                    OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false);
                }
                else
                {
                    Console.WriteLine(JsonConvert.SerializeObject(new
                    {
                        success = result.success,
                        error = result.error,
                        data = result.data
                    }, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                }

                return result.success ? 0 : 1;
            }

            result = sender.SendCommand(request);
            TryAttachWorkflowResult(parsed, result, result.success ? 0 : 1);
            OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false);
            return result.success ? 0 : 1;
        }

        private static int HandleRuntimeDiscover(ParsedArgs parsed, OutputMode outputMode)
        {
            var config = RuntimeConfig.Load();
            var timeoutMs = parsed.GetInt("timeout", RuntimeDiscoveryClient.DefaultDiscoveryTimeoutMs);
            var defaultUdpPort = config.discovery == null || config.discovery.udpPort <= 0
                ? RuntimeDiscoveryClient.DefaultDiscoveryPort
                : config.discovery.udpPort;
            var udpPort = parsed.GetInt("udpPort", defaultUdpPort);
            parsed.Options.TryGetValue("projectHint", out var projectHint);
            parsed.Options.TryGetValue("localIp", out var localIp);
            parsed.Options.TryGetValue("interface", out var interfaceName);
            parsed.Options.TryGetValue("token", out var token);
            var includeVirtual = parsed.GetBool("includeVirtual");
            var scanAllInterfaces = !parsed.Options.ContainsKey("scanAllInterfaces")
                || parsed.GetBool("scanAllInterfaces");

            CommandResult result;
            try
            {
                var discovery = new RuntimeDiscoveryClient();
                var discoveryResult = discovery.Discover(new RuntimeDiscoveryOptions
                {
                    timeoutMs = timeoutMs,
                    udpPort = udpPort,
                    projectHint = projectHint,
                    localIp = localIp,
                    interfaceName = interfaceName,
                    includeVirtual = includeVirtual,
                    scanAllInterfaces = scanAllInterfaces,
                    token = token
                });
                result = new CommandResult
                {
                    success = true,
                    data = discoveryResult
                };
            }
            catch (Exception ex)
            {
                result = new CommandResult
                {
                    success = false,
                    error = "Runtime LAN discovery failed: " + ex.Message,
                    data = new
                    {
                        udpPort = udpPort,
                        timeoutMs = timeoutMs,
                        localIp = localIp,
                        @interface = interfaceName,
                        includeVirtual = includeVirtual,
                        scanAllInterfaces = scanAllInterfaces
                    }
                };
            }

            TryAttachWorkflowResult(parsed, result, result.success ? 0 : 1);
            OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false);
            return result.success ? 0 : 1;
        }

        private static bool ShouldProbeRuntimeTargets(ParsedArgs parsed, string runtimeAction)
        {
            if (parsed == null)
            {
                return false;
            }

            if (parsed.GetBool("quick"))
            {
                return false;
            }

            if (parsed.GetBool("probe") || parsed.GetBool("scan-local") || parsed.GetBool("scanLocal"))
            {
                return true;
            }

            return string.Equals(runtimeAction, "runtime.diagnose", StringComparison.OrdinalIgnoreCase);
        }

        private static int ResolveRuntimeTimeout(ParsedArgs parsed, CommandRequest request, int timeout)
        {
            if (parsed == null || request == null || parsed.Options.ContainsKey("timeout"))
            {
                return timeout;
            }

            var action = RuntimePathHelper.GetRuntimeAction(request);
            if (!string.Equals(action, "runtime.perf", StringComparison.OrdinalIgnoreCase))
            {
                return timeout;
            }

            var durationMs = ReadRuntimePerfDurationMs(request.@params);
            return Math.Max(timeout, durationMs + RuntimePerfTimeoutPaddingMs);
        }

        private static int ReadRuntimePerfDurationMs(Dictionary<string, object> parameters)
        {
            if (parameters == null)
            {
                return 5000;
            }

            if (TryReadMilliseconds(parameters, "durationMs", out var durationMs))
            {
                return durationMs;
            }

            return TryReadMilliseconds(parameters, "duration", out durationMs) ? durationMs : 5000;
        }

        private static bool TryReadMilliseconds(Dictionary<string, object> parameters, string key, out int milliseconds)
        {
            milliseconds = 0;
            if (!parameters.TryGetValue(key, out var value) || value == null)
            {
                return false;
            }

            if (value is int intValue)
            {
                milliseconds = intValue;
                return milliseconds > 0;
            }

            if (value is long longValue)
            {
                milliseconds = ClampRuntimeTimeout(longValue);
                return milliseconds > 0;
            }

            if (value is double doubleValue)
            {
                milliseconds = ClampRuntimeTimeout((long)Math.Round(doubleValue));
                return milliseconds > 0;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();
            if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                milliseconds = ParseRuntimeTimeoutNumber(text.Substring(0, text.Length - 2), 1d);
                return milliseconds > 0;
            }

            if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                milliseconds = ParseRuntimeTimeoutNumber(text.Substring(0, text.Length - 1), 1000d);
                return milliseconds > 0;
            }

            milliseconds = ParseRuntimeTimeoutNumber(text, 1d);
            return milliseconds > 0;
        }

        private static int ParseRuntimeTimeoutNumber(string text, double multiplier)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return 0;
            }

            return ClampRuntimeTimeout((long)Math.Round(value * multiplier));
        }

        private static int ClampRuntimeTimeout(long value)
        {
            if (value <= 0L)
            {
                return 0;
            }

            return value > int.MaxValue ? int.MaxValue : (int)value;
        }
    }
}
