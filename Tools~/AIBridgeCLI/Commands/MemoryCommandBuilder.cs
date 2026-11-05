using System;
using System.Collections.Generic;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    public sealed class MemoryCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "memory";
        public override string Description => "Native Editor memory snapshot with explicit confirmation and no replay";
        public override string[] Actions => new[] { "capture_snapshot", "snapshot_status", "release_snapshot", "load_snapshot" };
        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["capture_snapshot"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "New direct .aibridge/memory/*.snap path; no overwrite", false),
                new ParameterInfo("target", "Only editor supported; no target switching", false, "editor"),
                new ParameterInfo("timeout", "Local wait limit 1000-60000ms; does not cancel capture", false, "30000"),
                new ParameterInfo("confirm", "Required; capture may stall Editor and expose heap data", true)
            },
            ["snapshot_status"] = new List<ParameterInfo> { new ParameterInfo("requestId", "Original request; default latest", false) },
            ["release_snapshot"] = new List<ParameterInfo>
            {
                new ParameterInfo("requestId", "Exact registered unknown request; release is not native cancellation", true),
                new ParameterInfo("confirm", "Required for local release", true)
            },
            ["load_snapshot"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Snapshot path; optional analysis API currently unsupported", true),
                new ParameterInfo("confirm", "Required before any analysis action", true)
            }
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            var normalized = string.IsNullOrWhiteSpace(action) ? "snapshot_status" : action.ToLowerInvariant();
            if (Array.IndexOf(Actions, normalized) < 0) throw new ArgumentException("Unknown memory action.");
            if (options.ContainsKey("json")) throw new ArgumentException("Memory actions require explicit parameters.");
            var request = base.Build(normalized, options); request.@params["action"] = normalized;
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "action" };
            foreach (var parameter in ActionParameters[normalized]) allowed.Add(parameter.Name);
            foreach (var key in request.@params.Keys)
                if (!allowed.Contains(key)) throw new ArgumentException("Unsupported memory parameter: " + key);
            if (normalized != "snapshot_status"
                && (!request.@params.TryGetValue("confirm", out var confirm) || !(confirm is bool flag) || !flag))
                throw new ArgumentException("Memory capture/load/release requires --confirm true.");
            if (normalized == "capture_snapshot")
            {
                if (!request.@params.ContainsKey("target")) request.@params["target"] = "editor";
                var timeout = options.TryGetValue("timeout", out var timeoutText) ? ParseValue(timeoutText) : 30000L;
                if (!(timeout is long value) || value < 1000 || value > 60000) throw new ArgumentException("timeout must be 1000-60000ms.");
                request.@params["timeout"] = value;
                if (!Equals(request.@params["target"], "editor")) throw new ArgumentException("Only local editor target is supported.");
            }
            return request;
        }
    }
}
