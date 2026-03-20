using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace AIBridgeCLI.Core
{
    public static class FlowParser
    {
        private static readonly Regex FlowRegex = new Regex(@"^FLOW\s+(?<name>.+)$", RegexOptions.Compiled);
        private static readonly Regex VarRegex = new Regex(@"^VAR\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.+)$", RegexOptions.Compiled);
        private static readonly Regex StepUnityRegex = new Regex(@"^STEP\s+(?<id>[A-Za-z0-9_\-]+)\s+UNITY\s+(?<command>.+)$", RegexOptions.Compiled);
        private static readonly Regex StepJobRegex = new Regex(@"^STEP\s+(?<id>[A-Za-z0-9_\-]+)\s+JOB\s+(?<jobType>[A-Za-z0-9_\.\-]+)(?:\s+(?<args>.+))?$", RegexOptions.Compiled);
        private static readonly Regex WaitUnityRegex = new Regex(@"^WAIT\s+(?<id>[A-Za-z0-9_\-]+)\s+UNITY\s+(?<command>.+)$", RegexOptions.Compiled);
        private static readonly Regex WaitJobRegex = new Regex(@"^WAIT\s+(?<id>[A-Za-z0-9_\-]+)\s+JOB\s+(?<jobType>[A-Za-z0-9_\.\-]+)$", RegexOptions.Compiled);
        private static readonly Regex AssertRegex = new Regex(@"^ASSERT\s+(?<expr>.+)$", RegexOptions.Compiled);
        private static readonly Regex VerifyRegex = new Regex(@"^VERIFY\s+(?<kind>FILE_EXISTS|DIR_EXISTS)\s+(?<target>.+)$", RegexOptions.Compiled);
        private static readonly Regex EndRegex = new Regex(@"^END$", RegexOptions.Compiled);

        public static FlowDefinition ParseFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Flow file not found.", filePath);
            }

            var lines = File.ReadAllLines(filePath);
            var definition = new FlowDefinition
            {
                SourceFilePath = Path.GetFullPath(filePath),
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))
            };

            var foundFlow = false;
            var foundEnd = false;
            var executableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < lines.Length; i++)
            {
                var lineNumber = i + 1;
                var rawLine = lines[i];
                var trimmed = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                {
                    continue;
                }

                if (foundEnd)
                {
                    throw new ArgumentException($"No statements are allowed after END (line {lineNumber}).");
                }

                var flowMatch = FlowRegex.Match(trimmed);
                if (flowMatch.Success)
                {
                    if (foundFlow)
                    {
                        throw new ArgumentException($"Multiple FLOW declarations are not supported (line {lineNumber}).");
                    }

                    definition.Name = flowMatch.Groups["name"].Value.Trim();
                    foundFlow = true;
                    continue;
                }

                if (!foundFlow)
                {
                    throw new ArgumentException($"Flow must start with FLOW before other statements (line {lineNumber}).");
                }

                var varMatch = VarRegex.Match(trimmed);
                if (varMatch.Success)
                {
                    definition.Variables[varMatch.Groups["name"].Value] = NormalizeLiteral(varMatch.Groups["value"].Value.Trim());
                    continue;
                }

                var stepUnityMatch = StepUnityRegex.Match(trimmed);
                if (stepUnityMatch.Success)
                {
                    var stepId = stepUnityMatch.Groups["id"].Value;
                    if (!executableIds.Add(stepId))
                    {
                        throw new ArgumentException($"Duplicate executable id '{stepId}' at line {lineNumber}.");
                    }

                    definition.Statements.Add(new FlowStatement
                    {
                        Type = FlowStatementType.Step,
                        Target = FlowExecutionTarget.Unity,
                        LineNumber = lineNumber,
                        RawText = trimmed,
                        StepId = stepId,
                        CommandLine = stepUnityMatch.Groups["command"].Value.Trim()
                    });
                    continue;
                }

                var stepJobMatch = StepJobRegex.Match(trimmed);
                if (stepJobMatch.Success)
                {
                    var stepId = stepJobMatch.Groups["id"].Value;
                    if (!executableIds.Add(stepId))
                    {
                        throw new ArgumentException($"Duplicate executable id '{stepId}' at line {lineNumber}.");
                    }

                    definition.Statements.Add(new FlowStatement
                    {
                        Type = FlowStatementType.Step,
                        Target = FlowExecutionTarget.Job,
                        LineNumber = lineNumber,
                        RawText = trimmed,
                        StepId = stepId,
                        JobType = stepJobMatch.Groups["jobType"].Value,
                        CommandLine = stepJobMatch.Groups["args"].Value?.Trim()
                    });
                    continue;
                }

                var waitUnityMatch = WaitUnityRegex.Match(trimmed);
                if (waitUnityMatch.Success)
                {
                    var waitId = waitUnityMatch.Groups["id"].Value;
                    if (!executableIds.Add(waitId))
                    {
                        throw new ArgumentException($"Duplicate executable id '{waitId}' at line {lineNumber}.");
                    }

                    var statement = new FlowStatement
                    {
                        Type = FlowStatementType.Wait,
                        Target = FlowExecutionTarget.Unity,
                        LineNumber = lineNumber,
                        RawText = trimmed,
                        StepId = waitId,
                        CommandLine = waitUnityMatch.Groups["command"].Value.Trim()
                    };

                    i = ParseWaitClauses(lines, i + 1, statement);
                    EnsureWaitClause(statement, lineNumber);
                    definition.Statements.Add(statement);
                    continue;
                }

                var waitJobMatch = WaitJobRegex.Match(trimmed);
                if (waitJobMatch.Success)
                {
                    var waitId = waitJobMatch.Groups["id"].Value;
                    if (!executableIds.Add(waitId))
                    {
                        throw new ArgumentException($"Duplicate executable id '{waitId}' at line {lineNumber}.");
                    }

                    var statement = new FlowStatement
                    {
                        Type = FlowStatementType.Wait,
                        Target = FlowExecutionTarget.Job,
                        LineNumber = lineNumber,
                        RawText = trimmed,
                        StepId = waitId,
                        JobType = waitJobMatch.Groups["jobType"].Value
                    };

                    i = ParseWaitClauses(lines, i + 1, statement);
                    EnsureWaitClause(statement, lineNumber);
                    definition.Statements.Add(statement);
                    continue;
                }

                var assertMatch = AssertRegex.Match(trimmed);
                if (assertMatch.Success)
                {
                    definition.Statements.Add(new FlowStatement
                    {
                        Type = FlowStatementType.Assert,
                        LineNumber = lineNumber,
                        RawText = trimmed,
                        AssertionExpression = assertMatch.Groups["expr"].Value.Trim()
                    });
                    continue;
                }

                var verifyMatch = VerifyRegex.Match(trimmed);
                if (verifyMatch.Success)
                {
                    definition.Statements.Add(new FlowStatement
                    {
                        Type = FlowStatementType.Verify,
                        LineNumber = lineNumber,
                        RawText = trimmed,
                        VerifyType = verifyMatch.Groups["kind"].Value.Equals("FILE_EXISTS", StringComparison.OrdinalIgnoreCase)
                            ? FlowVerifyType.FileExists
                            : FlowVerifyType.DirectoryExists,
                        VerifyTarget = NormalizeLiteral(verifyMatch.Groups["target"].Value.Trim())
                    });
                    continue;
                }

                if (EndRegex.IsMatch(trimmed))
                {
                    foundEnd = true;
                    definition.Statements.Add(new FlowStatement
                    {
                        Type = FlowStatementType.End,
                        LineNumber = lineNumber,
                        RawText = trimmed
                    });
                    continue;
                }

                throw new ArgumentException($"Unsupported flow statement at line {lineNumber}: {trimmed}");
            }

            if (!foundFlow)
            {
                throw new ArgumentException("Flow file is missing a FLOW declaration.");
            }

            if (definition.Statements.Count == 0 || definition.Statements[definition.Statements.Count - 1].Type != FlowStatementType.End)
            {
                throw new ArgumentException("Flow file must end with END.");
            }

            return definition;
        }

        private static string NormalizeLiteral(string value)
        {
            if (value.Length >= 2)
            {
                if ((value.StartsWith("\"") && value.EndsWith("\"")) || (value.StartsWith("'") && value.EndsWith("'")))
                {
                    return value.Substring(1, value.Length - 2);
                }
            }

            return value;
        }

        private static int ParseWaitClauses(string[] lines, int startIndex, FlowStatement statement)
        {
            var index = startIndex;
            while (index < lines.Length)
            {
                var rawLine = lines[index];
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    index++;
                    continue;
                }

                if (!char.IsWhiteSpace(rawLine[0]))
                {
                    break;
                }

                var trimmed = rawLine.Trim();
                if (trimmed.StartsWith("UNTIL ", StringComparison.OrdinalIgnoreCase))
                {
                    statement.UntilExpression = trimmed.Substring("UNTIL ".Length).Trim();
                }
                else if (trimmed.StartsWith("FAIL_IF ", StringComparison.OrdinalIgnoreCase))
                {
                    statement.FailIfExpression = trimmed.Substring("FAIL_IF ".Length).Trim();
                }
                else if (trimmed.StartsWith("POLL ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(trimmed.Substring("POLL ".Length).Trim(), out var poll))
                    {
                        throw new ArgumentException($"Invalid POLL value at line {index + 1}.");
                    }

                    statement.PollIntervalMs = poll;
                }
                else if (trimmed.StartsWith("TIMEOUT ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(trimmed.Substring("TIMEOUT ".Length).Trim(), out var timeout))
                    {
                        throw new ArgumentException($"Invalid TIMEOUT value at line {index + 1}.");
                    }

                    statement.TimeoutMs = timeout;
                }
                else
                {
                    throw new ArgumentException($"Unsupported WAIT clause at line {index + 1}: {trimmed}");
                }

                index++;
            }

            return index - 1;
        }

        private static void EnsureWaitClause(FlowStatement statement, int lineNumber)
        {
            if (string.IsNullOrWhiteSpace(statement.UntilExpression))
            {
                throw new ArgumentException($"WAIT at line {lineNumber} requires an UNTIL clause.");
            }
        }
    }
}
