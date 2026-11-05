using System;
using System.Collections.Generic;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Compile command builder: unity, dotnet, start, status
    /// Supports Unity internal compilation and external dotnet build.
    /// - 'unity': Recommended. Triggers Unity compilation and waits for result.
    /// - 'dotnet': Fallback. CLI-only, does not require Unity Editor.
    /// - 'start/status': Low-level commands for manual compilation control.
    /// </summary>
    public class CompileCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "compile";
        public override string Description => "Compilation operations (unity internal compile, dotnet build)";

        public override string[] Actions => new[]
        {
            "unity", "dotnet", "start", "status", "release"
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            if (!options.TryGetValue("backend", out var backend) || backend == "native") return base.Build(action, options);
            if (backend != "pipeline" || (action != "start" && action != "status" && action != "release"))
                throw new ArgumentException("Pipeline compile supports start/status/release; no native fallback.");
            var localStatus = action == "status" && options.ContainsKey("requestId");
            foreach (var key in options.Keys)
                if (key != "backend" && key != "pretty" && key != "raw" && key != "quiet"
                    && !(key == "timeout" && !localStatus && action != "release")
                    && !(key == "confirm" && action != "status")
                    && !(key == "requestId" && action != "start"))
                    throw new ArgumentException("Unsupported Pipeline compilation option: " + key);
            var request = new CommandRequest { type = Type, @params = new Dictionary<string, object>
                { { "action", action }, { "backend", "pipeline" } } };
            if (action == "start" || action == "release")
            {
                if (!options.TryGetValue("confirm", out var confirm) || confirm != "true")
                    throw new ArgumentException("Pipeline compile start/release requires literal --confirm true.");
                request.@params["confirm"] = true;
            }
            if (localStatus || action == "release")
            {
                if (!options.TryGetValue("requestId", out var id) || !SafeRequestId(id)) throw new ArgumentException("Exact requestId is required.");
                request.@params["requestId"] = id;
            }
            else
            {
                var timeout = options.TryGetValue("timeout", out var text) ? text : "10000";
                if (!long.TryParse(timeout, out var number) || number < 1000 || number > 60000)
                    throw new ArgumentException("Pipeline timeout must be 1000..60000 ms.");
                request.@params["timeout"] = number;
            }
            return request;
        }

        private static bool SafeRequestId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128) return false;
            foreach (var c in id) if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
            return true;
        }

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["unity"] = new List<ParameterInfo>
            {
                new ParameterInfo("timeout", "Total compilation timeout in milliseconds", false, "120000"),
                new ParameterInfo("poll-interval", "Status polling interval in milliseconds", false, "500"),
                new ParameterInfo("transport-timeout", "Per-request communication timeout in milliseconds (default: min(30000, timeout))", false, "auto")
            },
            ["dotnet"] = new List<ParameterInfo>
            {
                new ParameterInfo("solution", "Solution file path (relative to project root). If omitted, auto-detect from project root.", false, null),
                new ParameterInfo("configuration", "Build configuration (Debug/Release)", false, "Debug"),
                new ParameterInfo("verbosity", "MSBuild verbosity (quiet/minimal/normal/detailed)", false, "minimal"),
                new ParameterInfo("timeout", "Build timeout in milliseconds", false, "300000"),
                new ParameterInfo("no-filter", "Disable smart error filtering (show all errors)", false, "false"),
                new ParameterInfo("exclude", "Custom exclude paths (comma-separated)", false, null),
                new ParameterInfo("show-warnings", "Show warnings in output (default: hidden)", false, "false")
            },
            ["start"] = new List<ParameterInfo>
            {
                new ParameterInfo("backend", "native or pipeline; Pipeline only acknowledges one trigger, never compilation success", false, "native"),
                new ParameterInfo("confirm", "Pipeline requires literal true; acknowledged requests keep the local gate until explicit release", false, null),
                new ParameterInfo("timeout", "Pipeline acknowledgement wait: 1000..60000 ms", false, "10000")
            },
            ["release"] = new List<ParameterInfo>
            {
                new ParameterInfo("backend", "Must be pipeline; local gate release only", true, null),
                new ParameterInfo("requestId", "Exact original compile request id", true, null),
                new ParameterInfo("confirm", "Must be literal true; does not cancel or confirm completion", true, null)
            },
            ["status"] = new List<ParameterInfo>
            {
                new ParameterInfo("includeDetails", "Native error/warning details (not supported by Pipeline)", false, "true"),
                new ParameterInfo("backend", "native or pipeline; without requestId reads uncorrelated global observation", false, "native"),
                new ParameterInfo("requestId", "Pipeline only: read exact local trigger record instead of global observation", false, null)
            }
        };
    }
}
