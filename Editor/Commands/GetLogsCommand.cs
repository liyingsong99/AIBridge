using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;

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
$CLI get_logs [--count 100] [--logType Error|Warning] [--regex ""pattern""]
```";

        internal static List<LogEntry> GetConsoleLogsForSettingsPreview(int maxCount, string logTypeFilter, string regexPattern)
        {
            return new GetLogsCommand().GetConsoleLogs(maxCount, logTypeFilter, LogFilterMode.MinimumLevel, regexPattern);
        }

        public CommandResult Execute(CommandRequest request)
        {
            var defaultSettings = AIBridgeProjectSettings.Instance.LogRetrieval;
            var hasLogType = request.HasParam("logType");
            var hasRegex = request.HasParam("regex");
            var count = request.HasParam("count")
                ? request.GetParam("count", defaultSettings.Count)
                : defaultSettings.Count;
            var logType = hasLogType
                ? request.GetParam("logType", defaultSettings.LogType)
                : defaultSettings.LogType;
            logType = AIBridgeProjectSettings.NormalizeLogRetrievalType(logType);
            var regexPattern = hasRegex
                ? request.GetParam<string>("regex", null)
                : (defaultSettings.RegexFilterEnabled ? defaultSettings.RegexPattern : null);

            try
            {
                // 面板默认值使用“最低等级”语义；显式 CLI 参数保持历史精确筛选，避免破坏现有 AI 命令。
                var filterMode = hasLogType ? LogFilterMode.Exact : LogFilterMode.MinimumLevel;
                var logs = GetConsoleLogs(count, logType, filterMode, regexPattern);
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

        private List<LogEntry> GetConsoleLogs(int maxCount, string logTypeFilter, LogFilterMode filterMode, string regexPattern)
        {
            Regex regexFilter;
            var regexError = TryCreateRegex(regexPattern, out regexFilter);
            if (!string.IsNullOrEmpty(regexError))
            {
                throw new ArgumentException("Invalid regex: " + regexError);
            }

#if UNITY_2020_1_OR_NEWER
            return GetConsoleLogsForModernUnity(maxCount, logTypeFilter, filterMode, regexFilter);
#else
            return GetConsoleLogsForUnity2019(maxCount, logTypeFilter, filterMode, regexFilter);
#endif
        }

        private List<LogEntry> GetConsoleLogsForModernUnity(int maxCount, string logTypeFilter, LogFilterMode filterMode, Regex regexFilter)
        {
            try
            {
                var consoleReflection = ResolveConsoleReflectionForModernUnity();
                return ReadConsoleLogs(consoleReflection, maxCount, logTypeFilter, filterMode, regexFilter);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError("Failed to get console logs for modern Unity: " + ex.Message);
                return new List<LogEntry>();
            }
        }

        private List<LogEntry> GetConsoleLogsForUnity2019(int maxCount, string logTypeFilter, LogFilterMode filterMode, Regex regexFilter)
        {
            try
            {
                var consoleReflection = ResolveConsoleReflectionForUnity2019();
                return ReadConsoleLogs(consoleReflection, maxCount, logTypeFilter, filterMode, regexFilter);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError("Failed to get console logs for Unity 2019: " + ex.Message);
                return new List<LogEntry>();
            }
        }

        private List<LogEntry> ReadConsoleLogs(ConsoleReflection consoleReflection, int maxCount, string logTypeFilter, LogFilterMode filterMode, Regex regexFilter)
        {
            var logs = new List<LogEntry>();
            if (consoleReflection == null)
            {
                return logs;
            }

            try
            {
                var totalCount = (int)consoleReflection.GetCountMethod.Invoke(null, null);
                if (totalCount <= 0)
                {
                    return logs;
                }

                consoleReflection.StartGettingEntriesMethod.Invoke(null, null);

                try
                {
                    var startIndex = Math.Max(0, totalCount - maxCount);
                    for (var i = startIndex; i < totalCount; i++)
                    {
                        var entry = Activator.CreateInstance(consoleReflection.LogEntryType);
                        var success = (bool)consoleReflection.GetEntryInternalMethod.Invoke(null, new object[] { i, entry });
                        if (!success)
                        {
                            continue;
                        }

                        var message = GetLogMessage(consoleReflection, entry);
                        var mode = GetLogMode(consoleReflection, entry);
                        var normalizedType = NormalizeLogType(mode);

                        if (!ShouldIncludeLog(logTypeFilter, normalizedType, filterMode))
                        {
                            continue;
                        }

                        if (!ShouldIncludeByRegex(regexFilter, message))
                        {
                            continue;
                        }

                        logs.Add(new LogEntry
                        {
                            message = message,
                            type = normalizedType
                        });
                    }
                }
                finally
                {
                    consoleReflection.EndGettingEntriesMethod.Invoke(null, null);
                }
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError("Failed to read Unity console logs: " + ex.Message);
            }

            return logs;
        }

        private string TryCreateRegex(string pattern, out Regex regex)
        {
            regex = null;
            if (string.IsNullOrEmpty(pattern))
            {
                return null;
            }

            try
            {
                regex = new Regex(pattern);
                return null;
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }

        private ConsoleReflection ResolveConsoleReflectionForModernUnity()
        {
            var editorAssembly = Assembly.GetAssembly(typeof(UnityEditor.Editor));
            if (editorAssembly == null)
            {
                AIBridgeLogger.LogError("Failed to resolve UnityEditor assembly for get_logs.");
                return null;
            }

            var logEntriesType = editorAssembly.GetType("UnityEditor.LogEntries");
            var logEntryType = editorAssembly.GetType("UnityEditor.LogEntry");
            if (logEntriesType == null || logEntryType == null)
            {
                AIBridgeLogger.LogError("Failed to resolve modern Unity console reflection types for get_logs.");
                return null;
            }

            var methodFlags = BindingFlags.Public | BindingFlags.Static;
            var fieldFlags = BindingFlags.Public | BindingFlags.Instance;
            return BuildConsoleReflection(logEntriesType, logEntryType, methodFlags, fieldFlags, "message", "condition");
        }

        private ConsoleReflection ResolveConsoleReflectionForUnity2019()
        {
            var editorAssembly = Assembly.GetAssembly(typeof(UnityEditor.Editor));
            if (editorAssembly == null)
            {
                AIBridgeLogger.LogError("Failed to resolve UnityEditor assembly for get_logs.");
                return null;
            }

            var logEntriesType = ResolveType(editorAssembly, "UnityEditorInternal.LogEntries", "UnityEditor.LogEntries");
            var logEntryType = ResolveType(editorAssembly, "UnityEditorInternal.LogEntry", "UnityEditor.LogEntry");
            if (logEntriesType == null || logEntryType == null)
            {
                AIBridgeLogger.LogError("Failed to resolve Unity 2019 console reflection types for get_logs.");
                return null;
            }

            var methodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var fieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            return BuildConsoleReflection(logEntriesType, logEntryType, methodFlags, fieldFlags, "condition", "message");
        }

        private ConsoleReflection BuildConsoleReflection(
            Type logEntriesType,
            Type logEntryType,
            BindingFlags methodFlags,
            BindingFlags fieldFlags,
            string primaryMessageFieldName,
            string fallbackMessageFieldName)
        {
            var getCountMethod = logEntriesType.GetMethod("GetCount", methodFlags);
            var startGettingEntriesMethod = logEntriesType.GetMethod("StartGettingEntries", methodFlags);
            var endGettingEntriesMethod = logEntriesType.GetMethod("EndGettingEntries", methodFlags);
            var getEntryInternalMethod = logEntriesType.GetMethod("GetEntryInternal", methodFlags);

            if (getCountMethod == null ||
                startGettingEntriesMethod == null ||
                endGettingEntriesMethod == null ||
                getEntryInternalMethod == null)
            {
                AIBridgeLogger.LogError("Failed to resolve Unity console reflection methods for get_logs.");
                return null;
            }

            var primaryMessageField = logEntryType.GetField(primaryMessageFieldName, fieldFlags);
            var fallbackMessageField = logEntryType.GetField(fallbackMessageFieldName, fieldFlags);
            var modeField = logEntryType.GetField("mode", fieldFlags);

            if (modeField == null)
            {
                AIBridgeLogger.LogError("Failed to resolve Unity console mode field for get_logs.");
                return null;
            }

            if (primaryMessageField == null && fallbackMessageField == null)
            {
                AIBridgeLogger.LogError("Failed to resolve Unity console message fields for get_logs.");
                return null;
            }

            return new ConsoleReflection
            {
                LogEntryType = logEntryType,
                GetCountMethod = getCountMethod,
                StartGettingEntriesMethod = startGettingEntriesMethod,
                EndGettingEntriesMethod = endGettingEntriesMethod,
                GetEntryInternalMethod = getEntryInternalMethod,
                PrimaryMessageField = primaryMessageField,
                FallbackMessageField = fallbackMessageField,
                ModeField = modeField
            };
        }

        private Type ResolveType(Assembly assembly, params string[] typeNames)
        {
            for (var i = 0; i < typeNames.Length; i++)
            {
                var resolvedType = assembly.GetType(typeNames[i]);
                if (resolvedType != null)
                {
                    return resolvedType;
                }
            }

            return null;
        }

        private string GetLogMessage(ConsoleReflection consoleReflection, object entry)
        {
            // 2019.4 优先读 condition，缺失或为空再退回 message；高版本则优先读 message。
            var primaryMessage = GetStringFieldValue(consoleReflection.PrimaryMessageField, entry);
            if (!string.IsNullOrEmpty(primaryMessage))
            {
                return primaryMessage;
            }

            var fallbackMessage = GetStringFieldValue(consoleReflection.FallbackMessageField, entry);
            return fallbackMessage ?? string.Empty;
        }

        private string GetStringFieldValue(FieldInfo fieldInfo, object entry)
        {
            if (fieldInfo == null)
            {
                return null;
            }

            return fieldInfo.GetValue(entry) as string;
        }

        private int GetLogMode(ConsoleReflection consoleReflection, object entry)
        {
            var modeValue = consoleReflection.ModeField.GetValue(entry);
            if (modeValue is int intMode)
            {
                return intMode;
            }

            return 0;
        }

        private bool ShouldIncludeLog(string logTypeFilter, string entryType, LogFilterMode filterMode)
        {
            if (string.IsNullOrEmpty(logTypeFilter) || string.Equals(logTypeFilter, "all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (filterMode == LogFilterMode.MinimumLevel)
            {
                return GetLogSeverityRank(entryType) >= GetLogSeverityRank(logTypeFilter);
            }

            return string.Equals(entryType, logTypeFilter, StringComparison.OrdinalIgnoreCase);
        }

        private bool ShouldIncludeByRegex(Regex regexFilter, string message)
        {
            if (regexFilter == null)
            {
                return true;
            }

            return regexFilter.IsMatch(message ?? string.Empty);
        }

        private int GetLogSeverityRank(string logType)
        {
            if (string.Equals(logType, "Error", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (string.Equals(logType, "Warning", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            return 1;
        }

        private string NormalizeLogType(int mode)
        {
            var flags = (KnownLogMessageFlags)mode;

            if ((flags & ErrorLogMask) != 0)
            {
                return "Error";
            }

            if ((flags & WarningLogMask) != 0)
            {
                return "Warning";
            }

            return "Log";
        }

        [Serializable]
        internal class LogEntry
        {
            public string message;
            public string type;
        }

        private enum LogFilterMode
        {
            Exact,
            MinimumLevel
        }

        /// <summary>
        /// 缓存一次 Console 反射所需的类型、方法和字段，避免版本分支逻辑污染主流程。
        /// </summary>
        private class ConsoleReflection
        {
            public Type LogEntryType;
            public MethodInfo GetCountMethod;
            public MethodInfo StartGettingEntriesMethod;
            public MethodInfo EndGettingEntriesMethod;
            public MethodInfo GetEntryInternalMethod;
            public FieldInfo PrimaryMessageField;
            public FieldInfo FallbackMessageField;
            public FieldInfo ModeField;
        }

        /// <summary>
        /// 对齐 Unity 官方 LogMessageFlags，仅用于业务归类，不暴露内部位标志细节。
        /// </summary>
        [Flags]
        private enum KnownLogMessageFlags
        {
            None = 0,
            Error = 1 << 0,
            Assert = 1 << 1,
            Log = 1 << 2,
            Warning = 1 << 3,
            Fatal = 1 << 4,
            AssetImportError = 1 << 6,
            AssetImportWarning = 1 << 7,
            ScriptingError = 1 << 8,
            ScriptingWarning = 1 << 9,
            ScriptingLog = 1 << 10,
            ScriptCompileError = 1 << 11,
            ScriptCompileWarning = 1 << 12,
            ScriptingException = 1 << 17,
            ScriptingAssertion = 1 << 21
        }

        private const KnownLogMessageFlags ErrorLogMask =
            KnownLogMessageFlags.Error |
            KnownLogMessageFlags.Assert |
            KnownLogMessageFlags.Fatal |
            KnownLogMessageFlags.AssetImportError |
            KnownLogMessageFlags.ScriptingError |
            KnownLogMessageFlags.ScriptCompileError |
            KnownLogMessageFlags.ScriptingAssertion |
            KnownLogMessageFlags.ScriptingException;

        private const KnownLogMessageFlags WarningLogMask =
            KnownLogMessageFlags.Warning |
            KnownLogMessageFlags.AssetImportWarning |
            KnownLogMessageFlags.ScriptingWarning |
            KnownLogMessageFlags.ScriptCompileWarning;
    }
}
