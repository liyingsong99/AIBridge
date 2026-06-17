using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AIBridgeCLI.Commands;
using AIBridgeCLI.Core;
using AIBridgeCLI.Workflow;
using Newtonsoft.Json;

namespace AIBridgeCLI
{
    partial class Program
    {
        static ParsedArgs ParseArguments(string[] args)
        {
            var result = new ParsedArgs();

            var i = 0;
            while (i < args.Length)
            {
                var arg = args[i];

                if (arg.StartsWith("--"))
                {
                    var key = arg.Substring(2);

                    // Check for boolean flags
                    if (key == "help" || key == "raw" || key == "pretty" || key == "quiet" || key == "no-wait" || key == "stdin" || key == "show-warnings")
                    {
                        result.Options[key] = "true";
                        i++;
                        continue;
                    }

                    // Key-value pair
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    {
                        result.Options[key] = args[i + 1];
                        i += 2;
                    }
                    else
                    {
                        result.Options[key] = "true";
                        i++;
                    }
                }
                else if (arg.StartsWith("-"))
                {
                    // Short form not supported, treat as error
                    throw new ArgumentException($"Short form arguments not supported: {arg}");
                }
                else
                {
                    // Positional argument
                    if (result.CommandType == null)
                    {
                        result.CommandType = arg;
                    }
                    else if (result.Action == null)
                    {
                        // For multi command, treat all extra args as commands
                        if (result.CommandType.Equals("multi", StringComparison.OrdinalIgnoreCase))
                        {
                            result.ExtraArgs.Add(arg);
                        }
                        else
                        {
                            result.Action = arg;
                        }
                    }
                    else
                    {
                        if (IsRuntimeLogsClearShortcut(result, arg))
                        {
                            result.Options["clear"] = "true";
                            i++;
                            continue;
                        }

                        if (IsTextIndexSearchQueryShortcut(result))
                        {
                            result.ExtraArgs.Add(arg);
                            i++;
                            continue;
                        }

                        // For multi command, collect extra args
                        if (result.CommandType.Equals("multi", StringComparison.OrdinalIgnoreCase))
                        {
                            result.ExtraArgs.Add(arg);
                        }
                        else
                        {
                            throw new ArgumentException($"Unexpected argument: {arg}");
                        }
                    }
                    i++;
                }
            }

            return result;
        }

        private static bool IsRuntimeLogsClearShortcut(ParsedArgs result, string arg)
        {
            return result != null
                && result.CommandType != null
                && result.Action != null
                && result.CommandType.Equals("runtime", StringComparison.OrdinalIgnoreCase)
                && result.Action.Equals("logs", StringComparison.OrdinalIgnoreCase)
                && arg.Equals("clear", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTextIndexSearchQueryShortcut(ParsedArgs result)
        {
            if (result == null || result.CommandType == null || result.Action == null)
            {
                return false;
            }

            return result.CommandType.Equals("text_index", StringComparison.OrdinalIgnoreCase)
                   && result.Action.Equals("search", StringComparison.OrdinalIgnoreCase)
                   && result.ExtraArgs.Count == 0;
        }

        static string BuildWorkflowSourceCommand(ParsedArgs parsed)
        {
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.CommandType))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.Append(parsed.CommandType);
            if (!string.IsNullOrWhiteSpace(parsed.Action))
            {
                builder.Append(' ').Append(parsed.Action);
            }

            foreach (var option in parsed.Options)
            {
                if (IsWorkflowSourceCommandExcludedOption(option.Key))
                {
                    continue;
                }

                builder.Append(" --").Append(option.Key);
                if (!string.Equals(option.Value, "true", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append(' ').Append(QuoteWorkflowSourceValue(option.Value));
                }
            }

            foreach (var extraArg in parsed.ExtraArgs)
            {
                builder.Append(' ').Append(QuoteWorkflowSourceValue(extraArg));
            }

            return builder.ToString();
        }

        static void TryAttachWorkflowResult(ParsedArgs parsed, CommandResult result, int exitCode)
        {
            if (parsed == null || result == null)
            {
                return;
            }

            var attach = WorkflowArtifactSink.TryAttachCommandResult(
                parsed.Options,
                BuildWorkflowSourceCommand(parsed),
                result,
                exitCode);
            if (!string.IsNullOrWhiteSpace(attach.Error))
            {
                OutputFormatter.PrintWarning("Workflow artifact attach failed: " + attach.Error);
            }
        }

        private static bool IsWorkflowSourceCommandExcludedOption(string key)
        {
            return string.Equals(key, WorkflowRunContext.WorkflowRunOption, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "raw", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "pretty", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "quiet", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "help", StringComparison.OrdinalIgnoreCase);
        }

        private static string QuoteWorkflowSourceValue(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            return value.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '"' }) < 0
                ? value
                : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        class ParsedArgs
        {
            public string CommandType { get; set; }
            public string Action { get; set; }
            public List<string> ExtraArgs { get; } = new List<string>();
            public Dictionary<string, string> Options { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public bool GetBool(string key)
            {
                return Options.TryGetValue(key, out var value) &&
                       (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
            }

            public int GetInt(string key, int defaultValue)
            {
                if (Options.TryGetValue(key, out var value) && int.TryParse(value, out var intValue))
                {
                    return intValue;
                }
                return defaultValue;
            }
        }

        /// <summary>
        /// Handle multi command - execute multiple commands in one call
        /// </summary>
    }
}
