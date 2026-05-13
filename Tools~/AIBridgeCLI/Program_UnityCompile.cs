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
