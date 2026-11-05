using System;
using System.Collections.Generic;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    public sealed class ProfilerCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "profiler";
        public override string Description => "Editor Profiler diagnostics and AIBridge JSON snapshots";

        public override string[] Actions => new[]
        {
            "start",
            "stop",
            "get_status",
            "list_modules",
            "enable_module",
            "clear_data",
            "capture_frame",
            "get_memory_stats",
            "get_rendering_stats",
            "get_script_stats",
            "save_data",
            "load_data",
            "native_capabilities",
            "save_profile",
            "load_profile"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["start"] = new List<ParameterInfo>(),
            ["stop"] = new List<ParameterInfo>(),
            ["get_status"] = new List<ParameterInfo>(),
            ["list_modules"] = new List<ParameterInfo>(),
            ["enable_module"] = new List<ParameterInfo>
            {
                new ParameterInfo("module", "Profiler module name, e.g. Memory or Rendering", true),
                new ParameterInfo("enabled", "Local AIBridge module flag", false, "true")
            },
            ["clear_data"] = new List<ParameterInfo>(),
            ["capture_frame"] = new List<ParameterInfo>(),
            ["get_memory_stats"] = new List<ParameterInfo>(),
            ["get_rendering_stats"] = new List<ParameterInfo>(),
            ["get_script_stats"] = new List<ParameterInfo>(),
            ["save_data"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Output path for AIBridge profiler JSON snapshot", false, ".aibridge/profiler/<timestamp>.json")
            },
            ["native_capabilities"] = new List<ParameterInfo>(),
            ["save_profile"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "New .aibridge/profiler/native/*.data file; no overwrite", false),
                new ParameterInfo("confirm", "Required per native save; saves existing stopped history only", true)
            },
            ["load_profile"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Existing .aibridge/profiler/native/*.data file; replaces Editor history", true),
                new ParameterInfo("confirm", "Required per native load; may change shared Profiler history", true)
            },
            ["load_data"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Input path for AIBridge profiler JSON snapshot", true)
            }
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            var normalizedAction = string.IsNullOrWhiteSpace(action) ? "get_status" : action.Trim();
            var known = false;
            for (var i = 0; i < Actions.Length; i++)
            {
                if (string.Equals(Actions[i], normalizedAction, StringComparison.OrdinalIgnoreCase))
                {
                    normalizedAction = Actions[i];
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                throw new ArgumentException("Unknown profiler action: " + normalizedAction);
            }

            var request = base.Build(normalizedAction, options);
            request.@params["action"] = normalizedAction;
            if (normalizedAction == "native_capabilities" || normalizedAction == "save_profile" || normalizedAction == "load_profile")
            {
                if (options.ContainsKey("json")) throw new ArgumentException("Native profiler actions require explicit parameters.");
                foreach (var key in request.@params.Keys)
                    if (key != "action" && (normalizedAction == "native_capabilities" || (key != "path" && key != "confirm")))
                        throw new ArgumentException("Unsupported native profiler parameter: " + key);
                if (normalizedAction != "native_capabilities"
                    && (!request.@params.TryGetValue("confirm", out var confirmed) || !(confirmed is bool flag) || !flag))
                    throw new ArgumentException("Native Profiler save/load requires --confirm true.");
            }
            return request;
        }

        public override string GetHelp(string action = null)
        {
            if (string.IsNullOrWhiteSpace(action) || IsKnownAction(action))
            {
                return base.GetHelp(action);
            }

            return base.GetHelp(null);
        }

        private bool IsKnownAction(string action)
        {
            for (var i = 0; i < Actions.Length; i++)
            {
                if (string.Equals(Actions[i], action, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
