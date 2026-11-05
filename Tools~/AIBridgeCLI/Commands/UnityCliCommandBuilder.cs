using System.Collections.Generic;

namespace AIBridgeCLI.Commands
{
    public sealed class UnityCliCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "unity_cli";
        public override string Description => "Optional Unity CLI version/capability diagnostics; existing commands keep native backend";
        public override string[] Actions => new[] { "doctor", "version", "capabilities" };
        protected override Dictionary<string, List<ParameterInfo>> ActionParameters
        {
            get
            {
                var result = new Dictionary<string, List<ParameterInfo>>();
                foreach (var action in Actions) result[action] = DiagnosticParameters();
                return result;
            }
        }
        internal static List<ParameterInfo> DiagnosticParameters()
        {
            return new List<ParameterInfo>
            {
                new ParameterInfo("backend", "Selection diagnostic: native, auto, unity-cli (does not route existing commands)", false, "native"),
                new ParameterInfo("unity-cli-path", "Standalone Unity CLI executable path; never Unity Editor", false),
                new ParameterInfo("config", "Optional config path (schemaVersion/backend/executablePath/probeTimeoutMs)", false, ".aibridge/unity-cli.json"),
                new ParameterInfo("timeout", "Timeout per read-only probe in ms (1..60000)", false, "5000"),
                new ParameterInfo("format", "Diagnostic output: json, text", false, "json")
            };
        }
    }

    public sealed class PipelineCapabilityCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "pipeline";
        public override string Description => "Diagnose optional Unity CLI pipeline help capability (CLI-only)";
        public override string[] Actions => new[] { "capabilities" };
        protected override Dictionary<string, List<ParameterInfo>> ActionParameters =>
            new Dictionary<string, List<ParameterInfo>> { ["capabilities"] = UnityCliCommandBuilder.DiagnosticParameters() };
    }
}
