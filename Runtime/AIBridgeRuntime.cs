using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AIBridge.Internal.Json;
using AIBridge.Runtime.Diagnostics;
using AIBridge.Runtime.Transports;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIBridge.Runtime
{
    /// <summary>
    /// AIBridge Runtime MonoBehaviour singleton.
    /// Receives and processes commands from AI Code assistants during Play mode or built Player runtime.
    /// </summary>
    public class AIBridgeRuntime : MonoBehaviour
    {
        private const string RuntimeVersion = "1.0";
        private const string RuntimeDirArgument = "--aibridge-runtime-dir";
        private const string TargetIdArgument = "--aibridge-target-id";
        private const string RuntimeDirEnvironment = "AIBRIDGE_RUNTIME_DIR";
        private const string TargetIdEnvironment = "AIBRIDGE_TARGET_ID";
        private const string RuntimeDirectoryName = "runtime";
        private const string TargetsDirectoryName = "targets";
        private const string CommandsDirectoryName = "commands";
        private const string ResultsDirectoryName = "results";
        private const string ScreenshotsDirectoryName = "screenshots";
        private const string HeartbeatFileName = "heartbeat.json";

        private static readonly string[] BuiltInActions =
        {
            "runtime.ping",
            "runtime.status",
            "runtime.logs",
            "runtime.logs.clear",
            "runtime.perf",
            "runtime.screenshot",
            "runtime.handlers"
        };

        /// <summary>
        /// Singleton instance.
        /// </summary>
        public static AIBridgeRuntime Instance { get; private set; }

        public AIBridgeRuntimeSettings runtimeSettings = new AIBridgeRuntimeSettings();

        /// <summary>
        /// Polling interval in seconds.
        /// </summary>
        [Tooltip("How often to check for new commands (in seconds)")]
        public float pollIntervalSeconds = 0.1f;

        /// <summary>
        /// Maximum commands to process per frame.
        /// </summary>
        [Tooltip("Maximum number of commands to process per frame")]
        public int maxCommandsPerFrame = 5;

        /// <summary>
        /// Enable debug logging.
        /// </summary>
        [Tooltip("Enable debug logging")]
        public bool enableDebugLog = false;

        private string _runtimeRootPath;
        private string _targetPath;
        private string _commandsPath;
        private string _resultsPath;
        private string _screenshotsPath;
        private string _heartbeatPath;
        private string _targetId;

        private readonly Queue<AIBridgeRuntimeCommand> _commandQueue = new Queue<AIBridgeRuntimeCommand>();
        private readonly List<IAIBridgeHandler> _handlers = new List<IAIBridgeHandler>();
        private readonly List<IAIBridgeAsyncHandler> _asyncHandlers = new List<IAIBridgeAsyncHandler>();
        private readonly AIBridgeRuntimeLogBuffer _logBuffer = new AIBridgeRuntimeLogBuffer();
        private readonly object _pendingHttpResultsSyncRoot = new object();
        private readonly Dictionary<string, AIBridgeRuntimeCommandResult> _pendingHttpResults = new Dictionary<string, AIBridgeRuntimeCommandResult>();
        private readonly HashSet<string> _httpCommandIds = new HashSet<string>();
        private HttpRuntimeTransportServer _httpTransportServer;
        private LanRuntimeDiscoveryServer _lanDiscoveryServer;

        private float _lastPollTime;
        private float _lastHeartbeatTime;
        private DateTime _startedAtUtc;
        private bool _initialized;
        private bool _runInBackgroundChanged;
        private bool _previousRunInBackground;

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
            _startedAtUtc = DateTime.UtcNow;
            Initialize();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                StopLanDiscovery();
                StopHttpTransport();
                RestoreRunInBackgroundIfNeeded();
                Instance = null;
                _logBuffer.Dispose();
                LogDebug("Destroyed");
            }
        }

        private void Update()
        {
            if (!_initialized)
            {
                return;
            }

            WriteHeartbeatIfDue();

            var now = Time.realtimeSinceStartup;
            if (now - _lastPollTime >= pollIntervalSeconds)
            {
                _lastPollTime = now;
                ScanForCommands();
            }

            var processed = 0;
            while (processed < maxCommandsPerFrame)
            {
                AIBridgeRuntimeCommand cmd = null;
                lock (_commandQueue)
                {
                    if (_commandQueue.Count == 0)
                    {
                        break;
                    }

                    cmd = _commandQueue.Dequeue();
                }

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
            if (!IsRuntimeBridgeEnabled())
            {
                LogDebug("Runtime Bridge disabled by settings/build type.");
                return;
            }

            _targetId = ResolveTargetId();
            _runtimeRootPath = ResolveRuntimeRootPath();
            _targetPath = Path.Combine(_runtimeRootPath, TargetsDirectoryName, _targetId);
            _commandsPath = Path.Combine(_targetPath, CommandsDirectoryName);
            _resultsPath = Path.Combine(_targetPath, ResultsDirectoryName);
            _screenshotsPath = Path.Combine(_targetPath, ScreenshotsDirectoryName);
            _heartbeatPath = Path.Combine(_targetPath, HeartbeatFileName);

            try
            {
                Directory.CreateDirectory(_commandsPath);
                Directory.CreateDirectory(_resultsPath);
                Directory.CreateDirectory(_screenshotsPath);
                _logBuffer.Initialize(Math.Max(1, runtimeSettings.logBufferSize));
                _initialized = true;
                ApplyRunInBackgroundIfNeeded();
                StartHttpTransportIfNeeded();
                StartLanDiscoveryIfNeeded();
                WriteHeartbeat();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AIBridgeRuntime] Failed to create directories: {e.Message}");
            }

            LogDebug($"Initialized - Target: {_targetId}");
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
                        lock (_commandQueue)
                        {
                            _commandQueue.Enqueue(cmd);
                        }

                        File.Delete(file);
                        LogDebug($"Queued command: {cmd.Action} - {cmd.Id}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[AIBridgeRuntime] Failed to parse command: {file}\n{e.Message}");

                    try
                    {
                        File.Move(file, file + ".error");
                    }
                    catch
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
        }

        private void ProcessCommand(AIBridgeRuntimeCommand cmd)
        {
            var commandId = cmd == null ? "unknown" : cmd.Id;
            LogDebug(cmd == null ? "Processing null command" : $"Processing command: {cmd.Action} - {cmd.Id}");

            AIBridgeRuntimeCommandResult result = null;

            try
            {
                if (!ValidateCommand(cmd, out var validationError))
                {
                    result = AIBridgeRuntimeCommandResult.FromFailure(commandId, validationError);
                    WriteResult(result);
                    return;
                }

                if (TryHandleBuiltInCommand(cmd, out result, out var asyncStarted))
                {
                    if (!asyncStarted)
                    {
                        WriteResult(result);
                    }

                    return;
                }

                if (!IsCustomActionAllowed(cmd.Action))
                {
                    result = AIBridgeRuntimeCommandResult.FromFailure(
                        cmd.Id,
                        $"Runtime action is not allowed: {cmd.Action}");
                    WriteResult(result);
                    return;
                }

                ProcessCustomCommand(cmd, out result);
            }
            catch (Exception ex)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(commandId, $"{ex.GetType().Name}: {ex.Message}");
            }

            WriteResult(result);
        }

        private void ProcessCustomCommand(AIBridgeRuntimeCommand cmd, out AIBridgeRuntimeCommandResult result)
        {
            result = null;

            foreach (var handler in _asyncHandlers)
            {
                if (handler.SupportedActions != null && Array.IndexOf(handler.SupportedActions, cmd.Action) >= 0)
                {
                    if (handler.HandleCommandAsync(cmd, WriteResult))
                    {
                        LogDebug($"Command handled by async handler: {handler.GetType().Name}");
                        return;
                    }
                }
            }

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

            if (result == null)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(
                    cmd.Id,
                    $"No handler found for action: {cmd.Action}. Register a handler using AIBridgeRuntime.Instance.RegisterHandler()");
            }
        }

        private bool TryHandleBuiltInCommand(AIBridgeRuntimeCommand cmd, out AIBridgeRuntimeCommandResult result, out bool asyncStarted)
        {
            result = null;
            asyncStarted = false;

            switch (cmd.Action)
            {
                case "runtime.ping":
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, new
                    {
                        targetId = _targetId,
                        pong = true,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    return true;
                case "runtime.status":
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, BuildStatusData());
                    return true;
                case "runtime.logs":
                case "runtime.logs.clear":
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, BuildLogsData(cmd));
                    return true;
                case "runtime.perf":
                    StartCoroutine(SamplePerformance(cmd));
                    asyncStarted = true;
                    return true;
                case "runtime.handlers":
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, new
                    {
                        handlers = BuildHandlerSummaries(true),
                        targetId = _targetId
                    });
                    return true;
                case "runtime.screenshot":
                    StartCoroutine(CaptureScreenshot(cmd));
                    asyncStarted = true;
                    return true;
                default:
                    return false;
            }
        }

        private IEnumerator SamplePerformance(AIBridgeRuntimeCommand cmd)
        {
            RuntimePerfResult perfResult = null;
            AIBridgeRuntimeCommandResult finalResult = null;
            RuntimePerfSampler sampler = null;
            IEnumerator sampling = null;

            try
            {
                sampler = new RuntimePerfSampler(cmd);
                sampling = sampler.Sample(_targetId, value => perfResult = value);
            }
            catch (Exception ex)
            {
                finalResult = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "perf_failed: " + ex.Message);
            }

            // 手动驱动采样协程，确保采样异常也能写回 result，避免 CLI 只看到 timeout。
            while (finalResult == null && sampling != null)
            {
                object current = null;
                var moveNext = false;
                try
                {
                    moveNext = sampling.MoveNext();
                    if (moveNext)
                    {
                        current = sampling.Current;
                    }
                }
                catch (Exception ex)
                {
                    finalResult = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "perf_failed: " + ex.Message);
                    break;
                }

                if (!moveNext)
                {
                    break;
                }

                yield return current;
            }

            var samplingDisposable = sampling as IDisposable;
            if (samplingDisposable != null)
            {
                try { samplingDisposable.Dispose(); } catch { }
            }

            if (sampler != null)
            {
                try { sampler.Dispose(); } catch { }
            }

            if (finalResult == null)
            {
                finalResult = perfResult != null
                    ? AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, perfResult)
                    : AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "perf_failed: sampler produced no result.");
            }

            WriteResult(finalResult);
        }

        private IEnumerator CaptureScreenshot(AIBridgeRuntimeCommand cmd)
        {
            yield return new WaitForEndOfFrame();

            AIBridgeRuntimeCommandResult result;
            Texture2D texture = null;
            try
            {
                Directory.CreateDirectory(_screenshotsPath);
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (texture == null)
                {
                    result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "ScreenCapture returned no texture.");
                }
                else
                {
                    var capturedAtUtc = DateTime.UtcNow;
                    var capturedFrame = Time.frameCount;
                    var safeArea = Screen.safeArea;
                    var filename = "runtime_screenshot_" + capturedAtUtc.ToString("yyyyMMdd_HHmmss_fff") + ".png";
                    var path = Path.Combine(_screenshotsPath, filename);
                    var bytes = texture.EncodeToPNG();
                    File.WriteAllBytes(path, bytes);
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, new
                    {
                        action = "runtime.screenshot",
                        imagePath = path,
                        filename = filename,
                        width = texture.width,
                        height = texture.height,
                        fileSize = bytes == null ? 0 : bytes.Length,
                        orientation = Screen.orientation.ToString(),
                        safeArea = new
                        {
                            x = safeArea.x,
                            y = safeArea.y,
                            width = safeArea.width,
                            height = safeArea.height,
                            xMin = safeArea.xMin,
                            yMin = safeArea.yMin,
                            xMax = safeArea.xMax,
                            yMax = safeArea.yMax
                        },
                        screenDpi = Screen.dpi,
                        capturedFrame = capturedFrame,
                        capturedAtUtc = capturedAtUtc.ToString("o"),
                        sha256 = ComputeSha256(bytes),
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }
            }
            catch (Exception ex)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (texture != null)
                {
                    Destroy(texture);
                }
            }

            WriteResult(result);
        }

        private object BuildStatusData()
        {
            return new
            {
                targetId = _targetId,
                protocolVersion = 2,
                transport = _httpTransportServer != null && _httpTransportServer.IsRunning ? "http" : "file",
                capabilities = BuildCapabilitiesData(),
                httpUrl = BuildLocalHttpUrl(),
                httpPort = GetActualHttpPort(),
                httpBindAddress = runtimeSettings == null ? null : runtimeSettings.httpBindAddress,
                lanDiscoveryUdpPort = runtimeSettings == null ? 0 : runtimeSettings.discoveryUdpPort,
                runtimeVersion = RuntimeVersion,
                productName = Application.productName,
                applicationVersion = Application.version,
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                isEditor = Application.isEditor,
                isDebugBuild = Debug.isDebugBuild,
                runInBackground = Application.runInBackground,
                keepRunningInBackground = runtimeSettings != null && runtimeSettings.keepRunningInBackground,
                activeScene = SceneManager.GetActiveScene().name,
                loadedScenes = GetLoadedScenes(),
                uptimeSeconds = (DateTime.UtcNow - _startedAtUtc).TotalSeconds,
                frameCount = Time.frameCount,
                timeScale = Time.timeScale,
                approximateFps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f,
                logBufferCount = _logBuffer.Count,
                paths = new
                {
                    runtimeRoot = _runtimeRootPath,
                    targetPath = _targetPath,
                    commands = _commandsPath,
                    results = _resultsPath,
                    screenshots = _screenshotsPath
                }
            };
        }

        private object BuildLogsData(AIBridgeRuntimeCommand cmd)
        {
            var clear = string.Equals(cmd.Action, "runtime.logs.clear", StringComparison.OrdinalIgnoreCase)
                || ReadBoolParam(cmd, "clear", false)
                || string.Equals(ReadStringParam(cmd, "operation", null), "clear", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadStringParam(cmd, "mode", null), "clear", StringComparison.OrdinalIgnoreCase);

            if (clear)
            {
                var clearedCount = _logBuffer.Clear();
                return new
                {
                    logs = new AIBridgeRuntimeLogEntry[0],
                    count = 0,
                    clearedCount = clearedCount,
                    bufferCount = _logBuffer.Count,
                    targetId = _targetId
                };
            }

            var count = ReadIntParam(cmd, "tail", ReadIntParam(cmd, "count", 50));
            var logType = ReadStringParam(cmd, "logType", "all");
            var regex = ReadStringParam(cmd, "regex", null);
            var includeStackTrace = ReadBoolParam(cmd, "includeStackTrace", false);
            var sinceFrame = ReadNullableIntParam(cmd, "sinceFrame");
            var sinceTimestamp = ReadSinceTimestampParam(cmd);
            var logs = _logBuffer.GetEntries(count, logType, regex, includeStackTrace, sinceFrame, sinceTimestamp);
            return new
            {
                logs = logs,
                count = logs.Length,
                bufferCount = _logBuffer.Count,
                filters = new
                {
                    logType = logType,
                    regex = regex,
                    includeStackTrace = includeStackTrace,
                    tail = count,
                    sinceFrame = sinceFrame,
                    sinceTime = sinceTimestamp
                },
                targetId = _targetId
            };
        }

        private static bool ReadBoolParam(AIBridgeRuntimeCommand cmd, string key, bool defaultValue)
        {
            if (!TryGetCommandParam(cmd, key, out var value) || value == null)
            {
                return defaultValue;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is int intValue)
            {
                return intValue != 0;
            }

            if (value is long longValue)
            {
                return longValue != 0L;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "y", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "n", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return bool.TryParse(text, out var parsed) ? parsed : defaultValue;
        }

        private static int ReadIntParam(AIBridgeRuntimeCommand cmd, string key, int defaultValue)
        {
            if (!TryGetCommandParam(cmd, key, out var value) || value == null)
            {
                return defaultValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                return longValue < int.MinValue || longValue > int.MaxValue ? defaultValue : (int)longValue;
            }

            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : defaultValue;
        }

        private static int? ReadNullableIntParam(AIBridgeRuntimeCommand cmd, string key)
        {
            if (!TryGetCommandParam(cmd, key, out var value) || value == null)
            {
                return null;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                return longValue < int.MinValue || longValue > int.MaxValue ? (int?)null : (int)longValue;
            }

            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null;
        }

        private static string ReadStringParam(AIBridgeRuntimeCommand cmd, string key, string defaultValue)
        {
            if (!TryGetCommandParam(cmd, key, out var value) || value == null)
            {
                return defaultValue;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static long? ReadSinceTimestampParam(AIBridgeRuntimeCommand cmd)
        {
            if (TryGetCommandParam(cmd, "sinceTimestamp", out var timestamp)
                || TryGetCommandParam(cmd, "sinceTimeMs", out timestamp))
            {
                return ParseUnixTimestampMilliseconds(timestamp);
            }

            if (!TryGetCommandParam(cmd, "sinceTime", out var sinceTime) || sinceTime == null)
            {
                return null;
            }

            var numericTimestamp = ParseUnixTimestampMilliseconds(sinceTime);
            if (numericTimestamp.HasValue)
            {
                return numericTimestamp;
            }

            var text = Convert.ToString(sinceTime, CultureInfo.InvariantCulture);
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return parsed.ToUnixTimeMilliseconds();
            }

            return null;
        }

        private static long? ParseUnixTimestampMilliseconds(object value)
        {
            if (value == null)
            {
                return null;
            }

            long parsed;
            if (value is long longValue)
            {
                parsed = longValue;
            }
            else if (value is int intValue)
            {
                parsed = intValue;
            }
            else if (value is double doubleValue)
            {
                parsed = (long)Math.Round(doubleValue);
            }
            else if (value is float floatValue)
            {
                parsed = (long)Math.Round(floatValue);
            }
            else if (!long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return null;
            }

            // 允许 Unix 秒输入，内部统一为毫秒。
            return parsed > 0L && parsed < 100000000000L ? parsed * 1000L : parsed;
        }

        private static bool TryGetCommandParam(AIBridgeRuntimeCommand cmd, string key, out object value)
        {
            value = null;
            if (cmd == null || cmd.Params == null || string.IsNullOrEmpty(key))
            {
                return false;
            }

            foreach (var pair in cmd.Params)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            return false;
        }

        private List<object> GetLoadedScenes()
        {
            var scenes = new List<object>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new
                {
                    name = scene.name,
                    path = scene.path,
                    buildIndex = scene.buildIndex,
                    isLoaded = scene.isLoaded,
                    rootCount = scene.rootCount
                });
            }

            return scenes;
        }

        private List<object> BuildHandlerSummaries(bool includeBuiltIns)
        {
            var handlers = new List<object>();

            if (includeBuiltIns)
            {
                handlers.Add(new
                {
                    handler = "AIBridgeRuntime.BuiltIn",
                    async = false,
                    actions = BuiltInActions
                });
            }

            for (var i = 0; i < _handlers.Count; i++)
            {
                var handler = _handlers[i];
                if (handler == null)
                {
                    continue;
                }

                handlers.Add(new
                {
                    handler = handler.GetType().FullName,
                    async = false,
                    actions = handler.SupportedActions
                });
            }

            for (var i = 0; i < _asyncHandlers.Count; i++)
            {
                var handler = _asyncHandlers[i];
                if (handler == null)
                {
                    continue;
                }

                handlers.Add(new
                {
                    handler = handler.GetType().FullName,
                    async = true,
                    actions = handler.SupportedActions
                });
            }

            return handlers;
        }

        private bool ValidateCommand(AIBridgeRuntimeCommand cmd, out string error)
        {
            error = null;
            if (cmd == null)
            {
                error = "Runtime command is null.";
                return false;
            }

            if (string.IsNullOrEmpty(cmd.Id))
            {
                error = "Runtime command id is required.";
                return false;
            }

            if (string.IsNullOrEmpty(cmd.Action))
            {
                error = "Runtime command action is required.";
                return false;
            }

            if (!string.IsNullOrEmpty(runtimeSettings.authToken)
                && !string.Equals(runtimeSettings.authToken, cmd.Token, StringComparison.Ordinal))
            {
                error = "Runtime command token is invalid.";
                return false;
            }

            return true;
        }

        private bool IsCustomActionAllowed(string action)
        {
            if (IsBuiltInAction(action))
            {
                return true;
            }

            if (runtimeSettings.IsActionExplicitlyAllowed(action))
            {
                return true;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return runtimeSettings.allowedActions == null || runtimeSettings.allowedActions.Length == 0;
#else
            return false;
#endif
        }

        private bool IsRuntimeBridgeEnabled()
        {
            if (runtimeSettings == null || !runtimeSettings.enableRuntimeBridge)
            {
                return false;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return true;
#else
            return runtimeSettings.allowInReleaseBuild;
#endif
        }

        private static bool IsBuiltInAction(string action)
        {
            for (var i = 0; i < BuiltInActions.Length; i++)
            {
                if (string.Equals(BuiltInActions[i], action, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void WriteHeartbeatIfDue()
        {
            var interval = runtimeSettings == null ? 1f : Mathf.Max(0.1f, runtimeSettings.heartbeatIntervalSeconds);
            if (Time.realtimeSinceStartup - _lastHeartbeatTime < interval)
            {
                return;
            }

            WriteHeartbeat();
        }

        private void WriteHeartbeat()
        {
            _lastHeartbeatTime = Time.realtimeSinceStartup;
            var heartbeat = new Dictionary<string, object>
            {
                ["targetId"] = _targetId,
                ["protocolVersion"] = 2,
                ["transport"] = "file",
                ["capabilities"] = BuildCapabilitiesData(),
                ["httpUrl"] = BuildLocalHttpUrl(),
                ["httpPort"] = GetActualHttpPort(),
                ["httpBindAddress"] = runtimeSettings == null ? null : runtimeSettings.httpBindAddress,
                ["lanDiscoveryUdpPort"] = runtimeSettings == null ? 0 : runtimeSettings.discoveryUdpPort,
                ["runtimeVersion"] = RuntimeVersion,
                ["productName"] = Application.productName,
                ["applicationVersion"] = Application.version,
                ["unityVersion"] = Application.unityVersion,
                ["platform"] = Application.platform.ToString(),
                ["isEditor"] = Application.isEditor,
                ["isDebugBuild"] = Debug.isDebugBuild,
                ["runInBackground"] = Application.runInBackground,
                ["keepRunningInBackground"] = runtimeSettings != null && runtimeSettings.keepRunningInBackground,
                ["activeScene"] = SceneManager.GetActiveScene().name,
                ["uptimeSeconds"] = (DateTime.UtcNow - _startedAtUtc).TotalSeconds,
                ["lastHeartbeatUtc"] = DateTime.UtcNow.ToString("o"),
                ["processId"] = TryGetProcessId(),
                ["runtimeRoot"] = _runtimeRootPath,
                ["targetPath"] = _targetPath,
                ["commandsPath"] = _commandsPath,
                ["resultsPath"] = _resultsPath,
                ["screenshotsPath"] = _screenshotsPath
            };

            try
            {
                WriteTextAtomic(_heartbeatPath, AIBridgeJson.Serialize(heartbeat, pretty: true));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AIBridgeRuntime] Failed to write heartbeat: {ex.Message}");
            }
        }

        private void ApplyRunInBackgroundIfNeeded()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (runtimeSettings == null || !runtimeSettings.keepRunningInBackground)
            {
                return;
            }

            _previousRunInBackground = Application.runInBackground;
            if (Application.runInBackground)
            {
                return;
            }

            // Runtime Bridge 依赖帧循环刷新 heartbeat 和处理命令，失焦后保持运行可避免 CLI 误判超时。
            Application.runInBackground = true;
            _runInBackgroundChanged = true;
            LogDebug("Enabled Application.runInBackground for Runtime Bridge.");
#endif
        }

        private void RestoreRunInBackgroundIfNeeded()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_runInBackgroundChanged)
            {
                return;
            }

            if (Application.runInBackground)
            {
                Application.runInBackground = _previousRunInBackground;
            }

            _runInBackgroundChanged = false;
#endif
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
                var maxBytes = runtimeSettings == null ? 0 : runtimeSettings.maxResultBytes;
                if (maxBytes > 0 && Encoding.UTF8.GetByteCount(json) > maxBytes)
                {
                    result = AIBridgeRuntimeCommandResult.FromFailure(result.CommandId, "Runtime command result exceeded maxResultBytes.");
                    json = AIBridgeJson.Serialize(result, pretty: true);
                }

                CacheHttpResult(result);
                WriteTextAtomic(filePath, json);
                LogDebug($"Wrote result: {fileName}");
            }
            catch (Exception e)
            {
                CacheHttpResult(result);
                Debug.LogError($"[AIBridgeRuntime] Failed to write result: {e.Message}");
            }
        }

        private string ResolveRuntimeRootPath()
        {
            var configuredPath = runtimeSettings == null ? null : runtimeSettings.exchangeDirectory;
            if (!string.IsNullOrEmpty(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }

            var argPath = GetCommandLineArgValue(RuntimeDirArgument);
            if (!string.IsNullOrEmpty(argPath))
            {
                return Path.GetFullPath(argPath);
            }

            var envPath = Environment.GetEnvironmentVariable(RuntimeDirEnvironment);
            if (!string.IsNullOrEmpty(envPath))
            {
                return Path.GetFullPath(envPath);
            }

            var projectRoot = TryResolveEditorProjectRoot();
            if (!string.IsNullOrEmpty(projectRoot))
            {
                return Path.Combine(projectRoot, ".aibridge", RuntimeDirectoryName);
            }

            return Path.Combine(Application.persistentDataPath, ".aibridge", RuntimeDirectoryName);
        }

        private string ResolveTargetId()
        {
            var configuredTarget = runtimeSettings == null ? null : runtimeSettings.targetId;
            if (!string.IsNullOrEmpty(configuredTarget))
            {
                return SanitizeTargetId(configuredTarget);
            }

            var argTarget = GetCommandLineArgValue(TargetIdArgument);
            if (!string.IsNullOrEmpty(argTarget))
            {
                return SanitizeTargetId(argTarget);
            }

            var envTarget = Environment.GetEnvironmentVariable(TargetIdEnvironment);
            if (!string.IsNullOrEmpty(envTarget))
            {
                return SanitizeTargetId(envTarget);
            }

            var processId = TryGetProcessId();
            var productName = string.IsNullOrEmpty(Application.productName) ? "player" : Application.productName;
            return SanitizeTargetId(productName + "_" + processId);
        }

        private static string TryResolveEditorProjectRoot()
        {
            var dataPath = Application.dataPath;
            if (string.IsNullOrEmpty(dataPath))
            {
                return null;
            }

            var normalized = dataPath.Replace('\\', '/');
            if (!normalized.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return dataPath.Substring(0, dataPath.Length - "/Assets".Length);
        }

        private static string GetCommandLineArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                var prefix = name + "=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring(prefix.Length);
                }
            }

            return null;
        }

        private static string SanitizeTargetId(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "player";
            }

            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append('_');
                }
            }

            return builder.ToString();
        }

        private static int TryGetProcessId()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch
            {
                return 0;
            }
        }

        internal string TargetId => _targetId;

        internal bool IsHttpTransportEnabled()
        {
            if (runtimeSettings == null || !runtimeSettings.enableHttpTransport)
            {
                return false;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return true;
#else
            return runtimeSettings.allowInReleaseBuild;
#endif
        }

        internal bool IsLanDiscoveryEnabled()
        {
            if (runtimeSettings == null || !runtimeSettings.enableLanDiscovery || !IsHttpTransportEnabled())
            {
                return false;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return true;
#else
            return runtimeSettings.allowInReleaseBuild;
#endif
        }

        internal Dictionary<string, object> BuildHttpHealthData()
        {
            // HTTP 请求在后台线程处理，health 只读取运行时缓存字段，避免跨线程访问 Unity API。
            return new Dictionary<string, object>
            {
                ["targetId"] = _targetId,
                ["protocolVersion"] = 2,
                ["transport"] = "http",
                ["httpUrl"] = BuildLocalHttpUrl(),
                ["httpPort"] = GetActualHttpPort(),
                ["httpBindAddress"] = runtimeSettings == null ? null : runtimeSettings.httpBindAddress,
                ["runtimeVersion"] = RuntimeVersion,
                ["uptimeSeconds"] = (DateTime.UtcNow - _startedAtUtc).TotalSeconds,
                ["lastHeartbeatUtc"] = DateTime.UtcNow.ToString("o"),
                ["capabilities"] = BuildCapabilitiesData()
            };
        }

        internal void EnqueueHttpCommand(AIBridgeRuntimeCommand command)
        {
            if (command == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(command.Id))
            {
                lock (_pendingHttpResultsSyncRoot)
                {
                    _httpCommandIds.Add(command.Id);
                }
            }

            lock (_commandQueue)
            {
                _commandQueue.Enqueue(command);
            }
        }

        internal bool TryGetHttpResult(string commandId, bool remove, out AIBridgeRuntimeCommandResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(commandId))
            {
                return false;
            }

            lock (_pendingHttpResultsSyncRoot)
            {
                if (!_pendingHttpResults.TryGetValue(commandId, out result))
                {
                    return false;
                }

                if (remove)
                {
                    _pendingHttpResults.Remove(commandId);
                    _httpCommandIds.Remove(commandId);
                }

                return true;
            }
        }

        internal bool TryReadHttpScreenshotArtifact(string filename, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(filename) || string.IsNullOrEmpty(_screenshotsPath))
            {
                return false;
            }

            var safeName = Path.GetFileName(filename);
            if (string.IsNullOrEmpty(safeName) || !string.Equals(safeName, filename, StringComparison.Ordinal))
            {
                return false;
            }

            var path = Path.Combine(_screenshotsPath, safeName);
            if (!File.Exists(path))
            {
                return false;
            }

            bytes = File.ReadAllBytes(path);
            return true;
        }

        private string BuildLocalHttpUrl()
        {
            if (_httpTransportServer == null || !_httpTransportServer.IsRunning)
            {
                return null;
            }

            return _httpTransportServer.Url;
        }

        internal int GetActualHttpPort()
        {
            if (_httpTransportServer != null && _httpTransportServer.IsRunning)
            {
                return _httpTransportServer.Port;
            }

            return runtimeSettings == null ? 0 : runtimeSettings.httpPort;
        }

        private void StartHttpTransportIfNeeded()
        {
            if (!IsHttpTransportEnabled())
            {
                return;
            }

            if (_httpTransportServer != null)
            {
                return;
            }

            HttpRuntimeTransportServer server = null;
            try
            {
                server = new HttpRuntimeTransportServer(this, runtimeSettings);
                server.Start();
                _httpTransportServer = server;
            }
            catch (Exception ex)
            {
                if (server != null)
                {
                    server.Dispose();
                }

                Debug.LogError("[AIBridgeRuntime] Failed to start HTTP transport: " + ex.Message);
                _httpTransportServer = null;
            }
        }

        private void StopHttpTransport()
        {
            if (_httpTransportServer == null)
            {
                return;
            }

            _httpTransportServer.Dispose();
            _httpTransportServer = null;
        }

        private void StartLanDiscoveryIfNeeded()
        {
            if (!IsLanDiscoveryEnabled())
            {
                return;
            }

            if (_lanDiscoveryServer != null)
            {
                return;
            }

            LanRuntimeDiscoveryServer server = null;
            try
            {
                server = new LanRuntimeDiscoveryServer(this, runtimeSettings);
                server.Start();
                _lanDiscoveryServer = server;
            }
            catch (Exception ex)
            {
                if (server != null)
                {
                    server.Dispose();
                }

                Debug.LogError("[AIBridgeRuntime] Failed to start LAN discovery: " + ex.Message);
                _lanDiscoveryServer = null;
            }
        }

        private void StopLanDiscovery()
        {
            if (_lanDiscoveryServer == null)
            {
                return;
            }

            _lanDiscoveryServer.Dispose();
            _lanDiscoveryServer = null;
        }

        private void CacheHttpResult(AIBridgeRuntimeCommandResult result)
        {
            if (result == null || string.IsNullOrEmpty(result.CommandId))
            {
                return;
            }

            lock (_pendingHttpResultsSyncRoot)
            {
                if (!_httpCommandIds.Contains(result.CommandId))
                {
                    return;
                }

                _pendingHttpResults[result.CommandId] = result;
                Monitor.PulseAll(_pendingHttpResultsSyncRoot);
            }
        }

        internal Dictionary<string, object> BuildCapabilitiesData()
        {
            return new Dictionary<string, object>
            {
                ["perf"] = true,
                ["diagnose"] = true,
                ["screenshotPull"] = true,
                ["logsClear"] = true,
                ["file"] = true,
                ["http"] = _httpTransportServer != null && _httpTransportServer.IsRunning,
                ["lanDiscovery"] = _lanDiscoveryServer != null && _lanDiscoveryServer.IsRunning,
                ["ws"] = false
            };
        }

        private static string ComputeSha256(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private static void WriteTextAtomic(string path, string text)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时文件再替换，避免 CLI 读到半截 JSON。
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, text, Encoding.UTF8);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
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
