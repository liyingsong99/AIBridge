using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Editor;
using AIBridge.Internal.Json;
using AIBridge.Runtime.Internal;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace AIBridge.Editor.ScriptExecution
{
    /// <summary>
    /// 脚本执行器，负责逐行执行脚本并管理执行状态
    /// </summary>
    [InitializeOnLoad]
    public class ScriptExecutor
    {
        private static ScriptExecutor _instance;
        private ScriptExecutionState _state;
        private List<IScriptCommand> _commands;
        private bool _isExecuting;

        /// <summary>
        /// 当前执行状态
        /// </summary>
        public static ScriptExecutionState CurrentState => Instance._state;

        /// <summary>
        /// 是否正在执行
        /// </summary>
        public static bool IsExecuting => Instance._isExecuting;

        /// <summary>
        /// 日志更新事件
        /// </summary>
        public static event Action<string> OnLogUpdated;

        /// <summary>
        /// 状态更新事件
        /// </summary>
        public static event Action<ExecutionStatus> OnStatusChanged;

        private static ScriptExecutor Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new ScriptExecutor();
                }

                return _instance;
            }
        }

        static ScriptExecutor()
        {
            // 编辑器启动时自动恢复执行状态
            EditorApplication.delayCall += () =>
            {
                Instance.Initialize();
            };
        }

        private ScriptExecutor()
        {
            _state = new ScriptExecutionState();
            _commands = new List<IScriptCommand>();

            // 订阅编译事件
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        private void Initialize()
        {
            // 加载上次的执行状态
            _state = ScriptExecutionState.Load();

            if (_state.Status == ExecutionStatus.Running)
            {
                Log($"检测到未完成的脚本执行，自动恢复: {_state.ScriptPath}");
                ResumeExecution();
            }
            else if (_state.Status == ExecutionStatus.Paused && _state.PausedByCompilation)
            {
                Log($"检测到编译中断的脚本执行，等待编译结束后恢复: {_state.ScriptPath}");
                ResumeAfterCompilationIfNeeded();
            }
        }

        /// <summary>
        /// 开始执行脚本
        /// </summary>
        public static void Execute(string scriptPath)
        {
            Instance.ExecuteInternal(scriptPath, null, false);
        }

        /// <summary>
        /// 开始执行 batch 脚本，并关联结果写回信息
        /// </summary>
        public static void Execute(string scriptPath, string batchRequestId, bool deleteAfterExecution)
        {
            Instance.ExecuteInternal(scriptPath, batchRequestId, deleteAfterExecution);
        }

        /// <summary>
        /// 暂停执行
        /// </summary>
        public static void Pause()
        {
            Instance.PauseInternal();
        }

        /// <summary>
        /// 恢复执行
        /// </summary>
        public static void Resume()
        {
            Instance.ResumeExecution();
        }

        /// <summary>
        /// 停止执行
        /// </summary>
        public static void Stop()
        {
            Instance.StopInternal();
        }

        private void ExecuteInternal(string scriptPath, string batchRequestId, bool deleteAfterExecution)
        {
            if (_isExecuting)
            {
                Log("已有脚本正在执行，请先停止当前脚本");
                return;
            }

            _state = new ScriptExecutionState
            {
                ScriptPath = scriptPath,
                CurrentLine = 0,
                Status = ExecutionStatus.Running,
                StartTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                PausedByCompilation = false,
                BatchRequestId = batchRequestId,
                DeleteScriptAfterExecution = deleteAfterExecution
            };

            try
            {
                // 先落盘状态，再开始解析，这样即使解析失败也能保留 batch 结果写回所需上下文。
                _state.Save();

                // 解析脚本
                Log($"开始解析脚本: {scriptPath}");
                _commands = ScriptParser.Parse(scriptPath);
                Log($"脚本解析完成，共 {_commands.Count} 条命令");

                _state.Save();
                _isExecuting = true;
                NotifyStatusChanged(ExecutionStatus.Running);

                // 开始执行
                EditorApplication.update += ExecuteNextCommand;
            }
            catch (Exception ex)
            {
                LogError($"脚本执行失败: {ex.Message}");
                _isExecuting = false;
                _state.Status = ExecutionStatus.Error;
                _state.ErrorMessage = ex.Message;
                _state.Save();
                TryWriteBatchResultForCurrentState();
                NotifyStatusChanged(ExecutionStatus.Error);
            }
        }

        private void ResumeExecution()
        {
            if (_isExecuting)
            {
                Log("脚本已在执行中");
                return;
            }

            if (_state.Status != ExecutionStatus.Running && _state.Status != ExecutionStatus.Paused)
            {
                Log("没有可恢复的脚本执行");
                return;
            }

            if (EditorApplication.isCompiling && _state.PausedByCompilation)
            {
                EditorApplication.delayCall += ResumeAfterCompilationIfNeeded;
                return;
            }

            try
            {
                // 重新解析脚本
                Log($"恢复执行脚本: {_state.ScriptPath}，从第 {_state.CurrentLine + 1} 行开始");
                _commands = ScriptParser.Parse(_state.ScriptPath);

                _state.Status = ExecutionStatus.Running;
                _state.PausedByCompilation = false;
                _state.Save();

                _isExecuting = true;
                NotifyStatusChanged(ExecutionStatus.Running);

                // 继续执行
                EditorApplication.update += ExecuteNextCommand;
            }
            catch (Exception ex)
            {
                LogError($"恢复执行失败: {ex.Message}");
                _isExecuting = false;
                _state.Status = ExecutionStatus.Error;
                _state.ErrorMessage = ex.Message;
                _state.Save();
                TryWriteBatchResultForCurrentState();
                NotifyStatusChanged(ExecutionStatus.Error);
            }
        }

        private void PauseInternal()
        {
            PauseInternal(false);
        }

        private void PauseInternal(bool pausedByCompilation)
        {
            if (!_isExecuting)
            {
                Log("没有正在执行的脚本");
                return;
            }

            Log("暂停脚本执行");
            _isExecuting = false;
            _state.Status = ExecutionStatus.Paused;
            _state.PausedByCompilation = pausedByCompilation;
            _state.Save();

            EditorApplication.update -= ExecuteNextCommand;
            NotifyStatusChanged(ExecutionStatus.Paused);
        }

        private void StopInternal()
        {
            if (!_isExecuting && _state.Status != ExecutionStatus.Paused)
            {
                Log("没有正在执行的脚本");
                return;
            }

            Log("停止脚本执行");
            _isExecuting = false;
            _state.Status = ExecutionStatus.Idle;
            _state.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _state.PausedByCompilation = false;
            _state.Save();
            TryWriteBatchStoppedResult();

            EditorApplication.update -= ExecuteNextCommand;
            NotifyStatusChanged(ExecutionStatus.Idle);

            // 清除状态
            ScriptExecutionState.Clear();
        }

        private void ExecuteNextCommand()
        {
            if (!_isExecuting || _state.Status != ExecutionStatus.Running)
            {
                EditorApplication.update -= ExecuteNextCommand;
                return;
            }

            // 检查是否执行完成
            if (_state.CurrentLine >= _commands.Count)
            {
                Log("脚本执行完成");
                _isExecuting = false;
                _state.Status = ExecutionStatus.Completed;
                _state.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _state.Save();
                TryWriteBatchResultForCurrentState();

                EditorApplication.update -= ExecuteNextCommand;
                NotifyStatusChanged(ExecutionStatus.Completed);

                // 清除状态
                ScriptExecutionState.Clear();
                return;
            }

            try
            {
                // 执行当前命令
                var command = _commands[_state.CurrentLine];
                Log($"[{_state.CurrentLine + 1}/{_commands.Count}] 执行命令: {command.Type}");

                var context = new ScriptExecutionContext
                {
                    ScriptPath = _state.ScriptPath,
                    CurrentLine = _state.CurrentLine,
                    LogCallback = Log,
                    Variables = _state.Variables
                };

                var result = command.Execute(context);

                if (!result.Success)
                {
                    LogError($"命令执行失败: {result.Message}");
                    _isExecuting = false;
                    _state.Status = ExecutionStatus.Error;
                    _state.ErrorMessage = result.Message;
                    _state.Save();
                    TryWriteBatchResultForCurrentState();

                    EditorApplication.update -= ExecuteNextCommand;
                    NotifyStatusChanged(ExecutionStatus.Error);
                    return;
                }

                _state.Variables = context.Variables ?? new Dictionary<string, string>();

                // 等待类脚本命令用 Pending 表示本行尚未完成，下一帧继续重试同一行，避免阻塞 Unity 主线程。
                if (result.Pending)
                {
                    _state.Save();
                    return;
                }

                // 移动到下一行
                _state.CurrentLine++;
                _state.Save();
            }
            catch (Exception ex)
            {
                LogError($"执行命令时发生异常: {ex.Message}");
                _isExecuting = false;
                _state.Status = ExecutionStatus.Error;
                _state.ErrorMessage = ex.Message;
                _state.Save();
                TryWriteBatchResultForCurrentState();

                EditorApplication.update -= ExecuteNextCommand;
                NotifyStatusChanged(ExecutionStatus.Error);
            }
        }

        private void OnCompilationStarted(object obj)
        {
            // 编译开始时暂停执行
            if (_isExecuting)
            {
                Log("检测到编译开始，暂停脚本执行");
                PauseInternal(true);
            }
        }

        private void OnCompilationFinished(object obj)
        {
            if (_state.Status == ExecutionStatus.Paused && _state.PausedByCompilation)
            {
                EditorApplication.delayCall += ResumeAfterCompilationIfNeeded;
            }
        }

        /// <summary>
        /// 编译恢复要等 Unity 完全退出 compiling 状态后再继续，
        /// 否则会在域重载边界上出现恢复过早的问题。
        /// </summary>
        private void ResumeAfterCompilationIfNeeded()
        {
            if (_isExecuting)
            {
                return;
            }

            if (_state.Status != ExecutionStatus.Paused || !_state.PausedByCompilation)
            {
                return;
            }

            if (EditorApplication.isCompiling)
            {
                EditorApplication.delayCall += ResumeAfterCompilationIfNeeded;
                return;
            }

            Log("检测到编译完成，恢复脚本执行");
            ResumeExecution();
        }

        private void TryWriteBatchStoppedResult()
        {
            if (string.IsNullOrEmpty(_state.BatchRequestId))
            {
                return;
            }

            TryDeleteBatchScriptIfNeeded();
            WriteBatchResultFile(CommandResult.Failure(_state.BatchRequestId, "Script execution was stopped."));
        }

        private void TryWriteBatchResultForCurrentState()
        {
            if (string.IsNullOrEmpty(_state.BatchRequestId))
            {
                return;
            }

            TryDeleteBatchScriptIfNeeded();

            CommandResult result;
            if (_state.Status == ExecutionStatus.Completed)
            {
                result = CommandResult.Success(_state.BatchRequestId, new
                {
                    scriptPath = _state.ScriptPath,
                    status = "completed",
                    startTime = _state.StartTime,
                    endTime = _state.EndTime,
                    logs = _state.Logs
                });
            }
            else
            {
                result = CommandResult.Failure(_state.BatchRequestId, $"Script execution failed: {_state.ErrorMessage}");
            }

            WriteBatchResultFile(result);
        }

        private void TryDeleteBatchScriptIfNeeded()
        {
            if (!_state.DeleteScriptAfterExecution || string.IsNullOrEmpty(_state.ScriptPath) || !File.Exists(_state.ScriptPath))
            {
                return;
            }

            try
            {
                File.Delete(_state.ScriptPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ScriptExecutor] 删除临时脚本文件失败: {ex.Message}");
            }
        }

        private static void WriteBatchResultFile(CommandResult result)
        {
            try
            {
                var resultsDir = Path.Combine(AIBridge.BridgeDirectory, "results");
                if (!Directory.Exists(resultsDir))
                {
                    Directory.CreateDirectory(resultsDir);
                }

                var filePath = Path.Combine(resultsDir, result.id + ".json");
                var json = AIBridgeJson.Serialize(result, pretty: true);
                AIBridgeAtomicFile.WriteTextAtomic(filePath, json, System.Text.Encoding.UTF8);

                AIBridgeLogger.LogInfo($"Batch script result written: {result.id}, success={result.success}");
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"Failed to write batch result for {result.id}: {ex.Message}");
            }
        }

        private static void Log(string message)
        {
            Debug.Log($"[ScriptExecutor] {message}");
            Instance._state.AddLog(message);
            Instance._state.Save();
            OnLogUpdated?.Invoke(message);
        }

        private static void LogError(string message)
        {
            Debug.LogError($"[ScriptExecutor] {message}");
            Instance._state.AddLog($"[ERROR] {message}");
            Instance._state.Save();
            OnLogUpdated?.Invoke($"[ERROR] {message}");
        }

        private static void NotifyStatusChanged(ExecutionStatus status)
        {
            OnStatusChanged?.Invoke(status);
        }
    }
}
