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
    }
}
