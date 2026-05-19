using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Internal.Json;

namespace AIBridge.Editor.ScriptExecution
{
    /// <summary>
    /// 脚本执行状态，用于持久化和恢复
    /// </summary>
    [Serializable]
    public class ScriptExecutionState
    {
        /// <summary>
        /// 当前脚本路径
        /// </summary>
        public string ScriptPath { get; set; }

        /// <summary>
        /// 当前执行行号（从0开始）
        /// </summary>
        public int CurrentLine { get; set; }

        /// <summary>
        /// 执行状态
        /// </summary>
        public ExecutionStatus Status { get; set; }

        /// <summary>
        /// 开始时间
        /// </summary>
        public string StartTime { get; set; }

        /// <summary>
        /// 结束时间
        /// </summary>
        public string EndTime { get; set; }

        /// <summary>
        /// 执行日志（最近100条）
        /// </summary>
        public List<string> Logs { get; set; }

        /// <summary>
        /// 错误信息
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 是否因编译而暂停。
        /// 用于区分“用户手动暂停”和“编译触发的自动暂停”，避免错误地自动恢复手动暂停的脚本。
        /// </summary>
        public bool PausedByCompilation { get; set; }

        /// <summary>
        /// 关联的 batch 请求 ID。
        /// 存在该值时，脚本终态需要写回 .aibridge/results。
        /// </summary>
        public string BatchRequestId { get; set; }

        /// <summary>
        /// batch 脚本结束后是否删除脚本文件。
        /// </summary>
        public bool DeleteScriptAfterExecution { get; set; }

        private const string StateFilePath = ".aibridge/script-state.json";

        public ScriptExecutionState()
        {
            Logs = new List<string>();
            Status = ExecutionStatus.Idle;
        }

        /// <summary>
        /// 保存状态到文件
        /// </summary>
        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(StateFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = AIBridgeJson.Serialize(this, pretty: true);
                File.WriteAllText(StateFilePath, json);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[ScriptExecutor] 保存状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从文件加载状态
        /// </summary>
        public static ScriptExecutionState Load()
        {
            try
            {
                if (!File.Exists(StateFilePath))
                {
                    return new ScriptExecutionState();
                }

                var json = File.ReadAllText(StateFilePath);
                var data = AIBridgeJson.DeserializeObject(json);
                return FromDictionary(data);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[ScriptExecutor] 加载状态失败: {ex.Message}");
                return new ScriptExecutionState();
            }
        }

        /// <summary>
        /// 清除状态文件
        /// </summary>
        public static void Clear()
        {
            try
            {
                if (File.Exists(StateFilePath))
                {
                    File.Delete(StateFilePath);
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[ScriptExecutor] 清除状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 添加日志（保留最近100条）
        /// </summary>
        public void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            Logs.Add($"[{timestamp}] {message}");

            // 保留最近100条日志
            if (Logs.Count > 100)
            {
                Logs.RemoveAt(0);
            }
        }

        private static ScriptExecutionState FromDictionary(Dictionary<string, object> data)
        {
            if (data == null)
            {
                return new ScriptExecutionState();
            }

            var state = new ScriptExecutionState
            {
                ScriptPath = GetString(data, nameof(ScriptPath)),
                CurrentLine = GetInt(data, nameof(CurrentLine)),
                Status = GetExecutionStatus(data, nameof(Status)),
                StartTime = GetString(data, nameof(StartTime)),
                EndTime = GetString(data, nameof(EndTime)),
                Logs = GetStringList(data, nameof(Logs)),
                ErrorMessage = GetString(data, nameof(ErrorMessage)),
                PausedByCompilation = GetBool(data, nameof(PausedByCompilation)),
                BatchRequestId = GetString(data, nameof(BatchRequestId)),
                DeleteScriptAfterExecution = GetBool(data, nameof(DeleteScriptAfterExecution))
            };

            return state;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) && value != null ? value.ToString() : null;
        }

        private static int GetInt(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null)
            {
                return 0;
            }

            return Convert.ToInt32(value);
        }

        private static bool GetBool(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null)
            {
                return false;
            }

            return Convert.ToBoolean(value);
        }

        private static ExecutionStatus GetExecutionStatus(Dictionary<string, object> data, string key)
        {
            var value = GetString(data, key);
            ExecutionStatus status;
            return Enum.TryParse(value, out status) ? status : ExecutionStatus.Idle;
        }

        private static List<string> GetStringList(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null)
            {
                return new List<string>();
            }

            var result = new List<string>();
            var values = value as IEnumerable<object>;
            if (values == null)
            {
                return result;
            }

            foreach (var item in values)
            {
                if (item != null)
                {
                    result.Add(item.ToString());
                }
            }

            return result;
        }
    }

    /// <summary>
    /// 执行状态枚举
    /// </summary>
    public enum ExecutionStatus
    {
        /// <summary>
        /// 空闲
        /// </summary>
        Idle,

        /// <summary>
        /// 运行中
        /// </summary>
        Running,

        /// <summary>
        /// 已暂停
        /// </summary>
        Paused,

        /// <summary>
        /// 已完成
        /// </summary>
        Completed,

        /// <summary>
        /// 错误
        /// </summary>
        Error
    }
}
