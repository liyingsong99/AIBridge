using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace AIBridgeCLI.Core
{
    public sealed class FileRuntimeTransportClient : IRuntimeTransportClient
    {
        private const string TransportName = "file";
        private const string CheckPassed = "passed";
        private const string CheckFailed = "failed";
        private const string CheckWarning = "warning";
        private const string CheckSkipped = "skipped";

        private readonly string _runtimeDirectory;

        public FileRuntimeTransportClient(string runtimeDirectory)
        {
            _runtimeDirectory = runtimeDirectory;
        }

        public RuntimeTransportKind Kind => RuntimeTransportKind.File;

        public IReadOnlyList<RuntimeTargetInfo> ListTargets(RuntimeTargetQueryOptions options = null)
        {
            return RuntimePathHelper.ListTargets(_runtimeDirectory);
        }

        public RuntimeTargetInfo ResolveTarget(string target, RuntimeTargetQueryOptions options = null)
        {
            return RuntimePathHelper.ResolveTarget(_runtimeDirectory, target);
        }

        public RuntimeSendResult Send(RuntimeTargetInfo target, CommandRequest request)
        {
            try
            {
                Directory.CreateDirectory(target.commandsPath);
                Directory.CreateDirectory(target.resultsPath);

                var commandFile = Path.Combine(target.commandsPath, request.id + ".json");
                var tempFile = commandFile + ".tmp";
                var json = JsonConvert.SerializeObject(request, Formatting.None, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });

                // 先写临时文件再改名，避免 Player 读到半截命令。
                File.WriteAllText(tempFile, json, new UTF8Encoding(false));
                if (File.Exists(commandFile))
                {
                    File.Delete(commandFile);
                }

                File.Move(tempFile, commandFile);
                return new RuntimeSendResult
                {
                    Success = true,
                    CommandPath = commandFile
                };
            }
            catch (Exception ex)
            {
                return new RuntimeSendResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        public RuntimeReceiveResult WaitResult(RuntimeTargetInfo target, string commandId, int timeoutMs, int pollIntervalMs)
        {
            var resultFile = RuntimePathHelper.GetResultPath(target, commandId);
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
            {
                if (File.Exists(resultFile))
                {
                    Thread.Sleep(10);
                    try
                    {
                        var resultJson = File.ReadAllText(resultFile, Encoding.UTF8);
                        var result = RuntimeResultParser.Parse(commandId, resultJson);
                        try { File.Delete(resultFile); } catch { }
                        return new RuntimeReceiveResult
                        {
                            Success = true,
                            Result = result
                        };
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(pollIntervalMs);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        return new RuntimeReceiveResult
                        {
                            Success = false,
                            Error = ex.Message
                        };
                    }
                }

                Thread.Sleep(pollIntervalMs);
            }

            return new RuntimeReceiveResult
            {
                Success = false,
                TimedOut = true,
                Error = "Timeout waiting for runtime result."
            };
        }

        public void CleanupCommand(RuntimeTargetInfo target, string commandId)
        {
            if (target == null || string.IsNullOrEmpty(commandId))
            {
                return;
            }

            try
            {
                var commandFile = RuntimePathHelper.GetCommandPath(target, commandId);
                if (File.Exists(commandFile))
                {
                    File.Delete(commandFile);
                }
            }
            catch
            {
            }
        }

        public RuntimeDiagnosticReport Diagnose(string target, RuntimeCommandTrace commandTrace = null)
        {
            var targetName = string.IsNullOrWhiteSpace(target) ? RuntimeTransportOptions.DefaultTarget : target;
            var report = new RuntimeDiagnosticReport
            {
                transport = TransportName,
                runtimeDirectory = _runtimeDirectory
            };

            AddPathReadCheck(report, "runtimeDirectory", _runtimeDirectory, "Verify --runtime-dir points to the runtime directory written by the Player.");

            var targets = RuntimePathHelper.ListTargets(_runtimeDirectory);
            var targetInfo = RuntimePathHelper.ResolveTarget(_runtimeDirectory, targetName);
            if (targetInfo == null)
            {
                report.targetId = targetName;
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = "targetExists",
                    status = CheckFailed,
                    detail = "Runtime target was not found. Available target count: " + targets.Count,
                    fix = "Start Play Mode or a Player with AIBridgeRuntime, or pass the correct --runtime-dir/--target."
                });
                report.suggestions.Add("Run: $CLI runtime list_targets --transport file");
                report.suggestions.Add("Verify AIBridgeRuntime is active and enabled in the Player.");
                FinalizeReport(report);
                return report;
            }

            report.targetId = targetInfo.targetId;
            report.checks.Add(new RuntimeDiagnosticCheck
            {
                name = "targetExists",
                status = CheckPassed,
                detail = "Target path: " + targetInfo.path
            });

            AddHeartbeatChecks(report, targetInfo);
            AddDirectoryChecks(report, targetInfo);
            AddCommandTraceChecks(report, commandTrace);
            AddSuggestions(report, targetInfo, commandTrace);
            FinalizeReport(report);
            return report;
        }

        private static void AddHeartbeatChecks(RuntimeDiagnosticReport report, RuntimeTargetInfo targetInfo)
        {
            AddPathReadCheck(report, "heartbeatFile", targetInfo.heartbeatPath, "Verify the runtime is still running and can write heartbeat.json.");

            if (targetInfo.heartbeat == null)
            {
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = "heartbeatReadable",
                    status = CheckFailed,
                    detail = "heartbeat.json is missing or invalid.",
                    fix = "Restart the Player, or check whether the runtime directory was cleaned or locked."
                });
                return;
            }

            report.checks.Add(new RuntimeDiagnosticCheck
            {
                name = "heartbeatReadable",
                status = CheckPassed,
                detail = "Last heartbeat UTC: " + (targetInfo.lastHeartbeatUtc ?? "unknown")
            });

            if (targetInfo.stale)
            {
                var age = targetInfo.ageSeconds.HasValue ? targetInfo.ageSeconds.Value.ToString("0.0") + "s" : "unknown";
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = "heartbeatFresh",
                    status = CheckFailed,
                    detail = "Last heartbeat age: " + age,
                    fix = "Bring the Player to foreground, verify runInBackground/platform background limits, or restart the Development Build."
                });
            }
            else
            {
                var age = targetInfo.ageSeconds.HasValue ? targetInfo.ageSeconds.Value.ToString("0.0") + "s" : "unknown";
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = "heartbeatFresh",
                    status = CheckPassed,
                    detail = "Last heartbeat age: " + age
                });
            }
        }

        private static void AddDirectoryChecks(RuntimeDiagnosticReport report, RuntimeTargetInfo targetInfo)
        {
            AddPathReadCheck(report, "targetDirectory", targetInfo.path, "Verify the target directory exists and is readable by the current user.");
            AddDirectoryReadWriteCheck(report, "commandsDirectory", targetInfo.commandsPath, "Verify the CLI can write commands, and the Player can read/delete them.");
            AddDirectoryReadWriteCheck(report, "resultsDirectory", targetInfo.resultsPath, "Verify the CLI can read/delete results, and the Player can write them.");
            AddDirectoryReadWriteCheck(report, "screenshotsDirectory", targetInfo.screenshotsPath, "Verify the screenshots directory exists and is readable/writable.");
        }

        private static void AddCommandTraceChecks(RuntimeDiagnosticReport report, RuntimeCommandTrace commandTrace)
        {
            if (commandTrace == null || string.IsNullOrEmpty(commandTrace.CommandId))
            {
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = "lastCommand",
                    status = CheckSkipped,
                    detail = "No command trace was provided."
                });
                return;
            }

            var commandExists = !string.IsNullOrEmpty(commandTrace.CommandPath) && File.Exists(commandTrace.CommandPath);
            var resultExists = !string.IsNullOrEmpty(commandTrace.ResultPath) && File.Exists(commandTrace.ResultPath);

            if (commandExists)
            {
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = "lastCommandConsumed",
                    status = CheckFailed,
                    detail = "Command file still exists: " + commandTrace.CommandPath,
                    fix = "The Player did not consume the command. Check heartbeat, foreground/background state, and runtime command scanning."
                });

                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = "lastResultExists",
                    status = CheckSkipped,
                    detail = "Result is not expected until the command is consumed."
                });
                return;
            }

            report.checks.Add(new RuntimeDiagnosticCheck
            {
                name = "lastCommandConsumed",
                status = CheckPassed,
                detail = "Command file was consumed or cleaned: " + commandTrace.CommandId
            });

            report.checks.Add(new RuntimeDiagnosticCheck
            {
                name = "lastResultExists",
                status = resultExists ? CheckWarning : CheckFailed,
                detail = resultExists ? "Result file exists but was not read before timeout." : "No result file for command: " + commandTrace.CommandId,
                fix = resultExists
                    ? "Check whether the result file is locked, too large, or invalid JSON."
                    : "The command was consumed but no result was produced. Check stuck handlers, async callbacks, token/action validation, or result write failures."
            });
        }

        private static void AddSuggestions(RuntimeDiagnosticReport report, RuntimeTargetInfo targetInfo, RuntimeCommandTrace commandTrace)
        {
            report.suggestions.Add("Run: $CLI runtime status --transport file --target " + targetInfo.targetId);
            report.suggestions.Add("Run: $CLI runtime list_targets --transport file");

            if (targetInfo.stale)
            {
                report.suggestions.Add("Bring the Player to foreground or restart it if heartbeat stays stale.");
            }

            if (commandTrace != null && !string.IsNullOrEmpty(commandTrace.CommandId))
            {
                report.suggestions.Add("Inspect command/result paths for command: " + commandTrace.CommandId);
            }
        }

        private static void AddPathReadCheck(RuntimeDiagnosticReport report, string name, string path, string fix)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = name,
                    status = CheckFailed,
                    detail = "Path is empty.",
                    fix = fix
                });
                return;
            }

            if (!Directory.Exists(path) && !File.Exists(path))
            {
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = name,
                    status = CheckFailed,
                    detail = "Path does not exist: " + path,
                    fix = fix
                });
                return;
            }

            report.checks.Add(new RuntimeDiagnosticCheck
            {
                name = name,
                status = CheckPassed,
                detail = "Path exists: " + path
            });
        }

        private static void AddDirectoryReadWriteCheck(RuntimeDiagnosticReport report, string name, string path, string fix)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = name,
                    status = CheckFailed,
                    detail = "Path is empty.",
                    fix = fix
                });
                return;
            }

            if (!Directory.Exists(path))
            {
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = name,
                    status = CheckFailed,
                    detail = "Directory does not exist: " + path,
                    fix = fix
                });
                return;
            }

            var probePath = Path.Combine(path, ".aibridge_cli_probe_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // 诊断需要真实读写权限验证，探针文件写入后立即删除，避免污染运行目录。
                File.WriteAllText(probePath, "probe", new UTF8Encoding(false));
                File.ReadAllText(probePath, Encoding.UTF8);
                File.Delete(probePath);
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = name,
                    status = CheckPassed,
                    detail = "Directory is readable and writable: " + path
                });
            }
            catch (Exception ex)
            {
                try { if (File.Exists(probePath)) File.Delete(probePath); } catch { }
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = name,
                    status = CheckFailed,
                    detail = "Directory read/write check failed: " + path + " (" + ex.Message + ")",
                    fix = fix
                });
            }
        }

        private static void FinalizeReport(RuntimeDiagnosticReport report)
        {
            var failed = report.checks.Find(c => string.Equals(c.status, CheckFailed, StringComparison.OrdinalIgnoreCase));
            report.success = failed == null;
            if (report.success)
            {
                report.summary = "Runtime file transport diagnostics passed.";
                return;
            }

            report.summary = failed.detail;
        }
    }
}
