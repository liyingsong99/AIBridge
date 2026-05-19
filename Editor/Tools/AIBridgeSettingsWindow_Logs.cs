using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
        private bool _regexFilterEnabled;
        private string _regexPattern = string.Empty;
        private Vector2 _logPreviewScrollPosition;
        private List<GetLogsCommand.LogEntry> _logPreviewEntries = new List<GetLogsCommand.LogEntry>();
        private string _logPreviewError;

        private void LoadLogSettings()
        {
            var settings = AIBridgeProjectSettings.Instance.LogRetrieval;
            _logRetrievalCount = Mathf.Clamp(settings.Count, MinLogRetrievalCount, MaxLogRetrievalCount);
            _logTypeIndex = GetLogTypeIndex(settings.LogType);
            _regexFilterEnabled = settings.RegexFilterEnabled;
            _regexPattern = settings.RegexPattern ?? string.Empty;
        }

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
            EditorGUI.BeginChangeCheck();
            _logTypeIndex = EditorGUILayout.Popup("默认最低日志等级", _logTypeIndex, AIBridgeProjectSettings.SupportedLogRetrievalTypeLabels);
            _logRetrievalCount = EditorGUILayout.IntSlider("默认获取数量", _logRetrievalCount, MinLogRetrievalCount, MaxLogRetrievalCount);
            _regexFilterEnabled = EditorGUILayout.Toggle("启用全局正则筛选", _regexFilterEnabled);
            using (new EditorGUI.DisabledScope(!_regexFilterEnabled))
            {
                _regexPattern = EditorGUILayout.TextField("日志内容正则", _regexPattern);
            }

            if (EditorGUI.EndChangeCheck())
            {
                SaveLogRetrievalSettings();
            }

            EditorGUILayout.HelpBox(
                "这里按最低等级筛选：Info 及以上包含 Info、Warning、Error；Warning 及以上包含 Warning、Error；Error 只包含 Error。CLI 显式传入 --logType 时仍按指定类型精确筛选。",
                MessageType.Info);

            EditorGUILayout.HelpBox(
                "开启全局正则筛选后，未显式传入 --regex 的 get_logs 也会按日志内容正则过滤。",
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
            var newRegexPattern = _regexPattern ?? string.Empty;

            if (_regexFilterEnabled && !TryValidateRegex(newRegexPattern, out var regexError))
            {
                _logPreviewError = "正则表达式无效: " + regexError;
                return;
            }

            if (logSettings.Count == newCount &&
                string.Equals(logSettings.LogType, newLogType, StringComparison.Ordinal) &&
                logSettings.RegexFilterEnabled == _regexFilterEnabled &&
                string.Equals(logSettings.RegexPattern ?? string.Empty, newRegexPattern, StringComparison.Ordinal))
            {
                return;
            }

            logSettings.Count = newCount;
            logSettings.LogType = newLogType;
            logSettings.RegexFilterEnabled = _regexFilterEnabled;
            logSettings.RegexPattern = newRegexPattern;
            settings.SaveSettings();
            _logPreviewError = null;
        }

        private void ResetLogRetrievalSettings()
        {
            _logRetrievalCount = AIBridgeProjectSettings.DefaultLogRetrievalCount;
            _logTypeIndex = GetLogTypeIndex(AIBridgeProjectSettings.DefaultLogRetrievalType);
            _regexFilterEnabled = false;
            _regexPattern = string.Empty;
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
                    GetSelectedLogType(),
                    _regexFilterEnabled ? _regexPattern : null);
            }
            catch (Exception ex)
            {
                _logPreviewEntries = new List<GetLogsCommand.LogEntry>();
                _logPreviewError = "获取日志失败: " + ex.Message;
            }
        }

        private string BuildLogRetrievalCliExample()
        {
            var count = Mathf.Clamp(_logRetrievalCount, MinLogRetrievalCount, MaxLogRetrievalCount);
            return "./.aibridge/cli/AIBridgeCLI.exe get_logs --count " + count;
        }

        private bool TryValidateRegex(string pattern, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(pattern))
            {
                return true;
            }

            try
            {
                new Regex(pattern);
                return true;
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
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
