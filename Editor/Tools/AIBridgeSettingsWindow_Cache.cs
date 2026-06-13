using System;
using AIBridge.Runtime.Internal;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    public partial class AIBridgeSettingsWindow
    {
        private void DrawCacheCleanupSettingsTab()
        {
            var projectSettings = AIBridgeProjectSettings.Instance;
            var settings = projectSettings.CacheCleanup;

            EditorGUILayout.LabelField(AIBridgeEditorText.T("Cache Cleanup", "缓存清理"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "AIBridge automatically removes expired cache files under .aibridge. Cache that is still written or marked as recently used is retained.",
                    "AIBridge 会自动清理 .aibridge 下的过期缓存。仍在写入或被标记为最近使用的缓存会被保留。"),
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            settings.EnableAutoCleanup = EditorGUILayout.Toggle(
                AIBridgeEditorText.T("Enable Automatic Cleanup", "启用自动清理"),
                settings.EnableAutoCleanup);

            settings.RetentionDays = EditorGUILayout.IntSlider(
                AIBridgeEditorText.T("Retention Days", "保留天数"),
                settings.RetentionDays,
                AIBridgeCacheCleanup.MinRetentionDays,
                AIBridgeCacheCleanup.MaxRetentionDays);

            if (EditorGUI.EndChangeCheck())
            {
                settings.RetentionDays = AIBridgeCacheCleanup.ClampRetentionDays(settings.RetentionDays);
                projectSettings.SaveSettings();
            }

            EditorGUILayout.Space(8);
            if (GUILayout.Button(AIBridgeEditorText.T("Clean Expired Cache Now", "立即清理过期缓存"), GUILayout.Height(28)))
            {
                RunManualCacheCleanup(projectSettings);
            }

            EditorGUILayout.Space(8);
            DrawCacheCleanupSummary();
        }

        private static void RunManualCacheCleanup(AIBridgeProjectSettings projectSettings)
        {
            try
            {
                projectSettings.WriteCacheCleanupSettingsMirror();
                var result = AIBridgeCacheCleanup.CleanupExpired(AIBridge.BridgeDirectory, projectSettings.ToCacheCleanupSettings());
                Debug.Log(BuildCacheCleanupLogMessage(result));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AIBridge] Cache cleanup failed: " + ex.Message);
            }
        }

        private static void DrawCacheCleanupSummary()
        {
            AIBridgeCacheCleanupState state;
            try
            {
                state = AIBridgeCacheCleanup.LoadState(AIBridge.BridgeDirectory);
            }
            catch
            {
                state = new AIBridgeCacheCleanupState();
            }

            EditorGUILayout.LabelField(AIBridgeEditorText.T("Last Cleanup", "最近一次清理"), EditorStyles.boldLabel);
            if (string.IsNullOrEmpty(state.LastRunUtc))
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T("No cache cleanup has run yet.", "尚未执行过缓存清理。"),
                    MessageType.None);
                return;
            }

            EditorGUILayout.LabelField(AIBridgeEditorText.T("Finished At", "完成时间"), state.LastRunUtc);
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Deleted Files", "删除文件数"),
                state.LastDeletedFiles.ToString());
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Deleted Directories", "删除目录数"),
                state.LastDeletedDirectories.ToString());
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Freed Space", "释放空间"),
                FormatCacheBytes(state.LastFreedBytes));
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Errors", "错误数"),
                state.LastErrorCount.ToString());
        }

        private static string BuildCacheCleanupLogMessage(AIBridgeCacheCleanupResult result)
        {
            return AIBridgeEditorText.T(
                "[AIBridge] Cache cleanup finished. Deleted files: "
                + result.DeletedFiles
                + ", directories: "
                + result.DeletedDirectories
                + ", freed: "
                + FormatCacheBytes(result.FreedBytes)
                + ", errors: "
                + result.ErrorCount,
                "[AIBridge] 缓存清理完成。删除文件数: "
                + result.DeletedFiles
                + "，目录数: "
                + result.DeletedDirectories
                + "，释放空间: "
                + FormatCacheBytes(result.FreedBytes)
                + "，错误数: "
                + result.ErrorCount);
        }

        private static string FormatCacheBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            var kb = bytes / 1024d;
            if (kb < 1024d)
            {
                return kb.ToString("F1") + " KB";
            }

            var mb = kb / 1024d;
            if (mb < 1024d)
            {
                return mb.ToString("F1") + " MB";
            }

            return (mb / 1024d).ToString("F2") + " GB";
        }
    }
}
