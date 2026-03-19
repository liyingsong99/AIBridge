using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Internal.Json;
using UnityEngine;

namespace AIBridge.Runtime
{
    /// <summary>
    /// AIBridge Runtime MonoBehaviour singleton.
    /// Receives and processes commands from AI Code assistants during Play mode.
    ///
    /// Usage:
    /// 1. Add this component to a GameObject in your scene, or create it via code:
    ///    <code>
    ///    if (AIBridgeRuntime.Instance == null)
    ///    {
    ///        var go = new GameObject("AIBridgeRuntime");
    ///        go.AddComponent&lt;AIBridgeRuntime&gt;();
    ///    }
    ///    </code>
    ///
    /// 2. Register custom handlers to extend functionality:
    ///    <code>
    ///    AIBridgeRuntime.Instance.RegisterHandler(new MyUIHandler());
    ///    </code>
    /// </summary>
    public class AIBridgeRuntime : MonoBehaviour
    {
        /// <summary>
        /// Singleton instance
        /// </summary>
        public static AIBridgeRuntime Instance { get; private set; }

        /// <summary>
        /// Polling interval in seconds
        /// </summary>
        [Tooltip("How often to check for new commands (in seconds)")]
        public float pollIntervalSeconds = 0.1f;

        /// <summary>
        /// Maximum commands to process per frame
        /// </summary>
        [Tooltip("Maximum number of commands to process per frame")]
        public int maxCommandsPerFrame = 5;

        /// <summary>
        /// Enable debug logging
        /// </summary>
        [Tooltip("Enable debug logging")]
        public bool enableDebugLog = false;

        // Paths
        private string _commandsPath;
        private string _resultsPath;

        // Command queue
        private readonly Queue<AIBridgeRuntimeCommand> _commandQueue = new Queue<AIBridgeRuntimeCommand>();

        // Handlers
        private readonly List<IAIBridgeHandler> _handlers = new List<IAIBridgeHandler>();
        private readonly List<IAIBridgeAsyncHandler> _asyncHandlers = new List<IAIBridgeAsyncHandler>();

        // Timing
        private float _lastPollTime;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[AIBridgeRuntime] Duplicate instance detected, destroying...");
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            Initialize();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                LogDebug("Destroyed");
            }
        }

        private void Update()
        {
            // Poll for new commands
            if (Time.time - _lastPollTime >= pollIntervalSeconds)
            {
                _lastPollTime = Time.time;
                ScanForCommands();
            }

            // Process queued commands
            var processed = 0;
            while (processed < maxCommandsPerFrame && _commandQueue.Count > 0)
            {
                var cmd = _commandQueue.Dequeue();
                ProcessCommand(cmd);
                processed++;
            }
        }

        /// <summary>
        /// Register a command handler.
        /// Handlers are called in registration order until one returns a non-null result.
        /// </summary>
        public void RegisterHandler(IAIBridgeHandler handler)
        {
            if (handler != null && !_handlers.Contains(handler))
            {
                _handlers.Add(handler);
                LogDebug($"Registered handler: {handler.GetType().Name}");
            }
        }

        /// <summary>
        /// Unregister a command handler.
        /// </summary>
        public void UnregisterHandler(IAIBridgeHandler handler)
        {
            if (_handlers.Remove(handler))
            {
                LogDebug($"Unregistered handler: {handler.GetType().Name}");
            }
        }

        /// <summary>
        /// Register an async command handler.
        /// </summary>
        public void RegisterAsyncHandler(IAIBridgeAsyncHandler handler)
        {
            if (handler != null && !_asyncHandlers.Contains(handler))
            {
                _asyncHandlers.Add(handler);
                LogDebug($"Registered async handler: {handler.GetType().Name}");
            }
        }

        /// <summary>
        /// Unregister an async command handler.
        /// </summary>
        public void UnregisterAsyncHandler(IAIBridgeAsyncHandler handler)
        {
            if (_asyncHandlers.Remove(handler))
            {
                LogDebug($"Unregistered async handler: {handler.GetType().Name}");
            }
        }

        private void Initialize()
        {
            var projectRoot = Application.dataPath.Replace("/Assets", "");
            var exchangePath = Path.Combine(projectRoot, "AIBridgeCache");

            _commandsPath = Path.Combine(exchangePath, "commands");
            _resultsPath = Path.Combine(exchangePath, "results");

            try
            {
                if (!Directory.Exists(_commandsPath))
                {
                    Directory.CreateDirectory(_commandsPath);
                }

                if (!Directory.Exists(_resultsPath))
                {
                    Directory.CreateDirectory(_resultsPath);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[AIBridgeRuntime] Failed to create directories: {e.Message}");
            }

            LogDebug($"Initialized - Commands: {_commandsPath}");
            LogDebug($"Initialized - Results: {_resultsPath}");
        }

        private void ScanForCommands()
        {
            if (string.IsNullOrEmpty(_commandsPath) || !Directory.Exists(_commandsPath))
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(_commandsPath, "*.json");
            }
            catch
            {
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var commandData = AIBridgeJson.DeserializeObject(json);
                    var cmd = AIBridgeRuntimeCommand.FromDictionary(commandData);

                    if (cmd != null)
                    {
                        _commandQueue.Enqueue(cmd);
                        File.Delete(file);
                        LogDebug($"Queued command: {cmd.Action} - {cmd.Id}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[AIBridgeRuntime] Failed to parse command: {file}\n{e.Message}");

                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Ignore deletion errors
                    }
                }
            }
        }

        private void ProcessCommand(AIBridgeRuntimeCommand cmd)
        {
            LogDebug($"Processing command: {cmd.Action} - {cmd.Id}");

            AIBridgeRuntimeCommandResult result = null;

            // Try async handlers first
            foreach (var handler in _asyncHandlers)
            {
                if (handler.SupportedActions != null && Array.IndexOf(handler.SupportedActions, cmd.Action) >= 0)
                {
                    if (handler.HandleCommandAsync(cmd, WriteResult))
                    {
                        LogDebug($"Command handled by async handler: {handler.GetType().Name}");
                        return; // Async handler will call WriteResult when done
                    }
                }
            }

            // Try sync handlers
            foreach (var handler in _handlers)
            {
                if (handler.SupportedActions != null && Array.IndexOf(handler.SupportedActions, cmd.Action) >= 0)
                {
                    result = handler.HandleCommand(cmd);
                    if (result != null)
                    {
                        LogDebug($"Command handled by handler: {handler.GetType().Name}");
                        break;
                    }
                }
            }

            // If no handler processed the command, return an error
            if (result == null)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(
                    cmd.Id,
                    $"No handler found for action: {cmd.Action}. Register a handler using AIBridgeRuntime.Instance.RegisterHandler()");
            }

            WriteResult(result);
        }

        private void WriteResult(AIBridgeRuntimeCommandResult result)
        {
            if (result == null || string.IsNullOrEmpty(_resultsPath))
            {
                return;
            }

            try
            {
                var fileName = $"{result.CommandId}.json";
                var filePath = Path.Combine(_resultsPath, fileName);
                var json = AIBridgeJson.Serialize(result, pretty: true);
                File.WriteAllText(filePath, json);
                LogDebug($"Wrote result: {fileName}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[AIBridgeRuntime] Failed to write result: {e.Message}");
            }
        }

        private void LogDebug(string message)
        {
            if (enableDebugLog)
            {
                Debug.Log($"[AIBridgeRuntime] {message}");
            }
        }
    }
}
