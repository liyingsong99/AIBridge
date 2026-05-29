using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace AIBridge.Editor
{
    [InitializeOnLoad]
    internal static class AIBridgeCodeIndexEditorUtility
    {
        private const string PackageName = "cn.lys.aibridge";
        private const string CliCacheRelativeDirectory = ".aibridge/cli";
        private const string IndexRelativeDirectory = ".aibridge/code-index";
        private const string StatusFileName = "status.json";
        private const string LockFileName = "lock.json";
        private const string ConfigFileName = "config.json";
        private const string TempDirectoryName = "temp";
        private const string LogsDirectoryName = "logs";
        private const int StartupRetryDelaySeconds = 2;
        private const double PostCompileRefreshDelaySeconds = 1.0;
        private const string PendingPostCompileRefreshSessionKey = "AIBridge.CodeIndex.PendingPostCompileRefresh";

        private static bool _startupPrewarmScheduled;
        private static bool _startupPrewarmStarted;
        private static double _startupPrewarmTime;
        private static bool _snapshotRefreshPending;
        private static bool _snapshotRefreshManual;
        private static bool _snapshotRefreshStartWarmup;
        private static double _snapshotRefreshTime;
        private static string _snapshotRefreshReason;
        private static bool _snapshotRefreshRunning;
        private static bool _snapshotRefreshRunningManual;
        private static bool _snapshotRefreshRunningStartWarmup;
        private static string _snapshotRefreshRunningReason;
        private static Task<AIBridgeCodeIndexSnapshotUtility.SnapshotResult> _snapshotRefreshTask;

        static AIBridgeCodeIndexEditorUtility()
        {
            if (IsAssetImportWorker())
            {
                return;
            }

            EditorApplication.delayCall += InitializeDelayedCodeIndex;
            EditorApplication.quitting += ShutdownOnEditorQuitting;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        public static string GetIndexDirectory()
        {
            return Path.Combine(GetProjectRoot(), IndexRelativeDirectory);
        }

        public static string GetStatusPath()
        {
            return Path.Combine(GetIndexDirectory(), StatusFileName);
        }

        public static string GetSnapshotDirectory()
        {
            return AIBridgeCodeIndexSnapshotUtility.GetSnapshotDirectory();
        }

        public static string ResolveCliPath()
        {
            var projectRoot = GetProjectRoot();
            var cliExeName = GetCliExecutableName();
            var cachedCli = Path.Combine(projectRoot, CliCacheRelativeDirectory, cliExeName);
            if (File.Exists(cachedCli))
            {
                return cachedCli;
            }

            var directCli = Path.Combine(projectRoot, "Packages", PackageName, "Tools~", "CLI", GetPlatformRid(), cliExeName);
            if (File.Exists(directCli))
            {
                return directCli;
            }

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + PackageName);
            if (packageInfo != null)
            {
                var packageCli = Path.Combine(packageInfo.resolvedPath, "Tools~", "CLI", GetPlatformRid(), cliExeName);
                if (File.Exists(packageCli))
                {
                    return packageCli;
                }
            }

            return null;
        }

        public static bool StartWarmupNoWait(bool manual)
        {
            var settings = AIBridgeProjectSettings.Instance.CodeIndex;
            if (!settings.EnableCodeIndex || (!manual && !settings.PrewarmOnUnityStartup))
            {
                return false;
            }

            return ScheduleSnapshotRefresh(manual, startWarmup: true, reason: manual ? "manualWarmup" : "startupPrewarm");
        }

        public static bool ScheduleSnapshotRefresh(bool manual)
        {
            return ScheduleSnapshotRefresh(manual, startWarmup: false, reason: manual ? "manualSnapshot" : "autoRefresh");
        }

        private static bool BeginSnapshotRefresh(bool manual, bool startWarmup, string reason)
        {
            var settings = AIBridgeProjectSettings.Instance.CodeIndex;
            if (!settings.EnableCodeIndex || (!manual && startWarmup && !settings.PrewarmOnUnityStartup))
            {
                return false;
            }

            WriteCodeIndexConfig();
            try
            {
                _snapshotRefreshTask = AIBridgeCodeIndexSnapshotUtility.GenerateSnapshotAsync();
            }
            catch (Exception ex)
            {
                if (manual)
                {
                    AIBridgeLogger.LogWarning("[CodeIndex] Failed to start Unity compilation snapshot refresh: " + ex.Message);
                }

                return false;
            }

            _snapshotRefreshRunning = true;
            _snapshotRefreshRunningManual = manual;
            _snapshotRefreshRunningStartWarmup = startWarmup;
            _snapshotRefreshRunningReason = reason;
            EditorApplication.update -= PollSnapshotRefreshTask;
            EditorApplication.update += PollSnapshotRefreshTask;
            return true;
        }

        private static void PollSnapshotRefreshTask()
        {
            if (!_snapshotRefreshRunning || _snapshotRefreshTask == null)
            {
                EditorApplication.update -= PollSnapshotRefreshTask;
                return;
            }

            if (!_snapshotRefreshTask.IsCompleted)
            {
                return;
            }

            EditorApplication.update -= PollSnapshotRefreshTask;

            var manual = _snapshotRefreshRunningManual;
            var startWarmup = _snapshotRefreshRunningStartWarmup;
            var reason = _snapshotRefreshRunningReason;
            AIBridgeCodeIndexSnapshotUtility.SnapshotResult result;
            try
            {
                result = _snapshotRefreshTask.Result;
            }
            catch (Exception ex)
            {
                result = new AIBridgeCodeIndexSnapshotUtility.SnapshotResult(false, GetTaskExceptionMessage(ex));
            }

            _snapshotRefreshRunning = false;
            _snapshotRefreshRunningManual = false;
            _snapshotRefreshRunningStartWarmup = false;
            _snapshotRefreshRunningReason = null;
            _snapshotRefreshTask = null;

            CompleteSnapshotRefresh(result, manual, startWarmup, reason);

            if (!_snapshotRefreshPending)
            {
                SessionState.SetBool(PendingPostCompileRefreshSessionKey, false);
            }

            if (_snapshotRefreshPending)
            {
                EditorApplication.update -= TryRunScheduledSnapshotRefresh;
                EditorApplication.update += TryRunScheduledSnapshotRefresh;
            }
        }

        private static void CompleteSnapshotRefresh(
            AIBridgeCodeIndexSnapshotUtility.SnapshotResult result,
            bool manual,
            bool startWarmup,
            string reason)
        {
            if (result == null || !result.Success)
            {
                var message = result == null ? "unknown failure" : result.Message;
                if (manual)
                {
                    UnityEngine.Debug.LogWarning(AIBridgeEditorText.T(
                        "[AIBridge] Code Index snapshot failed: " + message,
                        "[AIBridge] Code Index 快照生成失败：" + message));
                }
                else
                {
                    AIBridgeLogger.LogWarning("[CodeIndex] Failed to generate Unity compilation snapshot: " + message);
                }

                return;
            }

            if (manual && !startWarmup)
            {
                UnityEngine.Debug.Log(AIBridgeEditorText.T(
                    "[AIBridge] Code Index snapshot generated: " + result.Message,
                    "[AIBridge] Code Index 快照已生成：" + result.Message));
            }

            if (startWarmup)
            {
                StartWarmupDaemonNoWait(manual);
            }

            AIBridgeLogger.LogDebug("[CodeIndex] Snapshot refresh completed. reason=" + (reason ?? "unknown") + ", " + result.Message);
        }

        private static bool StartWarmupDaemonNoWait(bool manual)
        {
            var settings = AIBridgeProjectSettings.Instance.CodeIndex;
            var cliPath = ResolveCliPath();
            if (string.IsNullOrEmpty(cliPath))
            {
                if (manual)
                {
                    AIBridgeLogger.LogWarning("[CodeIndex] AIBridgeCLI was not found for warmup.");
                }

                return false;
            }

            var args = "code_index warmup --no-wait --timeout 1000"
                       + " --unity-pid " + Process.GetCurrentProcess().Id
                       + " --auto-refresh " + ToCliBool(settings.AutoRefreshOnFileChange);
            return StartCli(cliPath, args, waitForExit: false, timeoutMs: 1000);
        }

        public static void ShutdownDaemon(string cleanupMode, int timeoutMs)
        {
            var status = ReadStatus();
            if (status != null && !string.IsNullOrEmpty(status.Endpoint))
            {
                TryPostShutdown(status.Endpoint, status.Token, timeoutMs);
            }

            if (status != null && status.DaemonPid > 0)
            {
                WaitOrKillDaemon(status.DaemonPid, timeoutMs);
            }

            CleanupIndexDirectory(cleanupMode);
        }

        public static void WriteCodeIndexConfig()
        {
            try
            {
                var settings = AIBridgeProjectSettings.Instance.CodeIndex;
                var directory = GetIndexDirectory();
                Directory.CreateDirectory(directory);
                var json = "{\n"
                           + "  \"workspaceMode\": \"unity-snapshot\",\n"
                           + "  \"enableCodeIndex\": " + ToJsonBool(settings.EnableCodeIndex) + ",\n"
                           + "  \"prewarmOnUnityStartup\": " + ToJsonBool(settings.PrewarmOnUnityStartup) + ",\n"
                           + "  \"warmupDelaySeconds\": " + Mathf.Max(0, settings.WarmupDelaySeconds) + ",\n"
                           + "  \"warmupMode\": \"" + EscapeJson(settings.WarmupMode) + "\",\n"
                           + "  \"autoRefreshOnFileChange\": " + ToJsonBool(settings.AutoRefreshOnFileChange) + ",\n"
                           + "  \"fallbackToTextSearch\": " + ToJsonBool(settings.FallbackToTextSearch) + ",\n"
                           + "  \"cleanupModeOnQuit\": \"" + EscapeJson(settings.CleanupModeOnQuit) + "\",\n"
                           + "  \"includePackageCacheSourceAssemblies\": " + ToJsonBool(settings.IncludePackageCacheSourceAssemblies) + ",\n"
                           + "  \"ignoredAssemblyPatterns\": " + ToJsonStringArray(SplitCodeIndexPatterns(settings.IgnoredAssemblyPatterns)) + ",\n"
                           + "  \"ignoredSourcePathPatterns\": " + ToJsonStringArray(SplitCodeIndexPatterns(settings.IgnoredSourcePathPatterns)) + "\n"
                           + "}\n";
                File.WriteAllText(Path.Combine(directory, ConfigFileName), json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("[CodeIndex] Failed to write config: " + ex.Message);
            }
        }

        public static void OpenIndexDirectory()
        {
            Directory.CreateDirectory(GetIndexDirectory());
            EditorUtility.RevealInFinder(GetIndexDirectory());
        }

        public static string BuildCliCommand(string commandBody)
        {
            return "$CLI " + commandBody;
        }

        private static void InitializeDelayedCodeIndex()
        {
            if (RestorePendingPostCompileRefresh())
            {
                return;
            }

            ScheduleStartupPrewarm();
        }

        private static void ScheduleStartupPrewarm()
        {
            if (_startupPrewarmScheduled || Application.isBatchMode)
            {
                return;
            }

            var settings = AIBridgeProjectSettings.Instance.CodeIndex;
            WriteCodeIndexConfig();
            if (!settings.EnableCodeIndex || !settings.PrewarmOnUnityStartup)
            {
                return;
            }

            if (settings.AutoRefreshOnFileChange && SessionState.GetBool(PendingPostCompileRefreshSessionKey, false))
            {
                return;
            }

            _startupPrewarmScheduled = true;
            _startupPrewarmTime = EditorApplication.timeSinceStartup + Mathf.Max(0, settings.WarmupDelaySeconds);
            EditorApplication.update += TryStartupPrewarm;
        }

        private static void TryStartupPrewarm()
        {
            if (_startupPrewarmStarted)
            {
                EditorApplication.update -= TryStartupPrewarm;
                return;
            }

            if (EditorApplication.timeSinceStartup < _startupPrewarmTime)
            {
                return;
            }

            if (EditorApplication.isCompiling
                || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                _startupPrewarmTime = EditorApplication.timeSinceStartup + StartupRetryDelaySeconds;
                return;
            }

            _startupPrewarmStarted = true;
            EditorApplication.update -= TryStartupPrewarm;
            StartWarmupNoWait(manual: false);
        }

        private static void OnCompilationFinished(object context)
        {
            var settings = AIBridgeProjectSettings.Instance.CodeIndex;
            if (!settings.EnableCodeIndex || !settings.AutoRefreshOnFileChange)
            {
                return;
            }

            SessionState.SetBool(PendingPostCompileRefreshSessionKey, true);
            ScheduleSnapshotRefresh(manual: false, startWarmup: settings.PrewarmOnUnityStartup, reason: "compilationFinished");
        }

        private static bool RestorePendingPostCompileRefresh()
        {
            if (!SessionState.GetBool(PendingPostCompileRefreshSessionKey, false))
            {
                return false;
            }

            SessionState.SetBool(PendingPostCompileRefreshSessionKey, false);
            var settings = AIBridgeProjectSettings.Instance.CodeIndex;
            if (!settings.EnableCodeIndex || !settings.AutoRefreshOnFileChange)
            {
                return false;
            }

            return ScheduleSnapshotRefresh(manual: false, startWarmup: settings.PrewarmOnUnityStartup, reason: "postReloadCompilationFinished");
        }

        private static bool ScheduleSnapshotRefresh(bool manual, bool startWarmup, string reason)
        {
            if (Application.isBatchMode)
            {
                return false;
            }

            var settings = AIBridgeProjectSettings.Instance.CodeIndex;
            if (!settings.EnableCodeIndex)
            {
                return false;
            }

            if (!manual && !startWarmup && !settings.AutoRefreshOnFileChange)
            {
                return false;
            }

            WriteCodeIndexConfig();
            _snapshotRefreshPending = true;
            _snapshotRefreshManual = _snapshotRefreshManual || manual;
            _snapshotRefreshStartWarmup = _snapshotRefreshStartWarmup || startWarmup;
            _snapshotRefreshReason = reason;
            _snapshotRefreshTime = Math.Max(_snapshotRefreshTime, EditorApplication.timeSinceStartup + PostCompileRefreshDelaySeconds);
            if (startWarmup)
            {
                _startupPrewarmStarted = true;
                _startupPrewarmScheduled = true;
                EditorApplication.update -= TryStartupPrewarm;
            }

            EditorApplication.update -= TryRunScheduledSnapshotRefresh;
            EditorApplication.update += TryRunScheduledSnapshotRefresh;
            return true;
        }

        private static void TryRunScheduledSnapshotRefresh()
        {
            if (!_snapshotRefreshPending)
            {
                EditorApplication.update -= TryRunScheduledSnapshotRefresh;
                return;
            }

            if (EditorApplication.timeSinceStartup < _snapshotRefreshTime)
            {
                return;
            }

            if (_snapshotRefreshRunning)
            {
                _snapshotRefreshTime = EditorApplication.timeSinceStartup + StartupRetryDelaySeconds;
                return;
            }

            if (!IsEditorIdleForCodeIndex())
            {
                _snapshotRefreshTime = EditorApplication.timeSinceStartup + StartupRetryDelaySeconds;
                return;
            }

            var manual = _snapshotRefreshManual;
            var startWarmup = _snapshotRefreshStartWarmup;
            var reason = _snapshotRefreshReason;
            _snapshotRefreshPending = false;
            _snapshotRefreshManual = false;
            _snapshotRefreshStartWarmup = false;
            _snapshotRefreshTime = 0;
            _snapshotRefreshReason = null;
            EditorApplication.update -= TryRunScheduledSnapshotRefresh;

            // Unity API 采集已完成后，文件 hash、token 扫描和写入都交给后台任务，避免刷新卡住 Editor。
            if (!BeginSnapshotRefresh(manual, startWarmup, reason))
            {
                SessionState.SetBool(PendingPostCompileRefreshSessionKey, false);
                AIBridgeLogger.LogWarning("[CodeIndex] Snapshot refresh was not started. reason=" + (reason ?? "unknown"));
            }
        }

        private static bool IsEditorIdleForCodeIndex()
        {
            return !EditorApplication.isCompiling
                   && !EditorApplication.isUpdating
                   && !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private static void ShutdownOnEditorQuitting()
        {
            try
            {
                var cleanupMode = AIBridgeProjectSettings.Instance.CodeIndex.CleanupModeOnQuit;
                ShutdownDaemon(cleanupMode, 3000);
            }
            catch
            {
            }
        }

        private static string GetProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        private static string GetCliExecutableName()
        {
#if UNITY_EDITOR_WIN
            return "AIBridgeCLI.exe";
#else
            return "AIBridgeCLI";
#endif
        }

        private static string GetPlatformRid()
        {
#if UNITY_EDITOR_WIN
            return "win-x64";
#elif UNITY_EDITOR_OSX
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";
#elif UNITY_EDITOR_LINUX
            return "linux-x64";
#else
            return "win-x64";
#endif
        }

        private static bool StartCli(string cliPath, string arguments, bool waitForExit, int timeoutMs)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = arguments,
                    WorkingDirectory = GetProjectRoot(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                var process = Process.Start(startInfo);
                if (process == null)
                {
                    return false;
                }

                if (!waitForExit)
                {
                    process.Dispose();
                    return true;
                }

                var exited = process.WaitForExit(timeoutMs);
                process.Dispose();
                return exited;
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("[CodeIndex] Failed to start CLI: " + ex.Message);
                return false;
            }
        }

        private static void TryPostShutdown(string endpoint, string token, int timeoutMs)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(endpoint.TrimEnd('/') + "/shutdown");
                request.Method = "POST";
                request.Timeout = Math.Max(500, timeoutMs);
                request.ContentType = "application/json";
                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers["X-AIBridge-CodeIndex-Token"] = token;
                }

                var body = Encoding.UTF8.GetBytes("{}");
                request.ContentLength = body.Length;
                using (var stream = request.GetRequestStream())
                {
                    stream.Write(body, 0, body.Length);
                }

                using (request.GetResponse())
                {
                }
            }
            catch
            {
            }
        }

        private static void WaitOrKillDaemon(int daemonPid, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, timeoutMs));
            while (DateTime.UtcNow < deadline)
            {
                if (!TryGetCodeIndexProcess(daemonPid, out var process))
                {
                    return;
                }

                process.Dispose();
                System.Threading.Thread.Sleep(100);
            }

            if (!TryGetCodeIndexProcess(daemonPid, out var remaining))
            {
                return;
            }

            using (remaining)
            {
                try
                {
                    remaining.Kill();
                    remaining.WaitForExit(1000);
                }
                catch
                {
                }
            }
        }

        private static bool TryGetCodeIndexProcess(int processId, out Process process)
        {
            process = null;
            try
            {
                var candidate = Process.GetProcessById(processId);
                if (candidate.HasExited
                    || candidate.ProcessName.IndexOf("AIBridgeCodeIndex", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    candidate.Dispose();
                    return false;
                }

                process = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CleanupIndexDirectory(string cleanupMode)
        {
            var normalized = AIBridgeProjectSettings.NormalizeCodeIndexCleanupMode(cleanupMode);
            var directory = GetIndexDirectory();
            if (!Directory.Exists(directory))
            {
                return;
            }

            if (normalized == "fullCleanup")
            {
                Directory.Delete(directory, true);
                return;
            }

            DeleteFileIfExists(Path.Combine(directory, StatusFileName));
            DeleteFileIfExists(Path.Combine(directory, LockFileName));
            DeleteDirectoryIfExists(Path.Combine(directory, TempDirectoryName));

            if (normalized == "processAndTemp")
            {
                DeleteDirectoryIfExists(Path.Combine(directory, LogsDirectoryName));
            }
        }

        private static CodeIndexStatusSnapshot ReadStatus()
        {
            var path = GetStatusPath();
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                return new CodeIndexStatusSnapshot
                {
                    Endpoint = ReadString(json, "endpoint"),
                    Token = ReadString(json, "token"),
                    DaemonPid = ReadInt(json, "daemonPid")
                };
            }
            catch
            {
                return null;
            }
        }

        private static string ReadString(string json, string key)
        {
            var match = Regex.Match(json ?? string.Empty, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"");
            return match.Success ? Regex.Unescape(match.Groups["value"].Value) : null;
        }

        private static string GetTaskExceptionMessage(Exception ex)
        {
            if (ex == null)
            {
                return string.Empty;
            }

            var aggregate = ex as AggregateException;
            if (aggregate != null && aggregate.InnerExceptions.Count > 0)
            {
                return aggregate.InnerExceptions[0].Message;
            }

            return ex.InnerException == null ? ex.Message : ex.InnerException.Message;
        }

        private static int ReadInt(string json, string key)
        {
            var match = Regex.Match(json ?? string.Empty, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<value>\\d+)");
            if (!match.Success)
            {
                return 0;
            }

            int.TryParse(match.Groups["value"].Value, out var value);
            return value;
        }

        private static void DeleteFileIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        private static bool IsAssetImportWorker()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-name", StringComparison.OrdinalIgnoreCase)
                    && i + 1 < args.Length
                    && args[i + 1].StartsWith("AssetImportWorker", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ToJsonBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string ToCliBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string[] SplitCodeIndexPatterns(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new string[0];
            }

            return value.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string ToJsonStringArray(string[] values)
        {
            var builder = new StringBuilder();
            builder.Append("[");
            for (var i = 0; values != null && i < values.Length; i++)
            {
                var value = values[i] == null ? string.Empty : values[i].Trim();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (builder.Length > 1)
                {
                    builder.Append(", ");
                }

                builder.Append("\"").Append(EscapeJson(value)).Append("\"");
            }

            builder.Append("]");
            return builder.ToString();
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private sealed class CodeIndexStatusSnapshot
        {
            public string Endpoint { get; set; }
            public string Token { get; set; }
            public int DaemonPid { get; set; }
        }
    }
}
