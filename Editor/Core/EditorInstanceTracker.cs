using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AIBridge.Internal.Json;
using UnityEngine;

namespace AIBridge.Editor
{
    internal static class EditorInstanceTracker
    {
        private const string MetadataFileName = "editor-instance.json";
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

        private static string _metadataPath;
        private static string _metadataDirectory;
        private static bool _metadataDirectoryReady;
        private static Process _currentProcess;
        private static int _processId;
        private static string _projectRoot;
        private static string _projectName;
        private static DateTime _lastWriteUtc = DateTime.MinValue;
        private static string _lastLoggedError;

        public static void Initialize(string bridgeDirectory)
        {
            if (string.IsNullOrEmpty(bridgeDirectory))
            {
                return;
            }

            _metadataDirectory = bridgeDirectory;
            _metadataPath = Path.Combine(bridgeDirectory, MetadataFileName);
            _metadataDirectoryReady = false;
            _projectRoot = Path.GetDirectoryName(Application.dataPath);
            _projectName = GetProjectName(_projectRoot);
            WriteMetadata(force: true);
        }

        public static void UpdateHeartbeat()
        {
            if (string.IsNullOrEmpty(_metadataPath))
            {
                return;
            }

            if (DateTime.UtcNow - _lastWriteUtc < HeartbeatInterval)
            {
                return;
            }

            WriteMetadata(force: true);
        }

        public static void Cleanup()
        {
            if (string.IsNullOrEmpty(_metadataPath))
            {
                return;
            }

            TryDeleteFile(_metadataPath);
            TryDeleteFile(_metadataPath + ".tmp");
            _lastWriteUtc = DateTime.MinValue;
        }

        private static void WriteMetadata(bool force)
        {
            if (!force && DateTime.UtcNow - _lastWriteUtc < HeartbeatInterval)
            {
                return;
            }

            try
            {
                WriteMetadataCore();
            }
            catch (DirectoryNotFoundException)
            {
                // Editor 心跳也是高频路径；目录正常只在初始化建一次，被外部删除后才恢复。
                try
                {
                    _metadataDirectoryReady = false;
                    WriteMetadataCore();
                }
                catch (Exception ex)
                {
                    LogWriteError(ex);
                }
            }
            catch (Exception ex)
            {
                LogWriteError(ex);
            }
        }

        private static void WriteMetadataCore()
        {
            EnsureMetadataDirectory();

            var process = GetCurrentProcess();
            process.Refresh();

            var metadata = new EditorInstanceMetadata
            {
                schemaVersion = 1,
                processId = _processId,
                projectRoot = _projectRoot,
                projectName = _projectName,
                windowTitle = NormalizeWindowTitle(process.MainWindowTitle),
                lastUpdatedUtc = DateTime.UtcNow.ToString("O")
            };

            var json = AIBridgeJson.Serialize(metadata, pretty: true);
            var tempPath = _metadataPath + ".tmp";

            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Copy(tempPath, _metadataPath, true);
            File.Delete(tempPath);

            _lastWriteUtc = DateTime.UtcNow;
            _lastLoggedError = null;
            AIBridgeLogger.LogDebug($"Updated editor instance metadata: {_metadataPath}");
        }

        private static void EnsureMetadataDirectory()
        {
            if (_metadataDirectoryReady)
            {
                return;
            }

            if (!string.IsNullOrEmpty(_metadataDirectory))
            {
                Directory.CreateDirectory(_metadataDirectory);
                _metadataDirectoryReady = true;
            }
        }

        private static Process GetCurrentProcess()
        {
            if (_currentProcess == null)
            {
                _currentProcess = Process.GetCurrentProcess();
                _processId = _currentProcess.Id;
            }

            return _currentProcess;
        }

        private static void LogWriteError(Exception ex)
        {
            if (!string.Equals(_lastLoggedError, ex.Message, StringComparison.Ordinal))
            {
                AIBridgeLogger.LogWarning($"Failed to update editor instance metadata: {ex.Message}");
                _lastLoggedError = ex.Message;
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogDebug($"Failed to delete editor instance metadata file '{path}': {ex.Message}");
            }
        }

        private static string GetProjectName(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                return string.Empty;
            }

            var trimmed = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(trimmed) ?? string.Empty;
        }

        private static string NormalizeWindowTitle(string windowTitle)
        {
            if (string.IsNullOrWhiteSpace(windowTitle) ||
                string.Equals(windowTitle, "null", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return windowTitle;
        }
    }
}
