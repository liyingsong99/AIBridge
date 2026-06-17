using System.Collections.Generic;

namespace AIBridgeCLI.Commands
{
    public class TextIndexCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "text_index";
        public override string Description => "CLI-only local indexed text search";

        public override string[] Actions => new[]
        {
            "status",
            "build",
            "search",
            "reset"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["status"] = CommonParameters(),
            ["build"] = CommonParameters(),
            ["reset"] = CommonParameters(),
            ["search"] = WithSearchParameters()
        };

        private static List<ParameterInfo> CommonParameters()
        {
            return new List<ParameterInfo>
            {
                new ParameterInfo("project-root", "Unity project root. Defaults to current Unity project", false)
            };
        }

        private static List<ParameterInfo> WithSearchParameters()
        {
            var parameters = CommonParameters();
            parameters.Add(new ParameterInfo("regex", "Treat query as a regular expression", false, "false"));
            parameters.Add(new ParameterInfo("glob", "Optional path glob filter, comma or semicolon separated", false));
            parameters.Add(new ParameterInfo("path", "Optional indexed path prefix filter", false));
            parameters.Add(new ParameterInfo("max-results", "Maximum result count", false, "100"));
            return parameters;
        }

        public override string GetHelp(string action = null)
        {
            if (string.IsNullOrEmpty(action) || action != "search")
            {
                return base.GetHelp(action);
            }

            return @"text_index search

Parameters:
  --project-root         (optional) Unity project root. Defaults to current Unity project
  --regex                (optional) Treat query as a regular expression [default: false]
  --glob                 (optional) Optional path glob filter, comma or semicolon separated
  --path                 (optional) Optional indexed path prefix filter
  --max-results          (optional) Maximum result count [default: 100]

Usage: AIBridgeCLI text_index search ""literal text"" [options]
";
        }
    }
}
