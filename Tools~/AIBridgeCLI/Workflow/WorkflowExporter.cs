using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public sealed class WorkflowExportResult
    {
        [JsonProperty("target")]
        public string Target { get; set; }

        [JsonProperty("recipeName")]
        public string RecipeName { get; set; }

        [JsonProperty("outputDirectory")]
        public string OutputDirectory { get; set; }

        [JsonProperty("files")]
        public List<string> Files { get; set; } = new List<string>();
    }

    public static class WorkflowExporter
    {
        public static WorkflowExportResult Export(string recipePath, string target, string outputDirectory)
        {
            var doc = WorkflowRecipeLoader.Load(recipePath);
            var validation = WorkflowValidator.ValidateDocument(doc);
            if (!validation.Success)
            {
                throw new InvalidOperationException("Workflow recipe is invalid: " + string.Join("; ", validation.Errors));
            }

            target = string.IsNullOrWhiteSpace(target) ? "codex-task-pack" : target.Trim();
            outputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(WorkflowPathHelper.GetWorkflowRootDirectory(), "exports")
                : WorkflowPathHelper.ResolvePath(outputDirectory);

            var recipeOutputDirectory = Path.Combine(outputDirectory, doc.Recipe.Name, target);
            Directory.CreateDirectory(recipeOutputDirectory);

            var result = new WorkflowExportResult
            {
                Target = target,
                RecipeName = doc.Recipe.Name,
                OutputDirectory = WorkflowPathHelper.ToDisplayPath(recipeOutputDirectory)
            };

            switch (target.ToLowerInvariant())
            {
                case "codex-task-pack":
                    WriteCodexTaskPack(doc, recipeOutputDirectory, result.Files);
                    break;
                case "generic-cli":
                    WriteGenericCliPlan(doc, recipeOutputDirectory, result.Files);
                    break;
                case "claude-workflow":
                    WriteClaudeWorkflow(doc, recipeOutputDirectory, result.Files);
                    break;
                default:
                    throw new ArgumentException("Unsupported workflow export target: " + target);
            }

            return result;
        }

        private static void WriteCodexTaskPack(WorkflowRecipeDocument doc, string outputDirectory, List<string> files)
        {
            var recipe = doc.Recipe;
            var markdownPath = Path.Combine(outputDirectory, recipe.Name + "-codex-task-pack.md");
            var schemaPath = Path.Combine(outputDirectory, recipe.Name + "-output-schema.json");

            var sb = new StringBuilder();
            sb.AppendLine("# Codex Task Pack: " + recipe.Title);
            sb.AppendLine();
            sb.AppendLine("Recipe: `" + recipe.Name + "`");
            sb.AppendLine();
            sb.AppendLine("## Boundary");
            sb.AppendLine();
            sb.AppendLine("- Use AIBridge CLI commands as evidence producers.");
            sb.AppendLine("- Treat `agent` and `manual` steps as external work; write structured output files and import them with `workflow import`.");
            sb.AppendLine("- Reference large logs, screenshots, and reports by artifact id or path; do not paste large payloads into the task context.");
            sb.AppendLine();
            sb.AppendLine("## Inputs");
            sb.AppendLine();
            foreach (var property in recipe.Inputs.Properties())
            {
                sb.AppendLine("- `" + property.Name + "`: " + property.Value);
            }

            sb.AppendLine();
            sb.AppendLine("## CLI Steps");
            sb.AppendLine();
            foreach (var step in recipe.Phases.SelectMany(phase => phase.Steps ?? new List<WorkflowStep>()).Where(step => string.Equals(step.Kind, "cli", StringComparison.OrdinalIgnoreCase)))
            {
                sb.AppendLine("- `" + step.Id + "`: `" + step.Command + "`");
            }

            sb.AppendLine();
            sb.AppendLine("## External Steps");
            sb.AppendLine();
            foreach (var step in recipe.Phases.SelectMany(phase => phase.Steps ?? new List<WorkflowStep>()).Where(step => string.Equals(step.Kind, "agent", StringComparison.OrdinalIgnoreCase) || string.Equals(step.Kind, "manual", StringComparison.OrdinalIgnoreCase)))
            {
                sb.AppendLine("- `" + step.Id + "` (`" + step.Kind + "` / `" + step.Role + "`): " + step.Description);
                sb.AppendLine("  - Expected outputs: " + string.Join(", ", step.Outputs.ToArray()));
                foreach (var importExample in BuildImportExamples(step))
                {
                    sb.AppendLine("  - Import example: `AIBridgeCLI workflow import --run <runId> --step " + step.Id + " --schema " + importExample.Schema + " --kind " + importExample.Kind + " --file <" + importExample.FileName + ">`");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## Gates");
            sb.AppendLine();
            foreach (var gate in recipe.Gates)
            {
                sb.AppendLine("- `" + gate.Id + "`: `" + gate.Kind + "`" + (gate.Required ? " required" : " optional"));
            }

            File.WriteAllText(markdownPath, sb.ToString(), new UTF8Encoding(false));

            var schema = new JObject
            {
                ["schemas"] = new JObject
                {
                    ["Verdict"] = new JObject
                    {
                        ["required"] = new JArray("claimId", "status", "reason"),
                        ["status"] = new JArray("confirmed", "refuted", "uncertain")
                    },
                    ["Finding"] = new JObject
                    {
                        ["required"] = new JArray("id", "severity", "claim", "evidence")
                    },
                    ["PatchProposal"] = new JObject
                    {
                        ["required"] = new JArray("id", "files", "summary", "validation")
                    },
                    ["ValidationResult"] = new JObject
                    {
                        ["required"] = new JArray("gate", "status", "evidence")
                    }
                }
            };
            File.WriteAllText(schemaPath, schema.ToString(Formatting.Indented), new UTF8Encoding(false));
            files.Add(WorkflowPathHelper.ToDisplayPath(markdownPath));
            files.Add(WorkflowPathHelper.ToDisplayPath(schemaPath));
        }

        private static List<ImportExample> BuildImportExamples(WorkflowStep step)
        {
            var result = new List<ImportExample>();
            var seenSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (step == null || step.Outputs == null)
            {
                return result;
            }

            foreach (var output in step.Outputs)
            {
                var example = CreateImportExample(output);
                if (example == null || !seenSchemas.Add(example.Schema))
                {
                    continue;
                }

                result.Add(example);
            }

            return result;
        }

        private static ImportExample CreateImportExample(string output)
        {
            if (string.Equals(output, "Verdict", StringComparison.OrdinalIgnoreCase))
            {
                return new ImportExample("Verdict", "verdict", "verdicts.json");
            }

            if (string.Equals(output, "Finding", StringComparison.OrdinalIgnoreCase))
            {
                return new ImportExample("Finding", "finding", "findings.json");
            }

            if (string.Equals(output, "PatchProposal", StringComparison.OrdinalIgnoreCase))
            {
                return new ImportExample("PatchProposal", "patch-proposal", "patch-proposals.json");
            }

            if (string.Equals(output, "ValidationResult", StringComparison.OrdinalIgnoreCase))
            {
                return new ImportExample("ValidationResult", "validation-report", "validation-results.json");
            }

            return null;
        }

        private static void WriteGenericCliPlan(WorkflowRecipeDocument doc, string outputDirectory, List<string> files)
        {
            var path = Path.Combine(outputDirectory, doc.Recipe.Name + "-generic-cli.md");
            var sb = new StringBuilder();
            sb.AppendLine("# Generic CLI Plan: " + doc.Recipe.Name);
            sb.AppendLine();
            foreach (var phase in doc.Recipe.Phases)
            {
                sb.AppendLine("## " + phase.Id);
                foreach (var step in phase.Steps ?? new List<WorkflowStep>())
                {
                    if (string.Equals(step.Kind, "cli", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine("```bash");
                        sb.AppendLine("AIBridgeCLI " + step.Command);
                        sb.AppendLine("```");
                    }
                    else
                    {
                        sb.AppendLine("- `" + step.Id + "` requires external `" + step.Kind + "` execution.");
                    }
                }
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            files.Add(WorkflowPathHelper.ToDisplayPath(path));
        }

        private static void WriteClaudeWorkflow(WorkflowRecipeDocument doc, string outputDirectory, List<string> files)
        {
            var path = Path.Combine(outputDirectory, doc.Recipe.Name + "-claude-workflow.js");
            var sb = new StringBuilder();
            sb.AppendLine("// Generated AIBridge workflow adapter. It does not execute AIBridge internal agents.");
            sb.AppendLine("module.exports = {");
            sb.AppendLine("  name: " + JsonConvert.ToString(doc.Recipe.Name) + ",");
            sb.AppendLine("  phases: [");
            foreach (var phase in doc.Recipe.Phases)
            {
                sb.AppendLine("    { id: " + JsonConvert.ToString(phase.Id) + ", type: " + JsonConvert.ToString(phase.Type) + ", steps: [");
                foreach (var step in phase.Steps ?? new List<WorkflowStep>())
                {
                    sb.AppendLine("      { id: " + JsonConvert.ToString(step.Id) + ", kind: " + JsonConvert.ToString(step.Kind) + ", command: " + JsonConvert.ToString(step.Command) + " },");
                }
                sb.AppendLine("    ] },");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("};");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            files.Add(WorkflowPathHelper.ToDisplayPath(path));
        }

        private sealed class ImportExample
        {
            public ImportExample(string schema, string kind, string fileName)
            {
                Schema = schema;
                Kind = kind;
                FileName = fileName;
            }

            public string Schema { get; private set; }
            public string Kind { get; private set; }
            public string FileName { get; private set; }
        }
    }
}
