using System;
using System.Collections.Generic;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Test command builder: run tests through Unity TestRunnerApi or query the latest status.
    /// </summary>
    public class TestCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "test";
        public override string Description => "Run Unity tests or query native test status. test run must start while the Editor is in Edit Mode";

        public override string[] Actions => new[]
        {
            "run", "status", "list", "release"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["run"] = new List<ParameterInfo>
            {
                new ParameterInfo("mode", "Test mode: EditMode or PlayMode. AIBridge currently starts runs only from Edit Mode", false, "EditMode"),
                new ParameterInfo("test-name", "Exact test full name, mapped to Unity Filter.testNames", false, null),
                new ParameterInfo("group-name", "Regex / fixture / namespace, mapped to Unity Filter.groupNames", false, null),
                new ParameterInfo("assembly-name", "Assembly name, mapped to Unity Filter.assemblyNames", false, null),
                new ParameterInfo("timeout", "Total wait timeout in milliseconds", false, "120000"),
                new ParameterInfo("backend", "native (default) or explicit pipeline; Pipeline uses its own filters", false, "native"),
                new ParameterInfo("pipeline-filter", "Pipeline-only substring (testName/assembly) or exact category", false, null),
                new ParameterInfo("filter-type", "Pipeline-only testName, assembly or category", false, "testName")
            },
            ["list"] = new List<ParameterInfo>
            {
                new ParameterInfo("backend", "Explicit pipeline only; read-only EditMode enumeration", true, "pipeline"),
                new ParameterInfo("mode", "EditMode only", false, "EditMode"),
                new ParameterInfo("timeout", "Diagnostic request timeout in ms: 1000..60000", false, "10000")
            },
            ["status"] = new List<ParameterInfo>
            {
                new ParameterInfo("run-id", "Native Unity test run id", false, null),
                new ParameterInfo("backend", "native (default) or explicit pipeline local journal", false, "native"),
                new ParameterInfo("requestId", "Pipeline original request ID; no remote global-slot observation", false, null)
            },
            ["release"] = new List<ParameterInfo>
            {
                new ParameterInfo("backend", "pipeline only", true, "pipeline"),
                new ParameterInfo("requestId", "Exact unknown Pipeline request ID", true, null),
                new ParameterInfo("confirm", "true releases local state only; remote tests may still run", true, "false")
            }
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            var pipeline = options.TryGetValue("backend", out var backend) && backend == "pipeline";
            if (options.ContainsKey("backend") && backend != "native" && !pipeline) throw new ArgumentException("Unsupported test backend.");
            if (pipeline || action == "list" || action == "release")
            {
                if (!pipeline) throw new ArgumentException("This test action requires --backend pipeline.");
                return BuildPipeline(action, options);
            }
            var request = base.Build(action, options);

            RenameParam(request.@params, "test-name", "testName");
            RenameParam(request.@params, "group-name", "groupName");
            RenameParam(request.@params, "assembly-name", "assemblyName");
            RenameParam(request.@params, "run-id", "runId");

            return request;
        }

        private CommandRequest BuildPipeline(string action, Dictionary<string, string> options)
        {
            string[] allowed;
            switch (action)
            {
                case "run": allowed = new[] { "mode", "timeout", "pipeline-filter", "filter-type" }; break;
                case "list": allowed = new[] { "mode", "timeout" }; break;
                case "status": allowed = new[] { "requestId" }; break;
                case "release": allowed = new[] { "requestId", "confirm" }; break;
                default: throw new ArgumentException("Unsupported Pipeline test action; remote cancel is unavailable.");
            }
            foreach (var key in options.Keys)
                if (key != "backend" && key != "pretty" && key != "raw" && key != "quiet" && Array.IndexOf(allowed, key) < 0)
                    throw new ArgumentException("Unsupported Pipeline test option: " + key);
            var request = new CommandRequest { id = PathHelper.GenerateCommandId(), type = Type,
                @params = new Dictionary<string, object> { { "action", action }, { "backend", "pipeline" } } };
            foreach (var key in allowed)
                if (options.TryGetValue(key, out var text) && key != "confirm" && key != "timeout") request.@params[key] = text;
            if (action == "list" || action == "run")
            {
                if (options.TryGetValue("mode", out var mode) && mode != "EditMode") throw new ArgumentException("Pipeline tests require EditMode.");
                var text = options.TryGetValue("timeout", out var supplied) ? supplied : action == "run" ? "120000" : "10000";
                var max = action == "run" ? 600000 : 60000;
                if (!long.TryParse(text, out var timeout) || timeout < 1000 || timeout > max) throw new ArgumentException("Pipeline timeout is outside the supported range.");
                request.@params["timeout"] = timeout; request.@params["mode"] = "EditMode";
            }
            if (action == "run" && options.TryGetValue("filter-type", out var filterType)
                && filterType != "testName" && filterType != "assembly" && filterType != "category")
                throw new ArgumentException("filter-type must be testName, assembly or category.");
            if (action == "release")
            {
                if (!options.TryGetValue("confirm", out var confirm) || confirm != "true"
                    || !options.TryGetValue("requestId", out var id) || string.IsNullOrWhiteSpace(id))
                    throw new ArgumentException("Local release requires exact --requestId and --confirm true.");
                request.@params["confirm"] = true;
            }
            return request;
        }

        protected override void ValidateParameters(string action, Dictionary<string, object> @params)
        {
            base.ValidateParameters(action, @params);

            if (!string.Equals(action, "run", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!@params.TryGetValue("mode", out var modeValue) || modeValue == null)
            {
                return;
            }

            var mode = modeValue.ToString();
            if (!string.Equals(mode, "EditMode", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mode, "PlayMode", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Invalid --mode value. Supported: EditMode, PlayMode");
            }
        }

        private void RenameParam(Dictionary<string, object> @params, string sourceKey, string targetKey)
        {
            if (@params == null || !@params.ContainsKey(sourceKey))
            {
                return;
            }

            @params[targetKey] = @params[sourceKey];
            @params.Remove(sourceKey);
        }
    }
}
