using System;
using System.Diagnostics;
using System.IO;
using AIBridge.Internal.Json;
using UnityEditor;

namespace AIBridge.Editor
{
    /// <summary>
    /// Watches the commands directory and processes incoming commands
    /// </summary>
    public class CommandWatcher
    {
        /// <summary>
        /// Timeout for stale command/result files (10 minutes)
        /// </summary>
        private static readonly TimeSpan StaleFileTimeout = TimeSpan.FromMinutes(10);

        private readonly string _commandsDir;
        private readonly string _resultsDir;
        private readonly CommandQueue _queue;

        public CommandWatcher(string baseDir)
        {
            _commandsDir = Path.Combine(baseDir, "commands");
            _resultsDir = Path.Combine(baseDir, "results");
            _queue = new CommandQueue();

            EnsureDirectoriesExist();
        }

        /// <summary>
        /// Scan for new command files and enqueue them
        /// </summary>
        public void ScanForCommands()
        {
            if (!Directory.Exists(_commandsDir))
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(_commandsDir, "*.json");
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"Failed to scan commands directory: {ex.Message}");
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    // Check if file is stale (older than timeout)
                    var fileInfo = new FileInfo(file);
                    var fileAge = DateTime.Now - fileInfo.CreationTime;
                    if (fileAge > StaleFileTimeout)
                    {
                        AIBridgeLogger.LogWarning($"Cleaning up stale command file: {Path.GetFileName(file)} (age: {fileAge.TotalMinutes:F1} minutes)");
                        File.Delete(file);
                        continue;
                    }

                    var json = File.ReadAllText(file, System.Text.Encoding.UTF8);

                    var commandData = AIBridgeJson.DeserializeObject(json);
                    if (commandData == null)
                    {
                        throw new InvalidOperationException("Command JSON root must be an object.");
                    }

                    var request = new CommandRequest
                    {
                        id = GetString(commandData, "id"),
                        type = GetString(commandData, "type"),
                        @params = new System.Collections.Generic.Dictionary<string, object>()
                    };

                    if (commandData.TryGetValue("params", out var paramsValue) && paramsValue is System.Collections.Generic.Dictionary<string, object> paramsObj)
                    {
                        foreach (var entry in paramsObj)
                        {
                            request.@params[entry.Key] = entry.Value;
                        }
                    }

                    if (request != null && !string.IsNullOrEmpty(request.id))
                    {
                        if (_queue.Enqueue(request))
                        {
                            AIBridgeLogger.LogDebug($"Enqueued command: {request.id} ({request.type})");
                            // Delete the command file after reading
                            File.Delete(file);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AIBridgeLogger.LogError($"Failed to parse command file {file}: {ex.Message}");
                    // Move failed file to prevent repeated errors
                    try
                    {
                        File.Move(file, file + ".error");
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }

            // Periodically trim processed IDs
            _queue.TrimProcessedIds();

            // Cleanup stale result files and error files
            CleanupStaleFiles();

            // Cleanup old screenshots (1 day retention)
            ScreenshotCacheManager.CleanupOldScreenshots();
        }

        /// <summary>
        /// Clean up stale result files and error command files
        /// </summary>
        private void CleanupStaleFiles()
        {
            // Cleanup stale result files
            if (Directory.Exists(_resultsDir))
            {
                try
                {
                    var resultFiles = Directory.GetFiles(_resultsDir, "*.json");
                    foreach (var file in resultFiles)
                    {
                        var fileInfo = new FileInfo(file);
                        var fileAge = DateTime.Now - fileInfo.CreationTime;
                        if (fileAge > StaleFileTimeout)
                        {
                            File.Delete(file);
                            AIBridgeLogger.LogDebug($"Cleaned up stale result file: {Path.GetFileName(file)}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AIBridgeLogger.LogError($"Failed to cleanup stale result files: {ex.Message}");
                }
            }

            // Cleanup stale error files in commands directory
            if (Directory.Exists(_commandsDir))
            {
                try
                {
                    var errorFiles = Directory.GetFiles(_commandsDir, "*.error");
                    foreach (var file in errorFiles)
                    {
                        var fileInfo = new FileInfo(file);
                        var fileAge = DateTime.Now - fileInfo.CreationTime;
                        if (fileAge > StaleFileTimeout)
                        {
                            File.Delete(file);
                            AIBridgeLogger.LogDebug($"Cleaned up stale error file: {Path.GetFileName(file)}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AIBridgeLogger.LogError($"Failed to cleanup stale error files: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Process one pending command
        /// </summary>
        /// <returns>True if a command was processed</returns>
        public bool ProcessOneCommand()
        {
            if (!_queue.TryDequeue(out var request))
            {
                return false;
            }

            var stopwatch = Stopwatch.StartNew();
            CommandResult result;

            try
            {
                if (!CommandRegistry.TryGetCommand(request.type, out var command))
                {
                    result = CommandResult.Failure(request.id, $"Unknown command type: {request.type}");
                }
                else
                {
                    result = command.Execute(request);

                    // If result is null, the command handles its own result writing (async commands)
                    if (result == null)
                    {
                        AIBridgeLogger.LogDebug($"Command {request.id} ({request.type}) started async processing");
                        return true;
                    }

                    result.id = request.id;

                    // Refresh AssetDatabase if command requires it
                    if (command.RequiresRefresh)
                    {
                        AssetDatabase.Refresh();
                    }
                }
            }
            catch (Exception ex)
            {
                result = CommandResult.FromException(request.id, ex);
            }

            stopwatch.Stop();
            if (string.Equals(request.type, "compile", StringComparison.OrdinalIgnoreCase))
            {
                result.executionTime = stopwatch.ElapsedMilliseconds;
            }

            WriteResult(result);
            if (result.executionTime.HasValue)
            {
                AIBridgeLogger.LogDebug($"Processed command {request.id} in {result.executionTime.Value}ms, success={result.success}");
            }
            else
            {
                AIBridgeLogger.LogDebug($"Processed command {request.id}, success={result.success}");
            }

            return true;
        }

        /// <summary>
        /// Write command result to file
        /// </summary>
        private void WriteResult(CommandResult result)
        {
            EnsureDirectoriesExist();

            var filePath = Path.Combine(_resultsDir, $"{result.id}.json");

            try
            {
                var json = AIBridgeJson.Serialize(result, pretty: true);
                File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"Failed to write result for {result.id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensure communication directories exist
        /// </summary>
        private void EnsureDirectoriesExist()
        {
            try
            {
                if (!Directory.Exists(_commandsDir))
                {
                    Directory.CreateDirectory(_commandsDir);
                }

                if (!Directory.Exists(_resultsDir))
                {
                    Directory.CreateDirectory(_resultsDir);
                }

                // Create .gitignore if not exists
                var gitignorePath = Path.Combine(Path.GetDirectoryName(_commandsDir), ".gitignore");
                if (!File.Exists(gitignorePath))
                {
                    File.WriteAllText(gitignorePath, "*\n!.gitignore\n");
                }
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"Failed to create directories: {ex.Message}");
            }
        }

        private static string GetString(System.Collections.Generic.Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value.ToString();
        }
    }
}
