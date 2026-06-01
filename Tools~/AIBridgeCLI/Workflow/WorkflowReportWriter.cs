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
            sb.AppendLine("| Step | Kind | Status | Command | Error |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var step in manifest.StepStates ?? new List<WorkflowStepState>())
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
