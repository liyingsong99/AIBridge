using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Compilation command: trigger Unity compilation, query status, or run dotnet build.
    /// Supports async compilation with status polling.
    /// </summary>
    public class CompileCommand : ICommand
    {
        public string Type => "compile";
        public bool RequiresRefresh => false;

        // Regex to parse MSBuild error format: path(line,column): error CS0001: message
        private static readonly Regex MsBuildErrorRegex = new Regex(
            @"^\s*(?<file>.+?)\((?<line>\d+),(?<column>\d+)\):\s*(?<type>error|warning)\s+(?<code>\w+):\s*(?<message>.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "start");

            try
            {
                switch (action.ToLower())
                {
                    case "start":
                        return StartCompilation(request);
                    case "status":
                        return GetCompilationStatus(request);
                    case "dotnet":
                        return RunDotnetBuild(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: start, status, dotnet");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        /// <summary>
        /// Start Unity script compilation asynchronously
        /// </summary>
        private CommandResult StartCompilation(CommandRequest request)
        {
            if (EditorApplication.isCompiling)
            {
                return CommandResult.Success(request.id, new
                {
                    action = "start",
                    compilationStarted = false,
                    alreadyCompiling = true,
                    message = "Compilation is already in progress. Use action=status to poll for results."
                });
            }

            // Reset tracker and start fresh
            CompilationTracker.Reset();
            CompilationTracker.StartTracking();

            // Request compilation
            CompilationPipeline.RequestScriptCompilation();

            return CommandResult.Success(request.id, new
            {
                action = "start",
                compilationStarted = true,
                message = "Compilation started. Use action=status to poll for results."
            });
        }

        /// <summary>
        /// Get current compilation status and results
        /// </summary>
        private CommandResult GetCompilationStatus(CommandRequest request)
        {
            var result = CompilationTracker.GetResult();
            var isCompiling = EditorApplication.isCompiling;

            // Convert status to string
            string statusStr;
            switch (result.status)
            {
                case CompilationTracker.CompilationStatus.Compiling:
                    statusStr = "compiling";
                    break;
                case CompilationTracker.CompilationStatus.Success:
                    statusStr = "success";
                    break;
                case CompilationTracker.CompilationStatus.Failed:
                    statusStr = "failed";
                    break;
                default:
                    statusStr = "idle";
                    break;
            }

            // If currently compiling, return minimal status
            if (isCompiling)
            {
                return CommandResult.Success(request.id, new
                {
                    action = "status",
                    status = "compiling",
                    isCompiling = true
                });
            }

            // Return full result
            var includeDetails = request.GetParam("includeDetails", true);

            if (includeDetails)
            {
                return CommandResult.Success(request.id, new
                {
                    action = "status",
                    status = statusStr,
                    isCompiling = false,
                    errorCount = result.errorCount,
                    warningCount = result.warningCount,
                    duration = result.durationSeconds,
                    errors = ConvertErrors(result.errors),
                    warnings = ConvertErrors(result.warnings)
                });
            }
            else
            {
                return CommandResult.Success(request.id, new
                {
                    action = "status",
                    status = statusStr,
                    isCompiling = false,
                    errorCount = result.errorCount,
                    warningCount = result.warningCount,
                    duration = result.durationSeconds
                });
            }
        }

        /// <summary>
        /// Run dotnet build on specified solution
        /// </summary>
        private CommandResult RunDotnetBuild(CommandRequest request)
        {
            var solution = request.GetParam<string>("solution", null);
            var configuration = request.GetParam("configuration", "Debug");
            var verbosity = request.GetParam("verbosity", "minimal");
            var timeoutMs = request.GetParam("timeout", 120000);

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var solutionResolution = ResolveSolutionPath(projectRoot, solution);
            if (!solutionResolution.success)
            {
                return CommandResult.Failure(request.id, solutionResolution.error);
            }

            var solutionPath = solutionResolution.solutionPath;

            var stopwatch = Stopwatch.StartNew();
            var errors = new List<object>();
            var warnings = new List<object>();
            var outputBuilder = new StringBuilder();

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{solutionPath}\" --configuration {configuration} --verbosity {verbosity} --no-incremental",
                    WorkingDirectory = projectRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (var process = new Process())
                {
                    process.StartInfo = startInfo;

                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            outputBuilder.AppendLine(e.Data);
                            ParseMsBuildOutput(e.Data, errors, warnings);
                        }
                    };

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            outputBuilder.AppendLine(e.Data);
                            ParseMsBuildOutput(e.Data, errors, warnings);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    var completed = process.WaitForExit(timeoutMs);
                    stopwatch.Stop();

                    if (!completed)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // Ignore kill errors
                        }

                        return CommandResult.Failure(request.id, $"Build timed out after {timeoutMs}ms");
                    }

                    var exitCode = process.ExitCode;
                    var success = exitCode == 0;

                    return CommandResult.Success(request.id, new
                    {
                        action = "dotnet",
                        solution = solutionPath,
                        configuration = configuration,
                        exitCode = exitCode,
                        success = success,
                        errorCount = errors.Count,
                        warningCount = warnings.Count,
                        duration = stopwatch.Elapsed.TotalSeconds,
                        errors = errors,
                        warnings = warnings,
                        output = outputBuilder.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return CommandResult.Failure(request.id, $"Failed to run dotnet build: {ex.Message}");
            }
        }

        private (bool success, string solutionPath, string error) ResolveSolutionPath(string projectRoot, string solution)
        {
            if (!string.IsNullOrWhiteSpace(solution))
            {
                var explicitPath = Path.IsPathRooted(solution)
                    ? Path.GetFullPath(solution)
                    : Path.GetFullPath(Path.Combine(projectRoot, solution));

                if (!File.Exists(explicitPath))
                {
                    return (false, null, $"Specified solution file not found: {explicitPath}");
                }

                return (true, explicitPath, null);
            }

            var candidates = Directory
                .EnumerateFiles(projectRoot, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(projectRoot, "*.slnx", SearchOption.TopDirectoryOnly))
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
            {
                return (false, null, "No solution file was found in project root. Pass --solution explicitly or regenerate project files from Unity.");
            }

            if (candidates.Count == 1)
            {
                return (true, candidates[0], null);
            }

            var projectName = new DirectoryInfo(projectRoot).Name;
            var projectNameMatches = candidates
                .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), projectName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (projectNameMatches.Count == 1)
            {
                return (true, projectNameMatches[0], null);
            }

            var candidateList = string.Join(", ", candidates.Select(Path.GetFileName));
            return (false, null, $"Multiple solution files were found in project root: {candidateList}. Pass --solution explicitly.");
        }

        /// <summary>
        /// Parse MSBuild output line for errors/warnings
        /// </summary>
        private void ParseMsBuildOutput(string line, List<object> errors, List<object> warnings)
        {
            var match = MsBuildErrorRegex.Match(line);
            if (match.Success)
            {
                var errorInfo = new
                {
                    file = match.Groups["file"].Value,
                    line = int.Parse(match.Groups["line"].Value),
                    column = int.Parse(match.Groups["column"].Value),
                    code = match.Groups["code"].Value,
                    message = match.Groups["message"].Value
                };

                if (match.Groups["type"].Value.Equals("error", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(errorInfo);
                }
                else
                {
                    warnings.Add(errorInfo);
                }
            }
        }

        /// <summary>
        /// Convert CompilerError list to anonymous objects for JSON serialization
        /// </summary>
        private List<object> ConvertErrors(List<CompilationTracker.CompilerError> errorList)
        {
            var result = new List<object>();
            if (errorList == null)
            {
                return result;
            }

            foreach (var error in errorList)
            {
                result.Add(new
                {
                    file = error.file,
                    line = error.line,
                    column = error.column,
                    message = error.message,
                    code = error.errorCode,
                    assembly = error.assemblyName
                });
            }

            return result;
        }
    }
}
