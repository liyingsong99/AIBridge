using System;
using System.IO;
using AIBridge.Runtime.Internal;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Manages screenshot cache directory.
    /// </summary>
    public static class ScreenshotCacheManager
    {
        private static readonly string[] ScreenshotExtensions = { ".png", ".jpg", ".jpeg", ".gif" };
        private static string _screenshotsDir;

        /// <summary>
        /// Get the screenshots directory path
        /// </summary>
        private static string ScreenshotsDir
        {
            get
            {
                if (string.IsNullOrEmpty(_screenshotsDir))
                {
                    _screenshotsDir = GetScreenshotsDirectory();
                }
                return _screenshotsDir;
            }
        }

        /// <summary>
        /// Cleanup old screenshot files.
        /// Should be called periodically (e.g., from CommandWatcher.ScanForCommands).
        /// </summary>
        public static void CleanupOldScreenshots()
        {
            try
            {
                var settings = AIBridgeProjectSettings.Instance;
                settings.WriteCacheCleanupSettingsMirror();
                var result = AIBridgeCacheCleanup.CleanupIfDue(AIBridge.BridgeDirectory, settings.ToCacheCleanupSettings());
                if (!result.Skipped && (result.DeletedFiles > 0 || result.DeletedDirectories > 0 || result.ErrorCount > 0))
                {
                    AIBridgeLogger.LogInfo(
                        "Cache cleanup: removed "
                        + result.DeletedFiles
                        + " file(s), "
                        + result.DeletedDirectories
                        + " directorie(s), errors: "
                        + result.ErrorCount);
                }
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"Failed to cleanup cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Force cleanup all screenshots regardless of age
        /// </summary>
        public static void ClearAllScreenshots()
        {
            try
            {
                var result = AIBridgeCacheCleanup.ClearScreenshotCache(AIBridge.BridgeDirectory);
                AIBridgeLogger.LogInfo($"Cleared all screenshots ({result.DeletedFiles} files, {result.ErrorCount} errors)");
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"Failed to clear screenshots: {ex.Message}");
            }
        }

        /// <summary>
        /// Get screenshots directory count and total size info
        /// </summary>
        public static (int count, long totalSize) GetCacheInfo()
        {
            if (!Directory.Exists(ScreenshotsDir))
            {
                return (0, 0);
            }

            try
            {
                var files = Directory.GetFiles(ScreenshotsDir);
                long totalSize = 0;
                var count = 0;

                foreach (var file in files)
                {
                    if (!IsScreenshotFile(file))
                    {
                        continue;
                    }

                    try
                    {
                        var fileInfo = new FileInfo(file);
                        totalSize += fileInfo.Length;
                        count++;
                    }
                    catch
                    {
                        // Ignore
                    }
                }

                return (count, totalSize);
            }
            catch
            {
                return (0, 0);
            }
        }

        private static bool IsScreenshotFile(string path)
        {
            var extension = Path.GetExtension(path);
            for (var i = 0; i < ScreenshotExtensions.Length; i++)
            {
                if (string.Equals(extension, ScreenshotExtensions[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetScreenshotsDirectory()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(projectRoot, ".aibridge", "screenshots");
        }
    }
}
