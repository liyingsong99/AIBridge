using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public sealed class WorkflowRunInsightResult
    {
        [JsonProperty("summary")]
        public WorkflowRunSummary Summary { get; set; }

        [JsonProperty("terminalState")]
        public string TerminalState { get; set; }

        [JsonProperty("terminalReason")]
        public string TerminalReason { get; set; }

        [JsonProperty("externalImportGaps")]
        public List<WorkflowExternalImportGap> ExternalImportGaps { get; set; } = new List<WorkflowExternalImportGap>();

        [JsonProperty("evidenceFreshness")]
        public List<WorkflowEvidenceFreshnessEntry> EvidenceFreshness { get; set; } = new List<WorkflowEvidenceFreshnessEntry>();
    }

    public sealed class WorkflowExternalImportGap
    {
        [JsonProperty("stepId")]
        public string StepId { get; set; }

        [JsonProperty("phaseId")]
        public string PhaseId { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("expectedOutputs")]
        public List<string> ExpectedOutputs { get; set; } = new List<string>();

        [JsonProperty("missingOutputs")]
        public List<string> MissingOutputs { get; set; } = new List<string>();

        [JsonProperty("importedArtifactIds")]
        public List<string> ImportedArtifactIds { get; set; } = new List<string>();

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }
    }

    public sealed class WorkflowEvidenceFreshnessEntry
    {
        [JsonProperty("refId")]
        public string RefId { get; set; }

        [JsonProperty("refType")]
        public string RefType { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("schema")]
        public string Schema { get; set; }

        [JsonProperty("stepId")]
        public string StepId { get; set; }

        [JsonProperty("source")]
        public string Source { get; set; }

        [JsonProperty("freshness")]
        public string Freshness { get; set; }

        [JsonProperty("ageMinutes")]
        public double? AgeMinutes { get; set; }

        [JsonProperty("thresholdMinutes")]
        public double? ThresholdMinutes { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }
    }

    public sealed class WorkflowVerdictCounts
    {
        public int Total { get; set; }
        public int Confirmed { get; set; }
        public int Refuted { get; set; }
        public int Uncertain { get; set; }
    }

    public static class WorkflowRunInsight
    {
        private static readonly HashSet<string> ImportableSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Verdict",
            "Finding",
            "PatchProposal",
            "ValidationResult",
            "EvidenceRef",
            "CommandEvidence",
            "SkillHandoff"
        };

        public static WorkflowRunInsightResult Analyze(WorkflowRunManifest manifest)
        {
            var externalImportGaps = CollectExternalImportGaps(manifest);
            var evidenceFreshness = CollectEvidenceFreshness(manifest);
            var summary = BuildSummary(manifest, externalImportGaps, evidenceFreshness);

            return new WorkflowRunInsightResult
            {
                Summary = summary,
                TerminalState = ResolveTerminalState(manifest, externalImportGaps, evidenceFreshness),
                TerminalReason = ResolveTerminalReason(manifest, externalImportGaps, evidenceFreshness),
                ExternalImportGaps = externalImportGaps,
                EvidenceFreshness = evidenceFreshness
            };
        }

        public static void UpdateSummary(WorkflowRunManifest manifest)
        {
            if (manifest == null)
            {
                return;
            }

            manifest.Summary = BuildSummary(manifest);
        }

        public static WorkflowRunSummary BuildSummary(WorkflowRunManifest manifest)
        {
            return BuildSummary(
                manifest,
                CollectExternalImportGaps(manifest),
                CollectEvidenceFreshness(manifest));
        }

        public static List<WorkflowExternalImportGap> CollectExternalImportGaps(WorkflowRunManifest manifest)
        {
            var result = new List<WorkflowExternalImportGap>();
            if (manifest == null || manifest.StepStates == null)
            {
                return result;
            }

            foreach (var step in manifest.StepStates)
            {
                if (!IsExternalStep(step))
                {
                    continue;
                }

                var expectedOutputs = NormalizeOutputs(step.Outputs);
                if (expectedOutputs.Count == 0)
                {
                    continue;
                }

                var importableOutputs = expectedOutputs.Where(IsImportableSchema).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (importableOutputs.Count == 0)
                {
                    continue;
                }

                var importedArtifactIds = new List<string>();
                var missingOutputs = new List<string>();
                foreach (var output in importableOutputs)
                {
                    var matchingArtifactIds = FindImportedArtifactIds(manifest, step.StepId, output);
                    if (matchingArtifactIds.Count == 0)
                    {
                        missingOutputs.Add(output);
                        continue;
                    }

                    importedArtifactIds.AddRange(matchingArtifactIds);
                }

                if (missingOutputs.Count == 0)
                {
                    continue;
                }

                result.Add(new WorkflowExternalImportGap
                {
                    StepId = step.StepId,
                    PhaseId = step.PhaseId,
                    Kind = step.Kind,
                    ExpectedOutputs = importableOutputs,
                    MissingOutputs = missingOutputs,
                    ImportedArtifactIds = importedArtifactIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Status = "partial",
                    Reason = "Missing imported external outputs: " + string.Join(", ", missingOutputs.ToArray())
                });
            }

            return result;
        }

        public static List<WorkflowEvidenceFreshnessEntry> CollectEvidenceFreshness(WorkflowRunManifest manifest)
        {
            var result = new List<WorkflowEvidenceFreshnessEntry>();
            if (manifest == null)
            {
                return result;
            }

            if (manifest.ArtifactRefs != null)
            {
                foreach (var artifact in manifest.ArtifactRefs)
                {
                    if (!ShouldIncludeArtifactForFreshness(artifact))
                    {
                        continue;
                    }

                    result.Add(BuildArtifactFreshnessEntry(artifact));
                }
            }

            if (manifest.CommandResults != null)
            {
                foreach (var commandResult in manifest.CommandResults)
                {
                    if (!ShouldIncludeCommandResultForFreshness(commandResult))
                    {
                        continue;
                    }

                    result.Add(BuildCommandFreshnessEntry(commandResult));
                }
            }

            result.Sort(CompareFreshnessEntries);
            return result;
        }

        public static WorkflowEvidenceFreshnessEntry EvaluateArtifactFreshness(WorkflowArtifactRef artifact)
        {
            return BuildArtifactFreshnessEntry(artifact);
        }

        public static WorkflowEvidenceFreshnessEntry EvaluateCommandFreshness(WorkflowCommandResultRef commandResult)
        {
            return BuildCommandFreshnessEntry(commandResult);
        }

        public static bool IsFresh(WorkflowEvidenceFreshnessEntry entry)
        {
            return entry != null && string.Equals(entry.Freshness, "fresh", StringComparison.OrdinalIgnoreCase);
        }

        public static WorkflowVerdictCounts CountVerdicts(WorkflowRunManifest manifest)
        {
            var counts = new WorkflowVerdictCounts();
            if (manifest == null || manifest.ArtifactRefs == null)
            {
                return counts;
            }

            foreach (var artifact in manifest.ArtifactRefs)
            {
                if (!IsVerdictArtifact(artifact))
                {
                    continue;
                }

                foreach (var verdict in ReadArtifactObjects(artifact))
                {
                    counts.Total++;
                    var status = (string)verdict["status"];
                    if (string.Equals(status, "confirmed", StringComparison.OrdinalIgnoreCase))
                    {
                        counts.Confirmed++;
                    }
                    else if (string.Equals(status, "refuted", StringComparison.OrdinalIgnoreCase))
                    {
                        counts.Refuted++;
                    }
                    else if (string.Equals(status, "uncertain", StringComparison.OrdinalIgnoreCase))
                    {
                        counts.Uncertain++;
                    }
                }
            }

            return counts;
        }

        public static string ResolveTerminalState(
            WorkflowRunManifest manifest,
            List<WorkflowExternalImportGap> externalImportGaps,
            List<WorkflowEvidenceFreshnessEntry> evidenceFreshness)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Status))
            {
                return null;
            }

            var status = manifest.Status.Trim().ToLowerInvariant();
            if (status == "running" || status == "pending")
            {
                return null;
            }

            if (status == "canceled")
            {
                return "needs-human";
            }

            if (externalImportGaps != null && externalImportGaps.Count > 0
                && !HasBlockingGateOrStepIssue(manifest))
            {
                // 外部回流缺口只在没有真实 gate/step 阻塞时提升为 external-handoff，避免通用 blocked 掩盖具体原因。
                return "external-handoff";
            }

            if (status == "blocked" || status == "failed")
            {
                return "blocked";
            }

            if (HasStaleEvidence(evidenceFreshness))
            {
                return "stale";
            }

            if (status == "passed" || status == "success")
            {
                return "success";
            }

            if (status == "partial")
            {
                return "partial";
            }

            return status;
        }

        public static string ResolveTerminalReason(
            WorkflowRunManifest manifest,
            List<WorkflowExternalImportGap> externalImportGaps,
            List<WorkflowEvidenceFreshnessEntry> evidenceFreshness)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Status))
            {
                return null;
            }

            var status = manifest.Status.Trim().ToLowerInvariant();
            if (status == "running" || status == "pending")
            {
                return null;
            }

            if (status == "canceled")
            {
                return "Run was canceled before completion.";
            }

            if (externalImportGaps != null && externalImportGaps.Count > 0
                && !HasBlockingGateOrStepIssue(manifest))
            {
                return externalImportGaps[0].Reason;
            }

            if (status == "blocked" || status == "failed")
            {
                var gateReason = FindFirstGateReason(manifest);
                if (!string.IsNullOrWhiteSpace(gateReason))
                {
                    return gateReason;
                }

                return "Required gate or step did not complete successfully.";
            }

            var staleEntry = FindFirstFreshnessEntry(evidenceFreshness, "stale");
            if (staleEntry != null)
            {
                return staleEntry.Reason;
            }

            if (status == "passed" || status == "success")
            {
                return "All required gates passed and all required external outputs were imported.";
            }

            if (status == "partial")
            {
                return "Run completed with open gaps.";
            }

            if (status == "stale")
            {
                return staleEntry == null ? "Evidence freshness is stale." : staleEntry.Reason;
            }

            if (status == "external-handoff")
            {
                return externalImportGaps != null && externalImportGaps.Count > 0
                    ? externalImportGaps[0].Reason
                    : "Required external outputs are still missing.";
            }

            if (status == "needs-human")
            {
                return "Manual intervention is required.";
            }

            return null;
        }

        private static bool HasBlockingGateOrStepIssue(WorkflowRunManifest manifest)
        {
            if (manifest == null)
            {
                return false;
            }

            if (manifest.GateResults != null)
            {
                foreach (var gate in manifest.GateResults)
                {
                    if (gate == null || !gate.Required)
                    {
                        continue;
                    }

                    if (!string.Equals(gate.Status, "passed", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            if (manifest.StepStates != null)
            {
                foreach (var step in manifest.StepStates)
                {
                    if (step == null)
                    {
                        continue;
                    }

                    if (string.Equals(step.Status, "failed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(step.Status, "blocked", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static WorkflowRunSummary BuildSummary(
            WorkflowRunManifest manifest,
            List<WorkflowExternalImportGap> externalImportGaps,
            List<WorkflowEvidenceFreshnessEntry> evidenceFreshness)
        {
            var summary = new WorkflowRunSummary();
            if (manifest == null)
            {
                return summary;
            }

            summary.CliCommandCount = manifest.CommandResults == null ? 0 : manifest.CommandResults.Count;
            summary.AgentStepCount = CountExternalSteps(manifest);
            summary.ArtifactCount = manifest.ArtifactRefs == null ? 0 : manifest.ArtifactRefs.Count;
            summary.FailedGateCount = manifest.GateResults == null
                ? 0
                : manifest.GateResults.Count(gate => string.Equals(gate.Status, "failed", StringComparison.OrdinalIgnoreCase));
            summary.IterationCount = CountIterationSteps(manifest);
            summary.ExternalSkippedCount = CountExternalSkipped(manifest);
            summary.MissingExternalImportCount = externalImportGaps == null
                ? 0
                : externalImportGaps.Sum(gap => gap == null || gap.MissingOutputs == null ? 0 : gap.MissingOutputs.Count);
            summary.OpenRiskCount = CountOpenRisks(manifest);

            var verdictCounts = CountVerdicts(manifest);
            summary.ImportedVerdictCount = verdictCounts.Total;
            summary.ConfirmedVerdictCount = verdictCounts.Confirmed;
            summary.RefutedVerdictCount = verdictCounts.Refuted;
            summary.UncertainVerdictCount = verdictCounts.Uncertain;

            if (evidenceFreshness != null)
            {
                summary.FreshEvidenceCount = evidenceFreshness.Count(entry => string.Equals(entry.Freshness, "fresh", StringComparison.OrdinalIgnoreCase));
                summary.StaleEvidenceCount = evidenceFreshness.Count(entry => string.Equals(entry.Freshness, "stale", StringComparison.OrdinalIgnoreCase));
                summary.MissingEvidenceCount = evidenceFreshness.Count(entry => string.Equals(entry.Freshness, "missing", StringComparison.OrdinalIgnoreCase));
                summary.UnknownEvidenceCount = evidenceFreshness.Count(entry => string.Equals(entry.Freshness, "unknown", StringComparison.OrdinalIgnoreCase));
            }

            return summary;
        }

        private static int CountIterationSteps(WorkflowRunManifest manifest)
        {
            if (manifest == null || manifest.StepStates == null)
            {
                return 0;
            }

            return manifest.StepStates.Count(step =>
                !string.Equals(step.Kind, "barrier", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(step.Kind, "report", StringComparison.OrdinalIgnoreCase));
        }

        private static int CountExternalSteps(WorkflowRunManifest manifest)
        {
            if (manifest == null || manifest.StepStates == null)
            {
                return 0;
            }

            return manifest.StepStates.Count(IsExternalStep);
        }

        private static int CountExternalSkipped(WorkflowRunManifest manifest)
        {
            if (manifest == null || manifest.StepStates == null)
            {
                return 0;
            }

            return manifest.StepStates.Count(step =>
                string.Equals(step.Status, "skipped_requires_external_executor", StringComparison.OrdinalIgnoreCase));
        }

        private static int CountOpenRisks(WorkflowRunManifest manifest)
        {
            if (manifest == null || manifest.GateResults == null)
            {
                return 0;
            }

            return manifest.GateResults.Count(gate =>
                gate.Required && !string.Equals(gate.Status, "passed", StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasStaleEvidence(List<WorkflowEvidenceFreshnessEntry> evidenceFreshness)
        {
            if (evidenceFreshness != null && evidenceFreshness.Any(entry => string.Equals(entry.Freshness, "stale", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        private static WorkflowEvidenceFreshnessEntry FindFirstFreshnessEntry(List<WorkflowEvidenceFreshnessEntry> entries, string freshness)
        {
            if (entries == null)
            {
                return null;
            }

            foreach (var entry in entries)
            {
                if (string.Equals(entry.Freshness, freshness, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }

        private static string FindFirstGateReason(WorkflowRunManifest manifest)
        {
            if (manifest == null || manifest.GateResults == null)
            {
                return null;
            }

            foreach (var gate in manifest.GateResults)
            {
                if (string.Equals(gate.Status, "passed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(gate.Message))
                {
                    return gate.Message;
                }
            }

            return null;
        }

        private static WorkflowEvidenceFreshnessEntry BuildArtifactFreshnessEntry(WorkflowArtifactRef artifact)
        {
            var path = ResolveEvidencePath(artifact == null ? null : artifact.Path);
            var sourcePath = ResolveEvidencePath(artifact == null ? null : artifact.SourcePath);
            var timestamp = ParseTimestamp(artifact == null ? null : artifact.CreatedAtUtc);
            if (timestamp == null && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                timestamp = File.GetLastWriteTimeUtc(path);
            }
            else if (timestamp == null && !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
            {
                timestamp = File.GetLastWriteTimeUtc(sourcePath);
            }

            if (artifact == null)
            {
                return new WorkflowEvidenceFreshnessEntry
                {
                    RefType = "artifact",
                    Freshness = "unknown",
                    Reason = "Artifact reference is missing."
                };
            }

            if (!string.Equals(artifact.Kind, "workflow-report", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(path) || !File.Exists(path)))
            {
                return new WorkflowEvidenceFreshnessEntry
                {
                    RefId = artifact.ArtifactId,
                    RefType = "artifact",
                    Kind = artifact.Kind,
                    Schema = artifact.Schema,
                    StepId = artifact.StepId,
                    Source = artifact.SourceCommand,
                    Freshness = "missing",
                    Reason = "Artifact file was not found."
                };
            }

            if (timestamp == null)
            {
                return new WorkflowEvidenceFreshnessEntry
                {
                    RefId = artifact.ArtifactId,
                    RefType = "artifact",
                    Kind = artifact.Kind,
                    Schema = artifact.Schema,
                    StepId = artifact.StepId,
                    Source = artifact.SourceCommand,
                    Freshness = "unknown",
                    Reason = "Created time is unavailable."
                };
            }

            var threshold = GetArtifactThreshold(artifact);
            var age = DateTime.UtcNow - timestamp.Value;
            var freshness = age <= threshold ? "fresh" : "stale";
            return new WorkflowEvidenceFreshnessEntry
            {
                RefId = artifact.ArtifactId,
                RefType = "artifact",
                Kind = artifact.Kind,
                Schema = artifact.Schema,
                StepId = artifact.StepId,
                Source = artifact.SourceCommand,
                Freshness = freshness,
                AgeMinutes = age.TotalMinutes,
                ThresholdMinutes = threshold.TotalMinutes,
                Reason = freshness == "fresh"
                    ? "Artifact is " + FormatDuration(age) + " old within the " + FormatDuration(threshold) + " freshness window."
                    : "Artifact is " + FormatDuration(age) + " old and exceeds the " + FormatDuration(threshold) + " freshness window."
            };
        }

        private static WorkflowEvidenceFreshnessEntry BuildCommandFreshnessEntry(WorkflowCommandResultRef commandResult)
        {
            if (commandResult == null)
            {
                return new WorkflowEvidenceFreshnessEntry
                {
                    RefType = "command",
                    Freshness = "unknown",
                    Reason = "Command result is missing."
                };
            }

            var path = ResolveEvidencePath(commandResult.ResultPath);
            var timestamp = ParseTimestamp(commandResult.EndedAtUtc);
            if (timestamp == null && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                timestamp = File.GetLastWriteTimeUtc(path);
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new WorkflowEvidenceFreshnessEntry
                {
                    RefId = commandResult.CommandId,
                    RefType = "command",
                    Kind = commandResult.Command,
                    Freshness = "missing",
                    Reason = "Command result file was not found."
                };
            }

            if (timestamp == null)
            {
                return new WorkflowEvidenceFreshnessEntry
                {
                    RefId = commandResult.CommandId,
                    RefType = "command",
                    Kind = commandResult.Command,
                    Freshness = "unknown",
                    Reason = "Ended time is unavailable."
                };
            }

            var threshold = GetCommandThreshold(commandResult);
            var age = DateTime.UtcNow - timestamp.Value;
            var freshness = age <= threshold ? "fresh" : "stale";
            return new WorkflowEvidenceFreshnessEntry
            {
                RefId = commandResult.CommandId,
                RefType = "command",
                Kind = commandResult.Command,
                Source = commandResult.ResultPath,
                Freshness = freshness,
                AgeMinutes = age.TotalMinutes,
                ThresholdMinutes = threshold.TotalMinutes,
                Reason = freshness == "fresh"
                    ? "Command result is " + FormatDuration(age) + " old within the " + FormatDuration(threshold) + " freshness window."
                    : "Command result is " + FormatDuration(age) + " old and exceeds the " + FormatDuration(threshold) + " freshness window."
            };
        }

        private static List<string> NormalizeOutputs(List<string> outputs)
        {
            if (outputs == null || outputs.Count == 0)
            {
                return new List<string>();
            }

            return outputs
                .Where(output => !string.IsNullOrWhiteSpace(output))
                .Select(output => output.Trim())
                .ToList();
        }

        private static bool IsImportableSchema(string schema)
        {
            return !string.IsNullOrWhiteSpace(schema) && ImportableSchemas.Contains(schema.Trim());
        }

        private static bool IsExternalStep(WorkflowStepState step)
        {
            return step != null && (string.Equals(step.Kind, "agent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(step.Kind, "manual", StringComparison.OrdinalIgnoreCase));
        }

        private static bool ShouldIncludeArtifactForFreshness(WorkflowArtifactRef artifact)
        {
            if (artifact == null)
            {
                return false;
            }

            if (string.Equals(artifact.Kind, "workflow-report", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(artifact.Kind, "command-result", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(artifact.SourceCommand)
                && artifact.SourceCommand.Trim().StartsWith("workflow ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool ShouldIncludeCommandResultForFreshness(WorkflowCommandResultRef commandResult)
        {
            if (commandResult == null || string.IsNullOrWhiteSpace(commandResult.Command))
            {
                return false;
            }

            return !commandResult.Command.Trim().StartsWith("workflow ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVerdictArtifact(WorkflowArtifactRef artifact)
        {
            if (artifact == null)
            {
                return false;
            }

            return string.Equals(artifact.Kind, "verdict", StringComparison.OrdinalIgnoreCase)
                || string.Equals(artifact.Schema, "Verdict", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> FindImportedArtifactIds(WorkflowRunManifest manifest, string stepId, string schema)
        {
            var result = new List<string>();
            if (manifest == null || manifest.ArtifactRefs == null)
            {
                return result;
            }

            foreach (var artifact in manifest.ArtifactRefs)
            {
                if (!string.Equals(artifact.StepId, stepId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (MatchesImportedSchema(artifact, schema))
                {
                    result.Add(artifact.ArtifactId);
                }
            }

            return result;
        }

        private static bool MatchesImportedSchema(WorkflowArtifactRef artifact, string schema)
        {
            if (artifact == null || string.IsNullOrWhiteSpace(schema))
            {
                return false;
            }

            return string.Equals(NormalizeArtifactSchema(artifact), schema, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeArtifactSchema(WorkflowArtifactRef artifact)
        {
            if (artifact == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(artifact.SemanticKind))
            {
                return artifact.SemanticKind.Trim();
            }

            if (!string.IsNullOrWhiteSpace(artifact.Schema))
            {
                return artifact.Schema.Trim();
            }

            if (string.Equals(artifact.Kind, "verdict", StringComparison.OrdinalIgnoreCase))
            {
                return "Verdict";
            }

            if (string.Equals(artifact.Kind, "finding", StringComparison.OrdinalIgnoreCase))
            {
                return "Finding";
            }

            if (string.Equals(artifact.Kind, "patch-proposal", StringComparison.OrdinalIgnoreCase))
            {
                return "PatchProposal";
            }

            if (string.Equals(artifact.Kind, "validation-report", StringComparison.OrdinalIgnoreCase))
            {
                return "ValidationResult";
            }

            if (string.Equals(artifact.Kind, "evidence", StringComparison.OrdinalIgnoreCase))
            {
                return "EvidenceRef";
            }

            if (string.Equals(artifact.Kind, "command-evidence", StringComparison.OrdinalIgnoreCase))
            {
                return "CommandEvidence";
            }

            if (string.Equals(artifact.Kind, "skill-handoff", StringComparison.OrdinalIgnoreCase))
            {
                return "SkillHandoff";
            }

            return artifact.Kind;
        }

        private static TimeSpan GetArtifactThreshold(WorkflowArtifactRef artifact)
        {
            var kind = NormalizeArtifactSchema(artifact);
            if (!string.IsNullOrWhiteSpace(kind))
            {
                kind = kind.Trim();
            }

            if (string.Equals(kind, "runtime-screenshot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "screenshot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "gif", StringComparison.OrdinalIgnoreCase))
            {
                return TimeSpan.FromHours(8.0);
            }

            if (string.Equals(kind, "console-log", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "runtime-log", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "runtime-status", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "runtime-perf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "runtime-handler-result", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "validation-report", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "ValidationResult", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "command-result", StringComparison.OrdinalIgnoreCase))
            {
                return TimeSpan.FromHours(12.0);
            }

            if (string.Equals(kind, "Verdict", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "Finding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "PatchProposal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "EvidenceRef", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "CommandEvidence", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "SkillHandoff", StringComparison.OrdinalIgnoreCase))
            {
                return TimeSpan.FromDays(7.0);
            }

            return TimeSpan.FromDays(1.0);
        }

        private static TimeSpan GetCommandThreshold(WorkflowCommandResultRef commandResult)
        {
            if (commandResult == null || string.IsNullOrWhiteSpace(commandResult.Command))
            {
                return TimeSpan.FromHours(12.0);
            }

            var command = commandResult.Command.Trim();
            if (command.StartsWith("compile ", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("get_logs", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("runtime ", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("test ", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("profiler ", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("code_index", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("harness status", StringComparison.OrdinalIgnoreCase))
            {
                return TimeSpan.FromHours(12.0);
            }

            return TimeSpan.FromHours(24.0);
        }

        private static int CompareFreshnessEntries(WorkflowEvidenceFreshnessEntry left, WorkflowEvidenceFreshnessEntry right)
        {
            if (left == null && right == null)
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            var freshnessOrder = CompareFreshnessState(left.Freshness, right.Freshness);
            if (freshnessOrder != 0)
            {
                return freshnessOrder;
            }

            return string.Compare(left.RefId, right.RefId, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareFreshnessState(string left, string right)
        {
            return GetFreshnessRank(left).CompareTo(GetFreshnessRank(right));
        }

        private static int GetFreshnessRank(string freshness)
        {
            if (string.Equals(freshness, "fresh", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(freshness, "stale", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (string.Equals(freshness, "missing", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            return 3;
        }

        private static IEnumerable<JObject> ReadArtifactObjects(WorkflowArtifactRef artifact)
        {
            var path = ResolveEvidencePath(artifact == null ? null : artifact.Path);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                yield break;
            }

            JToken payload;
            try
            {
                payload = JToken.Parse(File.ReadAllText(path));
            }
            catch
            {
                yield break;
            }

            var obj = payload as JObject;
            if (obj != null)
            {
                yield return obj;
                yield break;
            }

            var array = payload as JArray;
            if (array == null)
            {
                yield break;
            }

            foreach (var item in array)
            {
                obj = item as JObject;
                if (obj != null)
                {
                    yield return obj;
                }
            }
        }

        private static string ResolveEvidencePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            return Path.GetFullPath(Path.Combine(WorkflowPathHelper.GetProjectRoot(), path));
        }

        private static DateTime? ParseTimestamp(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            DateTime parsed;
            if (DateTime.TryParse(value, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed.ToUniversalTime();
            }

            return null;
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalDays >= 1.0)
            {
                return span.TotalDays.ToString("0.#", CultureInfo.InvariantCulture) + "d";
            }

            if (span.TotalHours >= 1.0)
            {
                return span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + "h";
            }

            if (span.TotalMinutes >= 1.0)
            {
                return span.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture) + "m";
            }

            return span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s";
        }
    }
}
