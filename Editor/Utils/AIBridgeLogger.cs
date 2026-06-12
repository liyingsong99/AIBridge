using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AIBridge.Editor
{
    /// <summary>
    /// Logger utility for AI Bridge with consistent prefix
    /// </summary>
    public static class AIBridgeLogger
    {
        private const string PREFIX = "[AIBridge]";
        private static EditorOption<bool> _debugEnabledOption;

        /// <summary>
        /// Enable or disable debug logging
        /// </summary>
        public static bool DebugEnabled
        {
            get
            {
                if (_debugEnabledOption == null)
                {
                    _debugEnabledOption = new EditorOption<bool>("AIBridge_DebugLogging", false, ReadDebugEnabled, WriteDebugEnabled);
                }
                return _debugEnabledOption.Value;
            }
            set
            {
                if (_debugEnabledOption == null)
                {
                    _debugEnabledOption = new EditorOption<bool>("AIBridge_DebugLogging", false, ReadDebugEnabled, WriteDebugEnabled);
                }
                _debugEnabledOption.Value = value;
            }
        }

        public static void LogDebug(string message)
        {
            if (DebugEnabled)
            {
                Debug.Log($"{PREFIX} {message}");
            }
        }

        public static void LogInfo(string message)
        {
            Debug.Log($"{PREFIX} {message}");
        }

        public static void LogWarning(string message)
        {
            Debug.LogWarning($"{PREFIX} {message}");
        }

        public static void LogError(string message)
        {
            Debug.LogError($"{PREFIX} {message}");
        }

        public static string FormatStartupTimingMessage(string scope, string step, long elapsedMs)
        {
            return "[StartupTiming] scope=" + (scope ?? "unknown")
                + " step=" + (step ?? "unknown")
                + " elapsedMs=" + Math.Max(0, elapsedMs);
        }

        public static void LogStartupTiming(string scope, string step, long elapsedMs)
        {
            LogInfo(FormatStartupTimingMessage(scope, step, elapsedMs));
        }

        public static void LogStartupTiming(string scope, string step, Stopwatch stopwatch)
        {
            if (stopwatch == null)
            {
                return;
            }

            LogStartupTiming(scope, step, stopwatch.ElapsedMilliseconds);
        }

        public static void MeasureStartupTiming(string scope, string step, Action action)
        {
            if (action == null)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                action();
            }
            finally
            {
                LogStartupTiming(scope, step, stopwatch);
            }
        }

        private static bool ReadDebugEnabled(string key, bool defaultValue)
        {
            return AIBridgeProjectSettings.Instance.DebugLogging;
        }

        private static void WriteDebugEnabled(string key, bool value)
        {
            var settings = AIBridgeProjectSettings.Instance;
            if (settings.DebugLogging == value)
            {
                return;
            }

            settings.DebugLogging = value;
            settings.SaveSettings();
        }
    }
}
