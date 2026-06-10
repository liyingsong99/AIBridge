using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public class WorkflowCliRunner
    {
        private readonly int _timeoutMs;

        public WorkflowCliRunner(int timeoutMs)
        {
            _timeoutMs = timeoutMs <= 0 ? 5000 : timeoutMs;
        }

        public WorkflowRunManifest Run(string recipePath, string inputsValue, string resumeRunId, string rerunMode)
        {
            var doc = WorkflowRecipeLoader.Load(recipePath);
            var validation = WorkflowValidator.ValidateDocument(doc);
            if (!validation.Success)
            {
                throw new InvalidOperationException("Workflow recipe is invalid: " + string.Join("; ", validation.Errors));
            }

            var inputs = WorkflowInputResolver.ResolveInputs(doc.Recipe, inputsValue);
            inputs["recipeFile"] = doc.Path;

            var store = string.IsNullOrWhiteSpace(resumeRunId) ? new WorkflowRunStore() : WorkflowRunStore.Open(resumeRunId);
            var manifest = string.IsNullOrWhiteSpace(resumeRunId)
                ? CreateManifest(doc, store)
                : store.LoadManifest();
            if (string.IsNullOrWhiteSpace(resumeRunId))
            {
                ApplyAutoCleanSummary(manifest);
            }

            manifest.Status = "running";
            manifest.EndedAtUtc = null;
            store.EnsureDirectories();
            store.SaveInputs(inputs);
            store.SaveManifest(manifest);

            var collector = new WorkflowArtifactCollector(store);
            foreach (var phase in doc.Recipe.Phases)
            {
                RunPhase(store, collector, manifest, phase, inputs, rerunMode);
                UpdateSummary(manifest);
                store.SaveManifest(manifest);
            }

            manifest.GateResults.Clear();
            var gateResults = WorkflowGateEvaluator.Evaluate(doc.Recipe, manifest);
            foreach (var gateResult in gateResults)
            {
                manifest.GateResults.Add(gateResult);
                store.SaveGateResult(gateResult);
            }

            manifest.Status = DetermineFinalStatus(manifest);
            var insight = WorkflowRunInsight.Analyze(manifest);
            manifest.Summary = insight.Summary;
            manifest.TerminalState = insight.TerminalState;
            manifest.TerminalReason = insight.TerminalReason;
            manifest.EndedAtUtc = DateTime.UtcNow.ToString("o");
            store.SaveManifest(manifest);

            var report = WorkflowReportWriter.WriteMarkdown(manifest);
            store.SaveReport(report);
            var reportArtifact = new WorkflowArtifactRef
            {
                ArtifactId = "art_workflow_report_" + manifest.RunId,
                Kind = "workflow-report",
                Path = WorkflowPathHelper.ToDisplayPath(store.ReportPath),
                SourceCommand = "workflow run-cli",
                Summary = "Workflow markdown report.",
                ContentType = "text/markdown",
                Copied = true,
                CreatedAtUtc = DateTime.UtcNow.ToString("o")
            };
            manifest.ArtifactRefs.Add(reportArtifact);
            store.SaveArtifact(reportArtifact);
            UpdateSummary(manifest);
            store.SaveManifest(manifest);

            return manifest;
        }

        private void RunPhase(
            WorkflowRunStore store,
            WorkflowArtifactCollector collector,
            WorkflowRunManifest manifest,
            WorkflowPhase phase,
            JObject inputs,
            string rerunMode)
        {
            var phaseState = new WorkflowPhaseState
            {
                PhaseId = phase.Id,
                Status = "running",
                StartedAtUtc = DateTime.UtcNow.ToString("o"),
                RequiredSkills = CopySkillList(phase.RequiredSkills),
                ReleaseSkillsAfter = CopySkillList(phase.ReleaseSkillsAfter)
            };
            store.SavePhaseState(phaseState);

            var anyFailed = false;
            var anyBlocked = false;
            var anySkipped = false;
            if (phase.Steps != null)
            {
                foreach (var step in phase.Steps)
                {
                    if (ShouldSkipPassedStep(manifest, step, rerunMode))
                    {
                        var existing = manifest.StepStates.First(state => string.Equals(state.StepId, step.Id, StringComparison.OrdinalIgnoreCase));
                        phaseState.StepIds.Add(existing.StepId);
                        phaseState.ArtifactIds.AddRange(existing.ArtifactIds);
                        continue;
                    }

                    var stepState = RunStep(store, collector, manifest, phase, step, inputs);
                    phaseState.StepIds.Add(stepState.StepId);
                    phaseState.ArtifactIds.AddRange(stepState.ArtifactIds);
                    anyFailed = anyFailed || string.Equals(stepState.Status, "failed", StringComparison.OrdinalIgnoreCase);
                    anyBlocked = anyBlocked || string.Equals(stepState.Status, "blocked", StringComparison.OrdinalIgnoreCase);
                    anySkipped = anySkipped || stepState.Status.StartsWith("skipped", StringComparison.OrdinalIgnoreCase);
                    store.SaveStepState(stepState);
                    UpsertStepState(manifest, stepState);
                    UpdateSummary(manifest);
                    store.SaveManifest(manifest);
                }
            }

            phaseState.Status = anyBlocked ? "blocked" : (anyFailed ? "failed" : (anySkipped ? "partial" : "passed"));
            phaseState.EndedAtUtc = DateTime.UtcNow.ToString("o");
            store.SavePhaseState(phaseState);
            UpsertPhaseState(manifest, phaseState);
        }

        private WorkflowStepState RunStep(
            WorkflowRunStore store,
            WorkflowArtifactCollector collector,
            WorkflowRunManifest manifest,
            WorkflowPhase phase,
            WorkflowStep step,
            JObject inputs)
        {
            var state = new WorkflowStepState
            {
                StepId = step.Id,
                PhaseId = phase.Id,
                Kind = step.Kind,
                StartedAtUtc = DateTime.UtcNow.ToString("o"),
                RequiredSkills = CopySkillList(step.RequiredSkills),
                ReleaseSkillsAfter = CopySkillList(step.ReleaseSkillsAfter),
                Outputs = CopyStringList(step.Outputs)
            };

            if (string.Equals(step.Kind, "cli", StringComparison.OrdinalIgnoreCase))
            {
                var command = WorkflowInputResolver.ResolveTemplate(step.Command, inputs);
                state.Command = command;
                var execution = WorkflowCommandLine.Execute(command, _timeoutMs);
                var commandResultPath = store.SaveCommandResult(execution.CommandId, execution.Result);
                var artifacts = collector.CollectForCommand(command, execution, commandResultPath);
                var commandResult = new WorkflowCommandResultRef
                {
                    CommandId = execution.CommandId,
                    Command = command,
                    Success = execution.Success,
                    ExitCode = execution.ExitCode,
                    ResultPath = WorkflowPathHelper.ToDisplayPath(commandResultPath),
                    StartedAtUtc = execution.StartedAtUtc,
                    EndedAtUtc = execution.EndedAtUtc
                };

                foreach (var artifact in artifacts)
                {
                    state.ArtifactIds.Add(artifact.ArtifactId);
                    commandResult.ArtifactIds.Add(artifact.ArtifactId);
                    manifest.ArtifactRefs.Add(artifact);
                }

                manifest.CommandResults.Add(commandResult);
                state.CommandResultRef = execution.CommandId;
                state.Status = execution.Success ? "passed" : (IsBlockedError(execution.Error) ? "blocked" : "failed");
                state.Error = execution.Success ? null : execution.Error;
            }
            else if (string.Equals(step.Kind, "agent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(step.Kind, "manual", StringComparison.OrdinalIgnoreCase))
            {
                state.Status = "skipped_requires_external_executor";
                state.Error = "This step requires an external agent or manual executor. workflow run-cli records it but does not execute it.";
            }
            else if (string.Equals(step.Kind, "barrier", StringComparison.OrdinalIgnoreCase)
                || string.Equals(step.Kind, "report", StringComparison.OrdinalIgnoreCase))
            {
                state.Status = "passed";
            }
            else
            {
                state.Status = "blocked";
                state.Error = "Unsupported step kind: " + step.Kind;
            }

            state.EndedAtUtc = DateTime.UtcNow.ToString("o");
            return state;
        }

        private static List<string> CopySkillList(List<string> skills)
        {
            return skills == null ? new List<string>() : new List<string>(skills);
        }

        private static List<string> CopyStringList(List<string> values)
        {
            return values == null ? new List<string>() : new List<string>(values);
        }

        private static WorkflowRunManifest CreateManifest(WorkflowRecipeDocument doc, WorkflowRunStore store)
        {
            return new WorkflowRunManifest
            {
                RunId = store.RunId,
                RecipeName = doc.Recipe.Name,
                RecipePath = WorkflowPathHelper.ToDisplayPath(doc.Path),
                ProjectRoot = WorkflowPathHelper.NormalizeSeparators(WorkflowPathHelper.GetProjectRoot()),
                StartedAtUtc = DateTime.UtcNow.ToString("o"),
                Status = "pending"
            };
        }

        private static bool ShouldSkipPassedStep(WorkflowRunManifest manifest, WorkflowStep step, string rerunMode)
        {
            if (manifest == null || step == null || manifest.StepStates == null)
            {
                return false;
            }

            if (string.Equals(rerunMode, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return manifest.StepStates.Any(state =>
                    string.Equals(state.StepId, step.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(state.Status, "passed", StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        private static bool IsBlockedError(string error)
        {
            return !string.IsNullOrWhiteSpace(error)
                && error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void UpsertStepState(WorkflowRunManifest manifest, WorkflowStepState state)
        {
            var existing = manifest.StepStates.FirstOrDefault(item => string.Equals(item.StepId, state.StepId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                manifest.StepStates.Remove(existing);
            }

            manifest.StepStates.Add(state);
        }

        private static void UpsertPhaseState(WorkflowRunManifest manifest, WorkflowPhaseState state)
        {
            var existing = manifest.PhaseStates.FirstOrDefault(item => string.Equals(item.PhaseId, state.PhaseId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                manifest.PhaseStates.Remove(existing);
            }

            manifest.PhaseStates.Add(state);
        }

        private static string DetermineFinalStatus(WorkflowRunManifest manifest)
        {
            var requiredGateFailed = manifest.GateResults.Any(gate => gate.Required && string.Equals(gate.Status, "failed", StringComparison.OrdinalIgnoreCase));
            if (requiredGateFailed)
            {
                return "failed";
            }

            var requiredGateBlocked = manifest.GateResults.Any(gate => gate.Required && string.Equals(gate.Status, "blocked", StringComparison.OrdinalIgnoreCase));
            if (requiredGateBlocked)
            {
                return "blocked";
            }

            var stepBlocked = manifest.StepStates.Any(step => string.Equals(step.Status, "blocked", StringComparison.OrdinalIgnoreCase));
            if (stepBlocked)
            {
                return "blocked";
            }

            var stepFailed = manifest.StepStates.Any(step => string.Equals(step.Status, "failed", StringComparison.OrdinalIgnoreCase));
            if (stepFailed)
            {
                return "failed";
            }

            var externalImportGaps = WorkflowRunInsight.CollectExternalImportGaps(manifest);
            if (externalImportGaps.Count > 0)
            {
                return "partial";
            }

            var skippedExternal = manifest.StepStates.Any(step => step.Status.StartsWith("skipped", StringComparison.OrdinalIgnoreCase));
            var requiredGateSkipped = manifest.GateResults.Any(gate => gate.Required && string.Equals(gate.Status, "skipped", StringComparison.OrdinalIgnoreCase));
            return skippedExternal || requiredGateSkipped ? "partial" : "passed";
        }

        private static void UpdateSummary(WorkflowRunManifest manifest)
        {
            WorkflowArtifactSink.UpdateSummary(manifest);
        }

        private static void ApplyAutoCleanSummary(WorkflowRunManifest manifest)
        {
            try
            {
                var result = WorkflowCleaner.AutoClean();
                if (result == null)
                {
                    return;
                }

                manifest.Summary.AutoCleanCandidateCount = result.Count;
                manifest.Summary.AutoCleanDeletedCount = result.DeletedCount;
            }
            catch (Exception ex)
            {
                manifest.Summary.AutoCleanError = ex.Message;
            }
        }
    }
}
