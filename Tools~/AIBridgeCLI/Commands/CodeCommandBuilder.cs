using System;
using System.Collections.Generic;
using System.IO;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Controlled temporary C# execution command builder.
    /// </summary>
    public class CodeCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "code";
        public override string Description => "Experimental controlled C# code execution (disabled by default in Unity settings)";

        public override string[] Actions => new[]
        {
            "execute",
            "status",
            "cancel"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["execute"] = new List<ParameterInfo>
            {
                new ParameterInfo("file", "Path under .aibridge/code to a .cs or .csx file", false),
                new ParameterInfo("code", "Short inline C# snippet", false),
                new ParameterInfo("timeout", "Execution and CLI wait timeout in milliseconds", false, "5000"),
                new ParameterInfo("allow-experimental", "Must be true for execution", true)
            },
            ["status"] = new List<ParameterInfo>(),
            ["cancel"] = new List<ParameterInfo>
            {
                new ParameterInfo("requestId", "Only cancel if the active code execution matches this request id", false)
            }
        };

        public override string GetHelp(string action = null)
        {
            var help = base.GetHelp(action);
            if (string.Equals(action, "execute", StringComparison.OrdinalIgnoreCase))
            {
                help += Environment.NewLine
                        + "Safety: disabled by default in Unity project settings. Enable AIBridge/Settings -> Basic -> Enable Code Execution first." + Environment.NewLine
                        + "Sources: provide exactly one of --file or --code. File paths must resolve under .aibridge/code and use .cs or .csx." + Environment.NewLine
                        + "Use file mode for complex one-off Editor C# tasks: generated assets, structured analysis, diagnostics, Runtime/Public API calls, or multi-step UnityEditor API orchestration." + Environment.NewLine
                        + "Prefer prefab patch dry-run for existing Prefab structure changes, inspector for properties, and gameobject/transform for simple scene object edits." + Environment.NewLine
                        + "code execute is single-flight. After a timeout, use `code status` to inspect whether Unity is still finishing the async Task, or `code cancel` to release AIBridge waiting state." + Environment.NewLine
                        + "Examples:" + Environment.NewLine
                        + "  AIBridgeCLI code execute --file .aibridge/code/check.csx --allow-experimental true --timeout 5000" + Environment.NewLine
                        + "  AIBridgeCLI code execute --code \"Debug.Log(\\\"hello\\\"); return 123;\" --allow-experimental true --timeout 5000" + Environment.NewLine
                        + "  AIBridgeCLI code status" + Environment.NewLine
                        + "  AIBridgeCLI code cancel" + Environment.NewLine;
            }

            return help;
        }

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            if (string.IsNullOrEmpty(action))
            {
                action = "execute";
            }

            var request = base.Build(action, options);
            ApplyAliases(request.@params);
            if (options.TryGetValue("timeout", out var timeoutValue))
            {
                request.@params["timeout"] = ParseValue(timeoutValue);
            }

            ValidateCodeRules(GetActionName(action, request.@params), request.@params);

            if (request.@params.TryGetValue("file", out var fileValue) && fileValue != null)
            {
                var filePath = fileValue.ToString();
                request.@params["file"] = Path.GetFullPath(filePath);
            }

            return request;
        }

        protected override void ValidateParameters(string action, Dictionary<string, object> @params)
        {
            ApplyAliases(@params);
            ValidateCodeRules(GetActionName(string.IsNullOrEmpty(action) ? "execute" : action, @params), @params);
        }

        private static string GetActionName(string fallbackAction, Dictionary<string, object> @params)
        {
            if (@params != null && @params.TryGetValue("action", out var actionValue) && actionValue != null)
            {
                return actionValue.ToString();
            }

            return fallbackAction;
        }

        private static void ValidateCodeRules(string action, Dictionary<string, object> @params)
        {
            if (string.Equals(action, "status", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "cancel", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.Equals(action, "execute", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Unknown action: {action}");
            }

            if (!HasTrue(@params, "allowExperimental"))
            {
                throw new ArgumentException("code execute requires --allow-experimental true.");
            }

            var hasFile = HasText(@params, "file");
            var hasCode = HasText(@params, "code");
            if (hasFile == hasCode)
            {
                throw new ArgumentException("Provide exactly one source: --file or --code.");
            }
        }

        private static void ApplyAliases(Dictionary<string, object> @params)
        {
            RenameParam(@params, "allow-experimental", "allowExperimental");
        }

        private static void RenameParam(Dictionary<string, object> @params, string sourceKey, string targetKey)
        {
            if (@params == null || !@params.ContainsKey(sourceKey))
            {
                return;
            }

            @params[targetKey] = @params[sourceKey];
            @params.Remove(sourceKey);
        }

        private static bool HasText(Dictionary<string, object> @params, string key)
        {
            if (@params == null || !@params.TryGetValue(key, out var value) || value == null)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(value.ToString());
        }

        private static bool HasTrue(Dictionary<string, object> @params, string key)
        {
            if (@params == null || !@params.TryGetValue(key, out var value) || value == null)
            {
                return false;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            return string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase)
                   || value.ToString() == "1";
        }
    }
}
