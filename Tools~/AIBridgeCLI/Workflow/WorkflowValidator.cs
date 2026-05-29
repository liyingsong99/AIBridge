using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public static class WorkflowValidator
    {
        private static readonly Regex RecipeNameRegex = new Regex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

        private static readonly HashSet<string> TopLevelFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "schemaVersion", "name", "title", "description", "version", "inputs", "phases", "gates", "artifacts"
        };

        private static readonly HashSet<string> PhaseFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "type", "description", "dependsOn", "itemSource", "steps"
        };

        private static readonly HashSet<string> PhaseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "serial", "parallel", "pipeline", "barrier", "report"
        };

        private static readonly HashSet<string> StepFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "kind", "description", "role", "command", "outputs"
        };

        private static readonly HashSet<string> StepKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cli", "agent", "manual", "barrier", "report"
        };

        private static readonly HashSet<string> GateFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "kind", "required", "threshold", "artifactKind", "min", "allow", "evidenceRefs"
        };

        private static readonly HashSet<string> GateKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "unityCompile", "dotnetBuild", "consoleErrors", "testRun", "screenshotExists",
            "runtimeReachable", "runtimeErrors", "artifactRequired", "externalVerdict"
        };

        private static readonly HashSet<string> ArtifactFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "kind", "description", "required"
        };

        public static WorkflowValidationResult ValidateFile(string path)
        {
            var result = new WorkflowValidationResult
            {
                File = WorkflowPathHelper.ToDisplayPath(path)
            };

            WorkflowRecipeDocument doc;
            try
            {
                var resolvedPath = WorkflowPathHelper.ResolveRecipePath(path);
                result.File = WorkflowPathHelper.ToDisplayPath(resolvedPath);
                doc = WorkflowRecipeLoader.Load(resolvedPath);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Invalid JSON: " + ex.Message);
                return result;
            }

            result.RecipeName = doc.Recipe.Name;
            ValidateDocument(doc, result);
            return result;
        }

        public static WorkflowValidationResult ValidateDocument(WorkflowRecipeDocument doc)
        {
            var result = new WorkflowValidationResult
            {
                File = WorkflowPathHelper.ToDisplayPath(doc.Path),
                RecipeName = doc.Recipe == null ? null : doc.Recipe.Name
            };
            ValidateDocument(doc, result);
            return result;
        }

        private static void ValidateDocument(WorkflowRecipeDocument doc, WorkflowValidationResult result)
        {
            if (doc == null || doc.Recipe == null || doc.Json == null)
            {
                result.Errors.Add("Recipe document is empty.");
                return;
            }

            ValidateUnsupportedFields(doc.Json, TopLevelFields, "recipe", result.Errors);

            var recipe = doc.Recipe;
            if (recipe.SchemaVersion != 1)
            {
                result.Errors.Add("schemaVersion must be 1.");
            }

            if (string.IsNullOrWhiteSpace(recipe.Name))
            {
                result.Errors.Add("Missing required field: name.");
            }
            else if (!RecipeNameRegex.IsMatch(recipe.Name))
            {
                result.Errors.Add("name must use lower kebab-case.");
            }

            if (string.IsNullOrWhiteSpace(recipe.Description))
            {
                result.Errors.Add("Missing required field: description.");
            }

            if (recipe.Phases == null || recipe.Phases.Count == 0)
            {
                result.Errors.Add("Missing required field: phases.");
            }

            if (recipe.Gates == null)
            {
                result.Errors.Add("Missing required field: gates.");
            }

            ValidatePhases(doc.Json["phases"] as JArray, recipe, result);
            ValidateGates(doc.Json["gates"] as JArray, recipe, result);
            ValidateArtifacts(doc.Json["artifacts"] as JArray, result);
        }

        private static void ValidatePhases(JArray phaseJson, WorkflowRecipe recipe, WorkflowValidationResult result)
        {
            if (recipe.Phases == null)
            {
                return;
            }

            var seenPhases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenSteps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < recipe.Phases.Count; i++)
            {
                var phase = recipe.Phases[i];
                var location = "phases[" + i + "]";
                var phaseObject = phaseJson != null && i < phaseJson.Count ? phaseJson[i] as JObject : null;
                if (phaseObject != null)
                {
                    ValidateUnsupportedFields(phaseObject, PhaseFields, location, result.Errors);
                }

                if (phase == null)
                {
                    result.Errors.Add(location + " is null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(phase.Id))
                {
                    result.Errors.Add(location + " missing required field: id.");
                }
                else if (!seenPhases.Add(phase.Id))
                {
                    result.Errors.Add("Duplicate phase id: " + phase.Id + ".");
                }

                if (string.IsNullOrWhiteSpace(phase.Type))
                {
                    result.Errors.Add(location + " missing required field: type.");
                }
                else if (!PhaseTypes.Contains(phase.Type))
                {
                    result.Errors.Add(location + " has unsupported type: " + phase.Type + ".");
                }

                if (phase.DependsOn != null)
                {
                    foreach (var dependency in phase.DependsOn)
                    {
                        if (!seenPhases.Contains(dependency))
                        {
                            result.Errors.Add(location + " dependsOn must reference an earlier phase: " + dependency + ".");
                        }
                    }
                }

                ValidateSteps(phaseObject == null ? null : phaseObject["steps"] as JArray, phase, location, seenSteps, result);
            }
        }

        private static void ValidateSteps(
            JArray stepJson,
            WorkflowPhase phase,
            string phaseLocation,
            HashSet<string> seenSteps,
            WorkflowValidationResult result)
        {
            if (phase.Steps == null)
            {
                return;
            }

            for (var i = 0; i < phase.Steps.Count; i++)
            {
                var step = phase.Steps[i];
                var location = phaseLocation + ".steps[" + i + "]";
                var stepObject = stepJson != null && i < stepJson.Count ? stepJson[i] as JObject : null;
                if (stepObject != null)
                {
                    ValidateUnsupportedFields(stepObject, StepFields, location, result.Errors);
                }

                if (step == null)
                {
                    result.Errors.Add(location + " is null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(step.Id))
                {
                    result.Errors.Add(location + " missing required field: id.");
                }
                else if (!seenSteps.Add(step.Id))
                {
                    result.Errors.Add("Duplicate step id: " + step.Id + ".");
                }

                if (string.IsNullOrWhiteSpace(step.Kind))
                {
                    result.Errors.Add(location + " missing required field: kind.");
                }
                else if (!StepKinds.Contains(step.Kind))
                {
                    result.Errors.Add(location + " has unsupported kind: " + step.Kind + ".");
                }

                if (string.Equals(step.Kind, "cli", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(step.Command))
                {
                    result.Errors.Add(location + " kind=cli requires command.");
                }
            }
        }

        private static void ValidateGates(JArray gateJson, WorkflowRecipe recipe, WorkflowValidationResult result)
        {
            if (recipe.Gates == null)
            {
                return;
            }

            var seenGates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < recipe.Gates.Count; i++)
            {
                var gate = recipe.Gates[i];
                var location = "gates[" + i + "]";
                var gateObject = gateJson != null && i < gateJson.Count ? gateJson[i] as JObject : null;
                if (gateObject != null)
                {
                    ValidateUnsupportedFields(gateObject, GateFields, location, result.Errors);
                }

                if (gate == null)
                {
                    result.Errors.Add(location + " is null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(gate.Id))
                {
                    result.Errors.Add(location + " missing required field: id.");
                }
                else if (!seenGates.Add(gate.Id))
                {
                    result.Errors.Add("Duplicate gate id: " + gate.Id + ".");
                }

                if (string.IsNullOrWhiteSpace(gate.Kind))
                {
                    result.Errors.Add(location + " missing required field: kind.");
                }
                else if (!GateKinds.Contains(gate.Kind))
                {
                    result.Errors.Add(location + " has unsupported kind: " + gate.Kind + ".");
                }
            }
        }

        private static void ValidateArtifacts(JArray artifactJson, WorkflowValidationResult result)
        {
            if (artifactJson == null)
            {
                return;
            }

            for (var i = 0; i < artifactJson.Count; i++)
            {
                var artifactObject = artifactJson[i] as JObject;
                if (artifactObject != null)
                {
                    ValidateUnsupportedFields(artifactObject, ArtifactFields, "artifacts[" + i + "]", result.Errors);
                }
            }
        }

        private static void ValidateUnsupportedFields(JObject obj, HashSet<string> allowedFields, string location, List<string> errors)
        {
            foreach (var property in obj.Properties())
            {
                if (!allowedFields.Contains(property.Name))
                {
                    errors.Add(location + " has unsupported field: " + property.Name + ".");
                }
            }
        }
    }
}
