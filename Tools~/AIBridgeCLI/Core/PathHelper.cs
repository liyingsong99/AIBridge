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

            // Method 1: Get from environment variable
            var projectRoot = Environment.GetEnvironmentVariable("UNITY_PROJECT_ROOT");

            // Method 2: Search up from current working directory to find Unity project
            if (string.IsNullOrEmpty(projectRoot))
            {
                projectRoot = FindUnityProjectRoot(Directory.GetCurrentDirectory());
            }

            // Method 3: Fallback to exe directory relative path (legacy compatibility)
            if (string.IsNullOrEmpty(projectRoot))
            {
                var exeDir = AppDomain.CurrentDomain.BaseDirectory;
                var toolsDir = Path.GetDirectoryName(exeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                _exchangeDir = Path.Combine(toolsDir, "Exchange");
                return _exchangeDir;
            }

            _exchangeDir = Path.Combine(projectRoot, "AIBridgeCache");
            return _exchangeDir;
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
                if (Directory.Exists(Path.Combine(dir, "Assets")) &&
                    File.Exists(Path.Combine(dir, "ProjectSettings", "ProjectSettings.asset")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
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
        /// Get the flow runs directory path.
        /// </summary>
        public static string GetFlowRunsDirectory()
        {
            return Path.Combine(GetExchangeDirectory(), "flow-runs");
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

            var flowRunsDir = GetFlowRunsDirectory();
            if (!Directory.Exists(flowRunsDir))
            {
                Directory.CreateDirectory(flowRunsDir);
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
