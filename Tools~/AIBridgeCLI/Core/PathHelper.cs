using System;
using System.IO;

namespace AIBridgeCLI.Core
{
    /// <summary>
    /// Helper class for resolving paths
    /// </summary>
    public static class PathHelper
    {
        private static string _exchangeDir;

        /// <summary>
        /// Get the Exchange directory path (where commands and results are stored)
        /// </summary>
        public static string GetExchangeDirectory()
        {
            if (_exchangeDir != null)
            {
                return _exchangeDir;
            }

            var projectRoot = TryGetUnityProjectRoot();

            // Method 3: Fallback to exe directory relative path (legacy compatibility)
            if (string.IsNullOrEmpty(projectRoot))
            {
                var exeDir = AppDomain.CurrentDomain.BaseDirectory;
                var toolsDir = Path.GetDirectoryName(exeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                _exchangeDir = Path.Combine(toolsDir, "Exchange");
                return _exchangeDir;
            }

            _exchangeDir = Path.Combine(projectRoot, ".aibridge");
            return _exchangeDir;
        }

        /// <summary>
        /// Try to find the Unity project root without falling back to the legacy Exchange directory.
        /// </summary>
        public static string TryGetUnityProjectRoot()
        {
            var projectRoot = Environment.GetEnvironmentVariable("UNITY_PROJECT_ROOT");
            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                // UNITY_PROJECT_ROOT 是显式覆盖，允许临时目录用于 CLI-only smoke test。
                return Path.GetFullPath(projectRoot);
            }

            projectRoot = FindUnityProjectRoot(Directory.GetCurrentDirectory());
            if (!string.IsNullOrEmpty(projectRoot))
            {
                return projectRoot;
            }

            return FindUnityProjectRoot(AppDomain.CurrentDomain.BaseDirectory);
        }

        /// <summary>
        /// Find Unity project root by searching up the directory tree
        /// </summary>
        private static string FindUnityProjectRoot(string startDir)
        {
            var dir = startDir;
            while (!string.IsNullOrEmpty(dir))
            {
                // Check for Unity project markers: Assets folder and ProjectSettings
                if (IsUnityProjectRoot(dir))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static bool IsUnityProjectRoot(string directory)
        {
            return !string.IsNullOrWhiteSpace(directory)
                && Directory.Exists(directory)
                && Directory.Exists(Path.Combine(directory, "Assets"))
                && File.Exists(Path.Combine(directory, "ProjectSettings", "ProjectSettings.asset"));
        }

        /// <summary>
        /// Get the commands directory path
        /// </summary>
        public static string GetCommandsDirectory()
        {
            return Path.Combine(GetExchangeDirectory(), "commands");
        }

        /// <summary>
        /// Get the results directory path
        /// </summary>
        public static string GetResultsDirectory()
        {
            return Path.Combine(GetExchangeDirectory(), "results");
        }

        /// <summary>
        /// Get the screenshots directory path
        /// </summary>
        public static string GetScreenshotsDirectory()
        {
            return Path.Combine(GetExchangeDirectory(), "screenshots");
        }

        /// <summary>
        /// Ensure all required directories exist
        /// </summary>
        public static void EnsureDirectoriesExist()
        {
            var commandsDir = GetCommandsDirectory();
            var resultsDir = GetResultsDirectory();

            if (!Directory.Exists(commandsDir))
            {
                Directory.CreateDirectory(commandsDir);
            }

            if (!Directory.Exists(resultsDir))
            {
                Directory.CreateDirectory(resultsDir);
            }
        }

        /// <summary>
        /// Generate a unique command ID
        /// </summary>
        public static string GenerateCommandId()
        {
            return $"cmd_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}".Substring(0, 32);
        }
    }
}
