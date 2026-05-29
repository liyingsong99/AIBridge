using System.Collections.Generic;
using System.Text;

namespace AIBridgeCLI.Commands
{
    public class WorkflowCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "workflow";
        public override string Description => "Workflow recipes, run artifacts, gates, and reports (CLI-only)";

        public override string[] Actions => new[]
        {
            "list",
            "validate",
            "plan",
            "init",
            "run-cli",
            "status",
            "report",
            "clean"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["list"] = new List<ParameterInfo>(),
            ["validate"] = new List<ParameterInfo>
            {
                new ParameterInfo("file", "Workflow recipe path or name", false),
                new ParameterInfo("recipe", "Built-in or project recipe name", false)
            },
            ["plan"] = new List<ParameterInfo>
            {
                new ParameterInfo("file", "Workflow recipe path or name", false),
                new ParameterInfo("recipe", "Built-in or project recipe name", false),
                new ParameterInfo("format", "Output format: json, markdown", false, "json")
            },
            ["init"] = new List<ParameterInfo>
            {
                new ParameterInfo("recipe", "Built-in recipe name", true),
                new ParameterInfo("output", "Output directory for copied recipe", false, ".aibridge/workflows/recipes")
            },
            ["run-cli"] = new List<ParameterInfo>
            {
                new ParameterInfo("file", "Workflow recipe path or name", false),
                new ParameterInfo("recipe", "Built-in or project recipe name", false),
                new ParameterInfo("inputs", "JSON object or path to inputs JSON", false),
                new ParameterInfo("resume", "Existing run id to resume", false),
                new ParameterInfo("rerun", "Rerun mode, e.g. failed", false),
                new ParameterInfo("timeout", "Per-step CLI command timeout in milliseconds", false, "5000")
            },
            ["status"] = new List<ParameterInfo>
            {
                new ParameterInfo("run", "Workflow run id", true)
            },
            ["report"] = new List<ParameterInfo>
            {
                new ParameterInfo("run", "Workflow run id", true),
                new ParameterInfo("format", "Output format: json, markdown", false, "json")
            },
            ["clean"] = new List<ParameterInfo>
            {
                new ParameterInfo("older-than", "Run age threshold, e.g. 30d, 12h", false, "30d"),
                new ParameterInfo("dry-run", "Only list candidates without deleting", false, "true"),
                new ParameterInfo("keep-failed", "Keep failed or blocked runs even when they are old", false, "true"),
                new ParameterInfo("keep-latest", "Keep the newest N runs regardless of age", false, "20"),
                new ParameterInfo("max-delete", "Maximum number of runs to delete in one clean call", false, "100"),
                new ParameterInfo("save-settings", "Persist clean options to .aibridge/workflows/settings.json", false, "false"),
                new ParameterInfo("auto-clean", "Enable automatic clean before workflow run-cli when settings are saved", false, "false")
            }
        };

        public override string GetHelp(string action = null)
        {
            var baseHelp = base.GetHelp(action);
            if (!string.IsNullOrEmpty(action))
            {
                return baseHelp;
            }

            var sb = new StringBuilder(baseHelp);
            sb.AppendLine();
            sb.AppendLine("Examples:");
            sb.AppendLine("  AIBridgeCLI workflow list");
            sb.AppendLine("  AIBridgeCLI workflow validate --recipe runtime-target-sweep");
            sb.AppendLine("  AIBridgeCLI workflow plan --recipe runtime-ui-validation --format markdown");
            sb.AppendLine("  AIBridgeCLI workflow init --recipe runtime-ui-validation");
            sb.AppendLine("  AIBridgeCLI workflow run-cli --file .aibridge/workflows/recipes/runtime-target-sweep.aibridge-workflow.json");
            sb.AppendLine("  AIBridgeCLI workflow report --run wf_20260529_213000_ab12cd34 --format markdown");
            sb.AppendLine("  AIBridgeCLI workflow clean --older-than 3d --dry-run false --keep-failed true --keep-latest 20");
            sb.AppendLine("  AIBridgeCLI workflow clean --older-than 3d --save-settings true --auto-clean true");
            return sb.ToString();
        }
    }
}
