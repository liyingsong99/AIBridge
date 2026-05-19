using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Manages screenshot cache directory.
    /// Auto-cleanup screenshots older than 1 day.
    /// </summary>
    public static class ScreenshotCacheManager
    {
        /// <summary>
        /// Cache retention period (1 day)
        /// </summary>
        private static readonly TimeSpan CacheRetentionPeriod = TimeSpan.FromDays(1);

        /// <summary>
        /// Minimum interval between cleanup operations (1 hour)
        /// </summary>
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

        private static DateTime _lastCleanupTime = DateTime.MinValue;
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
            // Check if enough time has passed since last cleanup
            if (DateTime.Now - _lastCleanupTime < CleanupInterval)
            {
                return;
            }

            _lastCleanupTime = DateTime.Now;

            if (!Directory.Exists(ScreenshotsDir))
            {
                return;
            }

            try
            {
                var files = Directory.GetFiles(ScreenshotsDir, "*.png");
                var cleanedCount = 0;

                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var fileAge = DateTime.Now - fileInfo.CreationTime;

                        if (fileAge > CacheRetentionPeriod)
                        {
                            File.Delete(file);
                            cleanedCount++;
                            AIBridgeLogger.LogDebug($"Cleaned up old screenshot: {Path.GetFileName(file)} (age: {fileAge.TotalHours:F1} hours)");
                        }
                    }
                    catch (Exception ex)
                    {
                        AIBridgeLogger.LogError($"Failed to delete screenshot {file}: {ex.Message}");
                    }
                }

                if (cleanedCount > 0)
                {
                    AIBridgeLogger.LogInfo($"Screenshot cache cleanup: removed {cleanedCount} old file(s)");
                }
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"Failed to cleanup screenshot cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Force cleanup all screenshots regardless of age
        /// </summary>
        public static void ClearAllScreenshots()
        {
            if (!Directory.Exists(ScreenshotsDir))
            {
                return;
            }

            try
            {
                var files = Directory.GetFiles(ScreenshotsDir, "*.png");
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Ignore individual file errors
                    }
                }

                AIBridgeLogger.LogInfo($"Cleared all screenshots ({files.Length} files)");
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
                var files = Directory.GetFiles(ScreenshotsDir, "*.png");
                long totalSize = 0;

                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        totalSize += fileInfo.Length;
                    }
                    catch
                    {
                        // Ignore
                    }
                }

                return (files.Length, totalSize);
            }
            catch
            {
                return (0, 0);
            }
        }

        private static string GetScreenshotsDirectory()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(projectRoot, ".aibridge", "screenshots");
        }
    }
}
