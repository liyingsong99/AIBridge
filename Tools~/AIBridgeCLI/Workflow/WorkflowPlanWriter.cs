using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public static class WorkflowPlanWriter
    {
        public static string WriteMarkdown(WorkflowRecipe recipe, string recipePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Workflow Plan: " + recipe.Name);
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(recipe.Title))
            {
                sb.AppendLine("Title: " + recipe.Title);
            }

            sb.AppendLine("Recipe: `" + WorkflowPathHelper.ToDisplayPath(recipePath) + "`");
            sb.AppendLine("Description: " + recipe.Description);
            AppendScalarLine(sb, "Terminal state", recipe.TerminalState);
            AppendScalarLine(sb, "Terminal reason", recipe.TerminalReason);
            AppendTokenLine(sb, "Retry budget", recipe.RetryBudget);
            AppendTokenLine(sb, "Stop when", recipe.StopWhen);
            AppendTokenLine(sb, "Loop iteration", recipe.LoopIteration);
            AppendListLine(sb, "Required skills", recipe.RequiredSkills);
            sb.AppendLine();
            sb.AppendLine("## Phases");
            foreach (var phase in recipe.Phases)
            {
                sb.AppendLine();
                sb.AppendLine("### " + phase.Id + " (" + phase.Type + ")");
                if (!string.IsNullOrWhiteSpace(phase.Description))
                {
                    sb.AppendLine(phase.Description);
                }

                if (phase.DependsOn != null && phase.DependsOn.Count > 0)
                {
                    sb.AppendLine("Depends on: `" + string.Join("`, `", phase.DependsOn) + "`");
                }

                if (!string.IsNullOrWhiteSpace(phase.ItemSource))
                {
                    sb.AppendLine("Item source: `" + phase.ItemSource + "`");
                }

                AppendListLine(sb, "Required skills", phase.RequiredSkills);
                AppendListLine(sb, "Release skills after phase", phase.ReleaseSkillsAfter);

                if (phase.Steps == null || phase.Steps.Count == 0)
                {
                    sb.AppendLine("- No steps.");
                    continue;
                }

                foreach (var step in phase.Steps)
                {
                    sb.Append("- `");
                    sb.Append(step.Id);
                    sb.Append("` [");
                    sb.Append(step.Kind);
                    sb.Append("]");
                    if (!string.IsNullOrWhiteSpace(step.Description))
                    {
                        sb.Append(" ");
                        sb.Append(step.Description);
                    }

                    sb.AppendLine();
                    if (!string.IsNullOrWhiteSpace(step.Command))
                    {
                        sb.AppendLine("  Command: `" + step.Command + "`");
                    }

                    AppendListLine(sb, "  Required skills", step.RequiredSkills);
                    AppendListLine(sb, "  Release skills after step", step.ReleaseSkillsAfter);
                }
            }

            sb.AppendLine();
            sb.AppendLine("## Gates");
            if (recipe.Gates == null || recipe.Gates.Count == 0)
            {
                sb.AppendLine("- No gates.");
            }
            else
            {
                foreach (var gate in recipe.Gates)
                {
                    sb.Append("- `");
                    sb.Append(gate.Id);
                    sb.Append("` [");
                    sb.Append(gate.Kind);
                    sb.Append(gate.Required ? ", required" : ", optional");
                    sb.AppendLine("]");
                }
            }

            return sb.ToString();
        }

        public static string WriteJson(WorkflowRecipe recipe)
        {
            return JsonConvert.SerializeObject(new
            {
                name = recipe.Name,
                title = recipe.Title,
                description = recipe.Description,
                terminalState = recipe.TerminalState,
                terminalReason = recipe.TerminalReason,
                retryBudget = recipe.RetryBudget,
                stopWhen = recipe.StopWhen,
                loopIteration = recipe.LoopIteration,
                requiredSkills = recipe.RequiredSkills,
                phases = recipe.Phases,
                gates = recipe.Gates,
                artifacts = recipe.Artifacts
            }, Formatting.Indented);
        }

        private static void AppendListLine(StringBuilder sb, string label, System.Collections.Generic.List<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return;
            }

            sb.AppendLine(label + ": `" + string.Join("`, `", values) + "`");
        }

        private static void AppendScalarLine(StringBuilder sb, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            sb.AppendLine(label + ": " + value);
        }

        private static void AppendTokenLine(StringBuilder sb, string label, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return;
            }

            sb.AppendLine(label + ": " + token.ToString(Formatting.None));
        }
    }
}
