using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public static class WorkflowExternalResultImporter
    {
        private static readonly HashSet<string> VerdictStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "confirmed", "refuted", "uncertain"
        };

        public static WorkflowArtifactRef Import(string runId, string stepId, string schema, string kind, string file)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                throw new ArgumentException("Missing import file.");
            }

            var sourcePath = WorkflowPathHelper.ResolvePath(file);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Import file was not found: " + sourcePath);
            }

            var payload = JToken.Parse(File.ReadAllText(sourcePath, Encoding.UTF8));
            schema = NormalizeSchema(schema, kind);
            kind = NormalizeKind(kind, schema);
            ValidatePayload(schema, payload);
            var redactedPayload = Redact(payload.DeepClone());

            var store = WorkflowRunStore.Open(runId);
            var manifest = store.LoadManifest();
            var artifactId = CreateArtifactId(kind);
            var artifactDirectory = store.GetArtifactDirectory(artifactId);
            var payloadPath = Path.Combine(artifactDirectory, "payload.json");
            File.WriteAllText(payloadPath, redactedPayload.ToString(Formatting.Indented), new UTF8Encoding(false));

            var artifact = new WorkflowArtifactRef
            {
                ArtifactId = artifactId,
                Kind = kind,
                Path = WorkflowPathHelper.ToDisplayPath(payloadPath),
                SourcePath = WorkflowPathHelper.ToDisplayPath(sourcePath),
                SourceCommand = "workflow import",
                StepId = stepId,
                Schema = schema,
                ContentType = "application/json",
                Summary = "Imported external " + schema + " result.",
                Copied = true,
                CreatedAtUtc = DateTime.UtcNow.ToString("o")
            };

            manifest.ArtifactRefs.Add(artifact);
            WorkflowArtifactSink.UpdateSummary(manifest);
            store.SaveArtifact(artifact);
            store.SaveManifest(manifest);
            return artifact;
        }

        private static void ValidatePayload(string schema, JToken payload)
        {
            if (string.Equals(schema, "Verdict", StringComparison.OrdinalIgnoreCase))
            {
                ValidateVerdictPayload(payload);
                return;
            }

            if (string.Equals(schema, "SkillHandoff", StringComparison.OrdinalIgnoreCase))
            {
                ValidateRequiredFields(
                    schema,
                    payload,
                    new[] { "completedMode", "summary" },
                    new[] { "releasedSkills", "nextRecommendedSkills", "artifactRefs", "gates", "openRisks" });
                return;
            }

            if (string.Equals(schema, "Finding", StringComparison.OrdinalIgnoreCase))
            {
                ValidateRequiredFields(schema, payload, new[] { "id", "severity", "claim", "evidence" }, new string[0]);
                return;
            }

            if (string.Equals(schema, "PatchProposal", StringComparison.OrdinalIgnoreCase))
            {
                ValidateRequiredFields(schema, payload, new[] { "id", "files", "summary", "validation" }, new string[0]);
                return;
            }

            if (string.Equals(schema, "ValidationResult", StringComparison.OrdinalIgnoreCase))
            {
                ValidateRequiredFields(schema, payload, new[] { "gate", "status", "evidence" }, new string[0]);
                return;
            }

            if (string.Equals(schema, "EvidenceRef", StringComparison.OrdinalIgnoreCase))
            {
                ValidateRequiredFields(schema, payload, new[] { "id", "kind", "summary" }, new string[0]);
                return;
            }

            if (string.Equals(schema, "CommandEvidence", StringComparison.OrdinalIgnoreCase))
            {
                ValidateRequiredFields(schema, payload, new[] { "id", "command", "status", "summary" }, new string[0]);
            }
        }

        private static void ValidateVerdictPayload(JToken payload)
        {
            foreach (var item in EnumerateObjects(payload))
            {
                var status = (string)item["status"];
                if (string.IsNullOrWhiteSpace(status) || !VerdictStatuses.Contains(status))
                {
                    throw new InvalidOperationException("Verdict.status must be confirmed, refuted, or uncertain.");
                }
            }
        }

        private static void ValidateRequiredFields(string schema, JToken payload, string[] requiredStrings, string[] requiredArrays)
        {
            foreach (var item in EnumerateObjects(payload))
            {
                foreach (var field in requiredStrings)
                {
                    if (string.IsNullOrWhiteSpace((string)item[field]))
                    {
                        throw new InvalidOperationException(schema + "." + field + " is required.");
                    }
                }

                foreach (var field in requiredArrays)
                {
                    if (!(item[field] is JArray))
                    {
                        throw new InvalidOperationException(schema + "." + field + " must be an array.");
                    }
                }
            }
        }

        private static IEnumerable<JObject> EnumerateObjects(JToken payload)
        {
            var obj = payload as JObject;
            if (obj != null)
            {
                yield return obj;
                yield break;
            }

            var array = payload as JArray;
            if (array == null)
            {
                throw new InvalidOperationException("Import payload must be a JSON object or array.");
            }

            foreach (var item in array)
            {
                obj = item as JObject;
                if (obj == null)
                {
                    throw new InvalidOperationException("Import array items must be JSON objects.");
                }

                yield return obj;
            }
        }

        private static string NormalizeSchema(string schema, string kind)
        {
            if (!string.IsNullOrWhiteSpace(schema))
            {
                return schema.Trim();
            }

            if (string.Equals(kind, "finding", StringComparison.OrdinalIgnoreCase))
            {
                return "Finding";
            }

            if (string.Equals(kind, "patch-proposal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "patchProposal", StringComparison.OrdinalIgnoreCase))
            {
                return "PatchProposal";
            }

            if (string.Equals(kind, "validation-result", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "validation-report", StringComparison.OrdinalIgnoreCase))
            {
                return "ValidationResult";
            }

            if (string.Equals(kind, "evidence", StringComparison.OrdinalIgnoreCase))
            {
                return "EvidenceRef";
            }

            if (string.Equals(kind, "command-evidence", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "commandEvidence", StringComparison.OrdinalIgnoreCase))
            {
                return "CommandEvidence";
            }

            if (string.Equals(kind, "skill-handoff", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "skillHandoff", StringComparison.OrdinalIgnoreCase))
            {
                return "SkillHandoff";
            }

            return "Verdict";
        }

        private static string NormalizeKind(string kind, string schema)
        {
            if (!string.IsNullOrWhiteSpace(kind))
            {
                var normalizedKind = kind.Trim();
                if (string.Equals(normalizedKind, "patchProposal", StringComparison.OrdinalIgnoreCase))
                {
                    return "patch-proposal";
                }

                if (string.Equals(normalizedKind, "validationResult", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedKind, "validation-result", StringComparison.OrdinalIgnoreCase))
                {
                    return "validation-report";
                }

                if (string.Equals(normalizedKind, "skillHandoff", StringComparison.OrdinalIgnoreCase))
                {
                    return "skill-handoff";
                }

                return normalizedKind;
            }

            if (string.Equals(schema, "Finding", StringComparison.OrdinalIgnoreCase))
            {
                return "finding";
            }

            if (string.Equals(schema, "PatchProposal", StringComparison.OrdinalIgnoreCase))
            {
                return "patch-proposal";
            }

            if (string.Equals(schema, "ValidationResult", StringComparison.OrdinalIgnoreCase))
            {
                return "validation-report";
            }

            if (string.Equals(schema, "EvidenceRef", StringComparison.OrdinalIgnoreCase))
            {
                return "evidence";
            }

            if (string.Equals(schema, "CommandEvidence", StringComparison.OrdinalIgnoreCase))
            {
                return "command-evidence";
            }

            if (string.Equals(schema, "SkillHandoff", StringComparison.OrdinalIgnoreCase))
            {
                return "skill-handoff";
            }

            return "verdict";
        }

        private static JToken Redact(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                foreach (var property in obj.Properties())
                {
                    if (IsSensitiveKey(property.Name))
                    {
                        property.Value = "[redacted]";
                    }
                    else
                    {
                        Redact(property.Value);
                    }
                }
            }

            var array = token as JArray;
            if (array != null)
            {
                foreach (var item in array)
                {
                    Redact(item);
                }
            }

            return token;
        }

        private static bool IsSensitiveKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return key.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("authorization", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("apiKey", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string CreateArtifactId(string kind)
        {
            return "art_imported_" + (kind ?? "result").Replace("-", "_") + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }
    }
}
