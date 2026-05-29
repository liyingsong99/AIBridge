using System.Text;

namespace AIBridgeCLI.Workflow
{
    public static class WorkflowReportWriter
    {
        public static string WriteMarkdown(WorkflowRunManifest manifest)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Workflow Report: " + manifest.RunId);
            sb.AppendLine();
            sb.AppendLine("- Recipe: `" + manifest.RecipeName + "`");
            sb.AppendLine("- Status: `" + manifest.Status + "`");
            sb.AppendLine("- Started: `" + manifest.StartedAtUtc + "`");
            if (!string.IsNullOrWhiteSpace(manifest.EndedAtUtc))
            {
                sb.AppendLine("- Ended: `" + manifest.EndedAtUtc + "`");
            }

            sb.AppendLine("- Run directory: `" + WorkflowPathHelper.ToDisplayPath(System.IO.Path.Combine(WorkflowPathHelper.GetRunsDirectory(), manifest.RunId)) + "`");
            sb.AppendLine();

            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("| Metric | Value |");
            sb.AppendLine("|---|---:|");
            sb.AppendLine("| CLI commands | " + manifest.Summary.CliCommandCount + " |");
            sb.AppendLine("| Agent/manual steps | " + manifest.Summary.AgentStepCount + " |");
            sb.AppendLine("| Artifacts | " + manifest.Summary.ArtifactCount + " |");
            sb.AppendLine("| Failed gates | " + manifest.Summary.FailedGateCount + " |");
            sb.AppendLine();

            sb.AppendLine("## Phases");
            sb.AppendLine();
            sb.AppendLine("| Phase | Status | Steps | Artifacts | Error |");
            sb.AppendLine("|---|---|---:|---:|---|");
            foreach (var phase in manifest.PhaseStates)
            {
                sb.AppendLine("| `" + phase.PhaseId + "` | `" + phase.Status + "` | " + phase.StepIds.Count + " | " + phase.ArtifactIds.Count + " | " + EscapeTable(phase.Error) + " |");
            }

            sb.AppendLine();
            sb.AppendLine("## Steps");
            sb.AppendLine();
            sb.AppendLine("| Step | Kind | Status | Command | Error |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var step in manifest.StepStates)
            {
                sb.AppendLine("| `" + step.StepId + "` | `" + step.Kind + "` | `" + step.Status + "` | " + EscapeTable(step.Command) + " | " + EscapeTable(step.Error) + " |");
            }

            sb.AppendLine();
            sb.AppendLine("## Gates");
            sb.AppendLine();
            sb.AppendLine("| Gate | Kind | Required | Status | Message |");
            sb.AppendLine("|---|---|---:|---|---|");
            foreach (var gate in manifest.GateResults)
            {
                sb.AppendLine("| `" + gate.GateId + "` | `" + gate.Kind + "` | " + (gate.Required ? "yes" : "no") + " | `" + gate.Status + "` | " + EscapeTable(gate.Message) + " |");
            }

            sb.AppendLine();
            sb.AppendLine("## Artifacts");
            sb.AppendLine();
            sb.AppendLine("| Artifact | Kind | Path | Source Command | Summary |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var artifact in manifest.ArtifactRefs)
            {
                sb.AppendLine("| `" + artifact.ArtifactId + "` | `" + artifact.Kind + "` | `" + artifact.Path + "` | " + EscapeTable(artifact.SourceCommand) + " | " + EscapeTable(artifact.Summary) + " |");
            }

            sb.AppendLine();
            sb.AppendLine("## Reproduce");
            sb.AppendLine();
            sb.AppendLine("```bash");
            sb.AppendLine("AIBridgeCLI workflow status --run " + manifest.RunId);
            sb.AppendLine("AIBridgeCLI workflow report --run " + manifest.RunId + " --format markdown");
            sb.AppendLine("```");
            return sb.ToString();
        }

        private static string EscapeTable(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            return value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
