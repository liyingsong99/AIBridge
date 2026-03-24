using System.Collections.Generic;

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
            "unity", "dotnet", "start", "status"
        };

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
            ["start"] = new List<ParameterInfo>(),
            ["status"] = new List<ParameterInfo>
            {
                new ParameterInfo("includeDetails", "Include error/warning details in response", false, "true")
            }
        };
    }
}
