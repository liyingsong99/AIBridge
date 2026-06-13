using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AIBridge.Internal.Json;

namespace AIBridge.Runtime.Internal
{
    public sealed class AIBridgeCacheCleanupSettings
    {
        public bool EnableAutoCleanup { get; set; } = true;
        public int RetentionDays { get; set; } = AIBridgeCacheCleanup.DefaultRetentionDays;
    }

    public sealed class AIBridgeCacheCleanupState
    {
        public string LastRunUtc { get; set; }
        public string LastStartedAtUtc { get; set; }
        public int LastDeletedFiles { get; set; }
        public int LastDeletedDirectories { get; set; }
        public long LastFreedBytes { get; set; }
        public int LastErrorCount { get; set; }
        public string LastReason { get; set; }
    }

    public sealed class AIBridgeCacheCleanupResult
    {
        public bool Skipped { get; set; }
        public string Reason { get; set; }
        public string StartedAtUtc { get; set; }
        public string FinishedAtUtc { get; set; }
        public int DeletedFiles { get; set; }
        public int DeletedDirectories { get; set; }
        public long FreedBytes { get; set; }
        public int ErrorCount { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public static class AIBridgeCacheCleanup
    {
        public const int MinRetentionDays = 1;
        public const int MaxRetentionDays = 30;
        public const int DefaultRetentionDays = 30;
        public const string SettingsFileName = "cache-cleanup-settings.json";
        public const string StateFileName = "cache-cleanup-state.json";
        public const string LastUsedMarkerFileName = ".last-used";

        private static readonly TimeSpan AutoCleanupInterval = TimeSpan.FromHours(24);
        private static readonly TimeSpan RuntimeOnlineHeartbeatAge = TimeSpan.FromSeconds(60);
        private static readonly string[] ScreenshotExtensions = { ".png", ".jpg", ".jpeg", ".gif" };
        private static readonly string[] CodeIndexCleanupDirectories = { "snapshot", "logs", "cache", "temp", "index", "daemon", "daemon-processes" };
        private static readonly string[] CodeIndexCleanupFiles = { "status.json", "lock.json", "daemon-process.json", "daemon-launch.lock" };

        public static AIBridgeCacheCleanupSettings NormalizeSettings(AIBridgeCacheCleanupSettings settings)
        {
            settings = settings ?? new AIBridgeCacheCleanupSettings();
            settings.RetentionDays = ClampRetentionDays(settings.RetentionDays);
            return settings;
        }

        public static int ClampRetentionDays(int value)
        {
            if (value < MinRetentionDays)
            {
                return MinRetentionDays;
            }

            return value > MaxRetentionDays ? MaxRetentionDays : value;
        }

        public static string GetSettingsPath(string bridgeDirectory)
        {
            return Path.Combine(NormalizeBridgeDirectory(bridgeDirectory), SettingsFileName);
        }

        public static string GetStatePath(string bridgeDirectory)
        {
            return Path.Combine(NormalizeBridgeDirectory(bridgeDirectory), StateFileName);
        }

        public static AIBridgeCacheCleanupSettings LoadSettings(string bridgeDirectory)
        {
            var settings = new AIBridgeCacheCleanupSettings();
            var path = GetSettingsPath(bridgeDirectory);
            var data = ReadJsonObject(path);
            if (data == null)
            {
                return settings;
            }

            settings.EnableAutoCleanup = GetBool(data, nameof(AIBridgeCacheCleanupSettings.EnableAutoCleanup), settings.EnableAutoCleanup);
            settings.RetentionDays = GetInt(data, nameof(AIBridgeCacheCleanupSettings.RetentionDays), settings.RetentionDays);
            return NormalizeSettings(settings);
        }

        public static void SaveSettings(string bridgeDirectory, AIBridgeCacheCleanupSettings settings)
        {
            settings = NormalizeSettings(settings);
            var path = GetSettingsPath(bridgeDirectory);
            EnsureParentDirectory(path);
            WriteJson(path, settings);
        }

        public static AIBridgeCacheCleanupState LoadState(string bridgeDirectory)
        {
            var path = GetStatePath(bridgeDirectory);
            var data = ReadJsonObject(path);
            if (data == null)
            {
                return new AIBridgeCacheCleanupState();
            }

            return new AIBridgeCacheCleanupState
            {
                LastRunUtc = GetString(data, nameof(AIBridgeCacheCleanupState.LastRunUtc), null),
                LastStartedAtUtc = GetString(data, nameof(AIBridgeCacheCleanupState.LastStartedAtUtc), null),
                LastDeletedFiles = GetInt(data, nameof(AIBridgeCacheCleanupState.LastDeletedFiles), 0),
                LastDeletedDirectories = GetInt(data, nameof(AIBridgeCacheCleanupState.LastDeletedDirectories), 0),
                LastFreedBytes = GetLong(data, nameof(AIBridgeCacheCleanupState.LastFreedBytes), 0L),
                LastErrorCount = GetInt(data, nameof(AIBridgeCacheCleanupState.LastErrorCount), 0),
                LastReason = GetString(data, nameof(AIBridgeCacheCleanupState.LastReason), null)
            };
        }

        public static AIBridgeCacheCleanupResult CleanupIfDue(string bridgeDirectory, AIBridgeCacheCleanupSettings settings)
        {
            return CleanupIfDue(bridgeDirectory, settings, DateTime.UtcNow);
        }

        public static AIBridgeCacheCleanupResult CleanupIfDue(string bridgeDirectory, AIBridgeCacheCleanupSettings settings, DateTime nowUtc)
        {
            settings = NormalizeSettings(settings);
            if (!settings.EnableAutoCleanup)
            {
                return BuildSkipped("disabled", nowUtc);
            }

            var state = LoadState(bridgeDirectory);
            DateTime lastRunUtc;
            if (TryParseUtc(state.LastRunUtc, out lastRunUtc) && nowUtc - lastRunUtc < AutoCleanupInterval)
            {
                return BuildSkipped("not_due", nowUtc);
            }

            return CleanupExpired(bridgeDirectory, settings, nowUtc);
        }

        public static AIBridgeCacheCleanupResult CleanupExpired(string bridgeDirectory, AIBridgeCacheCleanupSettings settings)
        {
            return CleanupExpired(bridgeDirectory, settings, DateTime.UtcNow);
        }

        public static AIBridgeCacheCleanupResult CleanupExpired(string bridgeDirectory, AIBridgeCacheCleanupSettings settings, DateTime nowUtc)
        {
            settings = NormalizeSettings(settings);
            var context = new CleanupContext(bridgeDirectory, settings.RetentionDays, nowUtc);

            // 所有删除都集中经过 CleanupContext，确保路径必须位于 .aibridge 内。
            CleanScreenshotDirectory(context, Path.Combine(context.BridgeDirectory, "screenshots"), false);
            CleanRuntimeTargets(context);
            CleanRuntimeHttpCache(context);
            CleanWorkflowRuns(context);
            CleanCodeIndex(context);
            CleanSkillLibraryCache(context);
            CleanScripts(context);
            CleanProfiler(context);
            CleanCompiledCode(context);
            CleanCliTempFiles(context);

            context.Result.FinishedAtUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture);
            SaveState(context);
            return context.Result;
        }

        public static AIBridgeCacheCleanupResult ClearScreenshotCache(string bridgeDirectory)
        {
            var nowUtc = DateTime.UtcNow;
            var context = new CleanupContext(bridgeDirectory, DefaultRetentionDays, nowUtc);
            CleanScreenshotDirectory(context, Path.Combine(context.BridgeDirectory, "screenshots"), true);
            context.Result.FinishedAtUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture);
            return context.Result;
        }

        public static void TouchLastUsed(string directory)
        {
            TouchLastUsed(directory, DateTime.UtcNow);
        }

        public static void TouchLastUsed(string directory, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var markerPath = Path.Combine(directory, LastUsedMarkerFileName);
            File.WriteAllText(markerPath, nowUtc.ToString("o", CultureInfo.InvariantCulture), new UTF8Encoding(false));
        }

        private static void CleanRuntimeTargets(CleanupContext context)
        {
            var targetsDirectory = Path.Combine(context.BridgeDirectory, "runtime", "targets");
            if (!Directory.Exists(targetsDirectory))
            {
                return;
            }

            foreach (var targetDirectory in SafeEnumerateDirectories(context, targetsDirectory))
            {
                CleanScreenshotDirectory(context, Path.Combine(targetDirectory, "screenshots"), false);

                if (IsRuntimeTargetOnline(targetDirectory, context.NowUtc))
                {
                    continue;
                }

                var lastUsedUtc = GetRuntimeTargetLastUsedUtc(targetDirectory);
                if (IsExpired(lastUsedUtc, context.CutoffUtc))
                {
                    context.DeleteDirectory(targetDirectory);
                }
            }
        }

        private static void CleanRuntimeHttpCache(CleanupContext context)
        {
            CleanExpiredChildren(context, Path.Combine(context.BridgeDirectory, "runtime-cache", "http"));
        }

        private static void CleanWorkflowRuns(CleanupContext context)
        {
            var runsDirectory = Path.Combine(context.BridgeDirectory, "workflows", "runs");
            if (!Directory.Exists(runsDirectory))
            {
                return;
            }

            var activeRunIds = ReadActiveWorkflowRunIds(context.BridgeDirectory);
            foreach (var runDirectory in SafeEnumerateDirectories(context, runsDirectory))
            {
                var runId = Path.GetFileName(runDirectory);
                if (activeRunIds.Contains(runId))
                {
                    continue;
                }

                var lastUsedUtc = GetLastUsedUtc(runDirectory, false);
                if (IsExpired(lastUsedUtc, context.CutoffUtc))
                {
                    context.DeleteDirectory(runDirectory);
                }
            }
        }

        private static void CleanCodeIndex(CleanupContext context)
        {
            var indexDirectory = Path.Combine(context.BridgeDirectory, "code-index");
            if (!Directory.Exists(indexDirectory))
            {
                return;
            }

            if (IsCodeIndexDaemonActive(indexDirectory))
            {
                return;
            }

            var lastUsedUtc = GetLastUsedUtc(indexDirectory, false);
            if (!IsExpired(lastUsedUtc, context.CutoffUtc))
            {
                return;
            }

            for (var i = 0; i < CodeIndexCleanupDirectories.Length; i++)
            {
                context.DeleteDirectory(Path.Combine(indexDirectory, CodeIndexCleanupDirectories[i]));
            }

            for (var i = 0; i < CodeIndexCleanupFiles.Length; i++)
            {
                context.DeleteFile(Path.Combine(indexDirectory, CodeIndexCleanupFiles[i]));
            }
        }

        private static void CleanSkillLibraryCache(CleanupContext context)
        {
            CleanExpiredChildren(context, Path.Combine(context.BridgeDirectory, "skill-library", "cache"));
        }

        private static void CleanScripts(CleanupContext context)
        {
            var scriptsDirectory = Path.Combine(context.BridgeDirectory, "scripts");
            if (!Directory.Exists(scriptsDirectory))
            {
                return;
            }

            var protectedScript = ReadCurrentScriptPath(context.BridgeDirectory);
            foreach (var file in SafeEnumerateFiles(context, scriptsDirectory))
            {
                if (IsProtectedFileName(file) || IsSamePath(file, protectedScript))
                {
                    continue;
                }

                if (IsExpired(GetLastUsedUtc(file, false), context.CutoffUtc))
                {
                    context.DeleteFile(file);
                }
            }

            foreach (var directory in SafeEnumerateDirectories(context, scriptsDirectory))
            {
                if (IsPathInDirectory(protectedScript, directory))
                {
                    continue;
                }

                if (IsExpired(GetLastUsedUtc(directory, true), context.CutoffUtc))
                {
                    context.DeleteDirectory(directory);
                }
            }
        }

        private static void CleanProfiler(CleanupContext context)
        {
            CleanExpiredChildren(context, Path.Combine(context.BridgeDirectory, "profiler"));
        }

        private static void CleanCompiledCode(CleanupContext context)
        {
            CleanExpiredChildren(context, Path.Combine(context.BridgeDirectory, "code", ".compiled"));
        }

        private static void CleanCliTempFiles(CleanupContext context)
        {
            var cliDirectory = Path.Combine(context.BridgeDirectory, "cli");
            if (!Directory.Exists(cliDirectory))
            {
                return;
            }

            foreach (var file in SafeEnumerateFiles(context, cliDirectory))
            {
                var name = Path.GetFileName(file);
                if (!IsCodeIndexCliTempName(name))
                {
                    continue;
                }

                if (IsExpired(GetLastUsedUtc(file, false), context.CutoffUtc))
                {
                    context.DeleteFile(file);
                }
            }

            foreach (var directory in SafeEnumerateDirectories(context, cliDirectory))
            {
                var name = Path.GetFileName(directory);
                if (!IsCodeIndexCliTempName(name))
                {
                    continue;
                }

                if (IsExpired(GetLastUsedUtc(directory, true), context.CutoffUtc))
                {
                    context.DeleteDirectory(directory);
                }
            }
        }

        private static void CleanExpiredChildren(CleanupContext context, string directory)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in SafeEnumerateFiles(context, directory))
            {
                if (IsProtectedFileName(file))
                {
                    continue;
                }

                if (IsExpired(GetLastUsedUtc(file, false), context.CutoffUtc))
                {
                    context.DeleteFile(file);
                }
            }

            foreach (var childDirectory in SafeEnumerateDirectories(context, directory))
            {
                if (IsExpired(GetLastUsedUtc(childDirectory, true), context.CutoffUtc))
                {
                    context.DeleteDirectory(childDirectory);
                }
            }
        }

        private static void CleanScreenshotDirectory(CleanupContext context, string directory, bool deleteAll)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in SafeEnumerateFiles(context, directory))
            {
                if (!IsScreenshotFile(file))
                {
                    continue;
                }

                if (deleteAll || IsExpired(GetLastUsedUtc(file, false), context.CutoffUtc))
                {
                    context.DeleteFile(file);
                }
            }
        }

        private static bool IsRuntimeTargetOnline(string targetDirectory, DateTime nowUtc)
        {
            var heartbeat = ReadJsonObject(Path.Combine(targetDirectory, "heartbeat.json"));
            if (heartbeat == null)
            {
                return false;
            }

            DateTime lastHeartbeatUtc;
            var value = GetString(heartbeat, "lastHeartbeatUtc", null);
            return TryParseUtc(value, out lastHeartbeatUtc)
                && nowUtc - lastHeartbeatUtc <= RuntimeOnlineHeartbeatAge;
        }

        private static DateTime GetRuntimeTargetLastUsedUtc(string targetDirectory)
        {
            var heartbeatPath = Path.Combine(targetDirectory, "heartbeat.json");
            var heartbeat = ReadJsonObject(heartbeatPath);
            DateTime lastHeartbeatUtc;
            if (heartbeat != null && TryParseUtc(GetString(heartbeat, "lastHeartbeatUtc", null), out lastHeartbeatUtc))
            {
                return lastHeartbeatUtc;
            }

            return GetLastUsedUtc(targetDirectory, true);
        }

        private static HashSet<string> ReadActiveWorkflowRunIds(string bridgeDirectory)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var data = ReadJsonObject(Path.Combine(bridgeDirectory, "workflows", "active-run.json"));
            var runId = data == null ? null : GetString(data, "runId", null);
            if (!string.IsNullOrWhiteSpace(runId))
            {
                result.Add(runId);
            }

            return result;
        }

        private static string ReadCurrentScriptPath(string bridgeDirectory)
        {
            var data = ReadJsonObject(Path.Combine(bridgeDirectory, "script-state.json"));
            var scriptPath = data == null ? null : GetString(data, "ScriptPath", null);
            return string.IsNullOrWhiteSpace(scriptPath) ? null : Path.GetFullPath(scriptPath);
        }

        private static bool IsCodeIndexDaemonActive(string indexDirectory)
        {
            var markerPaths = new List<string>();
            var daemonProcessPath = Path.Combine(indexDirectory, "daemon-process.json");
            if (File.Exists(daemonProcessPath))
            {
                markerPaths.Add(daemonProcessPath);
            }

            var processDirectory = Path.Combine(indexDirectory, "daemon-processes");
            if (Directory.Exists(processDirectory))
            {
                foreach (var markerPath in Directory.GetFiles(processDirectory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    markerPaths.Add(markerPath);
                }
            }

            var status = ReadJsonObject(Path.Combine(indexDirectory, "status.json"));
            var statusPid = status == null ? 0 : GetInt(status, "daemonPid", 0);
            if (statusPid > 0 && IsProcessAlive(statusPid, markerPaths.Count > 0 ? markerPaths[0] : null))
            {
                return true;
            }

            for (var i = 0; i < markerPaths.Count; i++)
            {
                var marker = ReadJsonObject(markerPaths[i]);
                var pid = marker == null ? 0 : GetInt(marker, "daemonPid", 0);
                if (pid > 0 && IsProcessAlive(pid, markerPaths[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsProcessAlive(int processId, string markerPath)
        {
            Process process = null;
            try
            {
                process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(markerPath) || !File.Exists(markerPath))
                {
                    return true;
                }

                var marker = ReadJsonObject(markerPath);
                var markerPid = marker == null ? 0 : GetInt(marker, "daemonPid", 0);
                var startedAtUtcTicks = marker == null ? 0L : GetLong(marker, "startedAtUtcTicks", 0L);
                if (markerPid != processId || startedAtUtcTicks <= 0L)
                {
                    return true;
                }

                var startTicks = process.StartTime.ToUniversalTime().Ticks;
                return Math.Abs(startTicks - startedAtUtcTicks) <= TimeSpan.FromSeconds(2).Ticks;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (process != null)
                {
                    process.Dispose();
                }
            }
        }

        private static DateTime GetLastUsedUtc(string path, bool recursiveDirectoryFallback)
        {
            var markerPath = Directory.Exists(path) ? Path.Combine(path, LastUsedMarkerFileName) : null;
            if (!string.IsNullOrEmpty(markerPath) && File.Exists(markerPath))
            {
                try
                {
                    DateTime markerUtc;
                    if (TryParseUtc(File.ReadAllText(markerPath), out markerUtc))
                    {
                        return markerUtc;
                    }
                }
                catch
                {
                }
            }

            if (File.Exists(path))
            {
                return File.GetLastWriteTimeUtc(path);
            }

            if (!Directory.Exists(path))
            {
                return DateTime.MinValue;
            }

            var lastWriteUtc = Directory.GetLastWriteTimeUtc(path);
            if (!recursiveDirectoryFallback)
            {
                return lastWriteUtc;
            }

            try
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    var fileTime = File.GetLastWriteTimeUtc(file);
                    if (fileTime > lastWriteUtc)
                    {
                        lastWriteUtc = fileTime;
                    }
                }
            }
            catch
            {
                return lastWriteUtc;
            }

            return lastWriteUtc;
        }

        private static bool IsExpired(DateTime lastUsedUtc, DateTime cutoffUtc)
        {
            return lastUsedUtc <= cutoffUtc;
        }

        private static bool IsScreenshotFile(string path)
        {
            if (IsProtectedFileName(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            for (var i = 0; i < ScreenshotExtensions.Length; i++)
            {
                if (string.Equals(extension, ScreenshotExtensions[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsProtectedFileName(string path)
        {
            return string.Equals(Path.GetFileName(path), ".gitignore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(path), SettingsFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(path), StateFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(path), LastUsedMarkerFileName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCodeIndexCliTempName(string name)
        {
            return !string.IsNullOrEmpty(name)
                && (name.StartsWith("CodeIndex.tmp.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("CodeIndex.old.", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPathInDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> SafeEnumerateFiles(CleanupContext context, string directory)
        {
            try
            {
                return Directory.Exists(directory) ? Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly) : Array.Empty<string>();
            }
            catch (Exception ex)
            {
                context.AddError(directory, ex);
                return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> SafeEnumerateDirectories(CleanupContext context, string directory)
        {
            try
            {
                return Directory.Exists(directory) ? Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly) : Array.Empty<string>();
            }
            catch (Exception ex)
            {
                context.AddError(directory, ex);
                return Array.Empty<string>();
            }
        }

        private static void SaveState(CleanupContext context)
        {
            var state = new AIBridgeCacheCleanupState
            {
                LastStartedAtUtc = context.Result.StartedAtUtc,
                LastRunUtc = context.Result.FinishedAtUtc,
                LastDeletedFiles = context.Result.DeletedFiles,
                LastDeletedDirectories = context.Result.DeletedDirectories,
                LastFreedBytes = context.Result.FreedBytes,
                LastErrorCount = context.Result.ErrorCount,
                LastReason = context.Result.Reason
            };

            var path = Path.Combine(context.BridgeDirectory, StateFileName);
            EnsureParentDirectory(path);
            WriteJson(path, state);
        }

        private static AIBridgeCacheCleanupResult BuildSkipped(string reason, DateTime nowUtc)
        {
            var timestamp = nowUtc.ToString("o", CultureInfo.InvariantCulture);
            return new AIBridgeCacheCleanupResult
            {
                Skipped = true,
                Reason = reason,
                StartedAtUtc = timestamp,
                FinishedAtUtc = timestamp
            };
        }

        private static void WriteJson(string path, object value)
        {
            File.WriteAllText(path, AIBridgeJson.Serialize(value, true), new UTF8Encoding(false));
        }

        private static Dictionary<string, object> ReadJsonObject(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                return AIBridgeJson.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
            }
            catch
            {
                return null;
            }
        }

        private static string GetString(Dictionary<string, object> data, string key, string defaultValue)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) && value != null ? value.ToString() : defaultValue;
        }

        private static bool GetBool(Dictionary<string, object> data, string key, bool defaultValue)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static int GetInt(Dictionary<string, object> data, string key, int defaultValue)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static long GetLong(Dictionary<string, object> data, string key, long defaultValue)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static bool TryParseUtc(string value, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            DateTimeOffset offset;
            if (DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out offset))
            {
                utc = offset.UtcDateTime;
                return true;
            }

            DateTime parsed;
            if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
            {
                utc = parsed.ToUniversalTime();
                return true;
            }

            return false;
        }

        private static void EnsureParentDirectory(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static string NormalizeBridgeDirectory(string bridgeDirectory)
        {
            if (string.IsNullOrWhiteSpace(bridgeDirectory))
            {
                throw new ArgumentException("Bridge directory is required.", nameof(bridgeDirectory));
            }

            var fullPath = Path.GetFullPath(bridgeDirectory);
            var info = new DirectoryInfo(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.Equals(info.Name, ".aibridge", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Cache cleanup is restricted to a .aibridge directory.");
            }

            return info.FullName;
        }

        private sealed class CleanupContext
        {
            private const int MaxStoredErrors = 20;

            public CleanupContext(string bridgeDirectory, int retentionDays, DateTime nowUtc)
            {
                BridgeDirectory = NormalizeBridgeDirectory(bridgeDirectory);
                RetentionDays = ClampRetentionDays(retentionDays);
                NowUtc = nowUtc;
                CutoffUtc = nowUtc.AddDays(-RetentionDays);
                Result = new AIBridgeCacheCleanupResult
                {
                    Reason = "expired",
                    StartedAtUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture)
                };
            }

            public string BridgeDirectory { get; private set; }
            public int RetentionDays { get; private set; }
            public DateTime NowUtc { get; private set; }
            public DateTime CutoffUtc { get; private set; }
            public AIBridgeCacheCleanupResult Result { get; private set; }

            public void DeleteFile(string path)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        return;
                    }

                    if (!IsInsideBridge(path) || IsProtectedFileName(path))
                    {
                        AddError(path, new InvalidOperationException("Refused to delete protected or out-of-scope file."));
                        return;
                    }

                    var length = new FileInfo(path).Length;
                    File.Delete(path);
                    Result.DeletedFiles++;
                    Result.FreedBytes += length;
                }
                catch (Exception ex)
                {
                    AddError(path, ex);
                }
            }

            public void DeleteDirectory(string path)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                    {
                        return;
                    }

                    if (!IsInsideBridge(path))
                    {
                        AddError(path, new InvalidOperationException("Refused to delete out-of-scope directory."));
                        return;
                    }

                    var metrics = GetDirectoryMetrics(path);
                    Directory.Delete(path, true);
                    Result.DeletedFiles += metrics.FileCount;
                    Result.DeletedDirectories += metrics.DirectoryCount;
                    Result.FreedBytes += metrics.TotalBytes;
                }
                catch (Exception ex)
                {
                    AddError(path, ex);
                }
            }

            public void AddError(string path, Exception ex)
            {
                Result.ErrorCount++;
                if (Result.Errors.Count < MaxStoredErrors)
                {
                    Result.Errors.Add(path + ": " + ex.Message);
                }
            }

            private bool IsInsideBridge(string path)
            {
                var fullPath = Path.GetFullPath(path);
                var root = BridgeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var rootWithSeparator = root + Path.DirectorySeparatorChar;
                return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                    || fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
            }
        }

        private struct DirectoryMetrics
        {
            public int FileCount;
            public int DirectoryCount;
            public long TotalBytes;
        }

        private static DirectoryMetrics GetDirectoryMetrics(string directory)
        {
            var metrics = new DirectoryMetrics
            {
                DirectoryCount = 1
            };

            try
            {
                foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        metrics.FileCount++;
                        metrics.TotalBytes += new FileInfo(file).Length;
                    }
                    catch
                    {
                    }
                }

                metrics.DirectoryCount += Directory.GetDirectories(directory, "*", SearchOption.AllDirectories).Length;
            }
            catch
            {
            }

            return metrics;
        }
    }
}
