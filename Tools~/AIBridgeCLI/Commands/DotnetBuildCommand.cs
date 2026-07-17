using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AIBridgeCLI.Core;
using Newtonsoft.Json;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Dotnet build command - CLI-only command that executes dotnet build directly.
    /// Does not require Unity Editor to be running.
    /// Supports intelligent error filtering to show only relevant errors.
    /// Filter configuration is loaded from compile-filter.json (hot-reload on each execution).
    /// </summary>
    public static class DotnetBuildCommand
    {
        internal const int MaxRawOutputChars = 200000;
        internal const int MaxDiagnosticLineChars = 32768;
        internal const int MaxRetainedDiagnosticsPerKind = 1000;
        private const int ProcessExitWaitMs = 5000;

        // Regex to parse MSBuild error format: path(line,column): error CS0001: message
        private static readonly Regex MsBuildErrorRegex = new Regex(
            @"^\s*(?<file>.+?)\((?<line>\d+),(?<column>\d+)\):\s*(?<type>error|warning)\s+(?<code>\w+):\s*(?<message>.+?)(?:\s*\[(?<project>.+?)\])?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private const string CONFIG_FILE_NAME = "compile-filter.json";

        /// <summary>
        /// Load filter configuration from compile-filter.json.
        /// Returns default config if file doesn't exist or is invalid.
        /// </summary>
        private static FilterConfig LoadFilterConfig()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CONFIG_FILE_NAME);

            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath, Encoding.UTF8);
                    var config = JsonConvert.DeserializeObject<FilterConfig>(json);
                    if (config != null && config.ExcludePaths != null && config.ExcludeCodes != null)
                    {
                        return config;
                    }
                }
                catch
                {
                    // If config file is invalid, use defaults
                }
            }

            return FilterConfig.GetDefault();
        }

        /// <summary>
        /// Execute dotnet build and return filtered results
        /// </summary>
        public static DotnetBuildResult Execute(DotnetBuildOptions options)
        {
            var result = new DotnetBuildResult();

            try
            {
                var projectRoot = FindUnityProjectRoot();
                if (projectRoot == null)
                {
                    result.Success = false;
                    result.Error = "Could not determine Unity project root for dotnet build.";
                    return result;
                }

                var solutionResolution = ResolveSolutionPath(projectRoot, options.Solution);
                if (!solutionResolution.Success)
                {
                    result.Success = false;
                    result.Error = solutionResolution.Error;
                    return result;
                }

                var solutionPath = solutionResolution.SolutionPath;

                result.SolutionPath = solutionPath;
                result.ProjectRoot = projectRoot;

                var filterConfig = LoadFilterConfig();
                var excludePaths = options.ExcludePaths ?? filterConfig.ExcludePaths.ToArray();
                var excludeCodes = options.ExcludeCodes ?? filterConfig.ExcludeCodes.ToArray();
                var hideWarnings = options.EnableFilter
                    ? options.HideWarnings && filterConfig.HideWarnings
                    : options.HideWarnings;
                var diagnostics = new DotnetBuildDiagnosticCollector(
                    projectRoot,
                    options.EnableFilter,
                    hideWarnings,
                    excludePaths,
                    excludeCodes);
                var stopwatch = Stopwatch.StartNew();

                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{solutionPath}\" --configuration {options.Configuration} --verbosity {options.Verbosity} --no-incremental",
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
                    process.Start();
                    var stdoutTask = BoundedProcessOutputReader.ReadAsync(
                        process.StandardOutput,
                        MaxRawOutputChars,
                        MaxDiagnosticLineChars,
                        diagnostics.AddLine);
                    var stderrTask = BoundedProcessOutputReader.ReadAsync(
                        process.StandardError,
                        MaxRawOutputChars,
                        MaxDiagnosticLineChars,
                        diagnostics.AddLine);

                    var completed = process.WaitForExit(options.TimeoutMs);
                    if (!completed)
                    {
                        TryKillProcessTree(process);
                        TryWaitForExit(process, ProcessExitWaitMs);
                    }
                    else
                    {
                        process.WaitForExit();
                    }

                    var stdout = stdoutTask.GetAwaiter().GetResult();
                    var stderr = stderrTask.GetAwaiter().GetResult();
                    stopwatch.Stop();

                    result.ExitCode = process.HasExited ? process.ExitCode : -1;
                    result.Duration = stopwatch.Elapsed.TotalSeconds;
                    result.RawOutput = CombineCapturedOutput(stdout.Text, stderr.Text, MaxRawOutputChars, out var combinedTruncated);
                    result.RawOutputCharsRead = stdout.CharsRead + stderr.CharsRead;
                    result.RawOutputTruncated = stdout.Truncated || stderr.Truncated || combinedTruncated;
                    result.TruncatedDiagnosticLineCount = stdout.TruncatedLineCount + stderr.TruncatedLineCount;
                    diagnostics.ApplyTo(result);

                    if (!completed)
                    {
                        result.Success = false;
                        result.Error = $"Build timed out after {options.TimeoutMs}ms";
                    }
                    else
                    {
                        result.Success = diagnostics.UnfilteredErrorCount == 0;
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = $"Failed to run dotnet build: {ex.Message}";
            }

            return result;
        }

        private static void TryKillProcessTree(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
                // Timeout cleanup is best-effort; the timeout remains visible in the result.
            }
        }

        private static void TryWaitForExit(Process process, int timeoutMs)
        {
            try
            {
                process?.WaitForExit(timeoutMs);
            }
            catch
            {
                // Timeout cleanup is best-effort; the timeout remains visible in the result.
            }
        }

        private static string CombineCapturedOutput(string stdout, string stderr, int maxChars, out bool truncated)
        {
            var combined = (stdout ?? string.Empty) + (stderr ?? string.Empty);
            if (combined.Length <= maxChars)
            {
                truncated = false;
                return combined;
            }

            truncated = true;
            return combined.Substring(0, maxChars);
        }

        /// <summary>
        /// Find Unity project root from environment, current directory, or CLI location.
        /// </summary>
        private static string FindUnityProjectRoot()
        {
            var envProjectRoot = Environment.GetEnvironmentVariable("UNITY_PROJECT_ROOT");
            if (!string.IsNullOrWhiteSpace(envProjectRoot))
            {
                var fullPath = Path.GetFullPath(envProjectRoot);
                if (IsUnityProjectRoot(fullPath))
                {
                    return fullPath;
                }
            }

            var cwdProjectRoot = SearchUpwardsForUnityProjectRoot(Directory.GetCurrentDirectory());
            if (cwdProjectRoot != null)
            {
                return cwdProjectRoot;
            }

            var exeProjectRoot = SearchUpwardsForUnityProjectRoot(AppDomain.CurrentDomain.BaseDirectory);
            if (exeProjectRoot != null)
            {
                return exeProjectRoot;
            }

            return null;
        }

        /// <summary>
        /// Resolve solution path from explicit input or project-root discovery.
        /// </summary>
        private static SolutionResolution ResolveSolutionPath(string projectRoot, string solution)
        {
            if (!string.IsNullOrWhiteSpace(solution))
            {
                var explicitPath = Path.IsPathRooted(solution)
                    ? Path.GetFullPath(solution)
                    : Path.GetFullPath(Path.Combine(projectRoot, solution));

                if (!File.Exists(explicitPath))
                {
                    return SolutionResolution.Failure($"Specified solution file not found: {explicitPath}");
                }

                return SolutionResolution.SuccessResult(explicitPath);
            }

            var candidates = FindSolutionCandidates(projectRoot);
            if (candidates.Count == 0)
            {
                return SolutionResolution.Failure("No solution file was found in project root. Pass --solution explicitly or regenerate project files from Unity.");
            }

            if (candidates.Count == 1)
            {
                return SolutionResolution.SuccessResult(candidates[0]);
            }

            var projectName = new DirectoryInfo(projectRoot).Name;
            var projectNameMatches = candidates
                .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), projectName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (projectNameMatches.Count == 1)
            {
                return SolutionResolution.SuccessResult(projectNameMatches[0]);
            }

            var candidateList = string.Join(", ", candidates.Select(Path.GetFileName));
            return SolutionResolution.Failure($"Multiple solution files were found in project root: {candidateList}. Pass --solution explicitly.");
        }

        /// <summary>
        /// Find solution candidates in the Unity project root only.
        /// </summary>
        private static List<string> FindSolutionCandidates(string projectRoot)
        {
            return Directory
                .EnumerateFiles(projectRoot, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(projectRoot, "*.slnx", SearchOption.TopDirectoryOnly))
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Search upward from a starting directory for a Unity project root.
        /// </summary>
        private static string SearchUpwardsForUnityProjectRoot(string startPath)
        {
            if (string.IsNullOrWhiteSpace(startPath))
            {
                return null;
            }

            var currentDir = new DirectoryInfo(Path.GetFullPath(startPath));

            while (currentDir != null)
            {
                if (IsUnityProjectRoot(currentDir.FullName))
                {
                    return currentDir.FullName;
                }

                currentDir = currentDir.Parent;
            }

            return null;
        }

        /// <summary>
        /// Check if the directory looks like a Unity project root.
        /// </summary>
        private static bool IsUnityProjectRoot(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            return Directory.Exists(Path.Combine(directory, "Assets")) &&
                   File.Exists(Path.Combine(directory, "ProjectSettings", "ProjectSettings.asset"));
        }

        private class SolutionResolution
        {
            public bool Success { get; private set; }
            public string SolutionPath { get; private set; }
            public string Error { get; private set; }

            public static SolutionResolution SuccessResult(string solutionPath)
            {
                return new SolutionResolution
                {
                    Success = true,
                    SolutionPath = solutionPath
                };
            }

            public static SolutionResolution Failure(string error)
            {
                return new SolutionResolution
                {
                    Success = false,
                    Error = error
                };
            }
        }

        private static bool TryParseMsBuildOutput(
            string line,
            string projectRoot,
            out BuildError diagnostic,
            out bool isError)
        {
            diagnostic = null;
            isError = false;
            var match = MsBuildErrorRegex.Match(line);
            if (!match.Success)
            {
                return false;
            }

            var filePath = match.Groups["file"].Value;
            if (projectRoot != null && filePath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                filePath = filePath.Substring(projectRoot.Length).TrimStart('\\', '/');
            }

            diagnostic = new BuildError
            {
                File = filePath,
                Line = int.Parse(match.Groups["line"].Value),
                Column = int.Parse(match.Groups["column"].Value),
                Code = match.Groups["code"].Value,
                Message = match.Groups["message"].Value.Trim(),
                Project = match.Groups["project"].Success ? match.Groups["project"].Value : null,
                RawLine = line
            };
            isError = match.Groups["type"].Value.Equals("error", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        private static bool ShouldExclude(BuildError diagnostic, string[] excludePaths, string[] excludeCodes)
        {
            foreach (var excludePath in excludePaths ?? Array.Empty<string>())
            {
                if (diagnostic.File.IndexOf(excludePath, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (diagnostic.Project != null
                    && diagnostic.Project.IndexOf(excludePath, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (diagnostic.RawLine != null
                    && diagnostic.RawLine.IndexOf(excludePath, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            foreach (var excludeCode in excludeCodes ?? Array.Empty<string>())
            {
                if (diagnostic.Code.Equals(excludeCode, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal sealed class DotnetBuildDiagnosticCollector
        {
            private readonly object _sync = new object();
            private readonly string _projectRoot;
            private readonly bool _enableFilter;
            private readonly bool _hideWarnings;
            private readonly string[] _excludePaths;
            private readonly string[] _excludeCodes;
            private readonly List<BuildError> _errors = new List<BuildError>();
            private readonly List<BuildError> _warnings = new List<BuildError>();
            private int _totalErrorCount;
            private int _totalWarningCount;
            private int _filteredErrorCount;
            private int _filteredWarningCount;
            private int _omittedErrorCount;
            private int _omittedWarningCount;

            public DotnetBuildDiagnosticCollector(
                string projectRoot,
                bool enableFilter,
                bool hideWarnings,
                string[] excludePaths,
                string[] excludeCodes)
            {
                _projectRoot = projectRoot;
                _enableFilter = enableFilter;
                _hideWarnings = hideWarnings;
                _excludePaths = excludePaths ?? Array.Empty<string>();
                _excludeCodes = excludeCodes ?? Array.Empty<string>();
            }

            public int UnfilteredErrorCount
            {
                get
                {
                    lock (_sync)
                    {
                        return _totalErrorCount - _filteredErrorCount;
                    }
                }
            }

            public void AddLine(string line, bool lineTruncated)
            {
                if (lineTruncated || string.IsNullOrEmpty(line))
                {
                    return;
                }

                if (!TryParseMsBuildOutput(line, _projectRoot, out var diagnostic, out var isError))
                {
                    return;
                }

                lock (_sync)
                {
                    if (isError)
                    {
                        _totalErrorCount++;
                        if (_enableFilter && ShouldExclude(diagnostic, _excludePaths, _excludeCodes))
                        {
                            _filteredErrorCount++;
                        }
                        else if (_errors.Count < MaxRetainedDiagnosticsPerKind)
                        {
                            _errors.Add(diagnostic);
                        }
                        else
                        {
                            _omittedErrorCount++;
                        }

                        return;
                    }

                    _totalWarningCount++;
                    if (_hideWarnings || (_enableFilter && ShouldExclude(diagnostic, _excludePaths, _excludeCodes)))
                    {
                        _filteredWarningCount++;
                    }
                    else if (_warnings.Count < MaxRetainedDiagnosticsPerKind)
                    {
                        _warnings.Add(diagnostic);
                    }
                    else
                    {
                        _omittedWarningCount++;
                    }
                }
            }

            public void ApplyTo(DotnetBuildResult result)
            {
                lock (_sync)
                {
                    result.Errors = new List<BuildError>(_errors);
                    result.Warnings = new List<BuildError>(_warnings);
                    result.TotalErrorCount = _totalErrorCount;
                    result.TotalWarningCount = _totalWarningCount;
                    result.FilteredErrorCount = _filteredErrorCount;
                    result.FilteredWarningCount = _filteredWarningCount;
                    result.OmittedErrorCount = _omittedErrorCount;
                    result.OmittedWarningCount = _omittedWarningCount;
                }
            }
        }
    }

    /// <summary>
    /// Options for dotnet build command
    /// </summary>
    public class DotnetBuildOptions
    {
        public string Solution { get; set; }
        public string Configuration { get; set; } = "Debug";
        public string Verbosity { get; set; } = "minimal";
        public int TimeoutMs { get; set; } = 300000; // 5 minutes default
        public bool EnableFilter { get; set; } = true;
        public string[] ExcludePaths { get; set; }
        public string[] ExcludeCodes { get; set; }
        public bool HideWarnings { get; set; } = true;
    }

    /// <summary>
    /// Result of dotnet build command
    /// </summary>
    public class DotnetBuildResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int ExitCode { get; set; }
        public double Duration { get; set; }
        public string SolutionPath { get; set; }
        public string ProjectRoot { get; set; }

        // Filtered results (what AI sees)
        public List<BuildError> Errors { get; set; } = new List<BuildError>();
        public List<BuildError> Warnings { get; set; } = new List<BuildError>();

        // Statistics
        public int TotalErrorCount { get; set; }
        public int TotalWarningCount { get; set; }
        public int FilteredErrorCount { get; set; }
        public int FilteredWarningCount { get; set; }
        public int OmittedErrorCount { get; set; }
        public int OmittedWarningCount { get; set; }

        // Raw output (for debugging)
        public string RawOutput { get; set; }
        public long RawOutputCharsRead { get; set; }
        public bool RawOutputTruncated { get; set; }
        public long TruncatedDiagnosticLineCount { get; set; }
    }

    /// <summary>
    /// Build error/warning information
    /// </summary>
    public class BuildError
    {
        public string File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string Project { get; set; }
        public string RawLine { get; set; }
    }

    /// <summary>
    /// Filter configuration for dotnet build
    /// </summary>
    public class FilterConfig
    {
        public List<string> ExcludePaths { get; set; } = new List<string>();
        public List<string> ExcludeCodes { get; set; } = new List<string>();
        public bool HideWarnings { get; set; } = true;

        public static FilterConfig GetDefault()
        {
            return new FilterConfig
            {
                ExcludePaths = new List<string>
                {
                    "ThirdParty",
                    "Plugins",
                    "Tests",
                    "Editor/Test"
                },
                ExcludeCodes = new List<string>(),
                HideWarnings = true
            };
        }
    }
}
