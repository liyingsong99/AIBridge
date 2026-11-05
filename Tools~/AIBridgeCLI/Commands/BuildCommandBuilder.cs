using System;
using System.Collections.Generic;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    public sealed class BuildCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "build";
        public override string Description => "Native Player build with atomic state and bounded report";
        public override string[] Actions => new[] { "run", "status", "report", "release" };
        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["run"] = new List<ParameterInfo>
            {
                new ParameterInfo("target", "Exact current active BuildTarget; no switching", false),
                new ParameterInfo("scenes", "JSON array of saved Assets/*.unity scenes; defaults to enabled Build Settings scenes", false),
                new ParameterInfo("development", "BuildOptions.Development; no PlayerSettings changes", false, "false"),
                new ParameterInfo("dry-run", "Validate only; do not write build artifacts or queue", false, "true"),
                new ParameterInfo("confirm", "Required per real Player build with dry-run=false", false, "false")
            },
            ["status"] = new List<ParameterInfo> { new ParameterInfo("requestId", "Original AIBridge build request; default latest", false) },
            ["report"] = new List<ParameterInfo> { new ParameterInfo("requestId", "Original AIBridge build request; default latest", false) },
            ["release"] = new List<ParameterInfo>
            {
                new ParameterInfo("requestId", "Exact registered unknown build request", true),
                new ParameterInfo("confirm", "Acknowledge local release; does not cancel a build", true)
            }
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            var normalized = string.IsNullOrWhiteSpace(action) ? "status" : action.ToLowerInvariant();
            if (Array.IndexOf(Actions, normalized) < 0) throw new ArgumentException("Unknown build action.");
            if (options.ContainsKey("json")) throw new ArgumentException("Use explicit build parameters; --json is not supported.");
            var request = base.Build(normalized, options);
            request.@params["action"] = normalized;
            if (normalized == "run")
            {
                if (!request.@params.ContainsKey("dry-run")) request.@params["dry-run"] = true;
                if (!request.@params.ContainsKey("confirm")) request.@params["confirm"] = false;
                if (!request.@params.ContainsKey("development")) request.@params["development"] = false;
            }
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "action" };
            foreach (var parameter in ActionParameters[normalized]) allowed.Add(parameter.Name);
            foreach (var key in request.@params.Keys)
                if (!allowed.Contains(key)) throw new ArgumentException("Unsupported build parameter: " + key);
            foreach (var key in new[] { "dry-run", "confirm", "development" })
                if (request.@params.TryGetValue(key, out var value) && !(value is bool)) throw new ArgumentException(key + " must be boolean.");
            if (normalized == "run" && request.@params["dry-run"] is bool dryRun && !dryRun && !Equals(request.@params["confirm"], true))
                throw new ArgumentException("Player build requires --dry-run false --confirm true.");
            if (normalized == "release" && !Equals(request.@params["confirm"], true))
                throw new ArgumentException("Local release requires --confirm true; this does not cancel execution.");
            return request;
        }

        public override string GetHelp(string action = null)
        {
            return base.GetHelp(action) + Environment.NewLine
                + "run defaults to validation. Confirmed builds queue then block the Editor main thread; CLI status/report read journals directly. "
                + "Output is a fresh .aibridge/builds/<buildId>/ directory; no overwrite, auto-run, arbitrary options or target switching. "
                + "Timeout/reload never replays; release frees only registered unknown local state, not cancellation." + Environment.NewLine;
        }
    }
}
