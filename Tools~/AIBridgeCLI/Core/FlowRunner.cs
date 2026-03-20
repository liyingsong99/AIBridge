using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AIBridgeCLI.Commands;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Core
{
    public sealed class FlowRunner
    {
        private static readonly HashSet<string> SupportedCommandTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "editor",
            "gameobject",
            "transform",
            "inspector",
            "selection",
            "scene",
            "prefab",
            "asset",
            "menu_item",
            "get_logs",
            "screenshot",
            "compile"
        };

        private readonly int _commandTimeout;
        private readonly OutputMode _outputMode;

        public FlowRunner(int commandTimeout, OutputMode outputMode)
        {
            _commandTimeout = commandTimeout;
            _outputMode = outputMode;
        }

        public CommandResult Run(string filePath)
        {
            var definition = FlowParser.ParseFile(filePath);
            definition.WorkingDirectory = Directory.GetCurrentDirectory();
            var runState = CreateRunState(definition);

            EnsureFlowRunDirectoryExists();
            PersistRunState(runState);
            WriteLastPointer(runState);
            AppendEvent(runState, "info", "Flow run created.", null);

            try
            {
                var sender = new CommandSender(_commandTimeout);

                for (var i = 0; i < definition.Statements.Count; i++)
                {
                    var statement = definition.Statements[i];
                    runState.CurrentStatementIndex = i;
                    runState.CurrentStepId = statement.StepId;
                    PersistRunState(runState);

                    switch (statement.Type)
                    {
                        case FlowStatementType.Step:
                            ExecuteStep(definition, statement, runState, sender);
                            break;
                        case FlowStatementType.Assert:
                            ExecuteAssert(statement, runState);
                            break;
                        case FlowStatementType.Wait:
                            ExecuteWait(definition, statement, runState, sender);
                            break;
                        case FlowStatementType.Verify:
                            ExecuteVerify(definition, statement, runState);
                            break;
                        case FlowStatementType.End:
                            AppendEvent(runState, "info", "Flow reached END.", null);
                            break;
                    }

                    PersistRunState(runState);
                }

                runState.Status = "succeeded";
                runState.CompletedAtUtc = DateTime.UtcNow.ToString("O");
                PersistRunState(runState);
                WriteResultFile(runState, true, null);

                return new CommandResult
                {
                    id = runState.RunId,
                    success = true,
                    data = runState,
                    executionTime = 0
                };
            }
            catch (Exception ex)
            {
                runState.Status = "failed";
                runState.Error = ex.Message;
                runState.CompletedAtUtc = DateTime.UtcNow.ToString("O");
                PersistRunState(runState);
                AppendEvent(runState, "error", ex.Message, runState.CurrentStepId);
                WriteResultFile(runState, false, ex.Message);

                return new CommandResult
                {
                    id = runState.RunId,
                    success = false,
                    error = ex.Message,
                    data = runState,
                    executionTime = 0
                };
            }
        }

        public CommandResult GetStatus(string runId)
        {
            var statePath = GetStateFilePath(runId);
            if (!File.Exists(statePath))
            {
                return new CommandResult
                {
                    id = runId,
                    success = false,
                    error = $"Flow run not found: {runId}"
                };
            }

            var json = File.ReadAllText(statePath);
            var state = JsonConvert.DeserializeObject<FlowRunState>(json);
            return new CommandResult
            {
                id = runId,
                success = true,
                data = state
            };
        }

        public CommandResult GetLastStatus()
        {
            var lastPath = Path.Combine(PathHelper.GetFlowRunsDirectory(), "last.json");
            if (!File.Exists(lastPath))
            {
                return new CommandResult
                {
                    success = false,
                    error = "No previous flow run found."
                };
            }

            var lastJson = File.ReadAllText(lastPath);
            var payload = JsonConvert.DeserializeObject<Dictionary<string, string>>(lastJson);
            if (payload == null || !payload.TryGetValue("runId", out var runId) || string.IsNullOrWhiteSpace(runId))
            {
                return new CommandResult
                {
                    success = false,
                    error = "Invalid last flow pointer."
                };
            }

            return GetStatus(runId);
        }

        private void ExecuteStep(FlowDefinition definition, FlowStatement statement, FlowRunState runState, CommandSender sender)
        {
            var stepState = new FlowStepState
            {
                StepId = statement.StepId,
                Type = "STEP",
                JobType = statement.JobType,
                LineNumber = statement.LineNumber,
                Status = "running",
                StartedAtUtc = DateTime.UtcNow.ToString("O"),
                Message = statement.CommandLine
            };
            runState.Steps.Add(stepState);
            AppendEvent(runState, "info", "Executing step.", statement.StepId);

            var request = statement.Target == FlowExecutionTarget.Job
                ? BuildWorkflowJobStartRequest(definition, statement)
                : BuildUnityRequest(Interpolate(statement.CommandLine, definition.Variables));
            stepState.CommandId = request.id;
            var result = sender.SendCommand(request);
            stepState.Result = result;
            stepState.CompletedAtUtc = DateTime.UtcNow.ToString("O");

            runState.LastResult = new FlowLastResult
            {
                Success = result.success,
                StepId = statement.StepId,
                Error = result.error,
                Data = result.data,
                CommandId = result.id
            };

            if (statement.Target == FlowExecutionTarget.Job)
            {
                var token = result.data != null ? JToken.FromObject(result.data) : null;
                stepState.JobId = token?["jobId"]?.ToString();
            }

            if (!result.success)
            {
                stepState.Status = "failed";
                stepState.Message = result.error;
                throw new InvalidOperationException($"Step '{statement.StepId}' failed: {result.error}");
            }

            stepState.Status = "succeeded";
        }

        private void ExecuteWait(FlowDefinition definition, FlowStatement statement, FlowRunState runState, CommandSender sender)
        {
            var waitState = new FlowStepState
            {
                StepId = statement.StepId,
                Type = "WAIT",
                JobType = statement.JobType,
                LineNumber = statement.LineNumber,
                Status = "running",
                StartedAtUtc = DateTime.UtcNow.ToString("O"),
                Message = statement.Target == FlowExecutionTarget.Job ? statement.JobType : statement.CommandLine
            };
            runState.Steps.Add(waitState);

            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalMilliseconds < statement.TimeoutMs)
            {
                var request = statement.Target == FlowExecutionTarget.Job
                    ? BuildWorkflowJobStatusRequest(statement, runState)
                    : BuildUnityRequest(Interpolate(statement.CommandLine, definition.Variables));

                waitState.CommandId = request.id;
                var result = sender.SendCommand(request);
                waitState.Result = result;
                runState.LastResult = new FlowLastResult
                {
                    Success = result.success,
                    StepId = statement.StepId,
                    Error = result.error,
                    Data = result.data,
                    CommandId = result.id
                };

                if (!result.success)
                {
                    waitState.Status = "failed";
                    waitState.CompletedAtUtc = DateTime.UtcNow.ToString("O");
                    throw new InvalidOperationException($"WAIT '{statement.StepId}' failed to poll: {result.error}");
                }

                if (!string.IsNullOrWhiteSpace(statement.FailIfExpression) && EvaluateExpression(result, statement.FailIfExpression))
                {
                    waitState.Status = "failed";
                    waitState.CompletedAtUtc = DateTime.UtcNow.ToString("O");
                    throw new InvalidOperationException($"WAIT '{statement.StepId}' hit FAIL_IF condition: {statement.FailIfExpression}");
                }

                if (EvaluateExpression(result, statement.UntilExpression))
                {
                    waitState.Status = "succeeded";
                    waitState.CompletedAtUtc = DateTime.UtcNow.ToString("O");
                    return;
                }

                Thread.Sleep(statement.PollIntervalMs);
            }

            waitState.Status = "failed";
            waitState.CompletedAtUtc = DateTime.UtcNow.ToString("O");
            throw new InvalidOperationException($"WAIT '{statement.StepId}' timed out after {statement.TimeoutMs}ms");
        }

        private void ExecuteAssert(FlowStatement statement, FlowRunState runState)
        {
            var expression = statement.AssertionExpression;
            if (!string.Equals(expression, "last.success == true", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(expression, "last.success == false", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported ASSERT expression at line {statement.LineNumber}: {expression}");
            }

            if (string.Equals(expression, "last.success == false", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("ASSERT last.success == false is not supported in the MVP because STEP failures are terminal.");
            }

            if (runState.LastResult == null)
            {
                throw new InvalidOperationException($"ASSERT at line {statement.LineNumber} requires a previous step result.");
            }

            var expected = expression.EndsWith("true", StringComparison.OrdinalIgnoreCase);
            if (runState.LastResult.Success != expected)
            {
                throw new InvalidOperationException($"ASSERT failed at line {statement.LineNumber}: expected last.success == {expected.ToString().ToLowerInvariant()}");
            }

            AppendEvent(runState, "info", "Assertion passed.", runState.LastResult.StepId);
        }

        private void ExecuteVerify(FlowDefinition definition, FlowStatement statement, FlowRunState runState)
        {
            var target = Interpolate(statement.VerifyTarget, definition.Variables);
            var baseDirectory = string.IsNullOrWhiteSpace(definition.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : definition.WorkingDirectory;
            var absoluteTarget = Path.IsPathRooted(target)
                ? target
                : Path.Combine(baseDirectory, target);

            var success = statement.VerifyType == FlowVerifyType.FileExists
                ? File.Exists(absoluteTarget)
                : Directory.Exists(absoluteTarget);

            if (!success)
            {
                throw new InvalidOperationException($"VERIFY failed at line {statement.LineNumber}: {statement.VerifyType} '{target}' was not found.");
            }

            AppendEvent(runState, "info", $"Verification passed for '{target}'.", null);
        }

        private CommandRequest BuildUnityRequest(string commandLine)
        {
            var parts = SplitCommandLine(commandLine);
            if (parts.Count == 0)
            {
                throw new InvalidOperationException("STEP command line is empty.");
            }

            var commandType = parts[0];
            if (!SupportedCommandTypes.Contains(commandType))
            {
                throw new InvalidOperationException($"Flow MVP does not support STEP command type '{commandType}'. Supported types: {string.Join(", ", SupportedCommandTypes)}");
            }

            if (!CommandRegistry.TryGet(commandType, out var builder))
            {
                throw new InvalidOperationException($"Unsupported flow command type: {commandType}");
            }

            string action = null;
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var i = 1;
            if (i < parts.Count && !parts[i].StartsWith("--", StringComparison.Ordinal))
            {
                action = parts[i];
                i++;
            }

            if (commandType.Equals("compile", StringComparison.OrdinalIgnoreCase) && string.Equals(action, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Flow does not support compile dotnet. Use compile unity or workflow_job compile.unity.");
            }

            while (i < parts.Count)
            {
                var part = parts[i];
                if (!part.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unexpected token in flow command: {part}");
                }

                var key = part.Substring(2);
                if (i + 1 < parts.Count && !parts[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options[key] = parts[i + 1];
                    i += 2;
                    continue;
                }

                options[key] = "true";
                i++;
            }

            return builder.Build(action, options);
        }

        private CommandRequest BuildWorkflowJobStartRequest(FlowDefinition definition, FlowStatement statement)
        {
            var request = new CommandRequest
            {
                id = PathHelper.GenerateCommandId(),
                type = "workflow_job",
                @params = new Dictionary<string, object>
                {
                    ["action"] = "start",
                    ["jobType"] = statement.JobType
                }
            };

            var interpolatedArgs = Interpolate(statement.CommandLine, definition.Variables);
            if (!string.IsNullOrWhiteSpace(interpolatedArgs))
            {
                var parts = SplitCommandLine(interpolatedArgs);
                for (var i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    if (!part.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Unexpected JOB argument token: {part}");
                    }

                    var key = part.Substring(2);
                    if (i + 1 < parts.Count && !parts[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        request.@params[key] = parts[i + 1];
                        i++;
                    }
                    else
                    {
                        request.@params[key] = true;
                    }
                }
            }

            return request;
        }

        private CommandRequest BuildWorkflowJobStatusRequest(FlowStatement statement, FlowRunState runState)
        {
            var jobId = FindLatestJobId(runState, statement.JobType);
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new InvalidOperationException($"WAIT '{statement.StepId}' requires a previous STEP JOB {statement.JobType} result with jobId.");
            }

            return new CommandRequest
            {
                id = PathHelper.GenerateCommandId(),
                type = "workflow_job",
                @params = new Dictionary<string, object>
                {
                    ["action"] = "status",
                    ["jobId"] = jobId
                }
            };
        }

        private static string FindLatestJobId(FlowRunState runState, string jobType)
        {
            for (var i = runState.Steps.Count - 1; i >= 0; i--)
            {
                var step = runState.Steps[i];
                if (!string.Equals(step.Type, "STEP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(step.JobType, jobType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(step.JobId))
                {
                    return step.JobId;
                }
            }

            return null;
        }

        private static List<string> SplitCommandLine(string commandLine)
        {
            var result = new List<string>();
            var current = string.Empty;
            var inQuote = false;
            var quoteChar = ' ';

            for (var i = 0; i < commandLine.Length; i++)
            {
                var c = commandLine[i];
                if (!inQuote && (c == '"' || c == '\''))
                {
                    inQuote = true;
                    quoteChar = c;
                }
                else if (inQuote && c == quoteChar)
                {
                    inQuote = false;
                    quoteChar = ' ';
                }
                else if (!inQuote && char.IsWhiteSpace(c))
                {
                    if (!string.IsNullOrEmpty(current))
                    {
                        result.Add(current);
                        current = string.Empty;
                    }
                }
                else
                {
                    current += c;
                }
            }

            if (!string.IsNullOrEmpty(current))
            {
                result.Add(current);
            }

            return result;
        }

        private static string Interpolate(string input, Dictionary<string, string> variables)
        {
            if (string.IsNullOrEmpty(input) || variables == null || variables.Count == 0)
            {
                return input;
            }

            var output = input;
            foreach (var pair in variables)
            {
                output = output.Replace("${" + pair.Key + "}", pair.Value ?? string.Empty);
            }

            return output;
        }

        private static bool EvaluateExpression(CommandResult result, string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return false;
            }

            var op = expression.Contains("!=", StringComparison.Ordinal) ? "!=" : "==";
            var parts = expression.Split(new[] { op }, 2, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException($"Unsupported expression: {expression}");
            }

            var left = parts[0].Trim();
            var right = NormalizeExpressionLiteral(parts[1].Trim());
            var actual = ResolveExpressionValue(result, left);
            var actualText = actual?.ToString();
            var equals = string.Equals(actualText, right, StringComparison.OrdinalIgnoreCase);
            return op == "==" ? equals : !equals;
        }

        private static object ResolveExpressionValue(CommandResult result, string path)
        {
            if (!path.StartsWith("$", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported expression root: {path}");
            }

            var token = JToken.FromObject(result);
            if (path == "$")
            {
                return token;
            }

            var normalized = path.StartsWith("$.", StringComparison.Ordinal) ? path.Substring(2) : path.Substring(1);
            var current = token;
            foreach (var segment in normalized.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = current?[segment];
                if (current == null)
                {
                    return null;
                }
            }

            return current.Type == JTokenType.Boolean ? current.Value<bool>().ToString().ToLowerInvariant() : ((JValue)current).Value;
        }

        private static string NormalizeExpressionLiteral(string literal)
        {
            if (literal.Length >= 2)
            {
                if ((literal.StartsWith("\"") && literal.EndsWith("\"")) || (literal.StartsWith("'") && literal.EndsWith("'")))
                {
                    return literal.Substring(1, literal.Length - 2);
                }
            }

            return literal;
        }

        private static FlowRunState CreateRunState(FlowDefinition definition)
        {
            return new FlowRunState
            {
                RunId = BuildRunId(),
                FlowName = definition.Name,
                SourceFilePath = definition.SourceFilePath,
                Status = "running",
                StartedAtUtc = DateTime.UtcNow.ToString("O"),
                Variables = new Dictionary<string, string>(definition.Variables, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static string BuildRunId()
        {
            return "flow_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static void EnsureFlowRunDirectoryExists()
        {
            var dir = PathHelper.GetFlowRunsDirectory();
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var gitIgnorePath = Path.Combine(dir, ".gitignore");
            if (!File.Exists(gitIgnorePath))
            {
                File.WriteAllText(gitIgnorePath, "*\n!.gitignore\n");
            }
        }

        private static void PersistRunState(FlowRunState runState)
        {
            var json = JsonConvert.SerializeObject(runState, Formatting.Indented);
            WriteAtomic(GetStateFilePath(runState.RunId), json);
        }

        private static void WriteResultFile(FlowRunState runState, bool success, string error)
        {
            var payload = new CommandResult
            {
                id = runState.RunId,
                success = success,
                error = error,
                data = runState
            };
            WriteAtomic(GetResultFilePath(runState.RunId), JsonConvert.SerializeObject(payload, Formatting.Indented));
        }

        private static void WriteLastPointer(FlowRunState runState)
        {
            var payload = new Dictionary<string, string>
            {
                ["runId"] = runState.RunId,
                ["flowName"] = runState.FlowName,
                ["sourceFilePath"] = runState.SourceFilePath
            };
            WriteAtomic(Path.Combine(PathHelper.GetFlowRunsDirectory(), "last.json"), JsonConvert.SerializeObject(payload, Formatting.Indented));
        }

        private static void AppendEvent(FlowRunState runState, string level, string message, string stepId)
        {
            var flowEvent = new FlowEvent
            {
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                RunId = runState.RunId,
                Level = level,
                Message = message,
                StepId = stepId
            };

            var eventPath = GetEventsFilePath(runState.RunId);
            File.AppendAllText(eventPath, JsonConvert.SerializeObject(flowEvent, Formatting.None) + Environment.NewLine);
        }

        private static string GetStateFilePath(string runId)
        {
            return Path.Combine(PathHelper.GetFlowRunsDirectory(), runId + ".json");
        }

        private static string GetResultFilePath(string runId)
        {
            return Path.Combine(PathHelper.GetFlowRunsDirectory(), runId + ".result.json");
        }

        private static string GetEventsFilePath(string runId)
        {
            return Path.Combine(PathHelper.GetFlowRunsDirectory(), runId + ".events.jsonl");
        }

        private static void WriteAtomic(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, content);

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }
    }
}
