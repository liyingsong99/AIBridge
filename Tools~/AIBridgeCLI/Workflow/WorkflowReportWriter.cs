using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public static class WorkflowReportWriter
    {
        public static string WriteMarkdown(WorkflowRunManifest manifest)
        {
            var insight = WorkflowRunInsight.Analyze(manifest);
            manifest.Summary = insight.Summary;
            manifest.TerminalState = insight.TerminalState;
            manifest.TerminalReason = insight.TerminalReason;

            var sb = new StringBuilder();
            sb.AppendLine("# Workflow Report: " + manifest.RunId);
            sb.AppendLine();
            sb.AppendLine("- Recipe: `" + manifest.RecipeName + "`");
            sb.AppendLine("- Status: `" + manifest.Status + "`");
            if (!string.IsNullOrWhiteSpace(manifest.TerminalState))
            {
                sb.AppendLine("- Terminal state: `" + manifest.TerminalState + "`");
            }

            if (!string.IsNullOrWhiteSpace(manifest.TerminalReason))
            {
                sb.AppendLine("- Terminal reason: " + manifest.TerminalReason);
            }

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
            sb.AppendLine("| Iteration count | " + manifest.Summary.IterationCount + " |");
            sb.AppendLine("| External skipped | " + manifest.Summary.ExternalSkippedCount + " |");
            sb.AppendLine("| Missing external imports | " + manifest.Summary.MissingExternalImportCount + " |");
            sb.AppendLine("| Artifacts | " + manifest.Summary.ArtifactCount + " |");
            sb.AppendLine("| Failed gates | " + manifest.Summary.FailedGateCount + " |");
            sb.AppendLine("| Fresh evidence | " + manifest.Summary.FreshEvidenceCount + " |");
            sb.AppendLine("| Stale evidence | " + manifest.Summary.StaleEvidenceCount + " |");
            sb.AppendLine("| Missing evidence | " + manifest.Summary.MissingEvidenceCount + " |");
            sb.AppendLine("| Unknown evidence | " + manifest.Summary.UnknownEvidenceCount + " |");
            sb.AppendLine("| Imported verdicts | " + manifest.Summary.ImportedVerdictCount + " |");
            sb.AppendLine("| Confirmed verdicts | " + manifest.Summary.ConfirmedVerdictCount + " |");
            sb.AppendLine("| Refuted verdicts | " + manifest.Summary.RefutedVerdictCount + " |");
            sb.AppendLine("| Uncertain verdicts | " + manifest.Summary.UncertainVerdictCount + " |");
            sb.AppendLine("| Open risks | " + manifest.Summary.OpenRiskCount + " |");
            sb.AppendLine();

            WriteSkillScopeSection(sb, manifest);

            sb.AppendLine("## Phases");
            sb.AppendLine();
            sb.AppendLine("| Phase | Status | Steps | Artifacts | Error |");
            sb.AppendLine("|---|---|---:|---:|---|");
            foreach (var phase in manifest.PhaseStates ?? new List<WorkflowPhaseState>())
            {
                sb.AppendLine("| `" + phase.PhaseId + "` | `" + phase.Status + "` | " + phase.StepIds.Count + " | " + phase.ArtifactIds.Count + " | " + EscapeTable(phase.Error) + " |");
            }

            sb.AppendLine();
            sb.AppendLine("## Steps");
            sb.AppendLine();
            sb.AppendLine("| Step | Kind | Status | Outputs | Missing Outputs | Command | Error |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var step in manifest.StepStates ?? new List<WorkflowStepState>())
            {
                sb.AppendLine("| `" + step.StepId + "` | `" + step.Kind + "` | `" + step.Status + "` | " + EscapeTable(FormatList(step.Outputs)) + " | " + EscapeTable(FormatList(FindMissingOutputs(step.StepId, insight.ExternalImportGaps))) + " | " + EscapeTable(step.Command) + " | " + EscapeTable(step.Error) + " |");
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
                var kind = artifact.Kind;
                if (!string.IsNullOrWhiteSpace(artifact.SemanticKind))
                {
                    kind += "/" + artifact.SemanticKind;
                }

                if (!string.IsNullOrWhiteSpace(artifact.Schema))
                {
                    kind += "/" + artifact.Schema;
                }

                sb.AppendLine("| `" + artifact.ArtifactId + "` | `" + kind + "` | `" + artifact.Path + "` | " + EscapeTable(artifact.SourceCommand) + " | " + EscapeTable(artifact.Summary) + " |");
            }

            WriteEvidenceFreshnessSection(sb, insight.EvidenceFreshness);
            WriteExternalImportGapSection(sb, insight.ExternalImportGaps);
            WriteVerdictSection(sb, manifest);

            sb.AppendLine();
            sb.AppendLine("## Reproduce");
            sb.AppendLine();
            sb.AppendLine("```bash");
            sb.AppendLine("AIBridgeCLI workflow status --run " + manifest.RunId);
            sb.AppendLine("AIBridgeCLI workflow report --run " + manifest.RunId + " --format markdown");
            sb.AppendLine("```");
            return sb.ToString();
        }

        private static void WriteSkillScopeSection(StringBuilder sb, WorkflowRunManifest manifest)
        {
            if (!HasSkillScopes(manifest))
            {
                return;
            }

            sb.AppendLine("## Skill Scope");
            sb.AppendLine();
            sb.AppendLine("| Scope | Required Skills | Release After |");
            sb.AppendLine("|---|---|---|");
            foreach (var phase in manifest.PhaseStates ?? new List<WorkflowPhaseState>())
            {
                if (!HasSkillScope(phase.RequiredSkills, phase.ReleaseSkillsAfter))
                {
                    continue;
                }

                sb.AppendLine("| `phase:" + phase.PhaseId + "` | " + FormatSkillList(phase.RequiredSkills) + " | " + FormatSkillList(phase.ReleaseSkillsAfter) + " |");
            }

            foreach (var step in manifest.StepStates ?? new List<WorkflowStepState>())
            {
                if (!HasSkillScope(step.RequiredSkills, step.ReleaseSkillsAfter))
                {
                    continue;
                }

                sb.AppendLine("| `step:" + step.StepId + "` | " + FormatSkillList(step.RequiredSkills) + " | " + FormatSkillList(step.ReleaseSkillsAfter) + " |");
            }

            sb.AppendLine();
        }

        private static bool HasSkillScopes(WorkflowRunManifest manifest)
        {
            if (manifest == null)
            {
                return false;
            }

            foreach (var phase in manifest.PhaseStates ?? new List<WorkflowPhaseState>())
            {
                if (HasSkillScope(phase.RequiredSkills, phase.ReleaseSkillsAfter))
                {
                    return true;
                }
            }

            foreach (var step in manifest.StepStates ?? new List<WorkflowStepState>())
            {
                if (HasSkillScope(step.RequiredSkills, step.ReleaseSkillsAfter))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasSkillScope(List<string> requiredSkills, List<string> releaseSkillsAfter)
        {
            return requiredSkills != null && requiredSkills.Count > 0
                || releaseSkillsAfter != null && releaseSkillsAfter.Count > 0;
        }

        private static string FormatSkillList(List<string> skills)
        {
            if (skills == null || skills.Count == 0)
            {
                return "";
            }

            return "`" + string.Join("`, `", skills.ToArray()) + "`";
        }

        private static string FormatList(List<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return "";
            }

            return string.Join(", ", values.ToArray());
        }

        private static List<string> FindMissingOutputs(string stepId, List<WorkflowExternalImportGap> gaps)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(stepId) || gaps == null)
            {
                return result;
            }

            foreach (var gap in gaps)
            {
                if (gap != null && string.Equals(gap.StepId, stepId, StringComparison.OrdinalIgnoreCase))
                {
                    result.AddRange(gap.MissingOutputs);
                }
            }

            return result;
        }

        private static void WriteEvidenceFreshnessSection(StringBuilder sb, List<WorkflowEvidenceFreshnessEntry> entries)
        {
            sb.AppendLine();
            sb.AppendLine("## Evidence Freshness");
            sb.AppendLine();
            if (entries == null || entries.Count == 0)
            {
                sb.AppendLine("- No evidence freshness entries.");
                return;
            }

            sb.AppendLine("| Type | Ref | Kind | Freshness | Age | Threshold | Reason |");
            sb.AppendLine("|---|---|---|---|---:|---:|---|");
            foreach (var entry in entries)
            {
                sb.AppendLine("| `" + EscapeTable(entry.RefType) + "` | `" + EscapeTable(entry.RefId) + "` | `" + EscapeTable(entry.Kind ?? entry.Schema) + "` | `" + EscapeTable(entry.Freshness) + "` | " + FormatMinutes(entry.AgeMinutes) + " | " + FormatMinutes(entry.ThresholdMinutes) + " | " + EscapeTable(entry.Reason) + " |");
            }
        }

        private static void WriteExternalImportGapSection(StringBuilder sb, List<WorkflowExternalImportGap> gaps)
        {
            sb.AppendLine();
            sb.AppendLine("## External Handoff");
            sb.AppendLine();
            if (gaps == null || gaps.Count == 0)
            {
                sb.AppendLine("- All required external outputs have been imported.");
                return;
            }

            sb.AppendLine("| Step | Phase | Kind | Missing Outputs | Imported Artifacts | Reason |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var gap in gaps)
            {
                sb.AppendLine("| `" + EscapeTable(gap.StepId) + "` | `" + EscapeTable(gap.PhaseId) + "` | `" + EscapeTable(gap.Kind) + "` | " + EscapeTable(FormatList(gap.MissingOutputs)) + " | " + EscapeTable(FormatList(gap.ImportedArtifactIds)) + " | " + EscapeTable(gap.Reason) + " |");
            }
        }

        private static string FormatMinutes(double? minutes)
        {
            if (!minutes.HasValue)
            {
                return "";
            }

            return minutes.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "m";
        }

        private static void WriteVerdictSection(StringBuilder sb, WorkflowRunManifest manifest)
        {
            var verdicts = ReadVerdicts(manifest);
            if (verdicts.Count == 0)
            {
                return;
            }

            sb.AppendLine();
            sb.AppendLine("## Imported Verdicts");
            WriteVerdictTable(sb, "confirmed", verdicts);
            WriteVerdictTable(sb, "refuted", verdicts);
            WriteVerdictTable(sb, "uncertain", verdicts);
        }

        private static void WriteVerdictTable(StringBuilder sb, string status, List<VerdictRow> verdicts)
        {
            var wroteHeader = false;
            foreach (var verdict in verdicts)
            {
                if (!string.Equals(verdict.Status, status, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!wroteHeader)
                {
                    sb.AppendLine();
                    sb.AppendLine("### " + status);
                    sb.AppendLine();
                    sb.AppendLine("| Claim | Artifact | Evidence | Reason | Remaining Risk |");
                    sb.AppendLine("|---|---|---|---|---|");
                    wroteHeader = true;
                }

                sb.AppendLine("| " + EscapeTable(verdict.ClaimId) + " | `" + verdict.ArtifactId + "` | " + EscapeTable(verdict.EvidenceRefs) + " | " + EscapeTable(verdict.Reason) + " | " + EscapeTable(verdict.RemainingRisk) + " |");
            }
        }

        private static List<VerdictRow> ReadVerdicts(WorkflowRunManifest manifest)
        {
            var result = new List<VerdictRow>();
            if (manifest == null || manifest.ArtifactRefs == null)
            {
                return result;
            }

            foreach (var artifact in manifest.ArtifactRefs)
            {
                if (!string.Equals(artifact.Kind, "verdict", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(artifact.Schema, "Verdict", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var obj in ReadArtifactObjects(artifact.Path))
                {
                    result.Add(new VerdictRow
                    {
                        ArtifactId = artifact.ArtifactId,
                        ClaimId = (string)obj["claimId"],
                        Status = (string)obj["status"],
                        Reason = (string)obj["reason"],
                        RemainingRisk = (string)obj["remainingRisk"],
                        EvidenceRefs = ReadEvidenceRefs(obj["evidenceRefs"] ?? obj["evidence"])
                    });
                }
            }

            return result;
        }

        private static IEnumerable<JObject> ReadArtifactObjects(string displayPath)
        {
            var path = ResolveArtifactPath(displayPath);
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

        private static string ReadEvidenceRefs(JToken token)
        {
            if (token == null)
            {
                return "";
            }

            var array = token as JArray;
            if (array == null)
            {
                return token.ToString();
            }

            var values = new List<string>();
            foreach (var item in array)
            {
                values.Add(item.ToString());
            }

            return string.Join(", ", values.ToArray());
        }

        private static string ResolveArtifactPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (Path.IsPathRooted(path))
            {
                return path;
            }

            return Path.GetFullPath(Path.Combine(WorkflowPathHelper.GetProjectRoot(), path));
        }

        private static string EscapeTable(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            return value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        private sealed class VerdictRow
        {
            public string ArtifactId { get; set; }
            public string ClaimId { get; set; }
            public string Status { get; set; }
            public string EvidenceRefs { get; set; }
            public string Reason { get; set; }
            public string RemainingRisk { get; set; }
        }
    }
}
