using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using AIBridgeCLI.Core;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public class WorkflowCommandExecution
    {
        public string CommandId { get; set; }
        public string Command { get; set; }
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string StartedAtUtc { get; set; }
        public string EndedAtUtc { get; set; }
        public string Error { get; set; }
        public JObject Result { get; set; }
    }

    public static class WorkflowCommandLine
    {
        public static WorkflowCommandExecution Execute(string command, int timeoutMs)
        {
            var commandId = PathHelper.GenerateCommandId();
            var startedAtUtc = DateTime.UtcNow.ToString("o");
            var tokens = Tokenize(command);
            if (tokens.Count == 0)
            {
                return CreateImmediateFailure(commandId, command, startedAtUtc, "Command is empty.");
            }

            if (tokens[0].Equals("workflow", StringComparison.OrdinalIgnoreCase)
                && tokens.Count > 1
                && tokens[1].Equals("run-cli", StringComparison.OrdinalIgnoreCase))
            {
                return CreateImmediateFailure(commandId, command, startedAtUtc, "workflow run-cli cannot call itself as a nested CLI step.");
            }

            if (!HasOutputMode(tokens))
            {
                tokens.Add("--raw");
            }

            var output = new StringBuilder();
            var error = new StringBuilder();
            var startInfo = CreateStartInfo(tokens);
            startInfo.WorkingDirectory = Directory.GetCurrentDirectory();
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            using (var process = new Process())
            {
                process.StartInfo = startInfo;
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        output.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        error.AppendLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var completed = process.WaitForExit(timeoutMs);
                if (!completed)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore cleanup failure.
                    }

                    return CreateImmediateFailure(commandId, command, startedAtUtc, "Command timed out after " + timeoutMs + "ms.");
                }

                process.WaitForExit();
                var endedAtUtc = DateTime.UtcNow.ToString("o");
                var stdout = output.ToString().Trim();
                var stderr = error.ToString().Trim();
                var data = TryParseJson(stdout);
                var success = process.ExitCode == 0 && ReadSuccess(data, process.ExitCode);
                var result = new JObject
                {
                    ["id"] = commandId,
                    ["success"] = success,
                    ["exitCode"] = process.ExitCode,
                    ["command"] = command,
                    ["startedAtUtc"] = startedAtUtc,
                    ["endedAtUtc"] = endedAtUtc
                };

                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    result["stdout"] = stdout;
                }

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    result["stderr"] = stderr;
                }

                if (data != null)
                {
                    result["data"] = data;
                }

                var errorMessage = ReadError(data);
                if (string.IsNullOrWhiteSpace(errorMessage))
                {
                    errorMessage = stderr;
                }

                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    result["error"] = errorMessage;
                }

                return new WorkflowCommandExecution
                {
                    CommandId = commandId,
                    Command = command,
                    Success = success,
                    ExitCode = process.ExitCode,
                    StartedAtUtc = startedAtUtc,
                    EndedAtUtc = endedAtUtc,
                    Error = success ? null : errorMessage,
                    Result = result
                };
            }
        }

        public static List<string> Tokenize(string command)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(command))
            {
                return result;
            }

            var current = new StringBuilder();
            var inQuote = false;
            var quoteChar = '\0';
            for (var i = 0; i < command.Length; i++)
            {
                var ch = command[i];
                if ((ch == '"' || ch == '\'') && (!inQuote || ch == quoteChar))
                {
                    if (inQuote)
                    {
                        inQuote = false;
                        quoteChar = '\0';
                    }
                    else
                    {
                        inQuote = true;
                        quoteChar = ch;
                    }

                    continue;
                }

                if (char.IsWhiteSpace(ch) && !inQuote)
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Length = 0;
                    }

                    continue;
                }

                if (ch == '\\' && inQuote && i + 1 < command.Length && command[i + 1] == quoteChar)
                {
                    current.Append(command[i + 1]);
                    i++;
                    continue;
                }

                current.Append(ch);
            }

            if (current.Length > 0)
            {
                result.Add(current.ToString());
            }

            return result;
        }

        private static ProcessStartInfo CreateStartInfo(List<string> tokens)
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var exePath = Path.Combine(baseDirectory, "AIBridgeCLI.exe");
            if (File.Exists(exePath))
            {
                var startInfo = new ProcessStartInfo { FileName = exePath };
                foreach (var token in tokens)
                {
                    startInfo.ArgumentList.Add(token);
                }

                return startInfo;
            }

            var dllPath = Path.Combine(baseDirectory, "AIBridgeCLI.dll");
            if (File.Exists(dllPath))
            {
                var startInfo = new ProcessStartInfo { FileName = "dotnet" };
                startInfo.ArgumentList.Add(dllPath);
                foreach (var token in tokens)
                {
                    startInfo.ArgumentList.Add(token);
                }

                return startInfo;
            }

            var currentProcessPath = Process.GetCurrentProcess().MainModule.FileName;
            var fallback = new ProcessStartInfo { FileName = currentProcessPath };
            foreach (var token in tokens)
            {
                fallback.ArgumentList.Add(token);
            }

            return fallback;
        }

        private static bool HasOutputMode(List<string> tokens)
        {
            foreach (var token in tokens)
            {
                if (token.Equals("--raw", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("--pretty", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("--quiet", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static JObject TryParseJson(string stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            try
            {
                return JObject.Parse(stdout);
            }
            catch
            {
                return null;
            }
        }

        private static bool ReadSuccess(JObject data, int exitCode)
        {
            if (data == null)
            {
                return exitCode == 0;
            }

            if (data.TryGetValue("success", StringComparison.OrdinalIgnoreCase, out var successToken))
            {
                return successToken.Type == JTokenType.Boolean && successToken.Value<bool>();
            }

            return exitCode == 0;
        }

        private static string ReadError(JObject data)
        {
            if (data == null)
            {
                return null;
            }

            if (data.TryGetValue("error", StringComparison.OrdinalIgnoreCase, out var errorToken))
            {
                return errorToken.Type == JTokenType.Null ? null : errorToken.ToString();
            }

            return null;
        }

        private static WorkflowCommandExecution CreateImmediateFailure(string commandId, string command, string startedAtUtc, string error)
        {
            var endedAtUtc = DateTime.UtcNow.ToString("o");
            return new WorkflowCommandExecution
            {
                CommandId = commandId,
                Command = command,
                Success = false,
                ExitCode = 1,
                StartedAtUtc = startedAtUtc,
                EndedAtUtc = endedAtUtc,
                Error = error,
                Result = new JObject
                {
                    ["id"] = commandId,
                    ["success"] = false,
                    ["exitCode"] = 1,
                    ["command"] = command,
                    ["startedAtUtc"] = startedAtUtc,
                    ["endedAtUtc"] = endedAtUtc,
                    ["error"] = error
                }
            };
        }
    }
}
