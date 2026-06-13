using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Runtime.Internal;
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

            if (!IsHelpOnlyRequest(parsed, help))
            {
                CleanupCacheIfDue();
            }

            // Handle multi command (special case - executes multiple commands efficiently)
            if (parsed.CommandType != null && parsed.CommandType.Equals("multi", StringComparison.OrdinalIgnoreCase))
            {
                return HandleMultiCommand(parsed, stdin, timeout, noWait, outputMode);
            }

            // Handle help
            if (help || parsed.CommandType == null)
            {
                if (parsed.CommandType != null && parsed.CommandType.Equals("dialog", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(DialogCommand.GetHelp());
                    return 0;
                }

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

            // Handle dialog command (CLI-only, no Unity communication needed)
            if (parsed.CommandType.Equals("dialog", StringComparison.OrdinalIgnoreCase))
            {
                return DialogCommand.Execute(
                    parsed.Action,
                    parsed.Options.ContainsKey,
                    key => parsed.Options.TryGetValue(key, out var value) ? value : null,
                    outputMode == OutputMode.Pretty);
            }

            // Handle code_index command (CLI-only daemon management and semantic queries)
            if (parsed.CommandType.Equals("code_index", StringComparison.OrdinalIgnoreCase))
            {
                return CodeIndexCommand.Execute(parsed.Action, parsed.Options, timeout, noWait, outputMode);
            }

            // Handle workflow command (CLI-only recipe schema, run artifacts, gates, reports)
            if (parsed.CommandType.Equals("workflow", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowCommand.Execute(parsed.Action, parsed.Options, timeout, outputMode);
            }

            // Handle harness command (CLI-only capability snapshot and readiness status)
            if (parsed.CommandType.Equals("harness", StringComparison.OrdinalIgnoreCase))
            {
                return HarnessCommand.Execute(parsed.Action, parsed.Options, outputMode);
            }

            // Handle exec command (CLI-only structured external process runner)
            if (parsed.CommandType.Equals("exec", StringComparison.OrdinalIgnoreCase))
            {
                return ExecCommand.Execute(parsed.Action, parsed.Options, stdin, timeout, outputMode);
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

            // Handle native Unity test commands
            if (parsed.CommandType.Equals("test", StringComparison.OrdinalIgnoreCase)
                && parsed.Action?.Equals("run", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (noWait)
                {
                    OutputFormatter.PrintError("test run does not support --no-wait. Use test status for polling.");
                    return 1;
                }

                return HandleTestRun(parsed, outputMode);
            }

            if (parsed.CommandType.Equals("test", StringComparison.OrdinalIgnoreCase)
                && parsed.Action?.Equals("status", StringComparison.OrdinalIgnoreCase) == true)
            {
                return HandleTestStatus(parsed, outputMode);
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
            if (parsed.CommandType.Equals("runtime", StringComparison.OrdinalIgnoreCase))
            {
                return HandleRuntimeCommand(parsed, request, timeout, noWait, outputMode);
            }

            var sender = CreateCommandSender(timeout, parsed, request);

            if (noWait)
            {
                var noWaitResult = sender.TrySendCommandNoWait(request);
                TryAttachWorkflowResult(parsed, noWaitResult, noWaitResult.success ? 0 : 1);
                if (!noWaitResult.success)
                {
                    OutputFormatter.PrintResult(noWaitResult, outputMode, includeIdInRaw: false);
                    return 1;
                }

                if (outputMode == OutputMode.Pretty)
                {
                    OutputFormatter.PrintInfo($"Command sent with ID: {noWaitResult.id}");
                }
                else
                {
                    Console.WriteLine(JsonConvert.SerializeObject(new { id = noWaitResult.id, status = "sent" }));
                }
                return 0;
            }

            var result = sender.SendCommand(request);
            TryAttachWorkflowResult(parsed, result, result.success ? 0 : 1);
            OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false);

            return result.success ? 0 : 1;
        }

        static bool IsHelpOnlyRequest(ParsedArgs parsed, bool help)
        {
            if (help || parsed.CommandType == null)
            {
                return true;
            }

            return string.Equals(parsed.Action, "help", StringComparison.OrdinalIgnoreCase)
                || (parsed.Options.ContainsKey("help") && !string.IsNullOrEmpty(parsed.Action));
        }

        static void CleanupCacheIfDue()
        {
            try
            {
                var projectRoot = PathHelper.TryGetUnityProjectRoot();
                if (string.IsNullOrEmpty(projectRoot))
                {
                    return;
                }

                var bridgeDirectory = Path.Combine(projectRoot, ".aibridge");
                var settings = AIBridgeCacheCleanup.LoadSettings(bridgeDirectory);
                AIBridgeCacheCleanup.CleanupIfDue(bridgeDirectory, settings);
            }
            catch
            {
                // CLI cache cleanup is opportunistic and must not change command behavior.
            }
        }

        static CommandSender CreateCommandSender(int timeout, ParsedArgs parsed, CommandRequest request = null)
        {
            var dialogAutoClickPlan = BatchDialogAutoClickPlan.ExtractFromRequest(request);
            if (parsed != null && parsed.Options.TryGetValue("on-dialog", out var onDialog))
            {
                return new CommandSender(timeout, onDialog, dialogAutoClickPlan: dialogAutoClickPlan);
            }

            return new CommandSender(timeout, dialogAutoClickPlan: dialogAutoClickPlan);
        }

    }
}
