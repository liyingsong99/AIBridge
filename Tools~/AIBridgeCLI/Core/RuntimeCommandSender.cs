using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Core
{
    public class RuntimeCommandSender
    {
        private readonly RuntimeTransportOptions _options;
        private readonly IRuntimeTransportClient _transportClient;
        private readonly string _target;
        private readonly int _timeout;
        private readonly int _pollInterval;
        private readonly RuntimeTargetQueryOptions _targetQueryOptions;
        private readonly bool _diagnoseTargetNotFound;
        private IReadOnlyList<RuntimeTargetInfo> _targets;

        public RuntimeCommandSender(
            string runtimeDirectoryOverride,
            string target,
            int timeout = 5000,
            int pollInterval = 50,
            string transport = null,
            bool probeTargets = false,
            bool diagnoseTargetNotFound = false)
        {
            _options = RuntimeTransportOptions.Create(transport, runtimeDirectoryOverride, target, timeout, pollInterval);
            _transportClient = RuntimeTransportClientFactory.Create(_options);
            _target = _options.Target;
            _timeout = timeout;
            _pollInterval = pollInterval;
            _targetQueryOptions = probeTargets ? RuntimeTargetQueryOptions.Probe : RuntimeTargetQueryOptions.Quick;
            _diagnoseTargetNotFound = diagnoseTargetNotFound;
        }

        public CommandResult SendCommand(CommandRequest request)
        {
            var runtimeAction = RuntimePathHelper.GetRuntimeAction(request);
            if (string.Equals(runtimeAction, "runtime.list_targets", StringComparison.OrdinalIgnoreCase))
            {
                return ListTargets(request?.id);
            }

            if (string.Equals(runtimeAction, "runtime.diagnose", StringComparison.OrdinalIgnoreCase))
            {
                return Diagnose(request?.id);
            }

            var targetInfo = ResolveTarget();
            if (targetInfo == null)
            {
                return CreateTargetNotFoundResult(request?.id);
            }

            EnsureRequestId(request);

            var sentAtUtc = DateTime.UtcNow.ToString("o");
            var sendResult = _transportClient.Send(targetInfo, request);
            if (!sendResult.Success)
            {
                return CreateTransportFailureResult(request.id, runtimeAction, targetInfo, sendResult.Error);
            }

            var commandTrace = new RuntimeCommandTrace
            {
                CommandId = request.id,
                Action = runtimeAction,
                CommandPath = sendResult.CommandPath,
                ResultPath = RuntimePathHelper.GetResultPath(targetInfo, request.id),
                SentAtUtc = sentAtUtc
            };

            var receiveResult = _transportClient.WaitResult(targetInfo, request.id, _timeout, _pollInterval);
            if (receiveResult.Success)
            {
                return FinalizeRuntimeResult(receiveResult.Result, request, runtimeAction);
            }

            if (!receiveResult.TimedOut)
            {
                return CreateTransportFailureResult(request.id, runtimeAction, targetInfo, receiveResult.Error);
            }

            var diagnostic = _transportClient.Diagnose(_target, commandTrace);
            _transportClient.CleanupCommand(targetInfo, request.id);

            return new CommandResult
            {
                id = request.id,
                success = false,
                error = "Timeout waiting for runtime result after " + _timeout + "ms.",
                data = new
                {
                    transport = GetTransportName(),
                    runtimeDirectory = _options.RuntimeDirectory,
                    target = targetInfo.targetId,
                    action = runtimeAction,
                    diagnostic = RuntimeDiagnosticSummary.FromReport(diagnostic)
                }
            };
        }

        public CommandResult TrySendCommandNoWait(CommandRequest request)
        {
            var runtimeAction = RuntimePathHelper.GetRuntimeAction(request);
            if (string.Equals(runtimeAction, "runtime.list_targets", StringComparison.OrdinalIgnoreCase))
            {
                return ListTargets(request?.id);
            }

            if (string.Equals(runtimeAction, "runtime.diagnose", StringComparison.OrdinalIgnoreCase))
            {
                return Diagnose(request?.id);
            }

            var targetInfo = ResolveTarget();
            if (targetInfo == null)
            {
                return CreateTargetNotFoundResult(request?.id);
            }

            EnsureRequestId(request);
            var sendResult = _transportClient.Send(targetInfo, request);
            if (!sendResult.Success)
            {
                return CreateTransportFailureResult(request.id, runtimeAction, targetInfo, sendResult.Error);
            }

            return new CommandResult
            {
                id = request.id,
                success = true,
                data = new
                {
                    id = request.id,
                    status = "sent",
                    transport = GetTransportName(),
                    target = targetInfo.targetId,
                    action = runtimeAction
                }
            };
        }

        public CommandResult ListTargets(string requestId = null)
        {
            var targets = GetTargets();
            return new CommandResult
            {
                id = requestId,
                success = true,
                data = new
                {
                    transport = GetTransportName(),
                    runtimeDirectory = _options.RuntimeDirectory,
                    mode = _targetQueryOptions.mode,
                    count = targets.Count,
                    targets = targets
                }
            };
        }

        public CommandResult Diagnose(string requestId = null)
        {
            var report = _transportClient.Diagnose(_target);
            return new CommandResult
            {
                id = requestId,
                success = report.success,
                error = report.success ? null : report.summary,
                data = report
            };
        }

        private CommandResult CreateTargetNotFoundResult(string requestId)
        {
            var targets = GetTargets();
            var diagnostic = _diagnoseTargetNotFound
                ? RuntimeDiagnosticSummary.FromReport(_transportClient.Diagnose(_target))
                : null;
            return new CommandResult
            {
                id = requestId,
                success = false,
                error = "Runtime target was not found. Start a Player with AIBridgeRuntime, run runtime discover for LAN targets, or pass --url/--target.",
                data = new
                {
                    transport = GetTransportName(),
                    runtimeDirectory = _options.RuntimeDirectory,
                    target = _target,
                    mode = _targetQueryOptions.mode,
                    targetCount = targets.Count,
                    targets = targets,
                    diagnostic = diagnostic,
                    suggestions = new[]
                    {
                        "Run: $CLI runtime list_targets --probe true",
                        "Run: $CLI runtime discover --timeout 500",
                        "Run: $CLI runtime diagnose --target " + _target
                    }
                }
            };
        }

        private IReadOnlyList<RuntimeTargetInfo> GetTargets()
        {
            if (_targets == null)
            {
                _targets = _transportClient.ListTargets(_targetQueryOptions);
            }

            return _targets;
        }

        private RuntimeTargetInfo ResolveTarget()
        {
            var targets = GetTargets();
            if (targets == null || targets.Count == 0)
            {
                return null;
            }

            var resolvedTarget = string.IsNullOrWhiteSpace(_target) ? RuntimeTransportOptions.DefaultTarget : _target;
            if (string.Equals(resolvedTarget, RuntimeTransportOptions.DefaultTarget, StringComparison.OrdinalIgnoreCase)
                || string.Equals(resolvedTarget, targets[0].targetId, StringComparison.OrdinalIgnoreCase))
            {
                return targets[0];
            }

            for (var i = 0; i < targets.Count; i++)
            {
                if (string.Equals(resolvedTarget, targets[i].targetId, StringComparison.OrdinalIgnoreCase))
                {
                    return targets[i];
                }
            }

            return null;
        }

        private CommandResult CreateTransportFailureResult(string requestId, string runtimeAction, RuntimeTargetInfo targetInfo, string error)
        {
            return new CommandResult
            {
                id = requestId,
                success = false,
                error = error,
                data = new
                {
                    transport = GetTransportName(),
                    runtimeDirectory = _options.RuntimeDirectory,
                    target = targetInfo == null ? _target : targetInfo.targetId,
                    action = runtimeAction
                }
            };
        }

        private static void EnsureRequestId(CommandRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrEmpty(request.id))
            {
                request.id = PathHelper.GenerateCommandId();
            }
        }

        private string GetTransportName()
        {
            return _options.Kind.ToString().ToLowerInvariant();
        }

        private static CommandResult FinalizeRuntimeResult(CommandResult result, CommandRequest request, string runtimeAction)
        {
            if (result == null
                || !result.success
                || !string.Equals(runtimeAction, "runtime.screenshot", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            TryAttachScreenshotOutput(result, request);
            return result;
        }

        private static void TryAttachScreenshotOutput(CommandResult result, CommandRequest request)
        {
            var data = result.data as JObject;
            if (data == null)
            {
                return;
            }

            var imagePath = ReadString(data, "imagePath");
            if (string.IsNullOrEmpty(imagePath))
            {
                return;
            }

            if (!TryGetRequestParam(request, "output", out var outputPath) || string.IsNullOrWhiteSpace(outputPath))
            {
                // 未指定 output 时仅把 imagePath 规范成绝对路径，不再额外输出与其重复的 pcPath
                if (File.Exists(imagePath))
                {
                    data["imagePath"] = Path.GetFullPath(imagePath);
                }

                return;
            }

            try
            {
                if (!File.Exists(imagePath))
                {
                    result.success = false;
                    result.error = "artifact_pull_failed: Screenshot file was not found: " + imagePath;
                    return;
                }

                var fullOutputPath = Path.GetFullPath(outputPath);
                var directory = Path.GetDirectoryName(fullOutputPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 复制到 output 后，imagePath 仍指向源文件、output 指向副本，二者语义不同；不再输出与 output 重复的 pcPath
                File.Copy(imagePath, fullOutputPath, true);
                data["output"] = fullOutputPath;
                data["copiedToOutput"] = true;
                data["sha256"] = ComputeSha256(fullOutputPath);
            }
            catch (Exception ex)
            {
                result.success = false;
                result.error = "artifact_pull_failed: " + ex.Message;
            }
        }

        private static bool TryGetRequestParam(CommandRequest request, string key, out string value)
        {
            value = null;
            if (request == null || request.@params == null)
            {
                return false;
            }

            foreach (var pair in request.@params)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                    return true;
                }
            }

            return false;
        }

        private static string ReadString(JObject data, string key)
        {
            return data.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) ? token.Value<string>() : null;
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(stream);
                var builder = new System.Text.StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }
    }

    internal sealed class RuntimeDiagnosticSummary
    {
        public bool success { get; set; }
        public string summary { get; set; }
        public string transport { get; set; }
        public string targetId { get; set; }
        public object[] failedChecks { get; set; }
        public string[] suggestions { get; set; }

        public static RuntimeDiagnosticSummary FromReport(RuntimeDiagnosticReport report)
        {
            var failed = new System.Collections.Generic.List<object>();
            if (report != null && report.checks != null)
            {
                foreach (var check in report.checks)
                {
                    if (string.Equals(check.status, "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        failed.Add(new
                        {
                            name = check.name,
                            detail = check.detail,
                            fix = check.fix
                        });
                    }
                }
            }

            return new RuntimeDiagnosticSummary
            {
                success = report != null && report.success,
                summary = report == null ? null : report.summary,
                transport = report == null ? null : report.transport,
                targetId = report == null ? null : report.targetId,
                failedChecks = failed.ToArray(),
                suggestions = report == null || report.suggestions == null ? null : report.suggestions.ToArray()
            };
        }
    }
}
