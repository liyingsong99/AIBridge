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
        private const string DaemonProcessFileName = "daemon-process.json";
        private const string DaemonProcessDirectoryName = "daemon-processes";
        private const string DaemonLaunchLockFileName = "daemon-launch.lock";
        private const string DaemonAssemblyName = "AIBridgeCodeIndex";
        private const string TempDirectoryName = "temp";
        private const string LogsDirectoryName = "logs";
        private const int StartupRetryDelaySeconds = 2;
        private const int DaemonLaunchLockWaitMs = 1500;
        private const int DaemonStatusProbeTimeoutMs = 250;
        private const int ExistingDaemonReachabilityWaitMs = 1200;
        private const int ExistingDaemonRetryDelayMs = 100;
        // 进程启动时间读取及序列化存在微小精度差；2 秒窗口与 daemon 端 owner monitor 保持一致。
        private const long OwnerStartTicksTolerance = TimeSpan.TicksPerSecond * 2L;
        private const double PostCompileRefreshDelaySeconds = 1.0;
        private const double SettingsPanelCleanupIntervalSeconds = 5.0;
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
        private static Task<bool> _daemonWarmupTask;
        private static double _lastSettingsPanelCleanupTime = -SettingsPanelCleanupIntervalSeconds;

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

        private static string GetDaemonProcessPath()
        {
            return Path.Combine(GetIndexDirectory(), DaemonProcessFileName);
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

            CleanupOrphanDaemons(logWhenChanged: manual);
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
                _snapshotRefreshTask = AIBridgeCodeIndexSnapshotUtility.GenerateSnapshotAsync(manual, reason);
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
            if (_daemonWarmupTask != null && !_daemonWarmupTask.IsCompleted)
            {
                return true;
            }

            var settings = AIBridgeProjectSettings.Instance.CodeIndex;
            var daemonPath = ResolveDaemonPath();
            if (string.IsNullOrEmpty(daemonPath))
            {
                if (manual)
                {
                    AIBridgeLogger.LogWarning("[CodeIndex] AIBridgeCodeIndex daemon was not found for warmup.");
                }

                return false;
            }

            int ownerPid;
            long ownerStartTicks;
            using (var currentProcess = Process.GetCurrentProcess())
            {
                ownerPid = currentProcess.Id;
                ownerStartTicks = GetProcessStartTicks(currentProcess);
            }

            // 预热是 Editor 受控入口，直接启动随包发布的 daemon；不能再经由公共 code_index 动作路由。
            var arguments = "--project-root " + QuoteProcessArgument(GetProjectRoot())
                            + " --status-path " + QuoteProcessArgument(GetStatusPath())
                            + " --token " + Guid.NewGuid().ToString("N")
                            + " --unity-pid " + ownerPid
                            + " --owner-pid " + ownerPid
                            + " --owner-start-ticks " + ownerStartTicks
                            + " --auto-refresh " + ToCliBool(settings.AutoRefreshOnFileChange);

            // 文件锁、HTTP 探测和保守等待均在后台完成，不能卡住 Unity 主线程。
            _daemonWarmupTask = Task.Run(() => StartWarmupDaemonUnderLaunchLock(daemonPath, arguments, manual ? "normal" : "low"));
            _daemonWarmupTask.ContinueWith(task =>
            {
                if (!task.IsFaulted)
                {
                    return;
                }

                var message = GetTaskExceptionMessage(task.Exception);
                EditorApplication.delayCall += () => AIBridgeLogger.LogWarning("[CodeIndex] Daemon warmup task failed: " + message);
            });
            return true;
        }

        private static bool StartWarmupDaemonUnderLaunchLock(string daemonPath, string arguments, string priority)
        {
            var indexDirectory = GetIndexDirectory();
            Directory.CreateDirectory(indexDirectory);
            FileStream launchLock = null;
            try
            {
                launchLock = AcquireDaemonLaunchLock(indexDirectory);
                if (launchLock == null)
                {
                    AIBridgeLogger.LogWarning("[CodeIndex] Timed out waiting for the daemon launch lock; existing daemon was left untouched.");
                    return false;
                }

                // 持有跨进程锁后必须重新读取状态并带 token 探测，防止连续预热重复启动。
                var status = ReadStatus();
                if (TryProbeDaemonStatus(status))
                {
                    return true;
                }

                if (WaitForReachableExistingDaemon(status))
                {
                    return true;
                }

                if (IsTrackedStatusDaemonAlive(status))
                {
                    AIBridgeLogger.LogWarning("[CodeIndex] Existing daemon process is still running but its endpoint is unavailable; skipped restart to avoid a duplicate daemon.");
                    return false;
                }

                // 前一个 Editor 可能已写入进程 marker，但 daemon 尚未来得及发布 status.json。
                // 必须以 marker 中的启动时间校验进程，不能因 status 暂缺而误判为可重启。
                if (HasLiveCurrentProjectDaemonProcessMarker())
                {
                    if (WaitForReachableDaemonStatus(status, requireTrackedStatusDaemon: false))
                    {
                        return true;
                    }

                    AIBridgeLogger.LogWarning("[CodeIndex] A daemon process marker is live but its status endpoint is unavailable; skipped restart to avoid a duplicate daemon.");
                    return false;
                }

                // 只有状态记录和当前项目的进程 marker 均未指向存活 daemon 时，才清理旧状态并启动新实例。
                CleanupStaleDaemonState(status);
                return StartDaemon(daemonPath, arguments, priority);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("[CodeIndex] Failed to coordinate daemon warmup: " + ex.Message);
                return false;
            }
            finally
            {
                if (launchLock != null)
                {
                    launchLock.Dispose();
                }
            }
        }

        private static FileStream AcquireDaemonLaunchLock(string indexDirectory)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(DaemonLaunchLockWaitMs);
            var lockPath = Path.Combine(indexDirectory, DaemonLaunchLockFileName);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(ExistingDaemonRetryDelayMs);
                }
                catch (UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(ExistingDaemonRetryDelayMs);
                }
            }

            return null;
        }

        private static bool WaitForReachableExistingDaemon(CodeIndexStatusSnapshot status)
        {
            return WaitForReachableDaemonStatus(status, requireTrackedStatusDaemon: true);
        }

        private static bool WaitForReachableDaemonStatus(CodeIndexStatusSnapshot status, bool requireTrackedStatusDaemon)
        {
            if (requireTrackedStatusDaemon && !IsTrackedStatusDaemonAlive(status))
            {
                return false;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(ExistingDaemonReachabilityWaitMs);
            var latestStatus = status;
            while (DateTime.UtcNow < deadline)
            {
                if (TryProbeDaemonStatus(latestStatus))
                {
                    return true;
                }

                System.Threading.Thread.Sleep(ExistingDaemonRetryDelayMs);
                latestStatus = ReadStatus() ?? latestStatus;
                if (requireTrackedStatusDaemon && !IsTrackedStatusDaemonAlive(latestStatus))
                {
                    return false;
                }
            }

            return false;
        }

        private static bool TryProbeDaemonStatus(CodeIndexStatusSnapshot status)
        {
            if (status == null || string.IsNullOrWhiteSpace(status.Endpoint) || string.IsNullOrWhiteSpace(status.Token))
            {
                return false;
            }

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(status.Endpoint.TrimEnd('/') + "/status");
                request.Method = "GET";
                request.Timeout = DaemonStatusProbeTimeoutMs;
                request.ReadWriteTimeout = DaemonStatusProbeTimeoutMs;
                request.Headers["X-AIBridge-CodeIndex-Token"] = status.Token;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTrackedStatusDaemonAlive(CodeIndexStatusSnapshot status)
        {
            if (status == null || status.DaemonPid <= 0)
            {
                return false;
            }

            var markerPath = GetDaemonProcessMarkerPath(status.DaemonPid);
            if (!File.Exists(markerPath))
            {
                markerPath = GetDaemonProcessPath();
            }

            if (!TryGetCodeIndexProcess(status.DaemonPid, markerPath, out var process))
            {
                return false;
            }

            process.Dispose();
            return true;
        }

        private static bool HasLiveCurrentProjectDaemonProcessMarker()
        {
            foreach (var markerPath in EnumerateDaemonProcessMarkerPaths())
            {
                try
                {
                    var json = File.Exists(markerPath) ? File.ReadAllText(markerPath, Encoding.UTF8) : null;
                    if (!MarkerMatchesCurrentProject(json))
                    {
                        continue;
                    }

                    var daemonPid = ReadInt(json, "daemonPid");
                    if (daemonPid <= 0)
                    {
                        continue;
                    }

                    if (TryGetCodeIndexProcess(daemonPid, markerPath, out var process))
                    {
                        // TryGetCodeIndexProcess 会通过 marker 的 daemonPid 和 startedAtUtcTicks 校验，避免 PID 复用误判。
                        process.Dispose();
                        return true;
                    }
                }
                catch
                {
                    // marker 可能正被另一 Editor 写入；本次无法确认时继续检查其他 marker。
                }
            }

            return false;
        }

        private static void CleanupStaleDaemonState(CodeIndexStatusSnapshot status)
        {
            if (status != null && status.DaemonPid > 0)
            {
                DeleteFileIfExists(GetDaemonProcessMarkerPath(status.DaemonPid));
            }

            DeleteFileIfExists(GetStatusPath());
            DeleteFileIfExists(GetDaemonProcessPath());
            DeleteFileIfExists(Path.Combine(GetIndexDirectory(), LockFileName));
        }

        private static string GetDaemonProcessMarkerPath(int daemonPid)
        {
            return Path.Combine(GetIndexDirectory(), DaemonProcessDirectoryName, daemonPid + ".json");
        }

        private static string ResolveDaemonPath()
        {
            var cliPath = ResolveCliPath();
            if (string.IsNullOrWhiteSpace(cliPath))
            {
                return null;
            }

            var cliDirectory = Path.GetDirectoryName(Path.GetFullPath(cliPath));
            if (string.IsNullOrWhiteSpace(cliDirectory))
            {
                return null;
            }

            var daemonPath = Path.Combine(cliDirectory, "CodeIndex", "AIBridgeCodeIndex" + Path.GetExtension(cliPath));
            return File.Exists(daemonPath) ? daemonPath : null;
        }

        public static void ShutdownDaemon(string cleanupMode, int timeoutMs)
        {
            var status = ReadStatus();
            if (!IsStatusOwnedByCurrentEditor(status))
            {
                // status 是多个 Editor 共享的；身份无法同时由 PID 与启动时间确认时，绝不能触碰其 endpoint、进程或共享文件。
                LogShutdownSkippedForForeignOrUnknownOwner(status);
                return;
            }

            if (!string.IsNullOrEmpty(status.Endpoint))
            {
                TryPostShutdown(status.Endpoint, status.Token, timeoutMs);
            }

            if (status.DaemonPid > 0)
            {
                WaitOrKillDaemon(status.DaemonPid, GetDaemonProcessMarkerPath(status.DaemonPid), timeoutMs);
            }

            CleanupIndexDirectory(cleanupMode);
        }

        public static void CleanupOrphanDaemonsFromSettingsPanel()
        {
            if (EditorApplication.timeSinceStartup - _lastSettingsPanelCleanupTime < SettingsPanelCleanupIntervalSeconds)
            {
                return;
            }

            _lastSettingsPanelCleanupTime = EditorApplication.timeSinceStartup;
            CleanupOrphanDaemons(logWhenChanged: true);
        }

        public static void WriteCodeIndexConfig()
        {
            try
            {
                var settings = AIBridgeProjectSettings.Instance.CodeIndex;
                var directory = GetIndexDirectory();
                Directory.CreateDirectory(directory);
                var json = "{\n"
                           + "  \"enableCodeIndex\": " + ToJsonBool(settings.EnableCodeIndex) + ",\n"
                           + "  \"prewarmOnUnityStartup\": " + ToJsonBool(settings.PrewarmOnUnityStartup) + ",\n"
                           + "  \"warmupDelaySeconds\": " + Mathf.Max(0, settings.WarmupDelaySeconds) + ",\n"
                           + "  \"autoRefreshOnFileChange\": " + ToJsonBool(settings.AutoRefreshOnFileChange) + ",\n"
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
            CleanupOrphanDaemons(logWhenChanged: true);
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

        private static bool StartDaemon(string daemonPath, string arguments, string priority)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = daemonPath,
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

                ApplyDaemonPriority(process, priority);
                WriteDaemonProcessMarker(process, daemonPath);
                process.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("[CodeIndex] Failed to start daemon: " + ex.Message);
                return false;
            }
        }

        private static void ApplyDaemonPriority(Process process, string priority)
        {
            if (process == null || !string.Equals(priority, "low", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // 进程优先级只是启动期优化；平台拒绝调整时仍继续完成预热。
            }
        }

        private static void WriteDaemonProcessMarker(Process process, string daemonPath)
        {
            try
            {
                var startedAtUtcTicks = 0L;
                try
                {
                    startedAtUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                }
                catch
                {
                }

                using (var ownerProcess = Process.GetCurrentProcess())
                {
                    var marker = "{\n"
                                 + "  \"markerVersion\": 2,\n"
                                 + "  \"projectRoot\": \"" + EscapeJson(GetProjectRoot()) + "\",\n"
                                 + "  \"daemonPid\": " + process.Id + ",\n"
                                 + "  \"startedAtUtcTicks\": " + startedAtUtcTicks + ",\n"
                                 + "  \"ownerPid\": " + ownerProcess.Id + ",\n"
                                 + "  \"ownerStartTicks\": " + GetProcessStartTicks(ownerProcess) + ",\n"
                                 + "  \"daemonPath\": \"" + EscapeJson(daemonPath) + "\"\n"
                                 + "}\n";
                    var indexDirectory = GetIndexDirectory();
                    Directory.CreateDirectory(indexDirectory);
                    File.WriteAllText(GetDaemonProcessPath(), marker, Encoding.UTF8);
                    var processDirectory = Path.Combine(indexDirectory, DaemonProcessDirectoryName);
                    Directory.CreateDirectory(processDirectory);
                    File.WriteAllText(Path.Combine(processDirectory, process.Id + ".json"), marker, Encoding.UTF8);
                }
            }
            catch
            {
                // marker 仅用于退出和孤儿清理；写入失败不能阻止已启动 daemon 工作。
            }
        }

        private static string QuoteProcessArgument(string value)
        {
            // ProcessStartInfo.Arguments 需要保留 Windows 路径分隔符；仅转义参数中的引号。
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
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

        private static void WaitOrKillDaemon(int daemonPid, string markerPath, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, timeoutMs));
            while (DateTime.UtcNow < deadline)
            {
                if (!TryGetCodeIndexProcess(daemonPid, markerPath, out var process))
                {
                    return;
                }

                process.Dispose();
                System.Threading.Thread.Sleep(100);
            }

            if (!TryGetCodeIndexProcess(daemonPid, markerPath, out var remaining))
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

        private static bool TryGetCodeIndexProcess(int processId, string markerPath, out Process process)
        {
            process = null;
            try
            {
                var candidate = Process.GetProcessById(processId);
                if (candidate.HasExited || !IsCodeIndexProcess(candidate, markerPath))
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

        private static bool IsCodeIndexProcess(Process candidate, string markerPath)
        {
            if (candidate == null)
            {
                return false;
            }

            if (candidate.ProcessName.IndexOf(DaemonAssemblyName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return MatchesDaemonProcessMarker(candidate, markerPath);
        }

        private static bool MatchesDaemonProcessMarker(Process candidate, string markerPath)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(markerPath) || !File.Exists(markerPath))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(markerPath, Encoding.UTF8);
                var pid = ReadInt(json, "daemonPid");
                var startedAtUtcTicks = ReadLong(json, "startedAtUtcTicks");
                if (pid != candidate.Id || startedAtUtcTicks <= 0)
                {
                    return false;
                }

                var processStartTicks = candidate.StartTime.ToUniversalTime().Ticks;
                return Math.Abs(processStartTicks - startedAtUtcTicks) <= TimeSpan.FromSeconds(2).Ticks;
            }
            catch
            {
                return false;
            }
        }

        private static void CleanupOrphanDaemons(bool logWhenChanged)
        {
            var cleaned = 0;
            foreach (var markerPath in EnumerateDaemonProcessMarkerPaths())
            {
                try
                {
                    var json = File.Exists(markerPath) ? File.ReadAllText(markerPath, Encoding.UTF8) : null;
                    if (!MarkerMatchesCurrentProject(json))
                    {
                        continue;
                    }

                    var daemonPid = ReadInt(json, "daemonPid");
                    if (daemonPid <= 0)
                    {
                        DeleteFileIfExists(markerPath);
                        cleaned++;
                        continue;
                    }

                    if (!TryGetCodeIndexProcess(daemonPid, markerPath, out var process))
                    {
                        DeleteFileIfExists(markerPath);
                        cleaned++;
                        continue;
                    }

                    using (process)
                    {
                        var startedAtUtcTicks = ReadLong(json, "startedAtUtcTicks");
                        var processStartTicks = GetProcessStartTicks(process);
                        if (startedAtUtcTicks > 0L
                            && processStartTicks > 0L
                            && Math.Abs(processStartTicks - startedAtUtcTicks) > TimeSpan.FromSeconds(2).Ticks)
                        {
                            DeleteFileIfExists(markerPath);
                            cleaned++;
                            continue;
                        }

                        var ownerPid = ReadInt(json, "ownerPid");
                        var ownerStartTicks = ReadLong(json, "ownerStartTicks");
                        if (ownerPid > 0 && !IsOwnerProcessAlive(ownerPid, ownerStartTicks))
                        {
                            try
                            {
                                process.Kill();
                                process.WaitForExit(1000);
                            }
                            catch
                            {
                            }

                            DeleteFileIfExists(markerPath);
                            cleaned++;
                        }
                    }
                }
                catch
                {
                }
            }

            if (cleaned > 0 && logWhenChanged)
            {
                UnityEngine.Debug.Log("[AIBridge] Code Index cleaned stale daemon markers/processes: " + cleaned);
            }
        }

        private static string[] EnumerateDaemonProcessMarkerPaths()
        {
            var result = new System.Collections.Generic.List<string>();
            var markerPath = GetDaemonProcessPath();
            if (File.Exists(markerPath))
            {
                result.Add(markerPath);
            }

            var markerDirectory = Path.Combine(GetIndexDirectory(), DaemonProcessDirectoryName);
            if (Directory.Exists(markerDirectory))
            {
                result.AddRange(Directory.GetFiles(markerDirectory, "*.json"));
            }

            return result.ToArray();
        }

        private static bool MarkerMatchesCurrentProject(string json)
        {
            return PathsEqual(ReadString(json, "projectRoot"), GetProjectRoot());
        }

        private static bool IsOwnerProcessAlive(int ownerPid, long ownerStartTicks)
        {
            try
            {
                using (var process = Process.GetProcessById(ownerPid))
                {
                    if (process.HasExited)
                    {
                        return false;
                    }

                    if (ownerStartTicks <= 0L)
                    {
                        return true;
                    }

                    var currentStartTicks = GetProcessStartTicks(process);
                    return currentStartTicks > 0L && Math.Abs(currentStartTicks - ownerStartTicks) <= TimeSpan.FromSeconds(2).Ticks;
                }
            }
            catch
            {
                return false;
            }
        }

        private static long GetProcessStartTicks(Process process)
        {
            try
            {
                return process == null ? 0L : process.StartTime.ToUniversalTime().Ticks;
            }
            catch
            {
                return 0L;
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                left = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                right = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
            }

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
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
            DeleteFileIfExists(Path.Combine(directory, DaemonProcessFileName));
            DeleteDirectoryIfExists(Path.Combine(directory, TempDirectoryName));

            if (normalized == "processAndTemp")
            {
                DeleteDirectoryIfExists(Path.Combine(directory, LogsDirectoryName));
            }
        }

        private static bool IsStatusOwnedByCurrentEditor(CodeIndexStatusSnapshot status)
        {
            if (status == null || status.OwnerPid <= 0 || status.OwnerStartTicks <= 0L)
            {
                return false;
            }

            try
            {
                using (var currentProcess = Process.GetCurrentProcess())
                {
                    var currentStartTicks = GetProcessStartTicks(currentProcess);
                    return currentStartTicks > 0L
                           && status.OwnerPid == currentProcess.Id
                           && Math.Abs(currentStartTicks - status.OwnerStartTicks) <= OwnerStartTicksTolerance;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void LogShutdownSkippedForForeignOrUnknownOwner(CodeIndexStatusSnapshot status)
        {
            var ownerState = status == null || status.OwnerPid <= 0 || status.OwnerStartTicks <= 0L
                ? "unknown"
                : "different";
            AIBridgeLogger.LogDebug("[CodeIndex] Skipped daemon shutdown and shared-state cleanup because status owner is " + ownerState + ".");
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
                    DaemonPid = ReadInt(json, "daemonPid"),
                    OwnerPid = ReadInt(json, "ownerPid"),
                    OwnerStartTicks = ReadLong(json, "ownerStartTicks")
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

        private static long ReadLong(string json, string key)
        {
            var match = Regex.Match(json ?? string.Empty, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<value>\\d+)");
            if (!match.Success)
            {
                return 0L;
            }

            long.TryParse(match.Groups["value"].Value, out var value);
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
            public int OwnerPid { get; set; }
            public long OwnerStartTicks { get; set; }
        }
    }
}
