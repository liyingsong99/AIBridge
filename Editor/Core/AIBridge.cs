using System.IO;
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
                    break;

                case PlayModeStateChange.ExitingPlayMode:
                    // Exiting play mode
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    // Back to edit mode - reinitialize if needed
                    break;
            }
        }

        private static void OnEditorQuitting()
        {
            EditorInstanceTracker.Cleanup();
        }

        /// <summary>
        /// Manually trigger a scan and process cycle
        /// </summary>
        public static void ProcessCommandsNow()
        {
            _watcher.ScanForCommands();
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
