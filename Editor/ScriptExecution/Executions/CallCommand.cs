using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AIBridge.Internal.Json;
using UnityEditor;

namespace AIBridge.Editor.ScriptExecution.Commands
{
    /// <summary>
    /// Executes a Batch script call line: call [command] [args...]
    /// </summary>
    public class CallCommand : IScriptCommand
    {
        public string Type => "call";

        private readonly string _arguments;
        private readonly int _timeout;

        public CallCommand(string arguments, int timeout = 60000)
        {
            _arguments = arguments;
            _timeout = timeout;
        }

        public ScriptCommandResult Execute(ScriptExecutionContext context)
        {
            try
            {
                var cliArgs = _arguments.Trim();

                bool handledDirectly;
                var directResult = TryExecuteDirectCommand(context, cliArgs, out handledDirectly);
                if (handledDirectly)
                {
                    return directResult;
                }

                return ExecuteExternalCli(context, cliArgs);
            }
            catch (Exception ex)
            {
                return ScriptCommandResult.Fail($"Exception while executing call: {ex.Message}", ex);
            }
        }

        private ScriptCommandResult TryExecuteDirectCommand(ScriptExecutionContext context, string cliArgs, out bool handled)
        {
            handled = false;

            var parts = SplitCommandLine(cliArgs);
            if (parts.Count == 0)
            {
                handled = true;
                return ScriptCommandResult.Fail("Missing command after call");
            }

            var request = BuildRequest(parts);
            ICommand command;
            if (!CommandRegistry.TryGetCommand(request.type, out command))
            {
                return null;
            }

            handled = true;
            context.Log($"[Call] Direct execute: {request.type} {request.GetParam("action", string.Empty)}");

            var result = command.Execute(request);
            if (result == null)
            {
                return ScriptCommandResult.Fail($"Command '{request.type}' started async processing and cannot finish inside call");
            }

            result.id = request.id;
            if (command.RequiresRefresh)
            {
                AssetDatabase.Refresh();
            }

            var json = AIBridgeJson.Serialize(result);
            context.Log($"[CallResult] {json}");
            if (!result.success)
            {
                return ScriptCommandResult.Fail(string.IsNullOrEmpty(result.error) ? "Command failed" : result.error);
            }

            return ScriptCommandResult.Ok($"Command executed successfully\n{json}");
        }

        private ScriptCommandResult ExecuteExternalCli(ScriptExecutionContext context, string cliArgs)
        {
            var cliPath = Path.Combine(Directory.GetCurrentDirectory(), ".aibridge", "cli", "AIBridgeCLI.exe");
            if (!File.Exists(cliPath))
            {
                cliPath = Path.Combine(Directory.GetCurrentDirectory(), "Packages", "cn.lys.aibridge", "Tools~", "CLI", "win-x64", "AIBridgeCLI.exe");
            }

            if (!File.Exists(cliPath))
            {
                return ScriptCommandResult.Fail("AIBridge CLI not found. Tried .aibridge/cli/AIBridgeCLI.exe and Packages/cn.lys.aibridge/Tools~/CLI/win-x64/AIBridgeCLI.exe");
            }

            context.Log($"[Call] External execute: {cliPath} {cliArgs}");

            var startInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = cliArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory()
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    return ScriptCommandResult.Fail("Failed to start AIBridge CLI process");
                }

                var outputData = string.Empty;
                var errorData = string.Empty;

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputData += e.Data + "\n";
                        context.Log($"[Output] {e.Data}");
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorData += e.Data + "\n";
                        context.Log($"[Error] {e.Data}");
                    }
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(_timeout))
                {
                    process.Kill();
                    return ScriptCommandResult.Fail($"Command timed out ({_timeout}ms)");
                }

                if (process.ExitCode != 0)
                {
                    return ScriptCommandResult.Fail($"Command failed (ExitCode: {process.ExitCode})\n{errorData}");
                }

                return ScriptCommandResult.Ok($"Command executed successfully\n{outputData}");
            }
        }

        private static CommandRequest BuildRequest(List<string> parts)
        {
            var request = new CommandRequest
            {
                id = "script_call_" + Guid.NewGuid().ToString("N"),
                type = parts[0],
                @params = new Dictionary<string, object>()
            };

            var index = 1;
            if (index < parts.Count && !parts[index].StartsWith("--", StringComparison.Ordinal))
            {
                var action = parts[index];
                if (string.Equals(request.type, "compile", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(action, "unity", StringComparison.OrdinalIgnoreCase))
                {
                    action = "start";
                }

                request.@params["action"] = action;
                index++;
            }

            while (index < parts.Count)
            {
                var token = parts[index];
                if (!token.StartsWith("--", StringComparison.Ordinal))
                {
                    index++;
                    continue;
                }

                var key = token.Substring(2);
                if (index + 1 < parts.Count && !parts[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    request.@params[key] = ParseValue(parts[index + 1]);
                    index += 2;
                }
                else
                {
                    request.@params[key] = true;
                    index++;
                }
            }

            return request;
        }

        private static List<string> SplitCommandLine(string commandLine)
        {
            var result = new List<string>();
            var current = string.Empty;
            var inQuote = false;
            var quoteChar = ' ';

            for (var i = 0; i < commandLine.Length; i++)
            {
                var c = commandLine[i];
                if (!inQuote && (c == '"' || c == '\''))
                {
                    inQuote = true;
                    quoteChar = c;
                    continue;
                }

                if (inQuote && c == quoteChar)
                {
                    inQuote = false;
                    quoteChar = ' ';
                    continue;
                }

                if (!inQuote && char.IsWhiteSpace(c))
                {
                    if (current.Length > 0)
                    {
                        result.Add(current);
                        current = string.Empty;
                    }

                    continue;
                }

                current += c;
            }

            if (current.Length > 0)
            {
                result.Add(current);
            }

            return result;
        }

        private static object ParseValue(string value)
        {
            bool boolValue;
            if (bool.TryParse(value, out boolValue))
            {
                return boolValue;
            }

            long longValue;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out longValue))
            {
                return longValue;
            }

            double doubleValue;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out doubleValue))
            {
                return doubleValue;
            }

            return value;
        }
    }
}
