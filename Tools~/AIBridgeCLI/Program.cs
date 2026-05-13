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
        private const int DEFAULT_TIMEOUT = 5000;
        private const int MULTI_COMMAND_TIMEOUT = 30000;

        static int Main(string[] args)
        {
            // Set console output encoding to UTF-8
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;

            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                OutputFormatter.PrintError(ex.Message);
                return 1;
            }
        }

        static int Run(string[] args)
        {
            // Initialize command registry
            CommandRegistry.Initialize();

            // Parse arguments
            var parsed = ParseArguments(args);

            // Handle global options
            var timeout = parsed.GetInt("timeout", DEFAULT_TIMEOUT);
            var noWait = parsed.GetBool("no-wait");
            var raw = parsed.GetBool("raw");
            var pretty = parsed.GetBool("pretty");
            var quiet = parsed.GetBool("quiet");
            var help = parsed.GetBool("help");
            var stdin = parsed.GetBool("stdin");

            var outputMode = quiet ? OutputMode.Quiet : (pretty ? OutputMode.Pretty : OutputMode.Raw);

            // Handle multi command (special case - executes multiple commands efficiently)
            if (parsed.CommandType != null && parsed.CommandType.Equals("multi", StringComparison.OrdinalIgnoreCase))
            {
                return HandleMultiCommand(parsed, stdin, timeout, noWait, outputMode);
            }

            // Handle help
            if (help || parsed.CommandType == null)
            {
                if (parsed.CommandType != null && CommandRegistry.TryGet(parsed.CommandType, out var cmdBuilder))
                {
                    Console.WriteLine(cmdBuilder.GetHelp(parsed.Action));
                }
                else
                {
                    Console.WriteLine(CommandRegistry.GetGlobalHelp());
                }
                return 0;
            }

            // Handle focus command (CLI-only, no Unity communication needed)
            if (parsed.CommandType.Equals("focus", StringComparison.OrdinalIgnoreCase))
            {
                var focusResult = FocusCommand.Execute();
                if (outputMode == OutputMode.Raw || outputMode == OutputMode.Quiet)
                {
                    Console.WriteLine(JsonConvert.SerializeObject(focusResult));
                }
                else
                {
                    if (focusResult.Success)
                    {
                        OutputFormatter.PrintSuccess($"Unity Editor focused: {focusResult.WindowTitle} (PID: {focusResult.ProcessId})");
                    }
                    else
                    {
                        OutputFormatter.PrintError(focusResult.Error);
                    }
                }
                return focusResult.Success ? 0 : 1;
            }

            // Handle compile dotnet command (CLI-only, no Unity communication needed)
            if (parsed.CommandType.Equals("compile", StringComparison.OrdinalIgnoreCase)
                && parsed.Action?.Equals("dotnet", StringComparison.OrdinalIgnoreCase) == true)
            {
                return HandleDotnetBuild(parsed, timeout, outputMode);
            }

            // Handle compile unity command (requires Unity Editor running)
            if (parsed.CommandType.Equals("compile", StringComparison.OrdinalIgnoreCase)
                && parsed.Action?.Equals("unity", StringComparison.OrdinalIgnoreCase) == true)
            {
                return HandleUnityCompile(parsed, outputMode);
            }

            // Get command builder
            if (!CommandRegistry.TryGet(parsed.CommandType, out var builder))
            {
                OutputFormatter.PrintError($"Unknown command type: {parsed.CommandType}");
                Console.WriteLine();
                Console.WriteLine("Available commands:");
                foreach (var type in CommandRegistry.GetTypes())
                {
                    Console.WriteLine($"  {type}");
                }
                return 1;
            }

            // Handle action help
            if (parsed.Action == "help" || (parsed.Options.ContainsKey("help") && !string.IsNullOrEmpty(parsed.Action)))
            {
                Console.WriteLine(builder.GetHelp(parsed.Action == "help" ? null : parsed.Action));
                return 0;
            }

            // Handle stdin input
            if (stdin)
            {
                var stdinJson = Console.In.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(stdinJson))
                {
                    try
                    {
                        var stdinParams = JsonConvert.DeserializeObject<Dictionary<string, string>>(stdinJson);
                        foreach (var kvp in stdinParams)
                        {
                            if (!parsed.Options.ContainsKey(kvp.Key))
                            {
                                parsed.Options[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    catch
                    {
                        // If not a dictionary, treat as json parameter
                        parsed.Options["json"] = stdinJson;
                    }
                }
            }

            // Build command request
            CommandRequest request;
            try
            {
                request = builder.Build(parsed.Action, parsed.Options);
            }
            catch (ArgumentException ex)
            {
                OutputFormatter.PrintError(ex.Message);
                Console.WriteLine();
                Console.WriteLine(builder.GetHelp(parsed.Action));
                return 1;
            }

            // Send command
            var sender = new CommandSender(timeout);

            if (noWait)
            {
                var commandId = sender.SendCommandNoWait(request);
                if (outputMode == OutputMode.Pretty)
                {
                    OutputFormatter.PrintInfo($"Command sent with ID: {commandId}");
                }
                else
                {
                    Console.WriteLine(JsonConvert.SerializeObject(new { id = commandId, status = "sent" }));
                }
                return 0;
            }

            var result = sender.SendCommand(request);
            OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false);

            return result.success ? 0 : 1;
        }

    }
}
