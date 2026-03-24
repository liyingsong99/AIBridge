using System;
using System.Collections.Generic;
using System.Linq;
using AIBridgeCLI.Commands;
using AIBridgeCLI.Core;
using Newtonsoft.Json;

namespace AIBridgeCLI
{
    class Program
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
            OutputFormatter.PrintResult(result, outputMode);

            return result.success ? 0 : 1;
        }

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
                // Support & as separator
                var cmds = cmdValue.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
                commandLines.AddRange(cmds);
            }

            // Collect from extra positional arguments (each arg is a command)
            // Note: Due to shell quoting issues, prefer using --cmd with | separator
            commandLines.AddRange(parsed.ExtraArgs);

            // Show help if no commands
            if (commandLines.Count == 0)
            {
                Console.WriteLine(GetMultiCommandHelp());
                return 0;
            }

            // Filter empty lines and comments
            commandLines = commandLines
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
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
            OutputFormatter.PrintResult(result, outputMode);

            return result.success ? 0 : 1;
        }

        static string GetMultiCommandHelp()
        {
            return @"AIBridgeCLI multi - Execute multiple commands efficiently

Usage:
  AIBridgeCLI multi --cmd ""cmd1&cmd2&cmd3"" [options]
  echo ""cmd1\ncmd2\ncmd3"" | AIBridgeCLI multi --stdin [options]

Examples:
  # Using & separator
  AIBridgeCLI multi --cmd ""editor log --message 'Step 1'&editor log --message 'Step 2'""

  # From stdin (one command per line)
  echo ""editor log --message 'Hello'
gameobject create --name Cube --primitiveType Cube"" | AIBridgeCLI multi --stdin

Options:
  --cmd <commands>   Commands separated by &
  --stdin            Read commands from stdin (one per line)
  --timeout <ms>     Timeout in milliseconds (default: 30000)
  --raw              Output compact raw JSON (default)
  --pretty           Output human-readable formatted text
  --quiet            Quiet mode
  --no-wait          Don't wait for result
";
        }

        /// <summary>
        /// Handle dotnet build command - CLI-only, does not require Unity
        /// </summary>
        static int HandleDotnetBuild(ParsedArgs parsed, int timeout, OutputMode outputMode)
        {
            var options = new DotnetBuildOptions
            {
                Solution = parsed.Options.TryGetValue("solution", out var sol) ? sol : null,
                Configuration = parsed.Options.TryGetValue("configuration", out var cfg) ? cfg : "Debug",
                Verbosity = parsed.Options.TryGetValue("verbosity", out var verb) ? verb : "minimal",
                TimeoutMs = parsed.Options.TryGetValue("timeout", out var t) && int.TryParse(t, out var tVal) ? tVal : 300000,
                EnableFilter = !parsed.GetBool("no-filter"),
                HideWarnings = !parsed.GetBool("show-warnings")
            };

            // Parse custom exclude paths
            if (parsed.Options.TryGetValue("exclude", out var excludeStr))
            {
                options.ExcludePaths = excludeStr.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            }

            if (outputMode == OutputMode.Pretty)
            {
                var solutionLabel = string.IsNullOrWhiteSpace(options.Solution) ? "auto-detected solution" : options.Solution;
                OutputFormatter.PrintInfo($"Building {solutionLabel} (configuration: {options.Configuration})...");
            }

            var result = DotnetBuildCommand.Execute(options);

            if (outputMode == OutputMode.Raw || outputMode == OutputMode.Quiet)
            {
                // JSON output for AI consumption
                var errorsList = new List<object>();
                foreach (var e in result.Errors)
                {
                    errorsList.Add(new
                    {
                        file = e.File,
                        line = e.Line,
                        column = e.Column,
                        code = e.Code,
                        message = e.Message
                    });
                }

                var warningsList = new List<object>();
                if (result.Warnings.Count <= 20)
                {
                    foreach (var w in result.Warnings)
                    {
                        warningsList.Add(new
                        {
                            file = w.File,
                            line = w.Line,
                            column = w.Column,
                            code = w.Code,
                            message = w.Message
                        });
                    }
                }

                var jsonResult = new
                {
                    success = result.Success,
                    exitCode = result.ExitCode,
                    duration = Math.Round(result.Duration, 2),
                    errorCount = result.Errors.Count,
                    warningCount = result.Warnings.Count,
                    totalErrorCount = result.TotalErrorCount,
                    totalWarningCount = result.TotalWarningCount,
                    filteredErrorCount = result.FilteredErrorCount,
                    filteredWarningCount = result.FilteredWarningCount,
                    errors = errorsList,
                    warnings = warningsList,
                    error = result.Error
                };
                Console.WriteLine(JsonConvert.SerializeObject(jsonResult, Formatting.None));
            }
            else
            {
                // Pretty output for human consumption
                if (result.Success)
                {
                    OutputFormatter.PrintSuccess($"Build succeeded in {result.Duration:F1}s");
                }
                else if (!string.IsNullOrEmpty(result.Error))
                {
                    OutputFormatter.PrintError(result.Error);
                }
                else
                {
                    OutputFormatter.PrintError($"Build failed with {result.Errors.Count} error(s)");
                }

                if (result.FilteredErrorCount > 0 || result.FilteredWarningCount > 0)
                {
                    OutputFormatter.PrintInfo($"Filtered: {result.FilteredErrorCount} errors, {result.FilteredWarningCount} warnings (third-party/test code)");
                }

                // Show errors
                foreach (var error in result.Errors)
                {
                    Console.WriteLine($"  {error.File}({error.Line},{error.Column}): error {error.Code}: {error.Message}");
                }

                // Show warnings (limited)
                var warningsToShow = Math.Min(result.Warnings.Count, 10);
                for (int i = 0; i < warningsToShow; i++)
                {
                    var warning = result.Warnings[i];
                    Console.WriteLine($"  {warning.File}({warning.Line},{warning.Column}): warning {warning.Code}: {warning.Message}");
                }

                if (result.Warnings.Count > warningsToShow)
                {
                    Console.WriteLine($"  ... and {result.Warnings.Count - warningsToShow} more warnings");
                }
            }

            return result.Success ? 0 : 1;
        }

        /// <summary>
        /// Handle Unity internal compilation - requires Unity Editor running.
        /// Sends compile start command and polls status until compilation completes.
        /// </summary>
        static int HandleUnityCompile(ParsedArgs parsed, OutputMode outputMode)
        {
            var compileTimeout = parsed.Options.TryGetValue("timeout", out var t) && int.TryParse(t, out var tVal) ? tVal : 120000;
            var pollInterval = parsed.Options.TryGetValue("poll-interval", out var p) && int.TryParse(p, out var pVal) ? pVal : 500;
            var commandTimeout = parsed.Options.TryGetValue("transport-timeout", out var ct) && int.TryParse(ct, out var ctVal)
                ? ctVal
                : GetDefaultUnityCompileTransportTimeout(compileTimeout, pollInterval);
            var startedByInvocation = false;
            var attachedToExistingCompilation = false;
            var startCommunicationTimedOut = false;
            var observedCompilationActivity = false;
            string lastCommunicationError = null;

            if (outputMode == OutputMode.Pretty)
            {
                OutputFormatter.PrintInfo("Starting Unity compilation...");
            }

            var sender = new CommandSender(commandTimeout);
            var startTime = DateTime.Now;

            // Step 1: Send compile start command
            var startRequest = new CommandRequest
            {
                id = PathHelper.GenerateCommandId(),
                type = "compile",
                @params = new Dictionary<string, object> { { "action", "start" } }
            };

            var startResult = sender.SendCommand(startRequest);
            if (!startResult.success)
            {
                if (!IsTransportTimeoutError(startResult.error))
                {
                    OutputUnityCompileResult(outputMode, false, "failed", 0, 0, 0,
                        new List<object>(), new List<object>(),
                        startResult.error ?? "Failed to start compilation. Make sure Unity Editor is running.",
                        startedByInvocation, attachedToExistingCompilation, false);
                    return 1;
                }

                startCommunicationTimedOut = true;
                lastCommunicationError = startResult.error;

                if (outputMode == OutputMode.Pretty)
                {
                    OutputFormatter.PrintInfo("Unity did not acknowledge the compile request within the transport timeout. Waiting for compilation status...");
                }
            }

            if (startResult.success)
            {
                // Check if already compiling or just started
                var data = startResult.data as Newtonsoft.Json.Linq.JObject;
                attachedToExistingCompilation = (bool?)data?["alreadyCompiling"] ?? false;
                startedByInvocation = (bool?)data?["compilationStarted"] ?? false;
                observedCompilationActivity = attachedToExistingCompilation || startedByInvocation;

                if (!startedByInvocation && !attachedToExistingCompilation)
                {
                    // No compilation needed (no code changes)
                    OutputUnityCompileResult(outputMode, true, "idle", 0, 0, 0,
                        new List<object>(), new List<object>(), null,
                        startedByInvocation, attachedToExistingCompilation, true);
                    return 0;
                }

                if (outputMode == OutputMode.Pretty && attachedToExistingCompilation)
                {
                    OutputFormatter.PrintInfo("Unity is already compiling. Attaching to the current compilation and waiting for the final result...");
                }
            }

            // Step 2: Poll for compilation status
            while ((DateTime.Now - startTime).TotalMilliseconds < compileTimeout)
            {
                System.Threading.Thread.Sleep(pollInterval);

                var statusRequest = new CommandRequest
                {
                    id = PathHelper.GenerateCommandId(),
                    type = "compile",
                    @params = new Dictionary<string, object>
                    {
                        { "action", "status" },
                        { "includeDetails", true }
                    }
                };

                var statusResult = sender.SendCommand(statusRequest);
                if (!statusResult.success)
                {
                    // Communication error, but compilation might still be running
                    lastCommunicationError = statusResult.error;
                    continue;
                }

                var statusData = statusResult.data as Newtonsoft.Json.Linq.JObject;
                var status = (string)statusData?["status"] ?? "unknown";
                var isCompiling = (bool?)statusData?["isCompiling"] ?? false;

                if (isCompiling || status == "compiling")
                {
                    // Still compiling, continue polling
                    observedCompilationActivity = true;
                    continue;
                }

                if (!observedCompilationActivity && (status == "idle" || status == "unknown"))
                {
                    // Start request may have timed out before Unity could respond.
                    // Keep polling until we observe an actual compilation state or hit the outer timeout.
                    continue;
                }

                // Compilation finished
                // Unity's duration may be 0 due to main thread blocking during compilation
                // Use CLI-side timing as fallback
                var unityDuration = (double?)statusData?["duration"] ?? 0;
                var cliDuration = (DateTime.Now - startTime).TotalSeconds;
                var duration = unityDuration > 0.1 ? unityDuration : cliDuration;
                var errorCount = (int?)statusData?["errorCount"] ?? 0;
                var warningCount = (int?)statusData?["warningCount"] ?? 0;
                var errorsArray = statusData?["errors"] as Newtonsoft.Json.Linq.JArray;
                var warningsArray = statusData?["warnings"] as Newtonsoft.Json.Linq.JArray;

                var errors = new List<object>();
                var warnings = new List<object>();

                if (errorsArray != null)
                {
                    foreach (var err in errorsArray)
                    {
                        errors.Add(new
                        {
                            file = (string)err["file"],
                            line = (int?)err["line"] ?? 0,
                            column = (int?)err["column"] ?? 0,
                            code = (string)err["code"],
                            message = (string)err["message"]
                        });
                    }
                }

                if (warningsArray != null && warningsArray.Count <= 20)
                {
                    foreach (var warn in warningsArray)
                    {
                        warnings.Add(new
                        {
                            file = (string)warn["file"],
                            line = (int?)warn["line"] ?? 0,
                            column = (int?)warn["column"] ?? 0,
                            code = (string)warn["code"],
                            message = (string)warn["message"]
                        });
                    }
                }

                var success = status == "success" || (observedCompilationActivity && status == "idle" && errorCount == 0);
                var resultError = success
                    ? null
                    : (startCommunicationTimedOut && string.IsNullOrEmpty(lastCommunicationError)
                        ? "Unity compile request was not acknowledged immediately, but the final compilation result was retrieved."
                        : null);

                OutputUnityCompileResult(outputMode, success, status, duration, errorCount, warningCount, errors, warnings, resultError,
                    startedByInvocation, attachedToExistingCompilation, true);
                return success ? 0 : 1;
            }

            // Timeout
            var timeoutReason = startCommunicationTimedOut
                ? $"Compilation status could not be confirmed within {compileTimeout}ms after the initial compile request timed out. Unity may still be compiling. Last communication error: {lastCommunicationError ?? "unknown"}"
                : $"Compilation timed out after {compileTimeout}ms. Unity may still be compiling.";

            OutputUnityCompileResult(outputMode, false, "timeout",
                (DateTime.Now - startTime).TotalSeconds, 0, 0,
                new List<object>(), new List<object>(),
                timeoutReason,
                startedByInvocation, attachedToExistingCompilation, false);
            return 1;
        }

        static bool IsTransportTimeoutError(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return false;
            }

            return error.IndexOf("Timeout waiting for result", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static int GetDefaultUnityCompileTransportTimeout(int compileTimeout, int pollInterval)
        {
            var normalizedPollInterval = Math.Max(pollInterval, 100);
            var preferredTimeout = Math.Min(30000, compileTimeout);
            return Math.Max(normalizedPollInterval, preferredTimeout);
        }

        /// <summary>
        /// Output Unity compile result in the appropriate format
        /// </summary>
        static void OutputUnityCompileResult(OutputMode outputMode, bool success, string status,
            double duration, int errorCount, int warningCount,
            List<object> errors, List<object> warnings, string error,
            bool startedByInvocation = false, bool attachedToExistingCompilation = false, bool statusConfirmed = true)
        {
            if (outputMode == OutputMode.Raw || outputMode == OutputMode.Quiet)
            {
                var jsonResult = new
                {
                    success = success,
                    status = status,
                    duration = Math.Round(duration, 2),
                    errorCount = errorCount,
                    warningCount = warningCount,
                    startedByInvocation = startedByInvocation,
                    attachedToExistingCompilation = attachedToExistingCompilation,
                    statusConfirmed = statusConfirmed,
                    errors = errors,
                    warnings = warnings,
                    error = error
                };
                Console.WriteLine(JsonConvert.SerializeObject(jsonResult, Formatting.None));
            }
            else
            {
                if (success)
                {
                    OutputFormatter.PrintSuccess($"Unity compilation succeeded in {duration:F1}s");

                    if (attachedToExistingCompilation)
                    {
                        OutputFormatter.PrintInfo("Result came from an existing Unity compilation already in progress.");
                    }
                }
                else if (!string.IsNullOrEmpty(error))
                {
                    OutputFormatter.PrintError(error);
                }
                else
                {
                    OutputFormatter.PrintError($"Unity compilation failed with {errorCount} error(s)");
                }

                // Show errors
                foreach (var err in errors)
                {
                    var errObj = err as dynamic;
                    Console.WriteLine($"  {errObj.file}({errObj.line},{errObj.column}): error {errObj.code}: {errObj.message}");
                }

                // Show warnings (limited)
                var warningsToShow = Math.Min(warnings.Count, 10);
                for (int i = 0; i < warningsToShow; i++)
                {
                    var warn = warnings[i] as dynamic;
                    Console.WriteLine($"  {warn.file}({warn.line},{warn.column}): warning {warn.code}: {warn.message}");
                }

                if (warnings.Count > warningsToShow)
                {
                    Console.WriteLine($"  ... and {warnings.Count - warningsToShow} more warnings");
                }
            }
        }
    }
}
