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
        public override string Description => "Experimental controlled C# code execution (enabled by default in Unity settings)";

        public override string[] Actions => new[]
        {
            "execute",
            "run",
            "runtime_execute",
            "status",
            "cancel",
            "job_cancel",
            "job_list",
            "job_wait"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["execute"] = new List<ParameterInfo>
            {
                new ParameterInfo("file", "Path under .aibridge/code to a .cs or .csx file", false),
                new ParameterInfo("code", "Short inline C# snippet", false),
                new ParameterInfo("dry-run", "Compile without loading/executing; default: native=false, pipeline=true", false),
                new ParameterInfo("backend", "Explicit code backend: native or pipeline", false, "native"),
                new ParameterInfo("confirm", "Required for each confirmed Pipeline code execution (dry-run=false)", false, "false"),
                new ParameterInfo("timeout", "Execution and CLI wait timeout in milliseconds", false, "5000")
            },
            ["run"] = new List<ParameterInfo>
            {
                new ParameterInfo("file", "Full C# class file directly under .aibridge/code (*.cs only)", true),
                new ParameterInfo("entry", "Explicit static entry: Namespace.Type.Method", true),
                new ParameterInfo("args", "One JSON array of method arguments; default []", false, "[]"),
                new ParameterInfo("dry-run", "Compile only (does not validate entry/args signature); default true", false, "true"),
                new ParameterInfo("confirm", "Required for every Pipeline run with dry-run=false", false, "false"),
                new ParameterInfo("backend", "Only pipeline is supported for code run", false, "pipeline"),
                new ParameterInfo("timeout", "Execution timeout; with job=true this limits local waiting only (1000-60000 ms)", false, "5000"),
                new ParameterInfo("job", "Explicit detached job; upstream execution has no timeout; requires confirm=true even for dry-run", false, "false")
            },
            ["runtime_execute"] = new List<ParameterInfo>
            {
                new ParameterInfo("file", "Path under .aibridge/code to a .cs or .csx file", false),
                new ParameterInfo("code", "Short inline C# snippet", false),
                new ParameterInfo("target", "Runtime target id or latest", false, "latest"),
                new ParameterInfo("runtime-dir", "Runtime exchange directory", false),
                new ParameterInfo("transport", "Runtime transport: http, file", false, "http"),
                new ParameterInfo("url", "HTTP runtime base URL, e.g. http://host:27182", false),
                new ParameterInfo("token", "Optional runtime auth token", false),
                new ParameterInfo("timeout", "Compile, dispatch, and CLI wait timeout in milliseconds", false, "5000")
            },
            ["status"] = new List<ParameterInfo>
            {
                new ParameterInfo("requestId", "Query a persisted Pipeline code operation by its original request id", false)
            },
            ["job_list"] = new List<ParameterInfo>
            {
                new ParameterInfo("limit", "List local registered jobs only (1-100); no remote enumeration", false, "20")
            },
            ["job_wait"] = new List<ParameterInfo>
            {
                new ParameterInfo("requestId", "Original AIBridge request id of a registered detached job", true),
                new ParameterInfo("timeout", "Bounded local observation in milliseconds (1000-60000); does not cancel execution", false, "5000")
            },
            ["job_cancel"] = new List<ParameterInfo>
            {
                new ParameterInfo("requestId", "Original AIBridge request id of an active registered detached job", true),
                new ParameterInfo("confirm", "Confirm cooperative remote cancellation; no rollback or strong stop", true)
            },
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
                        + "Safety: enabled by default in Unity project settings. Disable AIBridge/Settings -> Basic -> Enable Code Execution for untrusted projects or callers." + Environment.NewLine
                        + "Sources: provide exactly one of --file or --code. File paths must resolve under .aibridge/code and use .cs or .csx." + Environment.NewLine
                        + "Use file mode for complex one-off Editor C# tasks: generated assets, structured analysis, diagnostics, Runtime/Public API calls, or multi-step UnityEditor API orchestration." + Environment.NewLine
                        + "Prefer prefab patch dry-run for existing Prefab structure changes, inspector for properties, and gameobject/transform for simple scene object edits." + Environment.NewLine
                        + "code execute is single-flight. After timeout query code status; never replay or switch backends automatically." + Environment.NewLine
                        + "Explicit --backend pipeline uses verified Pipeline eval; dry-run is default. Execution requires --dry-run false --confirm true. Query the original request after timeout; never replay or switch backends automatically." + Environment.NewLine
                        + "Examples:" + Environment.NewLine
                        + "  AIBridgeCLI code execute --file .aibridge/code/check.csx --timeout 5000" + Environment.NewLine
                        + "  AIBridgeCLI code execute --code \"Debug.Log(\\\"hello\\\"); return 123;\" --timeout 5000" + Environment.NewLine
                        + "  AIBridgeCLI code status" + Environment.NewLine
                        + "  AIBridgeCLI code cancel" + Environment.NewLine;
            }
            else if (string.Equals(action, "run", StringComparison.OrdinalIgnoreCase))
            {
                help += Environment.NewLine
                    + "Uses verified Pipeline script run with a locked source snapshot and explicit static entry. Only .cs files directly under .aibridge/code are accepted." + Environment.NewLine
                    + "Default compile-only validates source compilation, not entry resolution or argument coercion. Execute with --dry-run false --confirm true." + Environment.NewLine
                    + "Arguments must be one JSON array. No inline code, hotpatch, custom references/defines or arbitrary command forwarding. Timeout/unknown must not be replayed; query code status." + Environment.NewLine
                    + "Explicit --job true --confirm true detaches: upstream execution has NO timeout; --timeout limits local waiting only. Even compile-only jobs require confirm. Query original requestId via status/job_wait; job_list is local only. job_cancel --requestId --confirm true requests cooperative remote cancellation, not rollback or strong stop. Reload retains unknown." + Environment.NewLine
                    + "Example: AIBridgeCLI code run --file .aibridge/code/task.cs --entry Tools.Task.Run --args '[42]' --dry-run false --confirm true" + Environment.NewLine;
            }
            else if (string.Equals(action, "runtime_execute", StringComparison.OrdinalIgnoreCase))
            {
                help += Environment.NewLine
                        + "Compiles the source in Unity Editor with runtime-safe references, sends the DLL to an AIBridgeRuntime target, then invokes it via Assembly.Load and reflection." + Environment.NewLine
                        + "Auto-enables only when com.code-philosophy.hybridclr is installed; otherwise Unity rejects the command before dispatch." + Environment.NewLine
                        + "Sources: provide exactly one of --file or --code. File paths must resolve under .aibridge/code and use .cs or .csx." + Environment.NewLine
                        + "Examples:" + Environment.NewLine
                        + "  AIBridgeCLI code runtime_execute --file .aibridge/code/player_probe.csx --transport http --url http://127.0.0.1:27182 --timeout 10000" + Environment.NewLine
                        + "  AIBridgeCLI code runtime_execute --code \"return Application.platform.ToString();\" --target latest --timeout 10000" + Environment.NewLine;
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
            if (string.Equals(action, "job_list", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "job_wait", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(action, "job_cancel", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var key in @params.Keys)
                    if (key != "action" && key != "requestId" && key != "confirm")
                        throw new ArgumentException("job_cancel accepts only requestId and confirm.");
                if (!HasText(@params, "requestId") || !@params.TryGetValue("confirm", out var confirmed) || !(confirmed is bool flag) || !flag)
                    throw new ArgumentException("job_cancel requires --requestId and --confirm true.");
                return;
            }
            if (string.Equals(action, "status", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "cancel", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(action, "run", StringComparison.OrdinalIgnoreCase))
            {
                var allowed = new HashSet<string>(StringComparer.Ordinal)
                    { "action", "backend", "file", "entry", "args", "dry-run", "confirm", "timeout", "job" };
                foreach (var key in @params.Keys)
                    if (!allowed.Contains(key)) throw new ArgumentException("Unsupported code run parameter: " + key);
                if (!HasText(@params, "file") || !HasText(@params, "entry"))
                    throw new ArgumentException("code run requires --file and --entry.");
                if (@params.TryGetValue("backend", out var backend) && !string.Equals(backend as string, "pipeline", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("code run supports only pipeline.");
                return;
            }

            if (!string.Equals(action, "execute", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(action, "runtime_execute", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Unknown action: {action}");
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

    }
}
