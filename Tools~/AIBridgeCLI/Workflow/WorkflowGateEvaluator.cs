using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public static class WorkflowGateEvaluator
    {
        public static List<WorkflowGateResult> Evaluate(WorkflowRecipe recipe, WorkflowRunManifest manifest)
        {
            var results = new List<WorkflowGateResult>();
            if (recipe == null || recipe.Gates == null)
            {
                return results;
            }

            foreach (var gate in recipe.Gates)
            {
                results.Add(EvaluateGate(gate, manifest));
            }

            return results;
        }

        private static WorkflowGateResult EvaluateGate(WorkflowGate gate, WorkflowRunManifest manifest)
        {
            if (gate == null)
            {
                return CreateResult(null, null, false, "blocked", null, "Gate is null.");
            }

            switch ((gate.Kind ?? string.Empty).Trim())
            {
                case "unityCompile":
                    return EvaluateCommandSuccess(gate, manifest, "compile unity", "Unity compile command passed.");
                case "dotnetBuild":
                    return EvaluateCommandSuccess(gate, manifest, "compile dotnet", "dotnet build command passed.");
                case "testRun":
                    return EvaluateCommandSuccess(gate, manifest, "test run", "Unity test run passed.");
                case "consoleErrors":
                    return EvaluateLogErrors(gate, manifest, "console-log", "get_logs", "Console error count");
                case "runtimeErrors":
                    return EvaluateLogErrors(gate, manifest, "runtime-log", "runtime logs", "Runtime error count");
                case "screenshotExists":
                    return EvaluateScreenshotExists(gate, manifest);
                case "runtimeReachable":
                    return EvaluateRuntimeReachable(gate, manifest);
                case "artifactRequired":
                    return EvaluateArtifactRequired(gate, manifest);
                case "externalVerdict":
                    return EvaluateExternalVerdict(gate, manifest);
                case "patchProposalRequired":
                    return EvaluatePatchProposalRequired(gate, manifest);
                default:
                    return CreateResult(gate, "blocked", new List<string>(), "Unsupported gate kind: " + gate.Kind + ".");
            }
        }

        private static WorkflowGateResult EvaluateCommandSuccess(
            WorkflowGate gate,
            WorkflowRunManifest manifest,
            string commandPrefix,
            string successMessage)
        {
            var evidence = new List<string>();
            var sawMatchingEvidence = false;
            var sawFreshEvidence = false;
            var staleEvidence = new List<string>();
            foreach (var commandResult in manifest.CommandResults)
            {
                if (StartsWithCommand(commandResult.Command, commandPrefix))
                {
                    sawMatchingEvidence = true;
                    evidence.Add(commandResult.CommandId);
                    var freshness = WorkflowRunInsight.EvaluateCommandFreshness(commandResult);
                    if (!WorkflowRunInsight.IsFresh(freshness))
                    {
                        staleEvidence.Add(commandResult.CommandId);
                        continue;
                    }

                    sawFreshEvidence = true;
                    if (commandResult.Success)
                    {
                        return CreateResult(gate, "passed", evidence, successMessage);
                    }
                }
            }

            if (!sawMatchingEvidence)
            {
                return CreateResult(gate, "skipped", evidence, "No command result matched `" + commandPrefix + "`.");
            }

            if (!sawFreshEvidence)
            {
                return CreateResult(gate, "blocked", evidence, "Matching command evidence is stale or missing: " + string.Join(", ", staleEvidence.ToArray()) + ".");
            }

            return CreateResult(gate, "failed", evidence, "No successful `" + commandPrefix + "` command result found.");
        }

        private static WorkflowGateResult EvaluateLogErrors(
            WorkflowGate gate,
            WorkflowRunManifest manifest,
            string artifactKind,
            string commandPrefix,
            string label)
        {
            var max = ReadThresholdInt(gate, "max", 0);
            var evidence = new List<string>();
            var totalErrors = 0;
            var evidenceErrors = new List<string>();
            var sawMatchingEvidence = false;
            var sawFreshCommandEvidence = false;
            var sawFreshArtifactEvidence = false;
            var sawStaleEvidence = false;
            foreach (var commandResult in manifest.CommandResults)
            {
                if (!StartsWithCommand(commandResult.Command, commandPrefix))
                {
                    continue;
                }

                sawMatchingEvidence = true;
                evidence.Add(commandResult.CommandId);
                var freshness = WorkflowRunInsight.EvaluateCommandFreshness(commandResult);
                if (!WorkflowRunInsight.IsFresh(freshness))
                {
                    sawStaleEvidence = true;
                    continue;
                }

                sawFreshCommandEvidence = true;
                if (!commandResult.Success)
                {
                    evidenceErrors.Add("Command failed: " + commandResult.CommandId + ".");
                    continue;
                }

                var countResult = CountErrorsInResult(commandResult.ResultPath);
                if (!countResult.Success)
                {
                    evidenceErrors.Add(countResult.Error);
                    continue;
                }

                totalErrors += countResult.ErrorCount;
            }

            if (!sawFreshCommandEvidence)
            {
                foreach (var artifact in manifest.ArtifactRefs)
                {
                    if (string.Equals(artifact.Kind, artifactKind, StringComparison.OrdinalIgnoreCase))
                    {
                        evidence.Add(artifact.ArtifactId);
                        var freshness = WorkflowRunInsight.EvaluateArtifactFreshness(artifact);
                        if (!WorkflowRunInsight.IsFresh(freshness))
                        {
                            sawMatchingEvidence = true;
                            sawStaleEvidence = true;
                            continue;
                        }

                        sawMatchingEvidence = true;
                        sawFreshArtifactEvidence = true;
                        var countResult = CountErrorsInResult(artifact.Path);
                        if (!countResult.Success)
                        {
                            evidenceErrors.Add(countResult.Error);
                            continue;
                        }

                        totalErrors += countResult.ErrorCount;
                    }
                }
            }

            if (!sawMatchingEvidence)
            {
                return CreateResult(gate, "skipped", evidence, "No log evidence found.");
            }

            if (!sawFreshCommandEvidence && !sawFreshArtifactEvidence && sawStaleEvidence)
            {
                return CreateResult(gate, "blocked", evidence, "Log evidence is stale or missing.");
            }

            if (evidenceErrors.Count > 0)
            {
                return CreateResult(gate, "blocked", evidence, "Log evidence could not be evaluated: " + string.Join(" ", evidenceErrors));
            }

            return totalErrors <= max
                ? CreateResult(gate, "passed", evidence, label + " <= " + max + ".")
                : CreateResult(gate, "failed", evidence, label + " is " + totalErrors + ", max allowed is " + max + ".");
        }

        private static WorkflowGateResult EvaluateScreenshotExists(WorkflowGate gate, WorkflowRunManifest manifest)
        {
            var evidence = new List<string>();
            var sawMatchingEvidence = false;
            var sawFreshEvidence = false;
            foreach (var artifact in manifest.ArtifactRefs)
            {
                if (!IsScreenshotKind(artifact.Kind))
                {
                    continue;
                }

                sawMatchingEvidence = true;
                evidence.Add(artifact.ArtifactId);
                var freshness = WorkflowRunInsight.EvaluateArtifactFreshness(artifact);
                if (!WorkflowRunInsight.IsFresh(freshness))
                {
                    continue;
                }

                var path = ResolveArtifactPath(artifact.Path);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    sawFreshEvidence = true;
                    return CreateResult(gate, "passed", evidence, "Screenshot artifact exists.");
                }
            }

            if (!sawMatchingEvidence)
            {
                return CreateResult(gate, "skipped", evidence, "No screenshot artifact found.");
            }

            return sawFreshEvidence
                ? CreateResult(gate, "failed", evidence, "Screenshot artifacts were referenced but files were not found.")
                : CreateResult(gate, "blocked", evidence, "Screenshot evidence is stale or missing.");
        }

        private static WorkflowGateResult EvaluateRuntimeReachable(WorkflowGate gate, WorkflowRunManifest manifest)
        {
            var evidence = new List<string>();
            var sawMatchingEvidence = false;
            var sawFreshEvidence = false;
            foreach (var commandResult in manifest.CommandResults)
            {
                if (StartsWithCommand(commandResult.Command, "runtime status")
                    || StartsWithCommand(commandResult.Command, "runtime ping")
                    || StartsWithCommand(commandResult.Command, "runtime list_targets")
                    || StartsWithCommand(commandResult.Command, "runtime discover"))
                {
                    sawMatchingEvidence = true;
                    evidence.Add(commandResult.CommandId);
                    var freshness = WorkflowRunInsight.EvaluateCommandFreshness(commandResult);
                    if (!WorkflowRunInsight.IsFresh(freshness))
                    {
                        continue;
                    }

                    sawFreshEvidence = true;
                    if (commandResult.Success)
                    {
                        return CreateResult(gate, "passed", evidence, "Runtime command returned successfully.");
                    }
                }
            }

            if (!sawMatchingEvidence)
            {
                return CreateResult(gate, "skipped", evidence, "No runtime reachability evidence found.");
            }

            return sawFreshEvidence
                ? CreateResult(gate, "failed", evidence, "Runtime reachability commands failed.")
                : CreateResult(gate, "blocked", evidence, "Runtime reachability evidence is stale or missing.");
        }

        private static WorkflowGateResult EvaluateArtifactRequired(WorkflowGate gate, WorkflowRunManifest manifest)
        {
            var artifactKind = gate.ArtifactKind;
            var schema = gate.Schema;
            var stepId = gate.StepId;
            var min = gate.Min ?? ReadThresholdInt(gate, "min", 1);
            var evidence = new List<string>();
            var freshCount = 0;
            var sawMatchingEvidence = false;
            foreach (var artifact in manifest.ArtifactRefs)
            {
                if (MatchesArtifactFilter(artifact, artifactKind, schema, stepId))
                {
                    sawMatchingEvidence = true;
                    evidence.Add(artifact.ArtifactId);
                    var freshness = WorkflowRunInsight.EvaluateArtifactFreshness(artifact);
                    if (WorkflowRunInsight.IsFresh(freshness))
                    {
                        freshCount++;
                    }
                }
            }

            if (!sawMatchingEvidence)
            {
                return CreateResult(gate, "failed", evidence, "Artifact requirement not met: 0 < " + min + ".");
            }

            if (freshCount == 0)
            {
                return CreateResult(gate, "blocked", evidence, "Matching artifact evidence is stale or missing.");
            }

            return freshCount >= min
                ? CreateResult(gate, "passed", evidence, "Artifact requirement met.")
                : CreateResult(gate, "failed", evidence, "Artifact requirement not met: " + freshCount + " < " + min + ".");
        }

        private static WorkflowGateResult EvaluateExternalVerdict(WorkflowGate gate, WorkflowRunManifest manifest)
        {
            var allowed = gate.Allow == null || gate.Allow.Count == 0
                ? new List<string> { "confirmed" }
                : gate.Allow;
            var evidence = new List<string>();
            var sawDisallowed = false;
            var sawUncertain = false;
            var disallowedStatus = "";
            var sawMatchingEvidence = false;
            var sawFreshEvidence = false;

            foreach (var artifact in manifest.ArtifactRefs)
            {
                if (!MatchesArtifactFilter(artifact, "verdict", "Verdict", gate.StepId))
                {
                    continue;
                }

                sawMatchingEvidence = true;
                evidence.Add(artifact.ArtifactId);
                var freshness = WorkflowRunInsight.EvaluateArtifactFreshness(artifact);
                if (!WorkflowRunInsight.IsFresh(freshness))
                {
                    continue;
                }

                sawFreshEvidence = true;
                foreach (var verdict in ReadArtifactObjects(artifact))
                {
                    var status = (string)verdict["status"];
                    if (IsAllowedStatus(status, allowed))
                    {
                        return CreateResult(gate, "passed", evidence, "Imported external verdict allowed: " + status + ".");
                    }

                    if (string.Equals(status, "uncertain", StringComparison.OrdinalIgnoreCase))
                    {
                        sawUncertain = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(status))
                    {
                        sawDisallowed = true;
                        disallowedStatus = status;
                    }
                }
            }

            if (!sawMatchingEvidence)
            {
                return CreateResult(gate, "skipped", evidence, "No imported Verdict artifact found.");
            }

            if (!sawFreshEvidence)
            {
                return CreateResult(gate, "blocked", evidence, "Imported Verdict evidence is stale or missing.");
            }

            if (sawDisallowed)
            {
                return CreateResult(gate, "failed", evidence, "Imported external verdict is not allowed: " + disallowedStatus + ".");
            }

            return sawUncertain
                ? CreateResult(gate, "skipped", evidence, "Imported Verdict is uncertain; more evidence is required.")
                : CreateResult(gate, "failed", evidence, "Imported Verdict artifact did not contain an allowed status.");
        }

        private static WorkflowGateResult EvaluatePatchProposalRequired(WorkflowGate gate, WorkflowRunManifest manifest)
        {
            var min = gate.Min ?? ReadThresholdInt(gate, "min", 1);
            var evidence = new List<string>();
            var freshCount = 0;
            var sawMatchingEvidence = false;
            foreach (var artifact in manifest.ArtifactRefs)
            {
                if (MatchesArtifactFilter(artifact, "patch-proposal", "PatchProposal", gate.StepId))
                {
                    sawMatchingEvidence = true;
                    evidence.Add(artifact.ArtifactId);
                    var freshness = WorkflowRunInsight.EvaluateArtifactFreshness(artifact);
                    if (WorkflowRunInsight.IsFresh(freshness))
                    {
                        freshCount++;
                    }
                }
            }

            if (!sawMatchingEvidence)
            {
                return CreateResult(gate, "failed", evidence, "Patch proposal requirement not met: 0 < " + min + ".");
            }

            if (freshCount == 0)
            {
                return CreateResult(gate, "blocked", evidence, "Matching patch proposal evidence is stale or missing.");
            }

            return freshCount >= min
                ? CreateResult(gate, "passed", evidence, "Patch proposal requirement met.")
                : CreateResult(gate, "failed", evidence, "Patch proposal requirement not met: " + freshCount + " < " + min + ".");
        }

        private static WorkflowGateResult CreateResult(WorkflowGate gate, string status, List<string> evidence, string message)
        {
            return CreateResult(gate == null ? null : gate.Id, gate == null ? null : gate.Kind, gate != null && gate.Required, status, evidence, message, gate == null ? null : gate.Threshold);
        }

        private static WorkflowGateResult CreateResult(string gateId, string kind, bool required, string status, List<string> evidence, string message, JObject threshold = null)
        {
            return new WorkflowGateResult
            {
                GateId = gateId,
                Kind = kind,
                Required = required,
                Status = status,
                Threshold = threshold,
                EvidenceRefs = evidence ?? new List<string>(),
                Message = message
            };
        }

        private static bool StartsWithCommand(string command, string prefix)
        {
            return !string.IsNullOrWhiteSpace(command)
                && command.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static int ReadThresholdInt(WorkflowGate gate, string key, int defaultValue)
        {
            if (gate == null || gate.Threshold == null)
            {
                return defaultValue;
            }

            if (gate.Threshold.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token)
                && token.Type == JTokenType.Integer)
            {
                return token.Value<int>();
            }

            return defaultValue;
        }

        private static LogErrorCountResult CountErrorsInResult(string resultPath)
        {
            var path = ResolveArtifactPath(resultPath);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return LogErrorCountResult.Failed("Log evidence file was not found: " + resultPath + ".");
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                if (TryReadBool(root, "success", out var success) && !success)
                {
                    return LogErrorCountResult.Failed("Log command result indicates failure: " + resultPath + ".");
                }

                if (TryReadInt(root, "errorCount", out var errorCount))
                {
                    return LogErrorCountResult.Passed(errorCount);
                }

                var payload = root["data"] as JObject;
                if (payload != null && TryReadInt(payload, "errorCount", out errorCount))
                {
                    return LogErrorCountResult.Passed(errorCount);
                }

                var nestedData = payload == null ? null : payload["data"] as JObject;
                if (nestedData != null && TryReadInt(nestedData, "errorCount", out errorCount))
                {
                    return LogErrorCountResult.Passed(errorCount);
                }

                return LogErrorCountResult.Passed(CountLogEntries(root));
            }
            catch (Exception ex)
            {
                return LogErrorCountResult.Failed("Log evidence is not valid JSON: " + resultPath + " (" + ex.Message + ").");
            }
        }

        private static int CountLogEntries(JToken token)
        {
            var count = 0;
            var obj = token as JObject;
            if (obj != null)
            {
                if (obj.TryGetValue("logType", StringComparison.OrdinalIgnoreCase, out var logType)
                    || obj.TryGetValue("type", StringComparison.OrdinalIgnoreCase, out logType))
                {
                    var text = logType.ToString();
                    if (text.Equals("Error", StringComparison.OrdinalIgnoreCase)
                        || text.Equals("Exception", StringComparison.OrdinalIgnoreCase)
                        || text.Equals("Assert", StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }

                foreach (var property in obj.Properties())
                {
                    count += CountLogEntries(property.Value);
                }
            }

            var array = token as JArray;
            if (array != null)
            {
                foreach (var item in array)
                {
                    count += CountLogEntries(item);
                }
            }

            return count;
        }

        private static bool TryReadInt(JObject obj, string key, out int value)
        {
            value = 0;
            if (obj != null
                && obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token)
                && token.Type == JTokenType.Integer)
            {
                value = token.Value<int>();
                return true;
            }

            return false;
        }

        private static bool TryReadBool(JObject obj, string key, out bool value)
        {
            value = false;
            if (obj != null
                && obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token)
                && token.Type == JTokenType.Boolean)
            {
                value = token.Value<bool>();
                return true;
            }

            return false;
        }

        private static bool IsScreenshotKind(string kind)
        {
            return string.Equals(kind, "screenshot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "runtime-screenshot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "gif", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesArtifactFilter(WorkflowArtifactRef artifact, string artifactKind, string schema, string stepId)
        {
            if (artifact == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(artifactKind)
                && !string.Equals(artifact.Kind, artifactKind, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(schema)
                && !string.Equals(artifact.Schema, schema, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(stepId)
                && !string.Equals(artifact.StepId, stepId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool IsAllowedStatus(string status, List<string> allowed)
        {
            if (string.IsNullOrWhiteSpace(status) || allowed == null)
            {
                return false;
            }

            foreach (var item in allowed)
            {
                if (string.Equals(status, item, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<JObject> ReadArtifactObjects(WorkflowArtifactRef artifact)
        {
            var path = ResolveArtifactPath(artifact.Path);
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

        private sealed class LogErrorCountResult
        {
            public bool Success { get; private set; }
            public int ErrorCount { get; private set; }
            public string Error { get; private set; }

            public static LogErrorCountResult Passed(int errorCount)
            {
                return new LogErrorCountResult
                {
                    Success = true,
                    ErrorCount = errorCount
                };
            }

            public static LogErrorCountResult Failed(string error)
            {
                return new LogErrorCountResult
                {
                    Success = false,
                    Error = error
                };
            }
        }
    }
}
