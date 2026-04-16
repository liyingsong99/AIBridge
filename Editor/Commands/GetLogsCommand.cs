using System;
using System.Collections.Generic;
using System.Reflection;

namespace AIBridge.Editor
{
    /// <summary>
    /// Get console logs from Unity Editor
    /// </summary>
    public class GetLogsCommand : ICommand
    {
        public string Type => "get_logs";
        public bool RequiresRefresh => false;

        public string SkillDescription => @"### `get_logs` - Get Console Logs

```bash
$CLI get_logs [--count 100] [--logType Error|Warning]
```";

        public CommandResult Execute(CommandRequest request)
        {
            var count = request.GetParam("count", 50);
            var logType = request.GetParam("logType", "all");

            try
            {
                var logs = GetConsoleLogs(count, logType);
                return CommandResult.Success(request.id, new
                {
                    logs = logs,
                    count = logs.Count
                });
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private List<LogEntry> GetConsoleLogs(int maxCount, string logTypeFilter)
        {
            var logs = new List<LogEntry>();

            try
            {
                // Use reflection to access internal LogEntries class
                var logEntriesType = System.Type.GetType("UnityEditor.LogEntries, UnityEditor");
                if (logEntriesType == null)
                {
                    return logs;
                }

                // Get log count
                var getCountMethod = logEntriesType.GetMethod("GetCount", BindingFlags.Public | BindingFlags.Static);
                if (getCountMethod == null)
                {
                    return logs;
                }

                var totalCount = (int)getCountMethod.Invoke(null, null);
                if (totalCount == 0)
                {
                    return logs;
                }

                // Start getting entries
                var startMethod = logEntriesType.GetMethod("StartGettingEntries", BindingFlags.Public | BindingFlags.Static);
                var endMethod = logEntriesType.GetMethod("EndGettingEntries", BindingFlags.Public | BindingFlags.Static);
                var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Public | BindingFlags.Static);

                if (startMethod == null || endMethod == null || getEntryMethod == null)
                {
                    return logs;
                }

                // Get LogEntry type
                var logEntryType = System.Type.GetType("UnityEditor.LogEntry, UnityEditor");
                if (logEntryType == null)
                {
                    return logs;
                }

                startMethod.Invoke(null, null);

                try
                {
                    var startIndex = Math.Max(0, totalCount - maxCount);
                    for (var i = startIndex; i < totalCount; i++)
                    {
                        var entry = Activator.CreateInstance(logEntryType);
                        var success = (bool)getEntryMethod.Invoke(null, new object[] { i, entry });

                        if (success)
                        {
                            var message = (string)logEntryType.GetField("message", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry);
                            var mode = (int)(logEntryType.GetField("mode", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) ?? 0);

                            var entryType = GetLogType(mode);

                            // Filter by log type
                            if (logTypeFilter != "all" && entryType.ToLower() != logTypeFilter.ToLower())
                            {
                                continue;
                            }

                            logs.Add(new LogEntry
                            {
                                message = message,
                                type = entryType
                            });
                        }
                    }
                }
                finally
                {
                    endMethod.Invoke(null, null);
                }
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"Failed to get console logs: {ex.Message}");
            }

            return logs;
        }

        private string GetLogType(int mode)
        {
            // Mode flags for log types
            if ((mode & (1 << 0)) != 0) return "Error";
            if ((mode & (1 << 1)) != 0) return "Assert";
            if ((mode & (1 << 2)) != 0) return "Log";
            if ((mode & (1 << 3)) != 0) return "Fatal";
            if ((mode & (1 << 4)) != 0) return "DontPreprocess";
            if ((mode & (1 << 7)) != 0) return "ScriptingError";
            if ((mode & (1 << 8)) != 0) return "ScriptingWarning";
            if ((mode & (1 << 9)) != 0) return "ScriptingLog";
            if ((mode & (1 << 11)) != 0) return "Warning";

            return "Log";
        }

        [Serializable]
        private class LogEntry
        {
            public string message;
            public string type;
        }
    }
}
