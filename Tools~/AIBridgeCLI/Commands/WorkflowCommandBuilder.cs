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
            "begin",
            "attach",
            "finish",
            "run-cli",
            "import",
            "export",
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
            ["begin"] = new List<ParameterInfo>
            {
                new ParameterInfo("file", "Workflow recipe path or name", false),
                new ParameterInfo("recipe", "Built-in or project recipe name", false),
                new ParameterInfo("inputs", "Inputs JSON file path. Inline JSON is supported but fragile in PowerShell", false)
            },
            ["attach"] = new List<ParameterInfo>
            {
                new ParameterInfo("run", "Workflow run id to set as active", true)
            },
            ["finish"] = new List<ParameterInfo>
            {
                new ParameterInfo("run", "Workflow run id. Defaults to active run or AIBRIDGE_WORKFLOW_RUN_ID", false),
                new ParameterInfo("status", "Final status: passed, success, partial, failed, blocked, canceled, stale, needs-human, external-handoff", false, "passed"),
                new ParameterInfo("allow-partial", "Treat partial workflow status as CLI success", false, "false"),
                new ParameterInfo("detail", "Output detail: compact, full", false, "compact")
            },
            ["run-cli"] = new List<ParameterInfo>
            {
                new ParameterInfo("file", "Workflow recipe path or name. Required unless --recipe is provided", false),
                new ParameterInfo("recipe", "Built-in or project recipe name. Required unless --file is provided", false),
                new ParameterInfo("inputs", "Inputs JSON file path. Inline JSON is supported but fragile in PowerShell", false),
                new ParameterInfo("resume", "Existing run id to resume. Still requires --file or --recipe", false),
                new ParameterInfo("rerun", "Rerun mode, e.g. failed", false),
                new ParameterInfo("timeout", "Per-step CLI command timeout in milliseconds", false, "5000"),
                new ParameterInfo("allow-partial", "Treat partial workflow status as CLI success", false, "false"),
                new ParameterInfo("detail", "Output detail: compact, full", false, "compact")
            },
            ["import"] = new List<ParameterInfo>
            {
                new ParameterInfo("run", "Workflow run id. Defaults to active run or AIBRIDGE_WORKFLOW_RUN_ID", false),
                new ParameterInfo("step", "Source workflow step id", false),
                new ParameterInfo("schema", "Imported schema: Verdict, Finding, PatchProposal, ValidationResult, EvidenceRef, CommandEvidence, SkillHandoff", false, "Verdict"),
                new ParameterInfo("kind", "Artifact kind override, e.g. verdict, finding, patch-proposal", false),
                new ParameterInfo("file", "JSON file to import", true)
            },
            ["export"] = new List<ParameterInfo>
            {
                new ParameterInfo("file", "Workflow recipe path or name", false),
                new ParameterInfo("recipe", "Built-in or project recipe name", false),
                new ParameterInfo("target", "Export target: claude-workflow, codex-task-pack, generic-cli", true),
                new ParameterInfo("output", "Output directory", false, ".aibridge/workflows/exports"),
                new ParameterInfo("workflow-run", "Optional workflow run id to attach export artifacts", false)
            },
            ["status"] = new List<ParameterInfo>
            {
                new ParameterInfo("run", "Workflow run id. Required; status does not default to active run", true),
                new ParameterInfo("detail", "Output detail: compact, full", false, "compact")
            },
            ["report"] = new List<ParameterInfo>
            {
                new ParameterInfo("run", "Workflow run id. Required; report does not default to active run", true),
                new ParameterInfo("format", "Output format: json, markdown", false, "json"),
                new ParameterInfo("detail", "Output detail for json: compact, full", false, "compact")
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
            sb.AppendLine("  AIBridgeCLI workflow plan --recipe runtime-debug-investigation --format markdown");
            sb.AppendLine("  AIBridgeCLI workflow plan --recipe runtime-ui-validation --format markdown");
            sb.AppendLine("  AIBridgeCLI workflow init --recipe runtime-ui-validation");
            sb.AppendLine("  AIBridgeCLI workflow begin --recipe unity-change-implementation");
            sb.AppendLine("  AIBridgeCLI workflow import --run wf_20260529_213000_ab12cd34 --step adversarial-verify --schema Verdict --file verdicts.json");
            sb.AppendLine("  AIBridgeCLI workflow export --recipe runtime-ui-validation --target codex-task-pack --output .aibridge/workflows/exports");
            sb.AppendLine("  AIBridgeCLI workflow finish --run wf_20260529_213000_ab12cd34 --status passed");
            sb.AppendLine("  AIBridgeCLI workflow run-cli --file .aibridge/workflows/recipes/runtime-target-sweep.aibridge-workflow.json --inputs .aibridge/workflows/inputs.json");
            sb.AppendLine("  AIBridgeCLI workflow run-cli --recipe unity-sharded-review --allow-partial true");
            sb.AppendLine("  AIBridgeCLI workflow run-cli --recipe unity-sharded-review --resume wf_20260529_213000_ab12cd34 --rerun failed");
            sb.AppendLine("  AIBridgeCLI workflow status --run wf_20260529_213000_ab12cd34");
            sb.AppendLine("  AIBridgeCLI workflow report --run wf_20260529_213000_ab12cd34 --format markdown");
            sb.AppendLine("  AIBridgeCLI workflow clean --older-than 3d --dry-run false --keep-failed true --keep-latest 20");
            sb.AppendLine("  AIBridgeCLI workflow clean --older-than 3d --save-settings true --auto-clean true");
            sb.AppendLine();
            sb.AppendLine("Notes:");
            sb.AppendLine("  workflow status/report require --run; read .aibridge/workflows/active-run.json first if you need the active run id.");
            sb.AppendLine("  workflow run-cli --resume <runId> still requires --file or --recipe.");
            sb.AppendLine("  Prefer a JSON file path for --inputs; inline JSON is fragile in PowerShell.");
            return sb.ToString();
        }
    }
}
