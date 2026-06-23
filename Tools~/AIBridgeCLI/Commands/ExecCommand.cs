using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AIBridgeCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Commands
{
    public static class ExecCommand
    {
        private const int DefaultExecTimeoutMs = 30000;
        private const int DefaultMaxOutputChars = 200000;

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        public static int Execute(string action, Dictionary<string, string> options, bool readStdin, int globalTimeout, OutputMode outputMode)
        {
            var normalizedAction = string.IsNullOrWhiteSpace(action) ? "run" : action.Trim().ToLowerInvariant();
            if (normalizedAction != "run" && normalizedAction != "batch")
            {
                OutputFormatter.PrintError("Unknown exec action: " + action);
                Console.WriteLine(new ExecCommandBuilder().GetHelp());
                return 1;
            }

            string requestJson;
            try
            {
                requestJson = LoadRequestJson(options, readStdin);
            }
            catch (Exception ex)
            {
                OutputFormatter.PrintError(ex.Message);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(requestJson))
            {
                Console.WriteLine(new ExecCommandBuilder().GetHelp(normalizedAction));
                return 0;
            }

            try
            {
                var root = JToken.Parse(requestJson);
                var defaultTimeoutMs = options.ContainsKey("timeout") ? globalTimeout : DefaultExecTimeoutMs;
                if (normalizedAction == "batch" || IsBatchRequest(root))
                {
                    var batchResult = ExecuteBatch(root, defaultTimeoutMs);
                    PrintResult(batchResult, outputMode);
                    return batchResult.success ? 0 : 1;
                }

                var request = root.ToObject<ExecRunRequest>();
                var result = ExecuteRun(request, defaultTimeoutMs, null, null, null);
                PrintResult(result, outputMode);
                return result.success ? 0 : 1;
            }
            catch (JsonReaderException ex)
            {
                var error = new ExecRunResult
                {
                    success = false,
                    error = "Invalid exec JSON request: " + ex.Message + Environment.NewLine + GetExecJsonUsageHint(),
                    exitCode = -1
                };
                PrintResult(error, outputMode);
                return 1;
            }
            catch (Exception ex)
            {
                var error = new ExecRunResult
                {
                    success = false,
                    error = ex.Message,
                    exitCode = -1
                };
                PrintResult(error, outputMode);
                return 1;
            }
        }

        private static string LoadRequestJson(Dictionary<string, string> options, bool readStdin)
        {
            string requestFile;
            if (!TryGetOption(options, "request-file", out requestFile))
            {
                TryGetOption(options, "file", out requestFile);
            }

            string inlineJson;
            TryGetOption(options, "json", out inlineJson);

            var sourceCount = 0;
            if (!string.IsNullOrWhiteSpace(requestFile)) sourceCount++;
            if (readStdin) sourceCount++;
            if (!string.IsNullOrWhiteSpace(inlineJson)) sourceCount++;
            if (sourceCount > 1)
            {
                throw new ArgumentException("Provide only one exec request source: --request-file, --stdin, or --json.");
            }

            if (!string.IsNullOrWhiteSpace(requestFile))
            {
                var path = Path.GetFullPath(requestFile);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("Exec request file not found: " + path, path);
                }

                return File.ReadAllText(path, Encoding.UTF8);
            }

            if (readStdin)
            {
                return Console.In.ReadToEnd();
            }

            return inlineJson;
        }

        private static string GetExecJsonUsageHint()
        {
            return "exec run --stdin expects a JSON object piped to stdin, not a raw shell command. "
                + "Example JSON: { \"command\": \"rg\", \"args\": [\"-n\"], \"queries\": [\"TODO\"], \"paths\": [\"Packages\"] }. "
                + "PowerShell: $request | & ./.aibridge/cli/AIBridgeCLI.exe exec run --stdin. "
                + "Do not write: AIBridgeCLI exec run --stdin rg -n TODO Packages.";
        }

        private static bool TryGetOption(Dictionary<string, string> options, string key, out string value)
        {
            value = null;
            return options != null && options.TryGetValue(key, out value);
        }

        private static bool IsBatchRequest(JToken root)
        {
            var obj = root as JObject;
            return obj != null && obj["jobs"] != null;
        }

        private static ExecBatchResult ExecuteBatch(JToken root, int defaultTimeoutMs)
        {
            var batch = root.ToObject<ExecBatchRequest>();
            var result = new ExecBatchResult
            {
                success = true,
                results = new List<ExecRunResult>()
            };

            if (batch == null || batch.jobs == null || batch.jobs.Count == 0)
            {
                result.success = false;
                result.error = "Exec batch requires at least one job.";
                return result;
            }

            var mode = string.IsNullOrWhiteSpace(batch.mode) ? "sequential" : batch.mode.Trim();
            if (!mode.Equals("sequential", StringComparison.OrdinalIgnoreCase))
            {
                result.success = false;
                result.error = "Exec batch currently supports only sequential mode.";
                return result;
            }

            result.total = batch.jobs.Count;
            var stopOnError = !batch.continueOnError.GetValueOrDefault();
            foreach (var job in batch.jobs)
            {
                var runResult = ExecuteRun(job, defaultTimeoutMs, batch.cwd, batch.env, batch);
                result.results.Add(runResult);
                if (!runResult.success)
                {
                    result.success = false;
                    if (stopOnError)
                    {
                        break;
                    }
                }
            }

            result.completed = result.results.Count;
            result.failed = result.results.Count(r => !r.success);
            return result;
        }

        private static ExecRunResult ExecuteRun(
            ExecRunRequest request,
            int defaultTimeoutMs,
            string batchCwd,
            Dictionary<string, string> batchEnv,
            ExecBatchRequest batch)
        {
            var result = new ExecRunResult
            {
                success = false,
                exitCode = -1
            };

            var stopwatch = Stopwatch.StartNew();
            Process process = null;
            try
            {
                if (request == null)
                {
                    throw new ArgumentException("Exec request is empty.");
                }

                if (request.shell.GetValueOrDefault())
                {
                    throw new ArgumentException("exec does not support shell=true. Use an explicit script mode instead.");
                }

                var cwd = ResolveWorkingDirectory(string.IsNullOrWhiteSpace(request.cwd) ? batchCwd : request.cwd);
                var command = ResolveCommand(request.command);
                if (string.IsNullOrWhiteSpace(command))
                {
                    throw new ArgumentException("Exec request requires command." + Environment.NewLine + GetExecJsonUsageHint());
                }

                var isRg = IsRgCommand(command, request.command);
                var args = BuildArguments(request, isRg, cwd);
                var stdinText = ResolveStdin(request, cwd);
                var hasStdin = stdinText != null;
                var timeoutMs = request.timeoutMs
                                ?? batch?.timeoutMs
                                ?? defaultTimeoutMs;
                var maxOutputChars = request.maxOutputChars
                                     ?? batch?.maxOutputChars
                                     ?? DefaultMaxOutputChars;
                var captureStdout = request.captureStdout.GetValueOrDefault(true);
                var captureStderr = request.captureStderr.GetValueOrDefault(true);

                result.label = request.label;
                result.command = command;
                result.args = args;
                result.cwd = cwd;

                var startInfo = new ProcessStartInfo
                {
                    FileName = command,
                    WorkingDirectory = cwd,
                    UseShellExecute = false,
                    RedirectStandardInput = hasStdin,
                    RedirectStandardOutput = captureStdout,
                    RedirectStandardError = captureStderr,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    StandardInputEncoding = hasStdin ? Encoding.UTF8 : null
                };

                foreach (var arg in args)
                {
                    startInfo.ArgumentList.Add(arg);
                }

                ApplyEnvironment(startInfo, batchEnv);
                ApplyEnvironment(startInfo, request.env);

                process = new Process
                {
                    StartInfo = startInfo
                };

                process.Start();
                var stdoutTask = captureStdout ? process.StandardOutput.ReadToEndAsync() : Task.FromResult<string>(null);
                var stderrTask = captureStderr ? process.StandardError.ReadToEndAsync() : Task.FromResult<string>(null);

                if (hasStdin)
                {
                    process.StandardInput.Write(stdinText);
                    process.StandardInput.Close();
                }

                var completed = process.WaitForExit(timeoutMs);
                if (!completed)
                {
                    result.timedOut = true;
                    TryKillProcess(process);
                    TryWaitForExit(process, 5000);
                }
                else
                {
                    process.WaitForExit();
                    result.exitCode = process.ExitCode;
                }

                var stdout = GetTaskResult(stdoutTask);
                var stderr = GetTaskResult(stderrTask);
                result.stdout = Truncate(stdout, maxOutputChars, out var stdoutTruncated);
                result.stderr = Truncate(stderr, maxOutputChars, out var stderrTruncated);
                result.stdoutTruncated = stdoutTruncated;
                result.stderrTruncated = stderrTruncated;

                var allowedExitCodes = GetAllowedExitCodes(request, isRg);
                result.success = !result.timedOut && allowedExitCodes.Contains(result.exitCode);
                if (!result.success && string.IsNullOrWhiteSpace(result.error))
                {
                    result.error = result.timedOut
                        ? "Process timed out after " + timeoutMs + "ms."
                        : "Process exited with code " + result.exitCode + ".";
                }
            }
            catch (Exception ex)
            {
                result.error = ex.Message;
                result.success = false;
            }
            finally
            {
                stopwatch.Stop();
                result.durationMs = (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds);
                process?.Dispose();
            }

            return result;
        }

        private static string ResolveCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            return command.Trim().Equals("search", StringComparison.OrdinalIgnoreCase)
                ? "rg"
                : command.Trim();
        }

        private static bool IsRgCommand(string resolvedCommand, string originalCommand)
        {
            if (!string.IsNullOrWhiteSpace(originalCommand)
                && originalCommand.Trim().Equals("search", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var fileName = Path.GetFileNameWithoutExtension(resolvedCommand);
            return fileName.Equals("rg", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> BuildArguments(ExecRunRequest request, bool isRg, string cwd)
        {
            var args = new List<string>();
            if (request.args != null)
            {
                foreach (var arg in request.args)
                {
                    if (arg != null)
                    {
                        args.Add(arg);
                    }
                }
            }

            if (HasRgProfileFields(request))
            {
                if (!isRg)
                {
                    throw new ArgumentException("queries/globs/types/paths/patternFile are supported only for rg/search exec requests.");
                }

                if (request.queries != null)
                {
                    foreach (var query in request.queries.Where(q => q != null))
                    {
                        args.Add("-e");
                        args.Add(query);
                    }
                }

                if (!string.IsNullOrWhiteSpace(request.patternFile))
                {
                    args.Add("-f");
                    args.Add(ResolveFilePath(request.patternFile, cwd));
                }

                if (request.types != null)
                {
                    foreach (var type in request.types.Where(t => !string.IsNullOrWhiteSpace(t)))
                    {
                        args.Add("-t");
                        args.Add(type);
                    }
                }

                if (request.globs != null)
                {
                    foreach (var glob in request.globs.Where(g => g != null))
                    {
                        args.Add("--glob");
                        args.Add(glob);
                    }
                }

                if (request.paths != null)
                {
                    foreach (var path in request.paths.Where(p => p != null))
                    {
                        args.Add(path);
                    }
                }
            }

            return args;
        }

        private static bool HasRgProfileFields(ExecRunRequest request)
        {
            return HasItems(request.queries)
                   || HasItems(request.globs)
                   || HasItems(request.types)
                   || HasItems(request.paths)
                   || !string.IsNullOrWhiteSpace(request.patternFile);
        }

        private static bool HasItems(List<string> values)
        {
            return values != null && values.Any(v => v != null);
        }

        private static string ResolveWorkingDirectory(string cwd)
        {
            var result = string.IsNullOrWhiteSpace(cwd)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(cwd);
            if (!Directory.Exists(result))
            {
                throw new DirectoryNotFoundException("Working directory not found: " + result);
            }

            return result;
        }

        private static string ResolveFilePath(string path, string cwd)
        {
            return Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(cwd, path));
        }

        private static string ResolveStdin(ExecRunRequest request, string cwd)
        {
            var hasInline = request.stdin != null;
            var hasFile = !string.IsNullOrWhiteSpace(request.stdinFile);
            if (hasInline && hasFile)
            {
                throw new ArgumentException("Provide only one stdin source: stdin or stdinFile.");
            }

            if (hasFile)
            {
                var path = ResolveFilePath(request.stdinFile, cwd);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("stdinFile not found: " + path, path);
                }

                return File.ReadAllText(path, Encoding.UTF8);
            }

            return hasInline ? request.stdin : null;
        }

        private static void ApplyEnvironment(ProcessStartInfo startInfo, Dictionary<string, string> env)
        {
            if (env == null)
            {
                return;
            }

            foreach (var kvp in env)
            {
                if (kvp.Value == null)
                {
                    startInfo.Environment.Remove(kvp.Key);
                }
                else
                {
                    startInfo.Environment[kvp.Key] = kvp.Value;
                }
            }
        }

        private static List<int> GetAllowedExitCodes(ExecRunRequest request, bool isRg)
        {
            if (request.allowedExitCodes != null && request.allowedExitCodes.Count > 0)
            {
                return request.allowedExitCodes;
            }

            return isRg ? new List<int> { 0, 1 } : new List<int> { 0 };
        }

        private static void TryKillProcess(Process process)
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
                // Best-effort timeout cleanup.
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
                // Best-effort timeout cleanup.
            }
        }

        private static string GetTaskResult(Task<string> task)
        {
            try
            {
                return task == null ? null : task.GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        private static string Truncate(string value, int maxOutputChars, out bool truncated)
        {
            truncated = false;
            if (value == null)
            {
                return null;
            }

            if (maxOutputChars < 0 || value.Length <= maxOutputChars)
            {
                return value;
            }

            truncated = true;
            return value.Substring(0, maxOutputChars);
        }

        private static void PrintResult(object result, OutputMode outputMode)
        {
            var formatting = outputMode == OutputMode.Pretty ? Formatting.Indented : Formatting.None;
            Console.WriteLine(JsonConvert.SerializeObject(result, formatting, JsonSettings));
        }

        public class ExecRunRequest
        {
            public string label { get; set; }
            public string command { get; set; }
            public List<string> args { get; set; }
            public string cwd { get; set; }
            public Dictionary<string, string> env { get; set; }
            public string stdin { get; set; }
            public string stdinFile { get; set; }
            public int? timeoutMs { get; set; }
            public List<int> allowedExitCodes { get; set; }
            public int? maxOutputChars { get; set; }
            public bool? captureStdout { get; set; }
            public bool? captureStderr { get; set; }
            public bool? shell { get; set; }
            public List<string> queries { get; set; }
            public List<string> globs { get; set; }
            public List<string> types { get; set; }
            public List<string> paths { get; set; }
            public string patternFile { get; set; }
        }

        public class ExecBatchRequest
        {
            public string mode { get; set; }
            public string cwd { get; set; }
            public Dictionary<string, string> env { get; set; }
            public int? timeoutMs { get; set; }
            public int? maxOutputChars { get; set; }
            public bool? continueOnError { get; set; }
            public List<ExecRunRequest> jobs { get; set; }
        }

        public class ExecRunResult
        {
            public string label { get; set; }
            public bool success { get; set; }
            public string command { get; set; }
            public List<string> args { get; set; }
            public string cwd { get; set; }
            public int exitCode { get; set; }
            public bool timedOut { get; set; }
            public long durationMs { get; set; }
            public string stdout { get; set; }
            public string stderr { get; set; }
            public bool stdoutTruncated { get; set; }
            public bool stderrTruncated { get; set; }
            public string error { get; set; }
        }

        public class ExecBatchResult
        {
            public bool success { get; set; }
            public string error { get; set; }
            public int total { get; set; }
            public int completed { get; set; }
            public int failed { get; set; }
            public List<ExecRunResult> results { get; set; }
        }
    }
}
