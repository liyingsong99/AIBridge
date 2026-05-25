using System;
using System.IO;
using System.Text;
using System.Threading;
using AIBridgeCLI.Commands;
using Newtonsoft.Json;

namespace AIBridgeCLI.Core
{
    /// <summary>
    /// Handles sending commands and receiving results
    /// </summary>
    public class CommandSender
    {
        private readonly string _commandsDir;
        private readonly string _resultsDir;
        private readonly int _timeout;
        private readonly int _pollInterval;
        private readonly string _onDialog;
        private readonly BatchDialogAutoClickPlan _dialogAutoClickPlan;

        /// <summary>
        /// Create a new CommandSender
        /// </summary>
        /// <param name="timeout">Timeout in milliseconds (default: 5000)</param>
        /// <param name="pollInterval">Poll interval in milliseconds (default: 50)</param>
        public CommandSender(
            int timeout = 5000,
            string onDialog = null,
            int pollInterval = 50,
            BatchDialogAutoClickPlan dialogAutoClickPlan = null)
        {
            _commandsDir = PathHelper.GetCommandsDirectory();
            _resultsDir = PathHelper.GetResultsDirectory();
            _timeout = timeout;
            _onDialog = onDialog;
            _pollInterval = pollInterval;
            _dialogAutoClickPlan = dialogAutoClickPlan;

            PathHelper.EnsureDirectoriesExist();
        }

        /// <summary>
        /// Send a command and wait for result
        /// </summary>
        public CommandResult SendCommand(CommandRequest request)
        {
            if (string.IsNullOrEmpty(request.id))
            {
                request.id = PathHelper.GenerateCommandId();
            }

            var autoPreflightDialogDiagnostic = HandleAutoDialogsUntilClear(request.id, isPreflight: true);
            if (autoPreflightDialogDiagnostic != null)
            {
                return CreateDialogFailure(request.id, autoPreflightDialogDiagnostic);
            }

            var preflightDialogDiagnostic = HandleBlockingDialog(isPreflight: true);
            if (preflightDialogDiagnostic != null)
            {
                return CreateDialogFailure(request.id, preflightDialogDiagnostic);
            }

            var commandFile = Path.Combine(_commandsDir, $"{request.id}.json");
            var resultFile = Path.Combine(_resultsDir, $"{request.id}.json");

            // Write command file with UTF-8 encoding (no BOM)
            var json = JsonConvert.SerializeObject(request, Formatting.None);
            File.WriteAllText(commandFile, json, new UTF8Encoding(false));

            // Wait for result
            var startTime = DateTime.Now;
            var lastAutoDialogPoll = DateTime.MinValue;
            while ((DateTime.Now - startTime).TotalMilliseconds < _timeout)
            {
                if (File.Exists(resultFile))
                {
                    // Small delay to ensure file is fully written
                    Thread.Sleep(10);

                    try
                    {
                        var resultJson = File.ReadAllText(resultFile, Encoding.UTF8);
                        var result = JsonConvert.DeserializeObject<CommandResult>(resultJson);

                        // Clean up result file
                        try { File.Delete(resultFile); } catch { }

                        return result;
                    }
                    catch (IOException)
                    {
                        // File might still be locked, retry
                        Thread.Sleep(_pollInterval);
                        continue;
                    }
                }

                if (ShouldPollAutoDialog(ref lastAutoDialogPoll))
                {
                    bool handled;
                    var autoDialogDiagnostic = TryHandleAutoClickDialog(request.id, isPreflight: false, out handled);
                    if (autoDialogDiagnostic != null)
                    {
                        return CreateDialogFailure(request.id, autoDialogDiagnostic);
                    }
                }

                Thread.Sleep(_pollInterval);
            }

            // Timeout - clean up command file if still exists
            var dialogDiagnostic = HandleBlockingDialog(isPreflight: false);
            if (dialogDiagnostic != null)
            {
                return CreateDialogFailure(request.id, dialogDiagnostic);
            }

            try { File.Delete(commandFile); } catch { }

            var timeoutError = $"Timeout waiting for result after {_timeout}ms. Make sure Unity Editor is running and AIBridge is active.";
            if (request != null
                && string.Equals(request.type, "code", StringComparison.OrdinalIgnoreCase)
                && request.@params != null
                && request.@params.TryGetValue("action", out var actionValue)
                && string.Equals(actionValue?.ToString(), "execute", StringComparison.OrdinalIgnoreCase))
            {
                timeoutError += " The code may still be running in Unity; run `code status` to inspect it or `code cancel` to release AIBridge waiting state.";
            }

            return new CommandResult
            {
                id = request.id,
                success = false,
                error = timeoutError
            };
        }

        /// <summary>
        /// Send a command without waiting for result
        /// </summary>
        public string SendCommandNoWait(CommandRequest request)
        {
            return TrySendCommandNoWait(request).id;
        }

        public CommandResult TrySendCommandNoWait(CommandRequest request)
        {
            if (string.IsNullOrEmpty(request.id))
            {
                request.id = PathHelper.GenerateCommandId();
            }

            var preflightDialogDiagnostic = HandleBlockingDialog(isPreflight: true);
            if (preflightDialogDiagnostic != null)
            {
                return CreateDialogFailure(request.id, preflightDialogDiagnostic);
            }

            var commandFile = Path.Combine(_commandsDir, $"{request.id}.json");

            // Write command file with UTF-8 encoding (no BOM)
            var json = JsonConvert.SerializeObject(request, Formatting.None);
            File.WriteAllText(commandFile, json, new UTF8Encoding(false));

            return new CommandResult
            {
                id = request.id,
                success = true,
                data = new
                {
                    id = request.id,
                    status = "sent"
                }
            };
        }

        /// <summary>
        /// Check if a result is available for a given command ID
        /// </summary>
        public CommandResult TryGetResult(string commandId)
        {
            var resultFile = Path.Combine(_resultsDir, $"{commandId}.json");

            if (!File.Exists(resultFile))
            {
                return null;
            }

            try
            {
                var resultJson = File.ReadAllText(resultFile, Encoding.UTF8);
                var result = JsonConvert.DeserializeObject<CommandResult>(resultJson);

                // Clean up result file
                try { File.Delete(resultFile); } catch { }

                return result;
            }
            catch
            {
                return null;
            }
        }

        private BlockingDialogDiagnostic HandleBlockingDialog(bool isPreflight)
        {
            var status = DialogService.GetStatus();
            if (status == null)
            {
                return null;
            }

            if (!status.success)
            {
                if (isPreflight)
                {
                    return null;
                }

                if (string.Equals(status.errorCode, "macos_accessibility_permission_required", StringComparison.OrdinalIgnoreCase))
                {
                    // macOS 没有辅助功能权限时，先保留命令文件，避免用户授权后原请求丢失。
                    return new BlockingDialogDiagnostic
                    {
                        Error = "Unity did not respond, and dialog inspection requires macOS Accessibility permission.",
                        Data = status
                    };
                }

                return null;
            }

            if (!DialogService.HasBlockingDialog(status))
            {
                return null;
            }

            // 检测到模态弹窗时，不删除原始命令文件，避免关闭弹窗后请求丢失。
            var normalizedAction = DialogService.NormalizeChoice(_onDialog);
            if (string.IsNullOrWhiteSpace(normalizedAction) || normalizedAction == "none")
            {
                return new BlockingDialogDiagnostic
                {
                    Error = "Unity is blocked by a modal dialog.",
                    Data = status
                };
            }

            if (normalizedAction == "wait")
            {
                return new BlockingDialogDiagnostic
                {
                    Error = "Unity is blocked by a modal dialog.",
                    Data = new
                    {
                        dialog = status,
                        wait = DialogService.Wait(_timeout)
                    }
                };
            }

            var click = DialogService.Click(normalizedAction, null, null);
            return new BlockingDialogDiagnostic
            {
                Error = "Unity is blocked by a modal dialog.",
                Data = new
                {
                    dialog = status,
                    click = click
                }
            };
        }

        private BlockingDialogDiagnostic HandleAutoDialogsUntilClear(string requestId, bool isPreflight)
        {
            for (var i = 0; i < 10; i++)
            {
                bool handled;
                var diagnostic = TryHandleAutoClickDialog(requestId, isPreflight, out handled);
                if (diagnostic != null)
                {
                    return diagnostic;
                }

                if (!handled)
                {
                    return null;
                }

                Thread.Sleep(_pollInterval);
            }

            return null;
        }

        private bool ShouldPollAutoDialog(ref DateTime lastPollTime)
        {
            if (_dialogAutoClickPlan == null || !_dialogAutoClickPlan.HasRules)
            {
                return false;
            }

            var now = DateTime.Now;
            if ((now - lastPollTime).TotalMilliseconds < 100)
            {
                return false;
            }

            lastPollTime = now;
            return true;
        }

        private BlockingDialogDiagnostic TryHandleAutoClickDialog(string requestId, bool isPreflight, out bool handled)
        {
            handled = false;
            if (_dialogAutoClickPlan == null || !_dialogAutoClickPlan.HasRules)
            {
                return null;
            }

            var targets = _dialogAutoClickPlan.GetActiveTargets(requestId, isPreflight);
            if (targets == null || targets.Count == 0)
            {
                return null;
            }

            // batch 内的弹窗会阻塞 Unity 主线程，所以这里由 CLI 进程按脚本状态代为点击。
            var status = DialogService.GetStatus();
            if (status == null || !status.success || !DialogService.HasBlockingDialog(status))
            {
                return null;
            }

            if (status.dialogs != null)
            {
                foreach (var dialog in status.dialogs)
                {
                    foreach (var target in targets)
                    {
                        if (target == null || string.IsNullOrWhiteSpace(target.Value))
                        {
                            continue;
                        }

                        var button = DialogService.SelectButton(dialog, target.Value, target.Value);
                        if (button == null)
                        {
                            continue;
                        }

                        var click = DialogService.Click(target.Value, target.Value, dialog.id);
                        if (click.success)
                        {
                            handled = true;
                            return null;
                        }

                        return new BlockingDialogDiagnostic
                        {
                            Error = "Unity is blocked by a modal dialog, and batch dialog auto-click failed.",
                            Data = new
                            {
                                dialog = status,
                                click = click,
                                autoClickTargets = GetTargetValues(targets)
                            }
                        };
                    }
                }
            }

            return new BlockingDialogDiagnostic
            {
                Error = "Unity is blocked by a modal dialog, but no button matched the active batch dialog click rule.",
                Data = new
                {
                    dialog = status,
                    autoClickTargets = GetTargetValues(targets)
                }
            };
        }

        private static string[] GetTargetValues(System.Collections.Generic.List<DialogAutoClickTarget> targets)
        {
            if (targets == null)
            {
                return new string[0];
            }

            var values = new System.Collections.Generic.List<string>();
            foreach (var target in targets)
            {
                if (target != null && !string.IsNullOrWhiteSpace(target.Value))
                {
                    values.Add(target.Value);
                }
            }

            return values.ToArray();
        }

        private static CommandResult CreateDialogFailure(string requestId, BlockingDialogDiagnostic diagnostic)
        {
            return new CommandResult
            {
                id = requestId,
                success = false,
                error = diagnostic.Error,
                data = diagnostic.Data
            };
        }

        private class BlockingDialogDiagnostic
        {
            public string Error { get; set; }
            public object Data { get; set; }
        }
    }
}
