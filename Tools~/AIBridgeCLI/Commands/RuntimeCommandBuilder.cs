using System;
using System.Collections.Generic;
using AIBridgeCLI.Core;
using Newtonsoft.Json;

namespace AIBridgeCLI.Commands
{
    public class RuntimeCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "runtime";
        public override string Description => "Connect to AIBridge Runtime in a built Player";

        public override string[] Actions => new[]
        {
            "list_targets",
            "discover",
            "ping",
            "status",
            "logs",
            "perf",
            "screenshot",
            "snapshot",
            "find",
            "raycast",
            "click",
            "key",
            "handlers",
            "diagnose",
            "call"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["list_targets"] = CommonTargetParameters(),
            ["discover"] = new List<ParameterInfo>
            {
                new ParameterInfo("timeout", "LAN discovery timeout in milliseconds", false, "1500"),
                new ParameterInfo("udpPort", "First UDP discovery port to scan", false, "27183"),
                new ParameterInfo("projectHint", "Optional project name hint", false),
                new ParameterInfo("localIp", "Scan only this local IPv4 address", false),
                new ParameterInfo("interface", "Scan only matching network interface name/description", false),
                new ParameterInfo("includeVirtual", "Include virtual/tunnel/reserved interfaces", false, "false"),
                new ParameterInfo("scanAllInterfaces", "Scan every valid IPv4 interface", false, "true"),
                new ParameterInfo("token", "Optional runtime auth token for health checks", false)
            },
            ["ping"] = CommonTargetParameters(),
            ["status"] = CommonTargetParameters(),
            ["logs"] = new List<ParameterInfo>
            {
                new ParameterInfo("target", "Runtime target id or latest", false, "latest"),
                new ParameterInfo("runtime-dir", "Runtime exchange directory", false),
                new ParameterInfo("transport", "Runtime transport: http, file", false, "http"),
                new ParameterInfo("count", "Maximum number of log entries", false, "50"),
                new ParameterInfo("tail", "Alias for count", false),
                new ParameterInfo("logType", "Filter by type: all, Log, Warning, Error, Exception, Assert", false, "all"),
                new ParameterInfo("regex", "Filter by message regex", false),
                new ParameterInfo("includeStackTrace", "Include stack traces", false, "false"),
                new ParameterInfo("sinceFrame", "Only return logs at or after this frame", false),
                new ParameterInfo("sinceTime", "Only return logs at or after this UTC time or Unix timestamp", false),
                new ParameterInfo("clear", "Clear runtime log buffer", false, "false"),
                new ParameterInfo("url", "HTTP runtime base URL, e.g. http://host:27182", false),
                new ParameterInfo("token", "Optional runtime auth token", false)
            },
            ["perf"] = new List<ParameterInfo>
            {
                new ParameterInfo("target", "Runtime target id or latest", false, "latest"),
                new ParameterInfo("runtime-dir", "Runtime exchange directory", false),
                new ParameterInfo("transport", "Runtime transport: http, file", false, "http"),
                new ParameterInfo("duration", "Sampling duration, e.g. 5s or 5000ms", false, "5s"),
                new ParameterInfo("interval", "Sampling interval, e.g. 100ms", false, "100ms"),
                new ParameterInfo("hitchThresholdMs", "Frame time threshold counted as a hitch", false, "50"),
                new ParameterInfo("url", "HTTP runtime base URL, e.g. http://host:27182", false),
                new ParameterInfo("token", "Optional runtime auth token", false)
            },
            ["screenshot"] = new List<ParameterInfo>
            {
                new ParameterInfo("target", "Runtime target id or latest", false, "latest"),
                new ParameterInfo("runtime-dir", "Runtime exchange directory", false),
                new ParameterInfo("transport", "Runtime transport: http, file", false, "http"),
                new ParameterInfo("output", "Optional PC output path for the screenshot", false),
                new ParameterInfo("url", "HTTP runtime base URL, e.g. http://host:27182", false),
                new ParameterInfo("token", "Optional runtime auth token", false)
            },
            ["snapshot"] = new List<ParameterInfo>
            {
                new ParameterInfo("target", "Runtime target id or latest", false, "latest"),
                new ParameterInfo("runtime-dir", "Runtime exchange directory", false),
                new ParameterInfo("transport", "Runtime transport: http, file", false, "http"),
                new ParameterInfo("maxResults", "Maximum number of button entries", false, "100"),
                new ParameterInfo("includeDisabled", "Include non-interactable active buttons", false, "false"),
                new ParameterInfo("url", "HTTP runtime base URL, e.g. http://host:27182", false),
                new ParameterInfo("token", "Optional runtime auth token", false)
            },
            ["find"] = new List<ParameterInfo>
            {
                new ParameterInfo("keyword", "Filter by button name, label, or path", false),
                new ParameterInfo("target", "Runtime target id or latest", false, "latest"),
                new ParameterInfo("runtime-dir", "Runtime exchange directory", false),
                new ParameterInfo("transport", "Runtime transport: http, file", false, "http"),
                new ParameterInfo("maxResults", "Maximum number of matched buttons", false, "100"),
                new ParameterInfo("includeDisabled", "Include non-interactable active buttons", false, "false"),
                new ParameterInfo("url", "HTTP runtime base URL, e.g. http://host:27182", false),
                new ParameterInfo("token", "Optional runtime auth token", false)
            },
            ["raycast"] = new List<ParameterInfo>
            {
                new ParameterInfo("x", "Screen X coordinate", false),
                new ParameterInfo("y", "Screen Y coordinate", false),
                new ParameterInfo("path", "Target GameObject path", false),
                new ParameterInfo("entityId", "Target GameObject entity ID from runtime UI snapshot", false),
                new ParameterInfo("instanceId", "Backward-compatible alias for entityId on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("target", "Runtime target id or latest", false, "latest"),
                new ParameterInfo("runtime-dir", "Runtime exchange directory", false),
                new ParameterInfo("transport", "Runtime transport: http, file", false, "http"),
                new ParameterInfo("maxResults", "Maximum number of raycast hits", false, "20"),
                new ParameterInfo("url", "HTTP runtime base URL, e.g. http://host:27182", false),
                new ParameterInfo("token", "Optional runtime auth token", false)
            },
            ["click"] = new List<ParameterInfo>
            {
                new ParameterInfo("x", "Screen X coordinate", false),
                new ParameterInfo("y", "Screen Y coordinate", false),
                new ParameterInfo("path", "Target GameObject path", false),
                new ParameterInfo("entityId", "Target GameObject entity ID from runtime UI snapshot", false),
                new ParameterInfo("instanceId", "Backward-compatible alias for entityId on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("target", "Runtime target id or latest", false, "latest"),
                new ParameterInfo("runtime-dir", "Runtime exchange directory", false),
                new ParameterInfo("transport", "Runtime transport: http, file", false, "http"),
                new ParameterInfo("url", "HTTP runtime base URL, e.g. http://host:27182", false),
                new ParameterInfo("token", "Optional runtime auth token", false)
            },
            ["key"] = new List<ParameterInfo>
            {
                new ParameterInfo("key", "Semantic key name, such as submit, cancel, tab, up, down", true),
                new ParameterInfo("path", "Target GameObject path", false),
                new ParameterInfo("entityId", "Target GameObject entity ID from runtime UI snapshot", false),
                new ParameterInfo("instanceId", "Backward-compatible alias for entityId on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("target", "Runtime target id or latest", false, "latest"),
                new ParameterInfo("runtime-dir", "Runtime exchange directory", false),
                new ParameterInfo("transport", "Runtime transport: http, file", false, "http"),
                new ParameterInfo("url", "HTTP runtime base URL, e.g. http://host:27182", false),
                new ParameterInfo("token", "Optional runtime auth token", false)
            },
            ["handlers"] = CommonTargetParameters(),
            ["diagnose"] = CommonTargetParameters(),
            ["call"] = new List<ParameterInfo>
            {
                new ParameterInfo("action", "Registered runtime business action", true),
                new ParameterInfo("target", "Runtime target id or latest", false, "latest"),
                new ParameterInfo("runtime-dir", "Runtime exchange directory", false),
                new ParameterInfo("transport", "Runtime transport: http, file", false, "http"),
                new ParameterInfo("json", "JSON parameters passed to the runtime handler", false),
                new ParameterInfo("url", "HTTP runtime base URL, e.g. http://host:27182", false),
                new ParameterInfo("token", "Optional runtime auth token", false)
            }
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            var normalizedAction = string.IsNullOrWhiteSpace(action) ? "status" : action.Trim();
            var request = new CommandRequest
            {
                id = PathHelper.GenerateCommandId(),
                type = Type,
                @params = new Dictionary<string, object>()
            };

            if (options.TryGetValue("token", out var token) && !string.IsNullOrEmpty(token))
            {
                request.@params["token"] = token;
            }

            switch (normalizedAction.ToLowerInvariant())
            {
                case "list_targets":
                    request.@params["action"] = "runtime.list_targets";
                    return request;
                case "discover":
                    request.@params["action"] = "runtime.discover";
                    CopyOptions(request, options, includeJson: false, excludeActionOption: false);
                    return request;
                case "ping":
                    request.@params["action"] = "runtime.ping";
                    break;
                case "status":
                    request.@params["action"] = "runtime.status";
                    break;
                case "logs":
                    request.@params["action"] = "runtime.logs";
                    CopyOptions(request, options, includeJson: false, excludeActionOption: false);
                    break;
                case "perf":
                    request.@params["action"] = "runtime.perf";
                    CopyOptions(request, options, includeJson: false, excludeActionOption: false);
                    break;
                case "screenshot":
                    request.@params["action"] = "runtime.screenshot";
                    CopyOptions(request, options, includeJson: false, excludeActionOption: false);
                    break;
                case "snapshot":
                    request.@params["action"] = "runtime.ui.snapshot";
                    CopyOptions(request, options, includeJson: false, excludeActionOption: false);
                    break;
                case "find":
                    request.@params["action"] = "runtime.ui.find";
                    CopyOptions(request, options, includeJson: false, excludeActionOption: false);
                    break;
                case "raycast":
                    request.@params["action"] = "runtime.ui.raycast";
                    CopyOptions(request, options, includeJson: false, excludeActionOption: false);
                    break;
                case "click":
                    request.@params["action"] = "runtime.ui.click";
                    CopyOptions(request, options, includeJson: false, excludeActionOption: false);
                    break;
                case "key":
                    request.@params["action"] = "runtime.input.key";
                    CopyOptions(request, options, includeJson: false, excludeActionOption: false);
                    break;
                case "handlers":
                    request.@params["action"] = "runtime.handlers";
                    break;
                case "diagnose":
                    request.@params["action"] = "runtime.diagnose";
                    break;
                case "call":
                    BuildCallRequest(request, options);
                    break;
                default:
                    throw new ArgumentException($"Unknown runtime action: {normalizedAction}");
            }

            return request;
        }

        private static void BuildCallRequest(CommandRequest request, Dictionary<string, string> options)
        {
            var hasAction = options.TryGetValue("action", out var businessAction) && !string.IsNullOrWhiteSpace(businessAction);
            if (!hasAction)
            {
                throw new ArgumentException("Missing required parameter: --action");
            }

            request.@params["action"] = businessAction;
            CopyJsonParams(request, options);
            CopyOptions(request, options, includeJson: false, excludeActionOption: true);
        }

        private static void CopyJsonParams(CommandRequest request, Dictionary<string, string> options)
        {
            if (!options.TryGetValue("json", out var jsonValue) || string.IsNullOrWhiteSpace(jsonValue))
            {
                return;
            }

            try
            {
                var jsonParams = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonValue);
                if (jsonParams == null)
                {
                    return;
                }

                foreach (var kvp in jsonParams)
                {
                    if (!string.Equals(kvp.Key, "action", StringComparison.OrdinalIgnoreCase))
                    {
                        request.@params[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Invalid JSON in --json parameter: {ex.Message}");
            }
        }

        private static void CopyOptions(
            CommandRequest request,
            Dictionary<string, string> options,
            bool includeJson,
            bool excludeActionOption)
        {
            foreach (var kvp in options)
            {
                if (IsGlobalOption(kvp.Key) || (!includeJson && string.Equals(kvp.Key, "json", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (excludeActionOption && string.Equals(kvp.Key, "action", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                request.@params[kvp.Key] = ParseStaticValue(kvp.Value);
            }
        }

        private static bool IsGlobalOption(string key)
        {
            return string.Equals(key, "stdin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "timeout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "transport-timeout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "poll-interval", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "no-wait", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "raw", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "pretty", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "quiet", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "on-dialog", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "workflow-run", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "target", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "runtime-dir", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "transport", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "url", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "token", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "platform", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "projectHint", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "probe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "scan-local", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "scanLocal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "quick", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "diagnose", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "deep", StringComparison.OrdinalIgnoreCase);
        }

        private static object ParseStaticValue(string value)
        {
            if (value == null)
            {
                return null;
            }

            if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (long.TryParse(value, out var longValue))
            {
                return longValue;
            }

            if (double.TryParse(value, out var doubleValue))
            {
                return doubleValue;
            }

            return value;
        }

        private static List<ParameterInfo> CommonTargetParameters()
        {
            return new List<ParameterInfo>
            {
                new ParameterInfo("target", "Runtime target id or latest", false, "latest"),
                new ParameterInfo("runtime-dir", "Runtime exchange directory", false),
                new ParameterInfo("transport", "Runtime transport: http, file", false, "http"),
                new ParameterInfo("url", "HTTP runtime base URL, e.g. http://host:27182", false),
                new ParameterInfo("token", "Optional runtime auth token", false),
                new ParameterInfo("platform", "Prefer cached HTTP target by platform, e.g. Android", false),
                new ParameterInfo("projectHint", "Prefer cached HTTP target by project name", false),
                new ParameterInfo("probe", "Probe local runtime ports while resolving targets", false, "false"),
                new ParameterInfo("scan-local", "Alias for --probe true", false, "false"),
                new ParameterInfo("quick", "Skip local runtime port scanning", false, "true"),
                new ParameterInfo("diagnose", "Run deep diagnostics when target resolution fails", false, "false")
            };
        }
    }
}
