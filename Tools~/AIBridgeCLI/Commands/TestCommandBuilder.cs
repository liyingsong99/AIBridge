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
            "run", "status"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["run"] = new List<ParameterInfo>
            {
                new ParameterInfo("mode", "Test mode: EditMode or PlayMode. AIBridge currently starts runs only from Edit Mode", false, "EditMode"),
                new ParameterInfo("test-name", "Exact test full name, mapped to Unity Filter.testNames", false, null),
                new ParameterInfo("group-name", "Regex / fixture / namespace, mapped to Unity Filter.groupNames", false, null),
                new ParameterInfo("assembly-name", "Assembly name, mapped to Unity Filter.assemblyNames", false, null),
                new ParameterInfo("timeout", "Total wait timeout in milliseconds", false, "120000")
            },
            ["status"] = new List<ParameterInfo>
            {
                new ParameterInfo("run-id", "Specific Unity test run id returned by test run", false, null)
            }
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            var request = base.Build(action, options);

            RenameParam(request.@params, "test-name", "testName");
            RenameParam(request.@params, "group-name", "groupName");
            RenameParam(request.@params, "assembly-name", "assemblyName");
            RenameParam(request.@params, "run-id", "runId");

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
