using System.Collections.Generic;

namespace AIBridgeCLI.Commands
{
    public class CodeIndexCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "code_index";
        public override string Description => "Read-only Unity snapshot C# declaration name index";

        public override string[] Actions => new[]
        {
            "symbol",
            "definition",
            "status",
            "doctor"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["symbol"] = WithQuery(new List<ParameterInfo>
            {
                new ParameterInfo("query", "C# declaration name or partial name", true)
            }),
            ["definition"] = WithQuery(new List<ParameterInfo>
            {
                new ParameterInfo("query", "C# declaration name", true)
            }),
            ["status"] = CommonParameters(),
            ["doctor"] = CommonParameters()
        };

        private static List<ParameterInfo> CommonParameters()
        {
            return new List<ParameterInfo>
            {
                new ParameterInfo("project-root", "Unity project root. Defaults to current Unity project", false),
                new ParameterInfo("unity-pid", "Unity Editor process id to monitor. Daemon exits when the process is gone", false),
                new ParameterInfo("auto-refresh", "Reload the snapshot workspace automatically when snapshot files change", false, "true")
            };
        }

        private static List<ParameterInfo> WithQuery(List<ParameterInfo> parameters)
        {
            parameters.AddRange(CommonParameters());
            return parameters;
        }

        public override string GetHelp(string action = null)
        {
            if (string.IsNullOrWhiteSpace(action))
            {
                return base.GetHelp(null);
            }

            var normalized = action.Trim().ToLowerInvariant();
            if (normalized == "symbol" || normalized == "definition" || normalized == "status" || normalized == "doctor")
            {
                return base.GetHelp(normalized);
            }

            return base.GetHelp(null);
        }
    }
}
