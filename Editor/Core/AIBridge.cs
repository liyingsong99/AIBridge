using System.IO;
using AIBridge.Runtime.Internal;
using AIBridge.Runtime;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Main entry point for AI Bridge.
    /// Manages polling loop and command processing.
    /// Auto-initialized via [InitializeOnLoad].
    /// </summary>
    [InitializeOnLoad]
    public static class AIBridge
    {
        /// <summary>
        /// Polling interval in seconds
        /// </summary>
        private const float POLL_INTERVAL = 0.1f;

        /// <summary>
        /// Maximum commands to process per frame
        /// </summary>
        private const int MAX_COMMANDS_PER_FRAME = 5;
        private const string PlayModeRuntimeObjectName = "AIBridgeRuntime (Play Mode)";

        private static double _lastPollTime;
        private static CommandWatcher _watcher;
        private static bool _enabled = true;
        private static EditorOption<bool> _enabledOption;

        /// <summary>
        /// Communication directory path
        /// </summary>
        public static string BridgeDirectory { get; private set; }

        /// <summary>
        /// Enable or disable the bridge
        /// </summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (_enabledOption != null)
                {
                    _enabledOption.Value = value;
                }
                AIBridgeLogger.LogInfo($"AI Bridge {(_enabled ? "enabled" : "disabled")}");
            }
        }

        static AIBridge()
        {
            Initialize();
        }

        /// <summary>
        /// Initialize the bridge
        /// </summary>
        private static void Initialize()
        {
            _enabledOption = new EditorOption<bool>("AIBridge_Enabled", true, ReadEnabled, WriteEnabled);
            _enabled = _enabledOption.Value;

            // Get the exchange directory in the Unity project root (.aibridge)
            BridgeDirectory = GetExchangeDirectory();

            // Initialize components
            CommandRegistry.Initialize();
            _watcher = new CommandWatcher(BridgeDirectory);
            LegacyCacheDirectoryCleaner.CleanupIfNeeded(GetProjectRoot(), BridgeDirectory);
            CodeCacheCleaner.CleanupIfNeeded(BridgeDirectory);
            CleanupCacheIfDue();
            EditorInstanceTracker.Initialize(BridgeDirectory);

            // Subscribe to editor update
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;

            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.quitting += OnEditorQuitting;

            // Handle play mode changes
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            AIBridgeLogger.LogInfo($"AI Bridge initialized. Directory: {BridgeDirectory}");
            AIBridgeLogger.LogInfo($"Registered commands: {string.Join(", ", CommandRegistry.GetRegisteredTypes())}");
        }

        /// <summary>
        /// Editor update callback - main polling loop
        /// </summary>
        private static void OnEditorUpdate()
        {
            EditorInstanceTracker.UpdateHeartbeat();

            if (!_enabled)
            {
                return;
            }

            // Check polling interval
            var currentTime = EditorApplication.timeSinceStartup;
            if (currentTime - _lastPollTime < POLL_INTERVAL)
            {
                return;
            }
            _lastPollTime = currentTime;

            // Scan for new commands
            _watcher.ScanForCommands();

            // Process commands (limited per frame to prevent blocking)
            var processed = 0;
            while (processed < MAX_COMMANDS_PER_FRAME && _watcher.ProcessOneCommand())
            {
                processed++;
            }
        }

        /// <summary>
        /// Handle play mode state changes
        /// </summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    // Entering play mode - continue processing
                    break;

                case PlayModeStateChange.EnteredPlayMode:
                    // In play mode - still process commands
                    EnsureRuntimeBridgeForPlayMode();
                    break;

                case PlayModeStateChange.ExitingPlayMode:
                    AIBridgeRuntimeBridgeEditorUtility.ShutdownPlayModeRuntimeBridges("exiting_play_mode");
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    AIBridgeRuntimeBridgeEditorUtility.ShutdownPlayModeRuntimeBridges("entered_edit_mode");
                    break;
            }
        }

        private static void EnsureRuntimeBridgeForPlayMode()
        {
            var settings = AIBridgeProjectSettings.Instance.RuntimeBridge;
            if (settings == null
                || !settings.EnableRuntimeBridge
                || !settings.AutoInjectRuntimeBridgeInEditorPlayMode)
            {
                return;
            }

            if (AIBridgeRuntimeBridgeEditorUtility.FindSceneRuntime() != null)
            {
                return;
            }

            // Play Mode 临时注入不写入场景，避免用户只为调试 Runtime Bridge 而手动挂组件。
            AIBridgeRuntimeBridgeEditorUtility.CreateConfiguredRuntimeObject(
                PlayModeRuntimeObjectName,
                HideFlags.HideInHierarchy | HideFlags.DontSave,
                useUndo: false);

            AIBridgeLogger.LogInfo(AIBridgeEditorText.T(
                "Runtime Bridge auto injected for Editor Play Mode.",
                "已为 Editor Play Mode 自动注入 Runtime Bridge。"));
        }

        private static void OnEditorQuitting()
        {
            EditorInstanceTracker.Cleanup();
        }

        private static void CleanupCacheIfDue()
        {
            try
            {
                var settings = AIBridgeProjectSettings.Instance;
                settings.WriteCacheCleanupSettingsMirror();

                var result = AIBridgeCacheCleanup.CleanupIfDue(BridgeDirectory, settings.ToCacheCleanupSettings());
                if (result.Skipped)
                {
                    return;
                }

                if (result.DeletedFiles > 0 || result.DeletedDirectories > 0 || result.ErrorCount > 0)
                {
                    AIBridgeLogger.LogInfo(
                        "[AIBridge] Cache cleanup finished. Deleted files: "
                        + result.DeletedFiles
                        + ", directories: "
                        + result.DeletedDirectories
                        + ", freed bytes: "
                        + result.FreedBytes
                        + ", errors: "
                        + result.ErrorCount);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[AIBridge] Cache cleanup skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// Manually trigger a scan and process cycle
        /// </summary>
        public static void ProcessCommandsNow()
        {
            _watcher.ScanForCommands(force: true);
            while (_watcher.ProcessOneCommand())
            {
                // Process all pending commands
            }
        }

        /// <summary>
        /// Open the bridge directory in file explorer
        /// </summary>
        public static void OpenBridgeDirectory()
        {
            if (!Directory.Exists(BridgeDirectory))
            {
                Directory.CreateDirectory(BridgeDirectory);
            }
            EditorUtility.RevealInFinder(BridgeDirectory);
        }

        /// <summary>
        /// Get the exchange directory path in the Unity project root
        /// </summary>
        private static string GetExchangeDirectory()
        {
            // Use .aibridge in Unity project root for better compatibility with git/UPM installation
            return Path.Combine(GetProjectRoot(), ".aibridge");
        }

        private static string GetProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        private static bool ReadEnabled(string key, bool defaultValue)
        {
            return AIBridgeProjectSettings.Instance.BridgeEnabled;
        }

        private static void WriteEnabled(string key, bool value)
        {
            var settings = AIBridgeProjectSettings.Instance;
            if (settings.BridgeEnabled == value)
            {
                return;
            }

            settings.BridgeEnabled = value;
            settings.SaveSettings();
        }
    }
}
