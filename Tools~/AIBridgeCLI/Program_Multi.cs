using System;
using System.Collections.Generic;
using System.Linq;
using AIBridgeCLI.Commands;
using AIBridgeCLI.Core;
using Newtonsoft.Json;

namespace AIBridgeCLI
{
    partial class Program
    {
        static int HandleMultiCommand(ParsedArgs parsed, bool stdin, int timeout, bool noWait, OutputMode outputMode)
        {
            var commandLines = new List<string>();

            // Collect commands from stdin
            if (stdin)
            {
                var input = Console.In.ReadToEnd();
                var lines = input.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                commandLines.AddRange(lines);
            }

            // Collect commands from --cmd options
            if (parsed.Options.TryGetValue("cmd", out var cmdValue))
            {
                // Split on & while preserving & inside quotes.
                var cmds = SplitMultiCommandArgument(cmdValue);
                commandLines.AddRange(cmds);
            }

            // Collect from extra positional arguments (each arg is a command).
            // Prefer --stdin for long scripts or complex quoting.
            commandLines.AddRange(parsed.ExtraArgs);

            // Show help if no commands
            if (commandLines.Count == 0)
            {
                Console.WriteLine(GetMultiCommandHelp());
                return 0;
            }

            // Filter empty lines; comments are valid batch script lines.
            commandLines = commandLines
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();

            if (commandLines.Count == 0)
            {
                OutputFormatter.PrintError("No valid commands provided");
                return 1;
            }

            // Build batch request
            var multiBuilder = new MultiCommandBuilder();
            CommandRequest request;
            try
            {
                request = multiBuilder.BuildFromCommands(commandLines.ToArray(), parsed.Options);
            }
            catch (Exception ex)
            {
                OutputFormatter.PrintError(ex.Message);
                return 1;
            }

            // Use longer timeout for multi commands
            var actualTimeout = timeout == DEFAULT_TIMEOUT ? MULTI_COMMAND_TIMEOUT : timeout;
            var sender = new CommandSender(actualTimeout);

            if (noWait)
            {
                var commandId = sender.SendCommandNoWait(request);
                if (outputMode == OutputMode.Pretty)
                {
                    OutputFormatter.PrintInfo($"Batch command sent with ID: {commandId} ({commandLines.Count} commands)");
                }
                else
                {
                    Console.WriteLine(JsonConvert.SerializeObject(new { id = commandId, status = "sent", count = commandLines.Count }));
                }
                return 0;
            }

            var result = sender.SendCommand(request);
            OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false);

            return result.success ? 0 : 1;
        }

        static string GetMultiCommandHelp()
        {
            return @"AIBridgeCLI multi - Execute multiple commands through the batch script runner

Each plain CLI line is written as `call <line>` in a temporary batch script.
Native batch lines are allowed and kept as-is: call, delay, log, menu, and # comments.
For complex quoting, JSON values, or long scripts, prefer --stdin.

Usage:
  AIBridgeCLI multi --cmd ""cmd1&cmd2&cmd3"" [options]
  AIBridgeCLI multi --stdin [options]

Examples:
  # Using & separator. Plain CLI commands are automatically prefixed with `call`.
  AIBridgeCLI multi --cmd ""editor log --message 'Step 1'&editor log --message 'Step 2'""

  # From PowerShell stdin (recommended for long scripts or JSON-heavy commands)
  $script = @'
editor log --message ""Hello""
delay 1000
get_logs --logType Error --count 1
'@
  $script | AIBridgeCLI multi --stdin

Options:
  --cmd <commands>   Commands separated by &
  --stdin            Read commands from stdin (one per line)
  --name <name>      Optional temporary script name
  --keep-file        Keep generated temporary script for debugging
  --timeout <ms>     Timeout in milliseconds (default: 30000)
  --raw              Output compact raw JSON (default)
  --pretty           Output human-readable formatted text
  --quiet            Quiet mode
  --no-wait          Don't wait for result
";
        }

        private static List<string> SplitMultiCommandArgument(string commandText)
        {
            var result = new List<string>();
            var current = "";
            var inQuote = false;
            var quoteChar = ' ';

            for (var i = 0; i < commandText.Length; i++)
            {
                var c = commandText[i];
                if (!inQuote && (c == '"' || c == '\''))
                {
                    inQuote = true;
                    quoteChar = c;
                    current += c;
                    continue;
                }

                if (inQuote && c == quoteChar)
                {
                    inQuote = false;
                    quoteChar = ' ';
                    current += c;
                    continue;
                }

                if (!inQuote && c == '&')
                {
                    if (!string.IsNullOrWhiteSpace(current))
                    {
                        result.Add(current.Trim());
                    }

                    current = "";
                    continue;
                }

                current += c;
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                result.Add(current.Trim());
            }

            return result;
        }

        /// <summary>
        /// Handle dotnet build command - CLI-only, does not require Unity
        /// </summary>
    }
}
