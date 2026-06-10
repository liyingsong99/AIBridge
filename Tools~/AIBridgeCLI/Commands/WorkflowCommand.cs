using System;
using System.Collections.Generic;
using System.IO;
using AIBridgeCLI.Core;
using AIBridgeCLI.Workflow;
using Newtonsoft.Json;

namespace AIBridgeCLI.Commands
{
    public static class WorkflowCommand
    {
        public static int Execute(string action, Dictionary<string, string> options, int timeoutMs, OutputMode outputMode)
        {
            var normalizedAction = string.IsNullOrWhiteSpace(action) ? "list" : action.Trim();
            switch (normalizedAction.ToLowerInvariant())
            {
                case "list":
                    return ExecuteList(outputMode);
                case "validate":
                    return ExecuteValidate(options, outputMode);
                case "plan":
                    return ExecutePlan(options, outputMode);
                case "init":
                    return ExecuteInit(options, outputMode);
                case "begin":
                    return ExecuteBegin(options, outputMode);
                case "attach":
                    return ExecuteAttach(options, outputMode);
                case "finish":
                    return ExecuteFinish(options, outputMode);
                case "run-cli":
                    return ExecuteRunCli(options, timeoutMs, outputMode);
                case "import":
                    return ExecuteImport(options, outputMode);
                case "export":
                    return ExecuteExport(options, outputMode);
                case "status":
                    return ExecuteStatus(options, outputMode);
                case "report":
                    return ExecuteReport(options, outputMode);
                case "clean":
                    return ExecuteClean(options, outputMode);
                default:
                    return PrintResult(new CommandResult
                    {
                        success = false,
                        error = "Unknown workflow action: " + normalizedAction
                    }, outputMode);
            }
        }

        public static string GetHelp()
        {
            return new WorkflowCommandBuilder().GetHelp();
        }

        private static int ExecuteList(OutputMode outputMode)
        {
            var recipes = WorkflowRecipeLoader.ListRecipes();
            return PrintResult(new CommandResult
            {
                success = true,
                data = new
                {
                    count = recipes.Count,
                    builtinDirectory = WorkflowPathHelper.ToDisplayPath(WorkflowPathHelper.GetBuiltInRecipesDirectory()),
                    projectDirectory = WorkflowPathHelper.ToDisplayPath(WorkflowPathHelper.GetProjectRecipesDirectory()),
                    recipes = recipes
                }
            }, outputMode);
        }

        private static int ExecuteValidate(Dictionary<string, string> options, OutputMode outputMode)
        {
            var recipePath = GetFileOrRecipe(options);
            var result = WorkflowValidator.ValidateFile(recipePath);
            return PrintResult(new CommandResult
            {
                success = result.Success,
                error = result.Success ? null : string.Join("; ", result.Errors),
                data = result
            }, outputMode);
        }

        private static int ExecutePlan(Dictionary<string, string> options, OutputMode outputMode)
        {
            var recipePath = GetFileOrRecipe(options);
            recipePath = WorkflowPathHelper.ResolveRecipePath(recipePath);
            var doc = WorkflowRecipeLoader.Load(recipePath);
            var validation = WorkflowValidator.ValidateDocument(doc);
            if (!validation.Success)
            {
                return PrintResult(new CommandResult
                {
                    success = false,
                    error = string.Join("; ", validation.Errors),
                    data = validation
                }, outputMode);
            }

            var format = GetOption(options, "format", "json");
            if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(WorkflowPlanWriter.WriteMarkdown(doc.Recipe, doc.Path));
                return 0;
            }

            return PrintResult(new CommandResult
            {
                success = true,
                data = JsonConvert.DeserializeObject(WorkflowPlanWriter.WriteJson(doc.Recipe))
            }, outputMode);
        }

        private static int ExecuteInit(Dictionary<string, string> options, OutputMode outputMode)
        {
            var recipe = GetOption(options, "recipe", null) ?? GetFileOrRecipe(options);
            var output = GetOption(options, "output", null);
            var targetPath = WorkflowRecipeLoader.SaveRecipe(recipe, output);
            return PrintResult(new CommandResult
            {
                success = true,
                data = new
                {
                    recipe = recipe,
                    output = WorkflowPathHelper.ToDisplayPath(targetPath)
                }
            }, outputMode);
        }

        private static int ExecuteRunCli(Dictionary<string, string> options, int timeoutMs, OutputMode outputMode)
        {
            var recipePath = GetFileOrRecipe(options, "Missing required option: --file or --recipe. workflow run-cli --resume <runId> still requires a recipe path or recipe name.");
            recipePath = WorkflowPathHelper.ResolveRecipePath(recipePath);
            var inputs = GetOption(options, "inputs", null);
            var resume = GetOption(options, "resume", null);
            var rerun = GetOption(options, "rerun", null);
            var allowPartial = GetBool(options, "allow-partial", false);
            var runner = new WorkflowCliRunner(timeoutMs);
            var manifest = runner.Run(recipePath, inputs, resume, rerun);
            var store = WorkflowRunStore.Open(manifest.RunId);
            var success = IsSuccessfulWorkflowStatus(manifest.Status, allowPartial);
            return PrintResult(new CommandResult
            {
                success = success,
                error = success ? null : BuildWorkflowStatusError(manifest),
                data = BuildWorkflowRunData(store, manifest, IsFullDetail(options))
            }, outputMode);
        }

        private static int ExecuteBegin(Dictionary<string, string> options, OutputMode outputMode)
        {
            var recipePath = WorkflowPathHelper.ResolveRecipePath(GetFileOrRecipe(options));
            var doc = WorkflowRecipeLoader.Load(recipePath);
            var validation = WorkflowValidator.ValidateDocument(doc);
            if (!validation.Success)
            {
                return PrintResult(new CommandResult
                {
                    success = false,
                    error = string.Join("; ", validation.Errors),
                    data = validation
                }, outputMode);
            }

            var inputs = WorkflowInputResolver.ResolveInputs(doc.Recipe, GetOption(options, "inputs", null));
            inputs["recipeFile"] = doc.Path;

            var store = new WorkflowRunStore();
            var manifest = CreateManifest(doc, store, "running");
            store.EnsureDirectories();
            store.SaveInputs(inputs);
            store.SaveManifest(manifest);
            var active = WorkflowActiveRunStore.Save(manifest);

            return PrintResult(new CommandResult
            {
                success = true,
                data = new
                {
                    runId = manifest.RunId,
                    status = manifest.Status,
                    activeRunPath = WorkflowPathHelper.ToDisplayPath(WorkflowActiveRunStore.ActiveRunPath),
                    runDirectory = WorkflowPathHelper.ToDisplayPath(store.RunDirectory),
                    activeRun = active
                }
            }, outputMode);
        }

        private static int ExecuteAttach(Dictionary<string, string> options, OutputMode outputMode)
        {
            var runId = GetRequiredOption(options, "run");
            var store = WorkflowRunStore.Open(runId);
            var manifest = store.LoadManifest();
            var active = WorkflowActiveRunStore.Save(manifest);
            return PrintResult(new CommandResult
            {
                success = true,
                data = new
                {
                    runId = manifest.RunId,
                    status = manifest.Status,
                    activeRunPath = WorkflowPathHelper.ToDisplayPath(WorkflowActiveRunStore.ActiveRunPath),
                    activeRun = active
                }
            }, outputMode);
        }

        private static int ExecuteFinish(Dictionary<string, string> options, OutputMode outputMode)
        {
            var runId = GetRunId(options, true);
            var status = NormalizeStatus(GetOption(options, "status", "passed"));
            var allowPartial = GetBool(options, "allow-partial", false);
            var store = WorkflowRunStore.Open(runId);
            var manifest = store.LoadManifest();

            RefreshGates(store, manifest);
            manifest.Status = ResolveFinishStatus(manifest, status);
            var insight = WorkflowRunInsight.Analyze(manifest);
            manifest.Summary = insight.Summary;
            manifest.TerminalState = insight.TerminalState;
            manifest.TerminalReason = insight.TerminalReason;
            manifest.EndedAtUtc = DateTime.UtcNow.ToString("o");
            store.SaveManifest(manifest);
            WorkflowArtifactSink.RefreshReportArtifact(store, manifest, "workflow finish");
            WorkflowActiveRunStore.Clear(runId);

            var success = IsSuccessfulWorkflowStatus(manifest.Status, allowPartial);
            return PrintResult(new CommandResult
            {
                success = success,
                error = success ? null : BuildWorkflowStatusError(manifest),
                data = BuildWorkflowRunData(store, manifest, IsFullDetail(options))
            }, outputMode);
        }

        private static int ExecuteImport(Dictionary<string, string> options, OutputMode outputMode)
        {
            var runId = GetRunId(options, true);
            var artifact = WorkflowExternalResultImporter.Import(
                runId,
                GetOption(options, "step", null),
                GetOption(options, "schema", null),
                GetOption(options, "kind", null),
                GetRequiredOption(options, "file"));

            var store = WorkflowRunStore.Open(runId);
            var manifest = store.LoadManifest();
            RefreshGates(store, manifest);
            WorkflowArtifactSink.RefreshReportArtifact(store, manifest, "workflow import");

            return PrintResult(new CommandResult
            {
                success = true,
                data = new
                {
                    runId = runId,
                    artifact = artifact,
                    reportPath = WorkflowPathHelper.ToDisplayPath(store.ReportPath)
                }
            }, outputMode);
        }

        private static int ExecuteExport(Dictionary<string, string> options, OutputMode outputMode)
        {
            var recipePath = WorkflowPathHelper.ResolveRecipePath(GetFileOrRecipe(options));
            var target = GetRequiredOption(options, "target");
            var output = GetOption(options, "output", null);
            var result = WorkflowExporter.Export(recipePath, target, output);
            var context = WorkflowRunContext.Resolve(options);
            var attached = new List<WorkflowArtifactRef>();
            if (context != null)
            {
                foreach (var file in result.Files)
                {
                    attached.Add(WorkflowArtifactSink.AttachFile(
                        context.RunId,
                        "workflow-report",
                        WorkflowPathHelper.ResolvePath(file),
                        "workflow export --target " + target,
                        "Workflow export output for " + target + ".",
                        null,
                        null));
                }
            }

            return PrintResult(new CommandResult
            {
                success = true,
                data = new
                {
                    export = result,
                    attachedArtifacts = attached
                }
            }, outputMode);
        }

        private static int ExecuteStatus(Dictionary<string, string> options, OutputMode outputMode)
        {
            var runId = GetRequiredOption(options, "run");
            var store = WorkflowRunStore.Open(runId);
            var manifest = store.LoadManifest();
            return PrintResult(new CommandResult
            {
                success = true,
                data = BuildWorkflowRunData(store, manifest, IsFullDetail(options))
            }, outputMode);
        }

        private static int ExecuteReport(Dictionary<string, string> options, OutputMode outputMode)
        {
            var runId = GetRequiredOption(options, "run");
            var store = WorkflowRunStore.Open(runId);
            var manifest = store.LoadManifest();
            var markdown = WorkflowReportWriter.WriteMarkdown(manifest);
            store.SaveReport(markdown);
            WorkflowArtifactSink.RefreshReportArtifact(store, manifest, "workflow report");

            var format = GetOption(options, "format", "json");
            if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(markdown);
                return 0;
            }

            return PrintResult(new CommandResult
            {
                success = true,
                data = BuildWorkflowRunData(store, manifest, IsFullDetail(options))
            }, outputMode);
        }

        private static int ExecuteClean(Dictionary<string, string> options, OutputMode outputMode)
        {
            var settings = WorkflowSettings.Load();
            var olderThan = GetOption(options, "older-than", settings.AutoCleanOlderThan ?? "30d");
            var dryRun = GetBool(options, "dry-run", true);
            var keepFailed = GetBool(options, "keep-failed", settings.KeepFailed);
            var keepLatest = GetInt(options, "keep-latest", settings.KeepLatest);
            var maxDelete = GetInt(options, "max-delete", settings.MaxDeletePerRun);
            var result = WorkflowCleaner.Clean(new WorkflowCleanOptions
            {
                OlderThan = olderThan,
                DryRun = dryRun,
                KeepFailed = keepFailed,
                KeepLatest = keepLatest,
                MaxDeletePerRun = maxDelete
            });

            if (GetBool(options, "save-settings", false))
            {
                settings.AutoCleanEnabled = GetBool(options, "auto-clean", settings.AutoCleanEnabled);
                settings.AutoCleanOlderThan = olderThan;
                settings.KeepFailed = keepFailed;
                settings.KeepLatest = keepLatest;
                settings.MaxDeletePerRun = maxDelete;
                settings.Save();
            }

            return PrintResult(new CommandResult
            {
                success = true,
                data = new
                {
                    clean = result,
                    settings = settings,
                    settingsPath = WorkflowPathHelper.ToDisplayPath(WorkflowSettings.GetSettingsPath())
                }
            }, outputMode);
        }

        private static int PrintResult(CommandResult result, OutputMode outputMode)
        {
            OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false);
            return result.success ? 0 : 1;
        }

        private static object BuildWorkflowRunData(WorkflowRunStore store, WorkflowRunManifest manifest, bool includeFullDetail)
        {
            var insight = WorkflowRunInsight.Analyze(manifest);
            manifest.Summary = insight.Summary;
            manifest.TerminalState = insight.TerminalState;
            manifest.TerminalReason = insight.TerminalReason;
            var data = new Dictionary<string, object>
            {
                { "runId", manifest.RunId },
                { "status", manifest.Status },
                { "terminalState", manifest.TerminalState },
                { "terminalReason", manifest.TerminalReason },
                { "recipeName", manifest.RecipeName },
                { "runDirectory", WorkflowPathHelper.ToDisplayPath(store.RunDirectory) },
                { "manifestPath", WorkflowPathHelper.ToDisplayPath(store.ManifestPath) },
                { "reportPath", File.Exists(store.ReportPath) ? WorkflowPathHelper.ToDisplayPath(store.ReportPath) : null },
                { "artifactIds", BuildArtifactIds(manifest) },
                { "summary", manifest.Summary },
                { "skillScopes", BuildSkillScopeSummary(manifest) },
                { "gateResults", BuildGateSummary(manifest) },
                { "externalImportGaps", BuildExternalImportGapSummary(manifest) }
            };

            if (includeFullDetail)
            {
                data["stepGaps"] = BuildStepGapSummary(manifest);
                data["evidenceFreshness"] = BuildEvidenceFreshnessSummary(manifest);
                data["failedCommands"] = BuildFailedCommandSummary(manifest);
                data["manifest"] = manifest;
            }

            return data;
        }

        private static List<string> BuildArtifactIds(WorkflowRunManifest manifest)
        {
            var result = new List<string>();
            if (manifest == null || manifest.ArtifactRefs == null)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in manifest.ArtifactRefs)
            {
                if (artifact == null || string.IsNullOrWhiteSpace(artifact.ArtifactId))
                {
                    continue;
                }

                if (seen.Add(artifact.ArtifactId))
                {
                    result.Add(artifact.ArtifactId);
                }
            }

            return result;
        }

        private static List<object> BuildGateSummary(WorkflowRunManifest manifest)
        {
            var result = new List<object>();
            if (manifest == null || manifest.GateResults == null)
            {
                return result;
            }

            foreach (var gate in manifest.GateResults)
            {
                result.Add(new
                {
                    gateId = gate.GateId,
                    kind = gate.Kind,
                    status = gate.Status,
                    required = gate.Required,
                    message = gate.Message,
                    evidenceRefs = gate.EvidenceRefs
                });
            }

            return result;
        }

        private static object BuildSkillScopeSummary(WorkflowRunManifest manifest)
        {
            return new
            {
                phases = BuildPhaseSkillScopeSummary(manifest),
                steps = BuildStepSkillScopeSummary(manifest)
            };
        }

        private static List<object> BuildPhaseSkillScopeSummary(WorkflowRunManifest manifest)
        {
            var result = new List<object>();
            if (manifest == null || manifest.PhaseStates == null)
            {
                return result;
            }

            foreach (var phase in manifest.PhaseStates)
            {
                if (IsEmptySkillScope(phase.RequiredSkills, phase.ReleaseSkillsAfter))
                {
                    continue;
                }

                result.Add(new
                {
                    phaseId = phase.PhaseId,
                    requiredSkills = phase.RequiredSkills,
                    releaseSkillsAfter = phase.ReleaseSkillsAfter
                });
            }

            return result;
        }

        private static List<object> BuildStepSkillScopeSummary(WorkflowRunManifest manifest)
        {
            var result = new List<object>();
            if (manifest == null || manifest.StepStates == null)
            {
                return result;
            }

            foreach (var step in manifest.StepStates)
            {
                if (IsEmptySkillScope(step.RequiredSkills, step.ReleaseSkillsAfter))
                {
                    continue;
                }

                result.Add(new
                {
                    stepId = step.StepId,
                    phaseId = step.PhaseId,
                    requiredSkills = step.RequiredSkills,
                    releaseSkillsAfter = step.ReleaseSkillsAfter
                });
            }

            return result;
        }

        private static bool IsEmptySkillScope(List<string> requiredSkills, List<string> releaseSkillsAfter)
        {
            return (requiredSkills == null || requiredSkills.Count == 0)
                && (releaseSkillsAfter == null || releaseSkillsAfter.Count == 0);
        }

        private static List<object> BuildStepGapSummary(WorkflowRunManifest manifest)
        {
            var result = new List<object>();
            if (manifest == null || manifest.StepStates == null)
            {
                return result;
            }

            var externalGaps = WorkflowRunInsight.CollectExternalImportGaps(manifest);
            var gapByStepId = new Dictionary<string, WorkflowExternalImportGap>(StringComparer.OrdinalIgnoreCase);
            foreach (var gap in externalGaps)
            {
                if (!string.IsNullOrWhiteSpace(gap.StepId) && !gapByStepId.ContainsKey(gap.StepId))
                {
                    gapByStepId.Add(gap.StepId, gap);
                }
            }

            foreach (var step in manifest.StepStates)
            {
                if (string.Equals(step.Status, "passed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new
                {
                    stepId = step.StepId,
                    kind = step.Kind,
                    status = step.Status,
                    command = step.Command,
                    error = step.Error,
                    outputs = step.Outputs,
                    missingOutputs = GetStepGapOutputs(gapByStepId, step.StepId),
                    importedArtifactIds = GetStepGapImportedArtifacts(gapByStepId, step.StepId),
                    requiredSkills = step.RequiredSkills,
                    releaseSkillsAfter = step.ReleaseSkillsAfter,
                    artifactIds = step.ArtifactIds
                });
            }

            return result;
        }

        private static List<object> BuildExternalImportGapSummary(WorkflowRunManifest manifest)
        {
            var result = new List<object>();
            foreach (var gap in WorkflowRunInsight.CollectExternalImportGaps(manifest))
            {
                result.Add(new
                {
                    stepId = gap.StepId,
                    phaseId = gap.PhaseId,
                    kind = gap.Kind,
                    expectedOutputs = gap.ExpectedOutputs,
                    missingOutputs = gap.MissingOutputs,
                    importedArtifactIds = gap.ImportedArtifactIds,
                    status = gap.Status,
                    reason = gap.Reason
                });
            }

            return result;
        }

        private static List<object> BuildEvidenceFreshnessSummary(WorkflowRunManifest manifest)
        {
            var result = new List<object>();
            foreach (var entry in WorkflowRunInsight.CollectEvidenceFreshness(manifest))
            {
                result.Add(new
                {
                    refId = entry.RefId,
                    refType = entry.RefType,
                    kind = entry.Kind,
                    schema = entry.Schema,
                    stepId = entry.StepId,
                    source = entry.Source,
                    freshness = entry.Freshness,
                    ageMinutes = entry.AgeMinutes,
                    thresholdMinutes = entry.ThresholdMinutes,
                    reason = entry.Reason
                });
            }

            return result;
        }

        private static List<string> GetStepGapOutputs(Dictionary<string, WorkflowExternalImportGap> gapByStepId, string stepId)
        {
            if (gapByStepId == null || string.IsNullOrWhiteSpace(stepId))
            {
                return new List<string>();
            }

            WorkflowExternalImportGap gap;
            return gapByStepId.TryGetValue(stepId, out gap) && gap != null
                ? gap.MissingOutputs
                : new List<string>();
        }

        private static List<string> GetStepGapImportedArtifacts(Dictionary<string, WorkflowExternalImportGap> gapByStepId, string stepId)
        {
            if (gapByStepId == null || string.IsNullOrWhiteSpace(stepId))
            {
                return new List<string>();
            }

            WorkflowExternalImportGap gap;
            return gapByStepId.TryGetValue(stepId, out gap) && gap != null
                ? gap.ImportedArtifactIds
                : new List<string>();
        }

        private static List<object> BuildFailedCommandSummary(WorkflowRunManifest manifest)
        {
            var result = new List<object>();
            if (manifest == null || manifest.CommandResults == null)
            {
                return result;
            }

            foreach (var command in manifest.CommandResults)
            {
                if (command.Success)
                {
                    continue;
                }

                result.Add(new
                {
                    commandId = command.CommandId,
                    command = command.Command,
                    exitCode = command.ExitCode,
                    resultPath = command.ResultPath,
                    artifactIds = command.ArtifactIds
                });
            }

            return result;
        }

        private static string GetFileOrRecipe(Dictionary<string, string> options, string missingMessage = null)
        {
            if (options.TryGetValue("file", out var file) && !string.IsNullOrWhiteSpace(file))
            {
                return file;
            }

            if (options.TryGetValue("recipe", out var recipe) && !string.IsNullOrWhiteSpace(recipe))
            {
                return recipe;
            }

            throw new ArgumentException(missingMessage ?? "Missing required option: --file or --recipe.");
        }

        private static string GetRunId(Dictionary<string, string> options, bool required)
        {
            if (options.TryGetValue("run", out var runId) && !string.IsNullOrWhiteSpace(runId))
            {
                return runId;
            }

            var context = WorkflowRunContext.Resolve(options);
            if (context != null && !string.IsNullOrWhiteSpace(context.RunId))
            {
                return context.RunId;
            }

            if (required)
            {
                throw new ArgumentException("Missing required option: --run, --workflow-run, active run, or AIBRIDGE_WORKFLOW_RUN_ID.");
            }

            return null;
        }

        private static WorkflowRunManifest CreateManifest(WorkflowRecipeDocument doc, WorkflowRunStore store, string status)
        {
            return new WorkflowRunManifest
            {
                RunId = store.RunId,
                RecipeName = doc.Recipe.Name,
                RecipePath = WorkflowPathHelper.ToDisplayPath(doc.Path),
                ProjectRoot = WorkflowPathHelper.NormalizeSeparators(WorkflowPathHelper.GetProjectRoot()),
                StartedAtUtc = DateTime.UtcNow.ToString("o"),
                Status = status
            };
        }

        private static void RefreshGates(WorkflowRunStore store, WorkflowRunManifest manifest)
        {
            var recipePath = ResolveManifestRecipePath(manifest);
            if (string.IsNullOrWhiteSpace(recipePath) || !File.Exists(recipePath))
            {
                WorkflowArtifactSink.UpdateSummary(manifest);
                store.SaveManifest(manifest);
                return;
            }

            var doc = WorkflowRecipeLoader.Load(recipePath);
            var validation = WorkflowValidator.ValidateDocument(doc);
            if (!validation.Success)
            {
                WorkflowArtifactSink.UpdateSummary(manifest);
                store.SaveManifest(manifest);
                return;
            }

            manifest.GateResults.Clear();
            foreach (var gateResult in WorkflowGateEvaluator.Evaluate(doc.Recipe, manifest))
            {
                manifest.GateResults.Add(gateResult);
                store.SaveGateResult(gateResult);
            }

            WorkflowArtifactSink.UpdateSummary(manifest);
            store.SaveManifest(manifest);
        }

        private static string ResolveManifestRecipePath(WorkflowRunManifest manifest)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.RecipePath))
            {
                return null;
            }

            try
            {
                var direct = WorkflowPathHelper.ResolvePath(manifest.RecipePath);
                if (File.Exists(direct))
                {
                    return direct;
                }

                return WorkflowPathHelper.ResolveRecipePath(manifest.RecipeName);
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeStatus(string status)
        {
            var normalized = string.IsNullOrWhiteSpace(status) ? "passed" : status.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "passed":
                case "success":
                case "partial":
                case "failed":
                case "blocked":
                case "canceled":
                case "stale":
                case "needs-human":
                case "external-handoff":
                    return normalized;
                default:
                    throw new ArgumentException("Unsupported workflow finish status: " + status + ".");
            }
        }

        private static string ResolveFinishStatus(WorkflowRunManifest manifest, string requestedStatus)
        {
            if (!string.Equals(requestedStatus, "passed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requestedStatus, "partial", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requestedStatus, "success", StringComparison.OrdinalIgnoreCase))
            {
                return requestedStatus;
            }

            foreach (var gate in manifest.GateResults)
            {
                if (!gate.Required)
                {
                    continue;
                }

                if (string.Equals(gate.Status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    return "failed";
                }

                if (string.Equals(gate.Status, "blocked", StringComparison.OrdinalIgnoreCase))
                {
                    return "blocked";
                }

                if (string.Equals(gate.Status, "skipped", StringComparison.OrdinalIgnoreCase))
                {
                    // required gate 缺少证据时不能被 finish --status passed 覆盖为通过。
                    return (string.Equals(requestedStatus, "passed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(requestedStatus, "success", StringComparison.OrdinalIgnoreCase))
                        ? "blocked"
                        : "partial";
                }
            }

            var externalImportGaps = WorkflowRunInsight.CollectExternalImportGaps(manifest);
            if (externalImportGaps.Count > 0)
            {
                return (string.Equals(requestedStatus, "passed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(requestedStatus, "success", StringComparison.OrdinalIgnoreCase))
                    ? "blocked"
                    : "partial";
            }

            return requestedStatus;
        }

        private static bool IsSuccessfulWorkflowStatus(string status, bool allowPartial)
        {
            return string.Equals(status, "passed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
                || (allowPartial && string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildWorkflowStatusError(WorkflowRunManifest manifest)
        {
            if (manifest == null)
            {
                return "Workflow run ended with status: unknown.";
            }

            var status = string.IsNullOrWhiteSpace(manifest.Status) ? "unknown" : manifest.Status;
            var message = "Workflow run ended with status: " + status + ".";
            if (!string.IsNullOrWhiteSpace(manifest.TerminalState))
            {
                message += " Terminal state: " + manifest.TerminalState + ".";
            }

            if (!string.IsNullOrWhiteSpace(manifest.TerminalReason))
            {
                message += " Reason: " + manifest.TerminalReason;
            }

            return message;
        }

        private static string GetRequiredOption(Dictionary<string, string> options, string key)
        {
            if (options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            throw new ArgumentException("Missing required option: --" + key + ".");
        }

        private static string GetOption(Dictionary<string, string> options, string key, string defaultValue)
        {
            return options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;
        }

        private static bool GetBool(Dictionary<string, string> options, string key, bool defaultValue)
        {
            if (!options.TryGetValue(key, out var value))
            {
                return defaultValue;
            }

            return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        private static bool IsFullDetail(Dictionary<string, string> options)
        {
            if (options == null)
            {
                return false;
            }

            string value;
            return options.TryGetValue("detail", out value)
                && value.Equals("full", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetInt(Dictionary<string, string> options, string key, int defaultValue)
        {
            if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return int.TryParse(value, out var intValue) ? intValue : defaultValue;
        }

    }
}
