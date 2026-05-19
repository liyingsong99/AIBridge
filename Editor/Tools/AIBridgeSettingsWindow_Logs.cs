using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    public partial class AIBridgeSettingsWindow
    {
        private const int MinLogRetrievalCount = 1;
        private const int MaxLogRetrievalCount = 500;

        private int _logRetrievalCount = AIBridgeProjectSettings.DefaultLogRetrievalCount;
        private int _logTypeIndex;
        private Vector2 _logPreviewScrollPosition;
        private List<GetLogsCommand.LogEntry> _logPreviewEntries = new List<GetLogsCommand.LogEntry>();
        private string _logPreviewError;

        private void DrawLogSettingsTab()
        {
            EditorGUILayout.LabelField("日志设置", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            DrawLogRetrievalSettings();
            EditorGUILayout.Space(10);

            DrawLogPreviewActions();
            EditorGUILayout.Space(10);

            DrawLogPreview();
        }

        private void DrawLogRetrievalSettings()
        {
            var settings = AIBridgeProjectSettings.Instance.LogRetrieval;
            _logRetrievalCount = Mathf.Clamp(settings.Count, MinLogRetrievalCount, MaxLogRetrievalCount);
            _logTypeIndex = GetLogTypeIndex(settings.LogType);

            EditorGUI.BeginChangeCheck();
            _logTypeIndex = EditorGUILayout.Popup("默认日志级别", _logTypeIndex, AIBridgeProjectSettings.SupportedLogRetrievalTypes);
            _logRetrievalCount = EditorGUILayout.IntSlider("默认获取数量", _logRetrievalCount, MinLogRetrievalCount, MaxLogRetrievalCount);

            if (EditorGUI.EndChangeCheck())
            {
                SaveLogRetrievalSettings();
            }

            EditorGUILayout.HelpBox(
                "当 CLI 未显式传入 --logType 或 --count 时，get_logs 会使用这里的默认值。",
                MessageType.Info);
        }

        private void DrawLogPreviewActions()
        {
            EditorGUILayout.LabelField("日志预览", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("获取日志", GUILayout.Height(24)))
            {
                RefreshLogPreview();
            }

            if (GUILayout.Button("只看 Error", GUILayout.Height(24)))
            {
                _logTypeIndex = GetLogTypeIndex("Error");
                SaveLogRetrievalSettings();
                RefreshLogPreview();
            }

            if (GUILayout.Button("重置默认值", GUILayout.Height(24)))
            {
                ResetLogRetrievalSettings();
            }

            if (GUILayout.Button("复制 CLI 示例", GUILayout.Height(24)))
            {
                EditorGUIUtility.systemCopyBuffer = BuildLogRetrievalCliExample();
                Debug.Log("[AIBridge] get_logs CLI 示例已复制。");
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawLogPreview()
        {
            if (!string.IsNullOrEmpty(_logPreviewError))
            {
                EditorGUILayout.HelpBox(_logPreviewError, MessageType.Error);
            }

            _logPreviewScrollPosition = EditorGUILayout.BeginScrollView(_logPreviewScrollPosition, GUI.skin.box, GUILayout.Height(220));

            if (_logPreviewEntries != null && _logPreviewEntries.Count > 0)
            {
                for (var i = 0; i < _logPreviewEntries.Count; i++)
                {
                    var entry = _logPreviewEntries[i];
                    if (entry == null)
                    {
                        continue;
                    }

                    var type = string.IsNullOrEmpty(entry.type) ? "Log" : entry.type;
                    EditorGUILayout.LabelField($"[{type}] {entry.message}", EditorStyles.wordWrappedLabel);
                    EditorGUILayout.Space(2);
                }
            }
            else
            {
                EditorGUILayout.LabelField("暂无预览日志", EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private void SaveLogRetrievalSettings()
        {
            var settings = AIBridgeProjectSettings.Instance;
            var logSettings = settings.LogRetrieval;
            var newCount = Mathf.Clamp(_logRetrievalCount, MinLogRetrievalCount, MaxLogRetrievalCount);
            var newLogType = GetSelectedLogType();

            if (logSettings.Count == newCount && string.Equals(logSettings.LogType, newLogType, StringComparison.Ordinal))
            {
                return;
            }

            logSettings.Count = newCount;
            logSettings.LogType = newLogType;
            settings.SaveSettings();
        }

        private void ResetLogRetrievalSettings()
        {
            _logRetrievalCount = AIBridgeProjectSettings.DefaultLogRetrievalCount;
            _logTypeIndex = GetLogTypeIndex(AIBridgeProjectSettings.DefaultLogRetrievalType);
            SaveLogRetrievalSettings();
            _logPreviewEntries.Clear();
            _logPreviewError = null;
            Debug.Log("[AIBridge] 日志设置已重置为默认值。");
        }

        private void RefreshLogPreview()
        {
            SaveLogRetrievalSettings();
            _logPreviewError = null;

            try
            {
                _logPreviewEntries = GetLogsCommand.GetConsoleLogsForSettingsPreview(
                    _logRetrievalCount,
                    GetSelectedLogType());
            }
            catch (Exception ex)
            {
                _logPreviewEntries = new List<GetLogsCommand.LogEntry>();
                _logPreviewError = "获取日志失败: " + ex.Message;
            }
        }

        private string BuildLogRetrievalCliExample()
        {
            var logType = GetSelectedLogType();
            var count = Mathf.Clamp(_logRetrievalCount, MinLogRetrievalCount, MaxLogRetrievalCount);
            return "./.aibridge/cli/AIBridgeCLI.exe get_logs --logType " + logType + " --count " + count;
        }

        private string GetSelectedLogType()
        {
            var options = AIBridgeProjectSettings.SupportedLogRetrievalTypes;
            return options[Mathf.Clamp(_logTypeIndex, 0, options.Length - 1)];
        }

        private int GetLogTypeIndex(string logType)
        {
            var normalizedType = AIBridgeProjectSettings.NormalizeLogRetrievalType(logType);
            var options = AIBridgeProjectSettings.SupportedLogRetrievalTypes;
            for (var i = 0; i < options.Length; i++)
            {
                if (string.Equals(options[i], normalizedType, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return 0;
        }
    }
}
