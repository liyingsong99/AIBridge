using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AIBridge.Runtime.Internal;
using AIBridgeCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Commands
{
    public static class CodeIndexCommand
    {
        private const string IndexDirectoryName = "code-index";
        private const string SnapshotDirectoryName = "snapshot";
        private const string DaemonDirectoryName = "CodeIndex";
        private const string DaemonAssemblyName = "AIBridgeCodeIndex";
        private const string DaemonProcessFileName = "daemon-process.json";
        private const string DaemonProcessDirectoryName = "daemon-processes";
        private const string DaemonLaunchLockFileName = "daemon-launch.lock";
        private const int SnapshotSchemaVersion = 2;
        private const int ManifestFormatKind = 1;
        private const int DefaultStatusTimeoutMs = 1500;
        private const int DefaultDoctorTimeoutMs = 5000;
        private const int DefaultWarmupTimeoutMs = 30000;
        private const int DefaultSymbolTimeoutMs = 30000;
        private const int DefaultDefinitionTimeoutMs = 15000;
        private const int DefaultHeavyQueryTimeoutMs = 30000;
        private const int DefaultFullDiagnosticsTimeoutMs = 60000;
        private const int DefaultQueryQueueTimeoutMs = 60000;
        private const int QueryTransportPaddingMs = 1000;
        private const int ExistingDaemonReachabilityWaitMs = 5000;
        private const int ExistingDaemonRetryDelayMs = 150;
        private const int MaxCodeIndexTimeoutMs = 600000;
        private const int MaxBatchItems = 100;
        private const string SnapshotMagic = "AIBCI";
        private const string DisabledMessage = "Code Index is disabled in AIBridge settings. Enable AIBridge > Settings > Code Index > Enable Code Index, or use rg and normal file reads.";
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        public static int Execute(string action, Dictionary<string, string> options, int timeout, bool noWait, OutputMode outputMode)
        {
            var stopwatch = Stopwatch.StartNew();
            JObject result;
            try
            {
                var normalizedAction = NormalizeAction(action);
                var effectiveTimeout = ResolveActionTimeout(normalizedAction, options, timeout);
                result = ExecuteAsync(normalizedAction, options, effectiveTimeout, noWait).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                result = BuildFailure(null, "code_index failed: " + ex.Message, "cli_error");
            }

            result["executionTime"] = stopwatch.ElapsedMilliseconds;
            Print(result, outputMode);
            return result.Value<bool>("success") ? 0 : 1;
        }

        private static async Task<JObject> ExecuteAsync(string action, Dictionary<string, string> options, int timeout, bool noWait)
        {
            var normalizedAction = NormalizeAction(action);
            var context = CodeIndexContext.Resolve(options);
            TouchCodeIndexLastUsed(context);
            if (!context.Enabled)
            {
                return await ExecuteDisabledAsync(normalizedAction, context, timeout);
            }

            switch (normalizedAction)
            {
                case "status":
                    return await BuildStatusAsync(context, timeout);
                case "doctor":
                    return await BuildDoctorAsync(context, timeout);
                case "build_snapshot":
                    return await BuildSnapshotAsync(context, options, timeout, noWait);
                case "warmup":
                    return await WarmupAsync(context, timeout, noWait);
                case "reset":
                    return await ResetAsync(context, timeout);
                case "symbol":
                case "definition":
                case "references":
                case "implementations":
                case "derived":
                case "callers":
                case "diagnostics":
                    return await QueryAsync(context, normalizedAction, options, timeout);
                case "batch":
                    return await BatchAsync(context, options, timeout);
                default:
                    return BuildFailure(context, "Unsupported code_index action: " + action);
            }
        }

        private static void TouchCodeIndexLastUsed(CodeIndexContext context)
        {
            try
            {
                if (context != null && Directory.Exists(context.IndexDirectory))
                {
                    AIBridgeCacheCleanup.TouchLastUsed(context.IndexDirectory);
                }
            }
            catch
            {
            }
        }

        private static string NormalizeAction(string action)
        {
            return string.IsNullOrWhiteSpace(action) ? "status" : action.Trim().ToLowerInvariant();
        }

        private static int ResolveActionTimeout(string normalizedAction, Dictionary<string, string> options, int timeout)
        {
            if (options != null && options.ContainsKey("timeout"))
            {
                return ClampTimeout(timeout);
            }

            switch (NormalizeAction(normalizedAction))
            {
                case "status":
                    return DefaultStatusTimeoutMs;
                case "doctor":
                case "reset":
                    return DefaultDoctorTimeoutMs;
                case "warmup":
                    return DefaultWarmupTimeoutMs;
                case "symbol":
                    return DefaultSymbolTimeoutMs;
                case "definition":
                    return DefaultDefinitionTimeoutMs;
                case "references":
                case "implementations":
                case "derived":
                case "callers":
                    return DefaultHeavyQueryTimeoutMs;
                case "diagnostics":
                    return ResolveOptionBool(options, "all", false)
                        ? DefaultFullDiagnosticsTimeoutMs
                        : DefaultHeavyQueryTimeoutMs;
                case "batch":
                    return DefaultHeavyQueryTimeoutMs;
                default:
                    return ClampTimeout(timeout);
            }
        }

        private static async Task<JObject> ExecuteDisabledAsync(string normalizedAction, CodeIndexContext context, int timeout)
        {
            switch (normalizedAction)
            {
                case "status":
                    return BuildDisabledStatus(context);
                case "doctor":
                    return BuildDisabledDoctor(context);
                case "reset":
                    return await ResetAsync(context, timeout);
                default:
                    return BuildDisabledFailure(context);
            }
        }

        private static async Task<JObject> QueryAsync(CodeIndexContext context, string action, Dictionary<string, string> options, int timeout)
        {
            ValidateQueryArguments(action, options);

            var status = await EnsureDaemonAsync(context, timeout, false);
            if (IsDaemonTransportFailure(status))
            {
                status["enabled"] = context.Enabled;
                return status;
            }

            if (IsReady(context, status))
            {
                var queryTimeouts = ResolveQueryTimeouts(action, options, timeout);
                JObject response;
                try
                {
                    response = await PostJsonAsync(
                        status.Value<string>("endpoint"),
                        "query",
                        status.Value<string>("token"),
                        new JObject
                        {
                            ["action"] = action,
                            ["parameters"] = JObject.FromObject(BuildQueryParameters(action, options)),
                            ["queueTimeoutMs"] = queryTimeouts.QueueTimeoutMs,
                            ["executeTimeoutMs"] = queryTimeouts.ExecuteTimeoutMs
                        },
                        queryTimeouts.TransportTimeoutMs);
                }
                catch (TaskCanceledException)
                {
                    return BuildFailure(
                        context,
                        "code_index request timed out after " + queryTimeouts.TransportTimeoutMs.ToString(CultureInfo.InvariantCulture)
                        + "ms before the daemon returned a queue or execution result.",
                        "http_timeout");
                }
                catch (HttpRequestException ex)
                {
                    return BuildFailure(context, "Code index daemon is not reachable: " + ex.Message, "daemon_unreachable");
                }
                catch (JsonException ex)
                {
                    return BuildFailure(context, "Code index daemon returned invalid JSON: " + ex.Message, "invalid_daemon_response");
                }

                if (response.Value<bool>("success"))
                {
                    response["enabled"] = true;
                    return response;
                }

                if (!IsFallbackEnabled(context, options)
                    || string.Equals(action, "diagnostics", StringComparison.OrdinalIgnoreCase)
                    || !ShouldFallbackForResponse(response))
                {
                    response["enabled"] = context.Enabled;
                    return response;
                }

                return BuildFallback(context, action, options, response.Value<string>("error"));
            }

            if (!IsFallbackEnabled(context, options) || string.Equals(action, "diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                return BuildFailure(context, "Unity snapshot workspace is not ready.", "workspace_not_ready");
            }

            return BuildFallback(context, action, options, "Unity snapshot workspace is not ready. Returned text-search candidates only.");
        }

        private static CodeIndexQueryTimeouts ResolveQueryTimeouts(string action, Dictionary<string, string> options, int timeout)
        {
            var explicitTimeout = options != null && options.ContainsKey("timeout");
            var defaultQueueTimeout = explicitTimeout ? timeout : DefaultQueryQueueTimeoutMs;
            var queueTimeout = ClampTimeout(ResolveOptionInt(options, "queue-timeout", defaultQueueTimeout));
            var executeTimeout = ClampTimeout(ResolveOptionInt(options, "execute-timeout", timeout));
            var transportTimeout = Math.Max(timeout, queueTimeout + executeTimeout + QueryTransportPaddingMs);

            return new CodeIndexQueryTimeouts
            {
                QueueTimeoutMs = queueTimeout,
                ExecuteTimeoutMs = executeTimeout,
                TransportTimeoutMs = ClampTimeout(transportTimeout)
            };
        }

        private static bool ShouldFallbackForResponse(JObject response)
        {
            var errorCode = response == null ? null : response.Value<string>("errorCode");
            if (string.IsNullOrWhiteSpace(errorCode))
            {
                return true;
            }

            switch (errorCode)
            {
                case "queue_timeout":
                case "queue_full":
                case "execute_timeout":
                case "client_cancelled":
                case "http_timeout":
                case "daemon_unreachable":
                    return false;
                default:
                    return true;
            }
        }

        private static bool IsDaemonTransportFailure(JObject response)
        {
            return response != null
                && response.Value<bool?>("success") == false
                && string.Equals(response.Value<string>("errorCode"), "daemon_unreachable", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<JObject> BatchAsync(CodeIndexContext context, Dictionary<string, string> options, int timeout)
        {
            var payload = ReadBatchPayload(context, options);
            if (payload.Value<bool?>("success") == false)
            {
                return payload;
            }

            var items = payload["items"] as JArray;
            if (items == null || items.Count == 0)
            {
                return BuildFailure(context, "Batch request must contain a non-empty items array.", "invalid_batch");
            }

            if (items.Count > MaxBatchItems)
            {
                return BuildFailure(context, "Batch request item count exceeds the " + MaxBatchItems.ToString(CultureInfo.InvariantCulture) + " item limit.", "batch_too_large");
            }

            var status = await EnsureDaemonAsync(context, timeout, false);
            if (IsDaemonTransportFailure(status))
            {
                status["enabled"] = context.Enabled;
                return status;
            }

            if (!IsReady(context, status))
            {
                return BuildFailure(context, "Unity snapshot workspace is not ready.", "workspace_not_ready");
            }

            var batchTimeouts = ResolveBatchTimeouts(payload, options, timeout);
            PrepareBatchPayload(payload, items, options, batchTimeouts);

            try
            {
                var response = await PostJsonAsync(
                    status.Value<string>("endpoint"),
                    "batch",
                    status.Value<string>("token"),
                    payload,
                    batchTimeouts.TransportTimeoutMs);
                response["enabled"] = context.Enabled;
                return response;
            }
            catch (TaskCanceledException)
            {
                return BuildFailure(
                    context,
                    "code_index batch timed out after " + batchTimeouts.TransportTimeoutMs.ToString(CultureInfo.InvariantCulture)
                    + "ms before the daemon returned a batch result.",
                    "http_timeout");
            }
            catch (HttpRequestException ex)
            {
                return BuildFailure(context, "Code index daemon is not reachable: " + ex.Message, "daemon_unreachable");
            }
            catch (JsonException ex)
            {
                return BuildFailure(context, "Code index daemon returned invalid JSON: " + ex.Message, "invalid_daemon_response");
            }
        }

        private static JObject ReadBatchPayload(CodeIndexContext context, Dictionary<string, string> options)
        {
            var json = ResolveStringOption(options, "json", null);
            if (string.IsNullOrWhiteSpace(json) && options != null && options.ContainsKey("stdin"))
            {
                json = Console.In.ReadToEnd();
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return BuildFailure(context, "Missing batch payload. Pass --stdin with JSON input or --json.", "missing_batch_payload");
            }

            try
            {
                var payload = JObject.Parse(json);
                return payload;
            }
            catch (JsonException ex)
            {
                return BuildFailure(context, "Invalid batch JSON: " + ex.Message, "invalid_batch_json");
            }
        }

        private static void PrepareBatchPayload(
            JObject payload,
            JArray items,
            Dictionary<string, string> options,
            CodeIndexQueryTimeouts batchTimeouts)
        {
            if (payload["continueOnError"] == null)
            {
                payload["continueOnError"] = ResolveOptionBool(options, "continue-on-error", true);
            }

            if (payload["timing"] == null)
            {
                payload["timing"] = ResolveOptionBool(options, "timing", true);
            }

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject;
                if (item == null)
                {
                    continue;
                }

                item.Remove("executeTimeoutMs");
            }

            payload["queueTimeoutMs"] = batchTimeouts.QueueTimeoutMs;
            payload["executeTimeoutMs"] = batchTimeouts.ExecuteTimeoutMs;
        }

        private static CodeIndexQueryTimeouts ResolveBatchTimeouts(JObject payload, Dictionary<string, string> options, int timeout)
        {
            var items = payload["items"] as JArray;
            var childTimeoutSum = 0L;
            if (items != null)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i] as JObject;
                    childTimeoutSum += ResolveBatchItemTimeout(item);
                }
            }

            var payloadQueueTimeout = payload.Value<int?>("queueTimeoutMs") ?? DefaultQueryQueueTimeoutMs;
            var payloadExecuteTimeout = payload.Value<int?>("executeTimeoutMs")
                ?? (int)Math.Min(MaxCodeIndexTimeoutMs, Math.Max(DefaultHeavyQueryTimeoutMs, childTimeoutSum));
            var explicitTimeout = options != null && options.ContainsKey("timeout");
            var queueDefault = explicitTimeout ? timeout : payloadQueueTimeout;
            var executeDefault = explicitTimeout ? timeout : payloadExecuteTimeout;
            var queueTimeout = ClampTimeout(ResolveOptionInt(options, "queue-timeout", queueDefault));
            var executeTimeout = ClampTimeout(ResolveOptionInt(options, "execute-timeout", executeDefault));
            var transportTimeout = Math.Max(timeout, queueTimeout + executeTimeout + QueryTransportPaddingMs);

            return new CodeIndexQueryTimeouts
            {
                QueueTimeoutMs = queueTimeout,
                ExecuteTimeoutMs = executeTimeout,
                TransportTimeoutMs = ClampTimeout(transportTimeout)
            };
        }

        private static int ResolveBatchItemTimeout(JObject item)
        {
            if (item == null)
            {
                return DefaultHeavyQueryTimeoutMs;
            }

            var explicitTimeout = item.Value<int?>("executeTimeoutMs");
            if (explicitTimeout.HasValue && explicitTimeout.Value > 0)
            {
                return ClampTimeout(explicitTimeout.Value);
            }

            var action = NormalizeAction(item.Value<string>("action"));
            if (string.Equals(action, "diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                var parameters = item["parameters"] as JObject;
                var all = parameters != null && parameters.Value<bool?>("all") == true;
                return all ? DefaultFullDiagnosticsTimeoutMs : DefaultHeavyQueryTimeoutMs;
            }

            switch (action)
            {
                case "symbol":
                    return DefaultSymbolTimeoutMs;
                case "definition":
                    return DefaultDefinitionTimeoutMs;
                case "references":
                case "implementations":
                case "derived":
                case "callers":
                    return DefaultHeavyQueryTimeoutMs;
                default:
                    return DefaultHeavyQueryTimeoutMs;
            }
        }

        private static async Task<JObject> WarmupAsync(CodeIndexContext context, int timeout, bool noWait)
        {
            var status = await EnsureDaemonAsync(context, timeout, noWait);
            if (status != null && status.Value<bool?>("success") == false)
            {
                return status;
            }

            var result = BuildStatusResult(context, status, status != null && status.Value<bool>("reachable"));
            result["success"] = true;
            result["semantic"] = IsReady(context, status);
            result["source"] = "unity-snapshot";
            if (status != null && string.Equals(status.Value<string>("state"), "failed", StringComparison.OrdinalIgnoreCase))
            {
                result["success"] = false;
                result["error"] = status.Value<string>("message") ?? "Code index warmup failed.";
            }

            return result;
        }

        private static async Task<JObject> ResetAsync(CodeIndexContext context, int timeout)
        {
            Directory.CreateDirectory(context.IndexDirectory);
            using (var launchLock = await AcquireDaemonLaunchLockAsync(context, timeout))
            {
                if (launchLock == null)
                {
                    return BuildFailure(context, "Timed out waiting for the code_index daemon launch lock.", "daemon_launch_lock_timeout");
                }

                var status = ReadStatusFile(context);
                var daemonPid = status == null ? 0 : status.Value<int?>("daemonPid") ?? 0;
                if (status != null && !string.IsNullOrWhiteSpace(status.Value<string>("endpoint")))
                {
                    try
                    {
                        await PostJsonAsync(status.Value<string>("endpoint"), "shutdown", status.Value<string>("token"), new JObject(), timeout);
                    }
                    catch
                    {
                    }
                }

                await EnsureDaemonStoppedAsync(daemonPid, context.DaemonProcessPath);
                CleanupOrphanDaemons(context, 0);
                ResetIndexDirectory(context, context.IncludeSnapshotOnReset);
            }

            return new JObject
            {
                ["success"] = true,
                ["enabled"] = context.Enabled,
                ["semantic"] = false,
                ["source"] = "reset",
                ["state"] = "reset",
                ["stale"] = true,
                ["projectRoot"] = context.ProjectRoot,
                ["solution"] = context.SolutionPath,
                ["workspaceMode"] = "unity-snapshot",
                ["snapshotExists"] = Directory.Exists(context.SnapshotDirectory),
                ["message"] = context.IncludeSnapshotOnReset
                    ? "code_index cache, daemon state, and Unity snapshot were reset."
                    : "code_index daemon state and internal cache were reset; Unity snapshot was preserved."
            };
        }

        private static void ResetIndexDirectory(CodeIndexContext context, bool includeSnapshot)
        {
            if (!Directory.Exists(context.IndexDirectory))
            {
                return;
            }

            DeleteFileIfExists(context.StatusPath);
            DeleteFileIfExists(Path.Combine(context.IndexDirectory, "lock.json"));
            DeleteFileIfExists(context.DaemonProcessPath);
            DeleteDirectoryIfExists(context.DaemonProcessDirectory);
            DeleteDirectoryIfExists(Path.Combine(context.IndexDirectory, "temp"));
            DeleteDirectoryIfExists(Path.Combine(context.IndexDirectory, "logs"));
            DeleteDirectoryIfExists(Path.Combine(context.IndexDirectory, "daemon"));
            DeleteDirectoryIfExists(Path.Combine(context.IndexDirectory, "cache"));
            DeleteDirectoryIfExists(Path.Combine(context.IndexDirectory, "index"));
            if (includeSnapshot)
            {
                DeleteDirectoryIfExists(context.SnapshotDirectory);
            }
        }

        private static async Task EnsureDaemonStoppedAsync(int daemonPid, string markerPath)
        {
            if (daemonPid <= 0)
            {
                return;
            }

            for (var i = 0; i < 20; i++)
            {
                if (!TryGetCodeIndexProcess(daemonPid, markerPath, out var process))
                {
                    return;
                }

                process.Dispose();
                await Task.Delay(100);
            }

            if (!TryGetCodeIndexProcess(daemonPid, markerPath, out var remaining))
            {
                return;
            }

            using (remaining)
            {
                KillCodeIndexProcess(remaining);
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
                var marker = JObject.Parse(File.ReadAllText(markerPath, Encoding.UTF8));
                var pid = marker.Value<int?>("daemonPid") ?? 0;
                var startedAtUtcTicks = marker.Value<long?>("startedAtUtcTicks") ?? 0L;
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

        private static async Task<JObject> BuildSnapshotAsync(CodeIndexContext context, Dictionary<string, string> options, int timeout, bool noWait)
        {
            if (options == null || !options.TryGetValue("input", out var inputPath) || string.IsNullOrWhiteSpace(inputPath))
            {
                return BuildFailure(context, "Missing required parameter: --input");
            }

            inputPath = Path.GetFullPath(inputPath);
            if (!File.Exists(inputPath))
            {
                return BuildFailure(context, "Snapshot compiler input does not exist: " + inputPath);
            }

            var daemon = CodeIndexDaemonExecutable.Resolve();
            if (!daemon.CanStart)
            {
                return BuildDaemonUnavailableFailure(context, daemon);
            }

            var workers = ResolveOptionInt(options, "workers", 0);
            var startInfo = daemon.CreateStartInfo();
            startInfo.WorkingDirectory = context.ProjectRoot;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = !noWait;
            startInfo.RedirectStandardError = !noWait;
            daemon.AddSnapshotWorkerArguments(startInfo, context, inputPath, workers);

            var process = Process.Start(startInfo);
            if (process == null)
            {
                return BuildFailure(context, "Failed to start AIBridgeCodeIndex snapshot worker.");
            }

            ApplyDaemonPriority(process, context.ProcessPriority);
            if (noWait)
            {
                var pid = process.Id;
                process.Dispose();
                return new JObject
                {
                    ["success"] = true,
                    ["enabled"] = context.Enabled,
                    ["semantic"] = false,
                    ["source"] = "snapshot-worker",
                    ["state"] = "running",
                    ["stale"] = true,
                    ["projectRoot"] = context.ProjectRoot,
                    ["workspaceMode"] = "unity-snapshot",
                    ["snapshotExists"] = File.Exists(context.SnapshotManifestPath),
                    ["inputPath"] = inputPath,
                    ["workerPid"] = pid
                };
            }

            string stdout;
            string stderr;
            using (process)
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeout))
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch
                    {
                    }

                    return BuildFailure(context, "Snapshot worker timed out after " + timeout.ToString(CultureInfo.InvariantCulture) + "ms.");
                }

                stdout = await stdoutTask;
                stderr = await stderrTask;
                if (process.ExitCode != 0)
                {
                    return BuildSnapshotWorkerFailure(context, inputPath, stdout, stderr);
                }
            }

            JObject result;
            try
            {
                result = string.IsNullOrWhiteSpace(stdout) ? new JObject() : JObject.Parse(stdout);
            }
            catch
            {
                return BuildFailure(context, "Snapshot worker returned invalid output: " + TrimPreview(stdout));
            }

            result["success"] = result.Value<bool?>("success") ?? true;
            result["enabled"] = context.Enabled;
            result["semantic"] = false;
            result["source"] = "snapshot-worker";
            result["state"] = result.Value<bool>("success") ? "snapshotGenerated" : "failed";
            result["stale"] = !result.Value<bool>("success");
            result["projectRoot"] = context.ProjectRoot;
            result["workspaceMode"] = "unity-snapshot";
            result["snapshotExists"] = File.Exists(context.SnapshotManifestPath);
            result["snapshotPath"] = context.SnapshotManifestPath;
            result["inputPath"] = inputPath;
            return result;
        }

        private static JObject BuildSnapshotWorkerFailure(CodeIndexContext context, string inputPath, string stdout, string stderr)
        {
            JObject workerResult = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    workerResult = JObject.Parse(stdout);
                }
            }
            catch
            {
            }

            var message = workerResult == null
                ? (TrimPreview(stderr) ?? TrimPreview(stdout))
                : workerResult.Value<string>("message");
            var result = BuildFailure(context, string.IsNullOrWhiteSpace(message) ? "Snapshot worker failed." : message);
            result["source"] = "snapshot-worker";
            result["inputPath"] = inputPath;
            return result;
        }

        private static async Task<JObject> BuildStatusAsync(CodeIndexContext context, int timeout)
        {
            var status = ReadStatusFile(context);
            var reachable = false;
            if (status != null)
            {
                var remote = await TryGetRemoteStatusAsync(status, timeout);
                if (remote != null)
                {
                    reachable = true;
                    CopyStatusRuntimeFields(status, remote);
                    status = remote;
                }
            }

            return BuildStatusResult(context, status, reachable);
        }

        private static async Task<JObject> BuildDoctorAsync(CodeIndexContext context, int timeout)
        {
            var issues = new JArray();
            var suggestions = new JArray();

            if (!context.HasUnityProjectMarkers)
            {
                issues.Add("Project root does not look like a Unity project. Assets/ and ProjectSettings/ProjectSettings.asset were not found.");
                suggestions.Add("Run from a Unity project root or pass --project-root.");
            }

            if (!File.Exists(context.SnapshotManifestPath))
            {
                issues.Add("No Unity compilation snapshot found.");
                suggestions.Add("Open the Unity project once or run Code Index prewarm from AIBridge settings.");
            }

            var daemon = CodeIndexDaemonExecutable.Resolve();
            if (!daemon.CanStart)
            {
                issues.Add(daemon.DiagnosticMessage);
                suggestions.Add(daemon.HasMissingFiles
                    ? "Rebuild and refresh the AIBridge CLI cache so Tools~/CLI/<rid>/CodeIndex is copied completely."
                    : "Build the CLI package so Tools~/CLI/<rid>/CodeIndex is available.");
            }

            var status = ReadStatusFile(context);
            var remote = status == null ? null : await TryGetRemoteStatusAsync(status, timeout);
            var reachable = remote != null;
            if (status == null)
            {
                suggestions.Add("Run code_index warmup to start the daemon.");
            }
            else if (!reachable)
            {
                issues.Add("status.json exists but daemon endpoint is not reachable.");
                suggestions.Add("Run code_index reset, then code_index warmup.");
            }

            var currentStatus = remote ?? status;
            var snapshotCounts = ReadSnapshotSemanticCounts(context);
            var snapshotHasSemanticContent = !snapshotCounts.Exists || snapshotCounts.HasSemanticContent;
            if (snapshotCounts.Exists && snapshotCounts.Unreadable)
            {
                issues.Add("Unity compilation snapshot manifest is unreadable.");
                suggestions.Add("Regenerate the Code Index snapshot from AIBridge settings, then run code_index warmup.");
            }
            else if (snapshotCounts.Exists && !snapshotHasSemanticContent)
            {
                issues.Add("Unity compilation snapshot contains no assemblies or source files.");
                suggestions.Add("Regenerate the Code Index snapshot from AIBridge settings, then run code_index warmup.");
            }

            var manifestStale = IsSnapshotStale(context, currentStatus);
            var snapshotStaleReason = snapshotCounts.Unreadable
                ? "snapshotUnreadable"
                : snapshotCounts.Exists && !snapshotCounts.HasSemanticContent
                    ? "emptySnapshot"
                    : manifestStale ? "snapshotContentChanged" : currentStatus?.Value<string>("staleReason");
            var result = new JObject
            {
                ["success"] = true,
                ["healthy"] = issues.Count == 0 && snapshotHasSemanticContent,
                ["enabled"] = true,
                ["semantic"] = reachable && IsReady(context, currentStatus),
                ["source"] = "doctor",
                ["state"] = remote == null ? (status == null ? "missing" : status.Value<string>("state")) : remote.Value<string>("state"),
                ["stale"] = !reachable || !IsReady(context, currentStatus) || manifestStale,
                ["projectRoot"] = context.ProjectRoot,
                ["solution"] = context.SolutionPath,
                ["workspaceMode"] = "unity-snapshot",
                ["snapshotExists"] = File.Exists(context.SnapshotManifestPath),
                ["snapshotPath"] = context.SnapshotManifestPath,
                ["snapshotVersion"] = currentStatus?.Value<int?>("snapshotVersion") ?? 0,
                ["generationId"] = currentStatus?.Value<string>("generationId"),
                ["snapshotContentHash"] = currentStatus?.Value<string>("snapshotContentHash"),
                ["assemblyCount"] = ResolveAssemblyCount(currentStatus, snapshotCounts),
                ["sourceFileCount"] = ResolveSourceFileCount(currentStatus, snapshotCounts),
                ["excludedAssemblyCount"] = currentStatus?.Value<int?>("excludedAssemblyCount") ?? 0,
                ["excludedSourceFileCount"] = currentStatus?.Value<int?>("excludedSourceFileCount") ?? 0,
                ["includePackageCacheSourceAssemblies"] = currentStatus?.Value<bool?>("includePackageCacheSourceAssemblies") ?? false,
                ["buildTarget"] = currentStatus?.Value<string>("buildTarget"),
                ["unityVersion"] = currentStatus?.Value<string>("unityVersion"),
                ["staleReason"] = snapshotStaleReason,
                ["statusPath"] = context.StatusPath,
                ["issues"] = issues,
                ["suggestions"] = suggestions
            };
            AddDaemonDiagnosticFields(result, daemon);
            return result;
        }

        private static JObject BuildDisabledDoctor(CodeIndexContext context)
        {
            return new JObject
            {
                ["success"] = true,
                ["healthy"] = false,
                ["enabled"] = false,
                ["semantic"] = false,
                ["source"] = "settings",
                ["state"] = "disabled",
                ["stale"] = true,
                ["projectRoot"] = context.ProjectRoot,
                ["solution"] = context.SolutionPath,
                ["workspaceMode"] = "unity-snapshot",
                ["snapshotExists"] = File.Exists(context.SnapshotManifestPath),
                ["snapshotPath"] = context.SnapshotManifestPath,
                ["statusPath"] = context.StatusPath,
                ["issues"] = new JArray("Code Index is disabled in AIBridge settings."),
                ["suggestions"] = new JArray("Enable AIBridge > Settings > Code Index > Enable Code Index, or use rg and normal file reads.")
            };
        }

        private static async Task<JObject> EnsureDaemonAsync(CodeIndexContext context, int timeout, bool noWait)
        {
            var status = ReadStatusFile(context);
            var remote = status == null ? null : await TryGetRemoteStatusAsync(status, timeout);
            if (remote != null)
            {
                CopyStatusRuntimeFields(status, remote);
                if (noWait || IsReady(context, remote))
                {
                    remote["reachable"] = true;
                    return remote;
                }

                return await WaitUntilReadyAsync(context, status, timeout);
            }

            if (!File.Exists(context.SnapshotManifestPath))
            {
                return BuildFailure(context, "No Unity compilation snapshot found. Open the Unity project once or run Code Index prewarm from AIBridge settings.");
            }

            var existingStatus = await WaitForReachableExistingDaemonAsync(context, status, timeout);
            if (existingStatus != null)
            {
                return existingStatus;
            }

            var startedStatus = await EnsureDaemonStartedUnderLockAsync(context, timeout);
            if (startedStatus == null)
            {
                return BuildFailure(context, "AIBridgeCodeIndex daemon did not write status.json before timeout.", "daemon_start_timeout");
            }

            if (startedStatus.Value<bool?>("success") == false)
            {
                return startedStatus;
            }

            if (noWait)
            {
                startedStatus["reachable"] = false;
                return startedStatus;
            }

            return await WaitUntilReadyAsync(context, startedStatus, timeout);
        }

        private static async Task<JObject> EnsureDaemonStartedUnderLockAsync(CodeIndexContext context, int timeout)
        {
            Directory.CreateDirectory(context.IndexDirectory);
            using (var launchLock = await AcquireDaemonLaunchLockAsync(context, timeout))
            {
                if (launchLock == null)
                {
                    return BuildFailure(context, "Timed out waiting for the code_index daemon launch lock.", "daemon_launch_lock_timeout");
                }

                // 多个 CLI 可能同时发现 daemon 不可用；拿到跨进程锁后必须重查，避免重复启动。
                var status = ReadStatusFile(context);
                var remote = status == null ? null : await TryGetRemoteStatusAsync(status, timeout);
                if (remote != null)
                {
                    CopyStatusRuntimeFields(status, remote);
                    remote["reachable"] = true;
                    return remote;
                }

                var existingStatus = await WaitForReachableExistingDaemonAsync(context, status, timeout);
                if (existingStatus != null)
                {
                    return existingStatus;
                }

                CleanupOrphanDaemons(context, 0);
                DeleteFileIfExists(context.StatusPath);
                DeleteFileIfExists(Path.Combine(context.IndexDirectory, "lock.json"));

                var daemon = CodeIndexDaemonExecutable.Resolve();
                if (!daemon.CanStart)
                {
                    return BuildDaemonUnavailableFailure(context, daemon);
                }

                StartDaemon(context, daemon);
                return await WaitForStatusFileAsync(context, timeout);
            }
        }

        private static async Task<JObject> WaitForReachableExistingDaemonAsync(CodeIndexContext context, JObject status, int timeout)
        {
            if (!IsStatusDaemonProcessAlive(context, status))
            {
                return null;
            }

            var waitMs = Math.Min(Math.Max(500, timeout), ExistingDaemonReachabilityWaitMs);
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            var latestStatus = status;
            while (DateTime.UtcNow < deadline)
            {
                var remote = latestStatus == null ? null : await TryGetRemoteStatusAsync(latestStatus, timeout);
                if (remote != null)
                {
                    CopyStatusRuntimeFields(latestStatus, remote);
                    remote["reachable"] = true;
                    return remote;
                }

                await Task.Delay(ExistingDaemonRetryDelayMs);
                latestStatus = ReadStatusFile(context) ?? latestStatus;
                if (!IsStatusDaemonProcessAlive(context, latestStatus))
                {
                    return null;
                }
            }

            return BuildFailure(
                context,
                "Existing code_index daemon process is still running, but its HTTP endpoint did not respond within "
                + waitMs.ToString(CultureInfo.InvariantCulture)
                + "ms. Automatic restart was skipped to avoid interrupting in-flight queries.",
                "daemon_unreachable");
        }

        private static bool IsStatusDaemonProcessAlive(CodeIndexContext context, JObject status)
        {
            Process process;
            if (!TryGetStatusDaemonProcess(context, status, out process))
            {
                return false;
            }

            using (process)
            {
                return true;
            }
        }

        private static bool TryGetStatusDaemonProcess(CodeIndexContext context, JObject status, out Process process)
        {
            process = null;
            if (context == null || status == null)
            {
                return false;
            }

            var daemonPid = status.Value<int?>("daemonPid") ?? 0;
            if (daemonPid <= 0)
            {
                return false;
            }

            var markerPath = GetDaemonProcessMarkerPath(context, daemonPid);
            if (!File.Exists(markerPath))
            {
                markerPath = context.DaemonProcessPath;
            }

            return TryGetCodeIndexProcess(daemonPid, markerPath, out process);
        }

        private static async Task<FileStream> AcquireDaemonLaunchLockAsync(CodeIndexContext context, int timeout)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, timeout));
            Directory.CreateDirectory(context.IndexDirectory);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    return new FileStream(context.DaemonLaunchLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    await Task.Delay(100);
                }
                catch (UnauthorizedAccessException)
                {
                    await Task.Delay(100);
                }
            }

            return null;
        }

        private static void StartDaemon(CodeIndexContext context, CodeIndexDaemonExecutable daemon)
        {
            Directory.CreateDirectory(context.IndexDirectory);
            if (!daemon.CanStart)
            {
                throw new FileNotFoundException(daemon.DiagnosticMessage, daemon.DisplayPath);
            }

            var token = Guid.NewGuid().ToString("N");
            var startInfo = daemon.CreateStartInfo();
            startInfo.WorkingDirectory = context.ProjectRoot;
            daemon.AddLaunchArguments(startInfo, context, token);
            if (daemon.UseShellExecuteForDetachedLaunch)
            {
                // Windows 可执行文件用 ShellExecute 启动，避免继承调用方捕获 stdout/stderr 时使用的管道句柄。
                startInfo.UseShellExecute = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            }
            else
            {
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                // dotnet/source fallback 仍显式重定向，避免 daemon 继承父 CLI 标准句柄。
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
            }

            var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start AIBridgeCodeIndex daemon.");
            }

            ApplyDaemonPriority(process, context.ProcessPriority);
            WriteDaemonProcessMarker(context, daemon, process);
            process.Dispose();
        }

        private static void WriteDaemonProcessMarker(CodeIndexContext context, CodeIndexDaemonExecutable daemon, Process process)
        {
            if (context == null || daemon == null || process == null || string.IsNullOrWhiteSpace(context.DaemonProcessPath))
            {
                return;
            }

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

                var daemonProcessDirectory = Path.GetDirectoryName(context.DaemonProcessPath);
                if (!string.IsNullOrWhiteSpace(daemonProcessDirectory))
                {
                    Directory.CreateDirectory(daemonProcessDirectory);
                }

                File.WriteAllText(
                    context.DaemonProcessPath,
                    BuildDaemonProcessMarker(context, daemon, process, startedAtUtcTicks).ToString(Formatting.None),
                    Encoding.UTF8);

                Directory.CreateDirectory(context.DaemonProcessDirectory);
                File.WriteAllText(
                    GetDaemonProcessMarkerPath(context, process.Id),
                    BuildDaemonProcessMarker(context, daemon, process, startedAtUtcTicks).ToString(Formatting.None),
                    Encoding.UTF8);
            }
            catch
            {
                // marker 只用于 dotnet 启动模式下的保守清理；写入失败不能阻止 daemon 启动。
            }
        }

        private static JObject BuildDaemonProcessMarker(
            CodeIndexContext context,
            CodeIndexDaemonExecutable daemon,
            Process process,
            long startedAtUtcTicks)
        {
            return new JObject
            {
                ["markerVersion"] = 2,
                ["daemonPid"] = process.Id,
                ["processName"] = process.ProcessName,
                ["startedAtUtcTicks"] = startedAtUtcTicks,
                ["launchMode"] = daemon.LaunchMode,
                ["daemonPath"] = daemon.DisplayPath,
                ["projectRoot"] = context.ProjectRoot,
                ["ownerPid"] = context.OwnerPid,
                ["ownerStartTicks"] = context.OwnerStartTicks
            };
        }

        private static string GetDaemonProcessMarkerPath(CodeIndexContext context, int processId)
        {
            return Path.Combine(context.DaemonProcessDirectory, processId.ToString(CultureInfo.InvariantCulture) + ".json");
        }

        private static void CleanupOrphanDaemons(CodeIndexContext context, int keepPid)
        {
            if (context == null)
            {
                return;
            }

            foreach (var markerPath in EnumerateDaemonProcessMarkerPaths(context))
            {
                var marker = ReadJsonFile(markerPath);
                if (!MarkerMatchesProject(marker, context))
                {
                    continue;
                }

                var pid = marker.Value<int?>("daemonPid") ?? 0;
                if (pid <= 0 || pid == keepPid)
                {
                    continue;
                }

                if (TryGetCodeIndexProcess(pid, markerPath, out var process))
                {
                    using (process)
                    {
                        KillCodeIndexProcess(process);
                    }
                }

                DeleteFileIfExists(markerPath);
            }
        }

        private static IEnumerable<string> EnumerateDaemonProcessMarkerPaths(CodeIndexContext context)
        {
            if (File.Exists(context.DaemonProcessPath))
            {
                yield return context.DaemonProcessPath;
            }

            if (!Directory.Exists(context.DaemonProcessDirectory))
            {
                yield break;
            }

            foreach (var markerPath in Directory.EnumerateFiles(context.DaemonProcessDirectory, "*.json"))
            {
                yield return markerPath;
            }
        }

        private static JObject ReadJsonFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                return JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch
            {
                return null;
            }
        }

        private static bool MarkerMatchesProject(JObject marker, CodeIndexContext context)
        {
            if (marker == null || context == null)
            {
                return false;
            }

            var projectRoot = marker.Value<string>("projectRoot");
            return !string.IsNullOrWhiteSpace(projectRoot) && PathsEqual(projectRoot, context.ProjectRoot);
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

            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(left, right, comparison);
        }

        private static void KillCodeIndexProcess(Process process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
            catch
            {
            }
        }

        private static void ApplyDaemonPriority(Process process, string priority)
        {
            if (process == null || string.IsNullOrWhiteSpace(priority))
            {
                return;
            }

            try
            {
                if (string.Equals(priority, "low", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(priority, "below-normal", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(priority, "belownormal", StringComparison.OrdinalIgnoreCase))
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
            }
            catch
            {
                // 降低优先级是启动体验优化，平台不支持或权限不足时不应影响 daemon 启动。
            }
        }

        private static async Task<JObject> WaitForStatusFileAsync(CodeIndexContext context, int timeout)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
            while (DateTime.UtcNow < deadline)
            {
                var status = ReadStatusFile(context);
                if (status != null)
                {
                    return status;
                }

                await Task.Delay(100);
            }

            return null;
        }

        private static async Task<JObject> WaitUntilReadyAsync(CodeIndexContext context, JObject status, int timeout)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
            JObject last = status;
            while (DateTime.UtcNow < deadline)
            {
                var remote = await TryGetRemoteStatusAsync(status, Math.Min(1000, timeout));
                if (remote != null)
                {
                    CopyStatusRuntimeFields(status, remote);
                    remote["reachable"] = true;
                    last = remote;
                    if (IsReady(context, remote) || string.Equals(remote.Value<string>("state"), "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        return remote;
                    }
                }

                await Task.Delay(250);
            }

            if (last != null)
            {
                last["reachable"] = true;
            }

            return last;
        }

        private static JObject BuildStatusResult(CodeIndexContext context, JObject status, bool reachable)
        {
            var daemon = CodeIndexDaemonExecutable.Resolve();
            var snapshotCounts = ReadSnapshotSemanticCounts(context);
            if (status == null)
            {
                var missingStatusEmptySnapshot = snapshotCounts.Exists && !snapshotCounts.HasSemanticContent;
                var missingStatus = new JObject
                {
                    ["success"] = true,
                    ["enabled"] = context.Enabled,
                    ["semantic"] = false,
                    ["source"] = "status-file",
                    ["state"] = "missing",
                    ["stale"] = true,
                    ["projectRoot"] = context.ProjectRoot,
                    ["solution"] = context.SolutionPath,
                    ["workspaceMode"] = "unity-snapshot",
                    ["snapshotExists"] = File.Exists(context.SnapshotManifestPath),
                    ["snapshotPath"] = context.SnapshotManifestPath,
                    ["assemblyCount"] = ResolveAssemblyCount(null, snapshotCounts),
                    ["sourceFileCount"] = ResolveSourceFileCount(null, snapshotCounts),
                    ["staleReason"] = snapshotCounts.Unreadable
                        ? "snapshotUnreadable"
                        : missingStatusEmptySnapshot ? "emptySnapshot" : "statusMissing",
                    ["statusPath"] = context.StatusPath,
                    ["reachable"] = false,
                    ["message"] = snapshotCounts.Unreadable
                        ? "Unity compilation snapshot manifest is unreadable. Regenerate the Code Index snapshot from AIBridge settings, then run code_index warmup."
                        : missingStatusEmptySnapshot
                            ? "Unity compilation snapshot contains no assemblies or source files. Regenerate the Code Index snapshot from AIBridge settings, then run code_index warmup."
                            : null
                };
                AddDaemonDiagnosticFields(missingStatus, daemon);
                return missingStatus;
            }

            var manifestStale = IsSnapshotStale(context, status);
            var semanticReady = IsReady(context, status);
            var emptySnapshot = snapshotCounts.Exists && !snapshotCounts.HasSemanticContent;
            var result = new JObject
            {
                ["success"] = true,
                ["enabled"] = context.Enabled,
                ["semantic"] = semanticReady,
                ["source"] = "status-file",
                ["state"] = status.Value<string>("state"),
                ["stale"] = !reachable || status.Value<bool?>("stale") == true || manifestStale || emptySnapshot,
                ["projectRoot"] = status.Value<string>("projectRoot") ?? context.ProjectRoot,
                ["solution"] = status.Value<string>("solution") ?? context.SolutionPath,
                ["workspaceMode"] = status.Value<string>("workspaceMode") ?? "unity-snapshot",
                ["snapshotExists"] = status.Value<bool?>("snapshotExists") ?? File.Exists(context.SnapshotManifestPath),
                ["snapshotPath"] = context.SnapshotManifestPath,
                ["snapshotVersion"] = status.Value<int?>("snapshotVersion") ?? 0,
                ["generationId"] = status.Value<string>("generationId"),
                ["snapshotContentHash"] = status.Value<string>("snapshotContentHash"),
                ["assemblyCount"] = ResolveAssemblyCount(status, snapshotCounts),
                ["sourceFileCount"] = ResolveSourceFileCount(status, snapshotCounts),
                ["excludedAssemblyCount"] = status.Value<int?>("excludedAssemblyCount") ?? 0,
                ["excludedSourceFileCount"] = status.Value<int?>("excludedSourceFileCount") ?? 0,
                ["includePackageCacheSourceAssemblies"] = status.Value<bool?>("includePackageCacheSourceAssemblies") ?? false,
                ["buildTarget"] = status.Value<string>("buildTarget"),
                ["unityVersion"] = status.Value<string>("unityVersion"),
                ["staleReason"] = snapshotCounts.Unreadable
                    ? "snapshotUnreadable"
                    : emptySnapshot
                        ? "emptySnapshot"
                        : manifestStale ? "snapshotContentChanged" : status.Value<string>("staleReason"),
                ["loadedProjects"] = status.Value<int?>("loadedProjects") ?? 0,
                ["loadedDocuments"] = status.Value<int?>("loadedDocuments") ?? 0,
                ["endpoint"] = status.Value<string>("endpoint"),
                ["daemonPid"] = status.Value<int?>("daemonPid") ?? 0,
                ["ownerPid"] = status.Value<int?>("ownerPid") ?? 0,
                ["ownerStartTicks"] = status.Value<long?>("ownerStartTicks") ?? 0L,
                ["ownerAlive"] = status.Value<bool?>("ownerAlive") ?? false,
                ["ownerMonitorMode"] = status.Value<string>("ownerMonitorMode"),
                ["statusPath"] = context.StatusPath,
                ["reachable"] = reachable,
                ["message"] = status.Value<string>("message"),
                ["queueLength"] = status.Value<int?>("queueLength") ?? 0,
                ["queueCapacity"] = status.Value<int?>("queueCapacity") ?? 0,
                ["activeRequestId"] = status.Value<string>("activeRequestId"),
                ["activeAction"] = status.Value<string>("activeAction"),
                ["activeStartedAt"] = status.Value<string>("activeStartedAt"),
                ["lastQueuedMs"] = status.Value<long?>("lastQueuedMs") ?? 0L,
                ["lastExecutionMs"] = status.Value<long?>("lastExecutionMs") ?? 0L,
                ["totalQueued"] = status.Value<long?>("totalQueued") ?? 0L,
                ["totalCompleted"] = status.Value<long?>("totalCompleted") ?? 0L,
                ["totalTimedOut"] = status.Value<long?>("totalTimedOut") ?? 0L,
                ["totalDeduplicated"] = status.Value<long?>("totalDeduplicated") ?? 0L,
                ["queryCacheCount"] = status.Value<int?>("queryCacheCount") ?? 0,
                ["queryCacheHits"] = status.Value<long?>("queryCacheHits") ?? 0L,
                ["queryCacheMisses"] = status.Value<long?>("queryCacheMisses") ?? 0L
            };
            AddDaemonDiagnosticFields(result, daemon);
            return result;
        }

        private static JObject BuildDisabledStatus(CodeIndexContext context)
        {
            return new JObject
            {
                ["success"] = true,
                ["enabled"] = false,
                ["semantic"] = false,
                ["source"] = "settings",
                ["state"] = "disabled",
                ["stale"] = true,
                ["projectRoot"] = context.ProjectRoot,
                ["solution"] = context.SolutionPath,
                ["workspaceMode"] = "unity-snapshot",
                ["snapshotExists"] = File.Exists(context.SnapshotManifestPath),
                ["snapshotPath"] = context.SnapshotManifestPath,
                ["statusPath"] = context.StatusPath,
                ["reachable"] = false,
                ["message"] = DisabledMessage
            };
        }

        private static JObject BuildDisabledFailure(CodeIndexContext context)
        {
            return new JObject
            {
                ["success"] = false,
                ["enabled"] = false,
                ["semantic"] = false,
                ["source"] = "settings",
                ["state"] = "disabled",
                ["stale"] = true,
                ["projectRoot"] = context.ProjectRoot,
                ["solution"] = context.SolutionPath,
                ["workspaceMode"] = "unity-snapshot",
                ["snapshotExists"] = File.Exists(context.SnapshotManifestPath),
                ["error"] = DisabledMessage
            };
        }

        private static bool IsSnapshotStale(CodeIndexContext context, JObject status)
        {
            if (context == null)
            {
                return true;
            }

            if (!File.Exists(context.SnapshotManifestPath))
            {
                return true;
            }

            var snapshotContentHash = status == null ? null : status.Value<string>("snapshotContentHash");
            if (!string.IsNullOrWhiteSpace(snapshotContentHash))
            {
                var currentSnapshotContentHash = ReadSnapshotContentHash(context.SnapshotManifestPath);
                return string.IsNullOrWhiteSpace(currentSnapshotContentHash)
                       || !string.Equals(snapshotContentHash, currentSnapshotContentHash, StringComparison.OrdinalIgnoreCase);
            }

            var generationId = status == null ? null : status.Value<string>("generationId");
            if (string.IsNullOrWhiteSpace(generationId))
            {
                return false;
            }

            return !string.Equals(generationId, ReadSnapshotGenerationId(context.SnapshotJsonPath), StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadSnapshotGenerationId(string manifestJsonPath)
        {
            if (string.IsNullOrWhiteSpace(manifestJsonPath) || !File.Exists(manifestJsonPath))
            {
                return null;
            }

            try
            {
                var text = File.ReadAllText(manifestJsonPath, Encoding.UTF8);
                var match = System.Text.RegularExpressions.Regex.Match(text, "\"generationId\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"");
                return match.Success ? System.Text.RegularExpressions.Regex.Unescape(match.Groups["value"].Value) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string ReadSnapshotContentHash(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                using (var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    var magic = Encoding.ASCII.GetString(reader.ReadBytes(SnapshotMagic.Length));
                    var headerSchema = reader.ReadInt32();
                    var formatKind = reader.ReadInt32();
                    reader.ReadInt64();
                    if (!string.Equals(magic, SnapshotMagic, StringComparison.Ordinal)
                        || headerSchema != SnapshotSchemaVersion
                        || formatKind != ManifestFormatKind)
                    {
                        return null;
                    }

                    var parts = new List<string>();
                    var schemaVersion = reader.ReadInt32();
                    AddContentPart(parts, "schemaVersion", schemaVersion.ToString(CultureInfo.InvariantCulture));
                    AddContentPart(parts, "projectRootHash", ReadSnapshotString(reader));
                    AddContentPart(parts, "unityVersion", ReadSnapshotString(reader));
                    AddContentPart(parts, "buildTarget", ReadSnapshotString(reader));
                    ReadSnapshotString(reader);
                    AddContentPart(parts, "includePackageCacheSourceAssemblies", reader.ReadBoolean() ? "true" : "false");
                    AddStringListContentParts(parts, "ignoredAssemblyPatterns", ReadSnapshotStringList(reader));
                    AddStringListContentParts(parts, "ignoredSourcePathPatterns", ReadSnapshotStringList(reader));
                    AddContentPart(parts, "filterHash", ReadSnapshotString(reader));
                    AddContentPart(parts, "excludedAssemblyCount", reader.ReadInt32().ToString(CultureInfo.InvariantCulture));
                    AddContentPart(parts, "excludedSourceFileCount", reader.ReadInt32().ToString(CultureInfo.InvariantCulture));
                    if (schemaVersion != SnapshotSchemaVersion)
                    {
                        return null;
                    }

                    var assemblyCount = reader.ReadInt32();
                    AddContentPart(parts, "assemblyCount", assemblyCount.ToString(CultureInfo.InvariantCulture));
                    for (var i = 0; i < assemblyCount; i++)
                    {
                        AddAssemblyContentParts(parts, reader, i);
                    }

                    return ComputeHash(parts);
                }
            }
            catch
            {
                return null;
            }
        }

        private static void AddAssemblyContentParts(List<string> parts, BinaryReader reader, int index)
        {
            AddContentPart(parts, "assembly[" + index + "].assemblyName", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].assemblyId", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].snapshotFile", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].nameIndexFile", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].tokenIndexFile", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].outputPath", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].asmdefPath", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].languageVersion", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].allowUnsafe", reader.ReadBoolean() ? "true" : "false");
            AddContentPart(parts, "assembly[" + index + "].sourceFileCount", reader.ReadInt32().ToString(CultureInfo.InvariantCulture));
            AddContentPart(parts, "assembly[" + index + "].referenceCount", reader.ReadInt32().ToString(CultureInfo.InvariantCulture));
            AddContentPart(parts, "assembly[" + index + "].definesHash", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].sourcesHash", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].referencesHash", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].compilerOptionsHash", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].assemblyHash", ReadSnapshotString(reader));
            AddContentPart(parts, "assembly[" + index + "].lastWriteTimeTicks", reader.ReadInt64().ToString(CultureInfo.InvariantCulture));
            AddStringListContentParts(parts, "assembly[" + index + "].dependencyAssemblyIds", ReadSnapshotStringList(reader));
            AddStringListContentParts(parts, "assembly[" + index + "].reverseDependencyAssemblyIds", ReadSnapshotStringList(reader));
        }

        private static List<string> ReadSnapshotStringList(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            var result = new List<string>(Math.Max(0, count));
            for (var i = 0; i < count; i++)
            {
                result.Add(ReadSnapshotString(reader));
            }

            return result;
        }

        private static string ReadSnapshotString(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length < 0)
            {
                return null;
            }

            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private static void AddStringListContentParts(List<string> parts, string name, List<string> values)
        {
            var count = values == null ? 0 : values.Count;
            AddContentPart(parts, name + ".count", count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; values != null && i < values.Count; i++)
            {
                AddContentPart(parts, name + "[" + i + "]", values[i]);
            }
        }

        private static void AddContentPart(List<string> parts, string name, string value)
        {
            parts.Add(name + "=" + (value ?? string.Empty));
        }

        private static string ComputeHash(IEnumerable<string> values)
        {
            var builder = new StringBuilder();
            if (values != null)
            {
                foreach (var value in values)
                {
                    builder.Append(value ?? string.Empty).Append('\n');
                }
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var result = new StringBuilder(bytes.Length * 2);
                for (var i = 0; i < bytes.Length; i++)
                {
                    result.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return result.ToString();
            }
        }

        private static JObject BuildFallback(CodeIndexContext context, string action, Dictionary<string, string> options, string warning)
        {
            var query = ResolveFallbackQuery(context, action, options);
            if (string.IsNullOrWhiteSpace(query))
            {
                return BuildFailure(context, "Unable to determine fallback text query.");
            }

            string source;
            var items = TryRunRg(context.ProjectRoot, query, out source);
            if (items == null)
            {
                source = "text-fallback";
                items = RunTextFallback(context.ProjectRoot, query);
            }

            return new JObject
            {
                ["success"] = true,
                ["enabled"] = context.Enabled,
                ["semantic"] = false,
                ["source"] = source,
                ["state"] = "fallback",
                ["stale"] = true,
                ["projectRoot"] = context.ProjectRoot,
                ["solution"] = context.SolutionPath,
                ["workspaceMode"] = "unity-snapshot",
                ["snapshotExists"] = File.Exists(context.SnapshotManifestPath),
                ["warning"] = string.IsNullOrWhiteSpace(warning) ? "Unity snapshot workspace is not ready. Returned text-search candidates only." : warning,
                ["items"] = JArray.FromObject(items, JsonSerializer.Create(JsonSettings))
            };
        }

        private static List<CodeIndexTextItem> TryRunRg(string projectRoot, string query, out string source)
        {
            source = "rg-fallback";
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "rg",
                    WorkingDirectory = projectRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("--line-number");
                startInfo.ArgumentList.Add("--column");
                startInfo.ArgumentList.Add("--no-messages");
                startInfo.ArgumentList.Add("--glob");
                startInfo.ArgumentList.Add("*.cs");
                startInfo.ArgumentList.Add("--fixed-strings");
                startInfo.ArgumentList.Add("--");
                startInfo.ArgumentList.Add(query);
                startInfo.ArgumentList.Add(".");

                var process = Process.Start(startInfo);
                if (process == null)
                {
                    return null;
                }

                var items = new List<CodeIndexTextItem>();
                while (items.Count < 100 && !process.StandardOutput.EndOfStream)
                {
                    CodeIndexTextItem item;
                    if (TryParseRgLine(process.StandardOutput.ReadLine(), query, out item))
                    {
                        items.Add(item);
                    }
                }

                if (items.Count >= 100 && !process.HasExited)
                {
                    process.Kill();
                }

                var exited = process.WaitForExit(5000);
                if (!exited && !process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }

                var exitCode = process.HasExited ? process.ExitCode : -1;
                if (exitCode != 0 && items.Count == 0)
                {
                    process.Dispose();
                    return new List<CodeIndexTextItem>();
                }

                process.Dispose();
                return items;
            }
            catch
            {
                source = "text-fallback";
                return null;
            }
        }

        private static List<CodeIndexTextItem> ParseRgOutput(string output, string query)
        {
            var items = new List<CodeIndexTextItem>();
            if (string.IsNullOrEmpty(output))
            {
                return items;
            }

            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (items.Count >= 100)
                {
                    break;
                }

                CodeIndexTextItem item;
                if (TryParseRgLine(line, query, out item))
                {
                    items.Add(item);
                }
            }

            return items;
        }

        private static bool TryParseRgLine(string line, string query, out CodeIndexTextItem item)
        {
            item = null;
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }

            var first = line.IndexOf(':');
            var second = first < 0 ? -1 : line.IndexOf(':', first + 1);
            var third = second < 0 ? -1 : line.IndexOf(':', second + 1);
            if (first <= 0 || second <= first || third <= second)
            {
                return false;
            }

            int row;
            int column;
            int.TryParse(line.Substring(first + 1, second - first - 1), out row);
            int.TryParse(line.Substring(second + 1, third - second - 1), out column);
            item = new CodeIndexTextItem
            {
                kind = "text",
                name = query,
                file = NormalizePath(line.Substring(0, first)),
                line = row,
                column = column,
                preview = TrimPreview(line.Substring(third + 1))
            };
            return true;
        }

        private static List<CodeIndexTextItem> RunTextFallback(string projectRoot, string query)
        {
            var items = new List<CodeIndexTextItem>();
            foreach (var file in EnumerateFallbackFiles(projectRoot))
            {
                if (items.Count >= 100)
                {
                    break;
                }

                if (ShouldSkipFallbackFile(projectRoot, file))
                {
                    continue;
                }

                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    var column = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (column < 0)
                    {
                        continue;
                    }

                    items.Add(new CodeIndexTextItem
                    {
                        kind = "text",
                        name = query,
                        file = NormalizePath(MakeRelativePath(projectRoot, file)),
                        line = lineNumber,
                        column = column + 1,
                        preview = TrimPreview(line)
                    });

                    if (items.Count >= 100)
                    {
                        break;
                    }
                }
            }

            return items;
        }

        private static IEnumerable<string> EnumerateFallbackFiles(string projectRoot)
        {
            var pending = new Stack<string>();
            pending.Push(projectRoot);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if (ShouldSkipFallbackDirectory(projectRoot, directory))
                {
                    continue;
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    files = Array.Empty<string>();
                }

                foreach (var file in files)
                {
                    yield return file;
                }

                IEnumerable<string> directories;
                try
                {
                    directories = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    directories = Array.Empty<string>();
                }

                foreach (var child in directories)
                {
                    if (!ShouldSkipFallbackDirectory(projectRoot, child))
                    {
                        pending.Push(child);
                    }
                }
            }
        }

        private static bool ShouldSkipFallbackFile(string projectRoot, string file)
        {
            var relative = MakeRelativePath(projectRoot, file).Replace('\\', '/');
            return relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("Library/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("Temp/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith(".aibridge/code-index/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldSkipFallbackDirectory(string projectRoot, string directory)
        {
            var relative = MakeRelativePath(projectRoot, directory).Replace('\\', '/').TrimEnd('/');
            return string.Equals(relative, ".git", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, "Library", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, "Temp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, "obj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, ".aibridge/code-index", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("Library/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("Temp/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith(".aibridge/code-index/", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveFallbackQuery(CodeIndexContext context, string action, Dictionary<string, string> options)
        {
            if (string.Equals(action, "symbol", StringComparison.OrdinalIgnoreCase)
                && options.TryGetValue("query", out var query))
            {
                return query;
            }

            if ((string.Equals(action, "implementations", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "derived", StringComparison.OrdinalIgnoreCase))
                && options.TryGetValue("type", out var type))
            {
                var trimmedType = type == null ? null : type.Trim();
                var lastDot = string.IsNullOrEmpty(trimmedType) ? -1 : trimmedType.LastIndexOf('.');
                return lastDot >= 0 && lastDot + 1 < trimmedType.Length
                    ? trimmedType.Substring(lastDot + 1)
                    : trimmedType;
            }

            if (!options.TryGetValue("file", out var file)
                || !options.TryGetValue("line", out var lineText)
                || !options.TryGetValue("column", out var columnText)
                || !int.TryParse(lineText, out var line)
                || !int.TryParse(columnText, out var column))
            {
                return null;
            }

            var fullPath = Path.IsPathRooted(file) ? file : Path.Combine(context.ProjectRoot, file);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            var sourceLine = File.ReadLines(fullPath).Skip(Math.Max(0, line - 1)).FirstOrDefault();
            if (string.IsNullOrEmpty(sourceLine))
            {
                return null;
            }

            var index = Math.Max(0, Math.Min(column - 1, Math.Max(0, sourceLine.Length - 1)));
            if (!IsIdentifierChar(sourceLine[index]))
            {
                return null;
            }

            var start = index;
            while (start > 0 && IsIdentifierChar(sourceLine[start - 1]))
            {
                start--;
            }

            var end = index;
            while (end + 1 < sourceLine.Length && IsIdentifierChar(sourceLine[end + 1]))
            {
                end++;
            }

            return sourceLine.Substring(start, end - start + 1);
        }

        private static bool IsIdentifierChar(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_';
        }

        private static async Task<JObject> TryGetRemoteStatusAsync(JObject status, int timeout)
        {
            if (status == null)
            {
                return null;
            }

            var endpoint = status.Value<string>("endpoint");
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return null;
            }

            try
            {
                return await GetJsonAsync(endpoint, "status", status.Value<string>("token"), Math.Min(timeout, 1500));
            }
            catch
            {
                return null;
            }
        }

        private static async Task<JObject> GetJsonAsync(string endpoint, string path, string token, int timeout)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMilliseconds(Math.Max(500, timeout));
                var request = new HttpRequestMessage(HttpMethod.Get, CombineUrl(endpoint, path));
                AddToken(request, token);
                var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                return string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            }
        }

        private static async Task<JObject> PostJsonAsync(string endpoint, string path, string token, JObject payload, int timeout)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMilliseconds(Math.Max(500, timeout));
                var request = new HttpRequestMessage(HttpMethod.Post, CombineUrl(endpoint, path));
                AddToken(request, token);
                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                return string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            }
        }

        private static void AddToken(HttpRequestMessage request, string token)
        {
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.TryAddWithoutValidation("X-AIBridge-CodeIndex-Token", token);
            }
        }

        private static string CombineUrl(string endpoint, string path)
        {
            return endpoint.TrimEnd('/') + "/" + path.TrimStart('/');
        }

        private static JObject ReadStatusFile(CodeIndexContext context)
        {
            if (!File.Exists(context.StatusPath))
            {
                return null;
            }

            try
            {
                return JObject.Parse(File.ReadAllText(context.StatusPath, Encoding.UTF8));
            }
            catch
            {
                return null;
            }
        }

        private static void CopyStatusRuntimeFields(JObject status, JObject remote)
        {
            if (status == null || remote == null)
            {
                return;
            }

            remote["endpoint"] = status.Value<string>("endpoint");
            remote["token"] = status.Value<string>("token");
            remote["daemonPid"] = status.Value<int?>("daemonPid") ?? 0;
            remote["ownerPid"] = status.Value<int?>("ownerPid") ?? 0;
            remote["ownerStartTicks"] = status.Value<long?>("ownerStartTicks") ?? 0L;
            remote["ownerAlive"] = status.Value<bool?>("ownerAlive") ?? false;
            remote["ownerMonitorMode"] = status.Value<string>("ownerMonitorMode");
        }

        private static bool IsReady(CodeIndexContext context, JObject status)
        {
            return status != null
                   && string.Equals(status.Value<string>("state"), "ready", StringComparison.OrdinalIgnoreCase)
                   && HasSemanticSnapshotContent(context, status);
        }

        private static bool HasSemanticSnapshotContent(CodeIndexContext context, JObject status)
        {
            var counts = ReadSnapshotSemanticCounts(context);
            if (counts.Exists)
            {
                return counts.HasSemanticContent;
            }

            return HasSemanticSnapshotContent(status);
        }

        private static bool HasSemanticSnapshotContent(JObject status)
        {
            if (status == null)
            {
                return false;
            }

            return (status.Value<int?>("assemblyCount") ?? 0) > 0
                   && (status.Value<int?>("sourceFileCount") ?? 0) > 0;
        }

        private static int ResolveAssemblyCount(JObject status, SnapshotSemanticCounts snapshotCounts)
        {
            if (snapshotCounts.Exists && !snapshotCounts.Unreadable)
            {
                return snapshotCounts.AssemblyCount;
            }

            return status?.Value<int?>("assemblyCount") ?? 0;
        }

        private static int ResolveSourceFileCount(JObject status, SnapshotSemanticCounts snapshotCounts)
        {
            if (snapshotCounts.Exists && !snapshotCounts.Unreadable)
            {
                return snapshotCounts.SourceFileCount;
            }

            return status?.Value<int?>("sourceFileCount") ?? 0;
        }

        private static SnapshotSemanticCounts ReadSnapshotSemanticCounts(CodeIndexContext context)
        {
            if (context == null || !File.Exists(context.SnapshotManifestPath))
            {
                return SnapshotSemanticCounts.Missing;
            }

            var binaryCounts = TryReadSnapshotSemanticCountsBinary(context.SnapshotManifestPath);
            if (binaryCounts.Exists && !binaryCounts.Unreadable)
            {
                return binaryCounts;
            }

            var jsonCounts = TryReadSnapshotSemanticCountsJson(context.SnapshotJsonPath);
            if (jsonCounts.Exists && !jsonCounts.Unreadable)
            {
                return jsonCounts;
            }

            return binaryCounts.Exists ? binaryCounts : jsonCounts;
        }

        private static SnapshotSemanticCounts TryReadSnapshotSemanticCountsJson(string manifestJsonPath)
        {
            if (string.IsNullOrWhiteSpace(manifestJsonPath) || !File.Exists(manifestJsonPath))
            {
                return SnapshotSemanticCounts.Missing;
            }

            try
            {
                var json = JObject.Parse(File.ReadAllText(manifestJsonPath, Encoding.UTF8));
                var assemblyCount = json.Value<int?>("assemblyCount");
                var sourceFileCount = json.Value<int?>("sourceFileCount");
                if (!assemblyCount.HasValue || !sourceFileCount.HasValue)
                {
                    return SnapshotSemanticCounts.UnreadableSnapshot;
                }

                return SnapshotSemanticCounts.FromCounts(assemblyCount.Value, sourceFileCount.Value);
            }
            catch
            {
                return SnapshotSemanticCounts.UnreadableSnapshot;
            }
        }

        private static SnapshotSemanticCounts TryReadSnapshotSemanticCountsBinary(string manifestPath)
        {
            try
            {
                using (var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    var magic = Encoding.ASCII.GetString(reader.ReadBytes(SnapshotMagic.Length));
                    var headerSchema = reader.ReadInt32();
                    var formatKind = reader.ReadInt32();
                    reader.ReadInt64();
                    if (!string.Equals(magic, SnapshotMagic, StringComparison.Ordinal)
                        || headerSchema != SnapshotSchemaVersion
                        || formatKind != ManifestFormatKind)
                    {
                        return SnapshotSemanticCounts.UnreadableSnapshot;
                    }

                    var schemaVersion = reader.ReadInt32();
                    ReadSnapshotString(reader);
                    ReadSnapshotString(reader);
                    ReadSnapshotString(reader);
                    ReadSnapshotString(reader);
                    reader.ReadBoolean();
                    ReadSnapshotStringList(reader);
                    ReadSnapshotStringList(reader);
                    ReadSnapshotString(reader);
                    reader.ReadInt32();
                    reader.ReadInt32();
                    if (schemaVersion != SnapshotSchemaVersion)
                    {
                        return SnapshotSemanticCounts.UnreadableSnapshot;
                    }

                    var assemblyCount = reader.ReadInt32();
                    var sourceFileCount = 0;
                    for (var i = 0; i < assemblyCount; i++)
                    {
                        SkipSnapshotAssemblyRecord(reader, out var assemblySourceCount);
                        sourceFileCount += assemblySourceCount;
                    }

                    return SnapshotSemanticCounts.FromCounts(assemblyCount, sourceFileCount);
                }
            }
            catch
            {
                return SnapshotSemanticCounts.UnreadableSnapshot;
            }
        }

        private static void SkipSnapshotAssemblyRecord(BinaryReader reader, out int sourceFileCount)
        {
            ReadSnapshotString(reader);
            ReadSnapshotString(reader);
            ReadSnapshotString(reader);
            ReadSnapshotString(reader);
            ReadSnapshotString(reader);
            ReadSnapshotString(reader);
            ReadSnapshotString(reader);
            ReadSnapshotString(reader);
            reader.ReadBoolean();
            sourceFileCount = reader.ReadInt32();
            reader.ReadInt32();
            ReadSnapshotString(reader);
            ReadSnapshotString(reader);
            ReadSnapshotString(reader);
            ReadSnapshotString(reader);
            ReadSnapshotString(reader);
            reader.ReadInt64();
            ReadSnapshotStringList(reader);
            ReadSnapshotStringList(reader);
        }

        private sealed class SnapshotSemanticCounts
        {
            public static readonly SnapshotSemanticCounts Missing = new SnapshotSemanticCounts(false, false, 0, 0);
            public static readonly SnapshotSemanticCounts UnreadableSnapshot = new SnapshotSemanticCounts(true, true, 0, 0);

            private SnapshotSemanticCounts(bool exists, bool unreadable, int assemblyCount, int sourceFileCount)
            {
                Exists = exists;
                Unreadable = unreadable;
                AssemblyCount = assemblyCount;
                SourceFileCount = sourceFileCount;
            }

            public bool Exists { get; private set; }
            public bool Unreadable { get; private set; }
            public int AssemblyCount { get; private set; }
            public int SourceFileCount { get; private set; }
            public bool HasSemanticContent
            {
                get { return AssemblyCount > 0 && SourceFileCount > 0; }
            }

            public static SnapshotSemanticCounts FromCounts(int assemblyCount, int sourceFileCount)
            {
                return new SnapshotSemanticCounts(true, false, assemblyCount, sourceFileCount);
            }
        }

        private static bool IsFallbackEnabled(CodeIndexContext context, Dictionary<string, string> options)
        {
            if (options.TryGetValue("fallback", out var value))
            {
                return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase);
            }

            return context == null || context.FallbackToTextSearch;
        }

        private static bool ResolveOptionBool(Dictionary<string, string> options, string key, bool defaultValue)
        {
            if (options == null || !options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase);
        }

        private static int ResolveOptionInt(Dictionary<string, string> options, string key, int defaultValue)
        {
            if (options == null || !options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return int.TryParse(value, out var result) ? result : defaultValue;
        }

        private static string ResolveStringOption(Dictionary<string, string> options, string key, string defaultValue)
        {
            if (options == null || !options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return value;
        }

        private static int ClampTimeout(int timeout)
        {
            return Math.Min(MaxCodeIndexTimeoutMs, Math.Max(1000, timeout));
        }

        private static Dictionary<string, object> BuildQueryParameters(string action, Dictionary<string, string> options)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.Equals(action, "symbol", StringComparison.OrdinalIgnoreCase))
            {
                result["query"] = options["query"];
                return result;
            }

            if (string.Equals(action, "implementations", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "derived", StringComparison.OrdinalIgnoreCase))
            {
                result["type"] = options["type"];
                return result;
            }

            if (string.Equals(action, "diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                if (options.TryGetValue("file", out var file) && !string.IsNullOrWhiteSpace(file))
                {
                    result["file"] = file;
                }

                result["all"] = ResolveOptionBool(options, "all", false);
                return result;
            }

            result["file"] = options["file"];
            result["line"] = int.Parse(options["line"]);
            result["column"] = int.Parse(options["column"]);
            return result;
        }

        private static void ValidateQueryArguments(string action, Dictionary<string, string> options)
        {
            if (string.Equals(action, "symbol", StringComparison.OrdinalIgnoreCase))
            {
                if (!options.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
                {
                    throw new ArgumentException("Missing required parameter: --query");
                }

                return;
            }

            if (string.Equals(action, "implementations", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "derived", StringComparison.OrdinalIgnoreCase))
            {
                if (!options.TryGetValue("type", out var type) || string.IsNullOrWhiteSpace(type))
                {
                    throw new ArgumentException("Missing required parameter: --type");
                }

                return;
            }

            if (string.Equals(action, "diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                var hasFile = options.TryGetValue("file", out var file) && !string.IsNullOrWhiteSpace(file);
                var all = ResolveOptionBool(options, "all", false);
                if (!hasFile && !all)
                {
                    throw new ArgumentException("Missing required parameter: --file. Use --all true to run full workspace diagnostics.");
                }

                return;
            }

            foreach (var key in new[] { "file", "line", "column" })
            {
                if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("Missing required parameter: --" + key);
                }
            }
        }

        private static JObject BuildFailure(CodeIndexContext context, string error)
        {
            return BuildFailure(context, error, null);
        }

        private static JObject BuildDaemonUnavailableFailure(CodeIndexContext context, CodeIndexDaemonExecutable daemon)
        {
            var result = BuildFailure(
                context,
                daemon == null ? "AIBridgeCodeIndex daemon executable or project was not found." : daemon.DiagnosticMessage,
                daemon != null && daemon.HasMissingFiles ? "code_index_daemon_incomplete" : "code_index_daemon_missing");
            AddDaemonDiagnosticFields(result, daemon);
            return result;
        }

        private static void AddDaemonDiagnosticFields(JObject result, CodeIndexDaemonExecutable daemon)
        {
            if (result == null || daemon == null)
            {
                return;
            }

            result["daemon"] = daemon.DisplayPath;
            result["daemonLaunchMode"] = daemon.LaunchMode;
            result["daemonReady"] = daemon.CanStart;
            if (daemon.HasMissingFiles)
            {
                result["daemonMissingFiles"] = CreateStringArray(daemon.MissingFiles);
            }
        }

        private static JArray CreateStringArray(IEnumerable<string> values)
        {
            var array = new JArray();
            if (values == null)
            {
                return array;
            }

            foreach (var value in values)
            {
                array.Add(value);
            }

            return array;
        }

        private static JObject BuildFailure(CodeIndexContext context, string error, string errorCode)
        {
            var result = new JObject
            {
                ["success"] = false,
                ["semantic"] = false,
                ["source"] = "code_index",
                ["state"] = "failed",
                ["stale"] = true,
                ["projectRoot"] = context == null ? null : context.ProjectRoot,
                ["solution"] = context == null ? null : context.SolutionPath,
                ["workspaceMode"] = "unity-snapshot",
                ["snapshotExists"] = context != null && File.Exists(context.SnapshotManifestPath),
                ["error"] = error
            };

            if (!string.IsNullOrWhiteSpace(errorCode))
            {
                result["errorCode"] = errorCode;
            }

            return result;
        }

        private static void DeleteFileIfExists(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        private static void Print(JObject result, OutputMode outputMode)
        {
            if (outputMode == OutputMode.Quiet && !result.Value<bool>("success"))
            {
                Console.Error.WriteLine(result.Value<string>("error") ?? "code_index failed");
                return;
            }

            var formatting = outputMode == OutputMode.Pretty ? Formatting.Indented : Formatting.None;
            Console.WriteLine(JsonConvert.SerializeObject(result, formatting, JsonSettings));
        }

        private static string MakeRelativePath(string root, string path)
        {
            var rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var fileUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').TrimStart('.', '/');
        }

        private static string TrimPreview(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            var trimmed = line.Trim();
            return trimmed.Length <= 300 ? trimmed : trimmed.Substring(0, 300);
        }

        private sealed class CodeIndexContext
        {
            public string ProjectRoot { get; private set; }
            public string SolutionPath { get; private set; }
            public string IndexDirectory { get; private set; }
            public string SnapshotDirectory { get; private set; }
            public string SnapshotManifestPath { get; private set; }
            public string SnapshotJsonPath { get; private set; }
            public string StatusPath { get; private set; }
            public string DaemonProcessPath { get; private set; }
            public string DaemonProcessDirectory { get; private set; }
            public string DaemonLaunchLockPath { get; private set; }
            public bool HasUnityProjectMarkers { get; private set; }
            public int UnityPid { get; private set; }
            public int OwnerPid { get; private set; }
            public long OwnerStartTicks { get; private set; }
            public bool Enabled { get; private set; }
            public bool AutoRefresh { get; private set; }
            public string WarmupMode { get; private set; }
            public string ProcessPriority { get; private set; }
            public bool FallbackToTextSearch { get; private set; }
            public bool IncludeSnapshotOnReset { get; private set; }

            public static CodeIndexContext Resolve(Dictionary<string, string> options)
            {
                var projectRoot = ResolveProjectRoot(options);
                var solutionPath = ResolveSolutionPath(projectRoot);
                var indexDirectory = Path.Combine(projectRoot, ".aibridge", IndexDirectoryName);
                var snapshotDirectory = Path.Combine(indexDirectory, SnapshotDirectoryName);
                var unityPid = ResolveInt(options, "unity-pid");
                var ownerPid = ResolveInt(options, "owner-pid");
                var ownerStartTicks = ResolveLong(options, "owner-start-ticks");
                if (ownerPid <= 0 && unityPid > 0)
                {
                    ownerPid = unityPid;
                }

                var config = ReadCodeIndexConfig(indexDirectory);
                return new CodeIndexContext
                {
                    ProjectRoot = projectRoot,
                    SolutionPath = solutionPath,
                    IndexDirectory = indexDirectory,
                    SnapshotDirectory = snapshotDirectory,
                    SnapshotManifestPath = Path.Combine(snapshotDirectory, "manifest.bin"),
                    SnapshotJsonPath = Path.Combine(snapshotDirectory, "manifest.json"),
                    StatusPath = Path.Combine(indexDirectory, "status.json"),
                    DaemonProcessPath = Path.Combine(indexDirectory, DaemonProcessFileName),
                    DaemonProcessDirectory = Path.Combine(indexDirectory, DaemonProcessDirectoryName),
                    DaemonLaunchLockPath = Path.Combine(indexDirectory, DaemonLaunchLockFileName),
                    UnityPid = unityPid,
                    OwnerPid = ownerPid,
                    OwnerStartTicks = ownerStartTicks,
                    Enabled = GetConfigBool(config, "enableCodeIndex", false),
                    AutoRefresh = ResolveBool(options, "auto-refresh", GetConfigBool(config, "autoRefreshOnFileChange", true)),
                    WarmupMode = ResolveString(options, "warmup-mode", GetConfigString(config, "warmupMode", "semantic")),
                    ProcessPriority = ResolveString(options, "priority", "normal"),
                    FallbackToTextSearch = ResolveBool(options, "fallback", GetConfigBool(config, "fallbackToTextSearch", true)),
                    IncludeSnapshotOnReset = ResolveBool(options, "include-snapshot", false),
                    HasUnityProjectMarkers = Directory.Exists(Path.Combine(projectRoot, "Assets"))
                                             && File.Exists(Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset"))
                };
            }

            private static JObject ReadCodeIndexConfig(string indexDirectory)
            {
                if (string.IsNullOrWhiteSpace(indexDirectory))
                {
                    return null;
                }

                var path = Path.Combine(indexDirectory, "config.json");
                if (!File.Exists(path))
                {
                    return null;
                }

                try
                {
                    return JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                }
                catch
                {
                    return null;
                }
            }

            private static bool GetConfigBool(JObject config, string key, bool defaultValue)
            {
                return config == null ? defaultValue : config.Value<bool?>(key) ?? defaultValue;
            }

            private static string GetConfigString(JObject config, string key, string defaultValue)
            {
                var value = config == null ? null : config.Value<string>(key);
                return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
            }

            private static int ResolveInt(Dictionary<string, string> options, string key)
            {
                if (options == null || !options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    return 0;
                }

                int.TryParse(value, out var result);
                return result;
            }

            private static long ResolveLong(Dictionary<string, string> options, string key)
            {
                if (options == null || !options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    return 0L;
                }

                long.TryParse(value, out var result);
                return result;
            }

            private static bool ResolveBool(Dictionary<string, string> options, string key, bool defaultValue)
            {
                if (options == null || !options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    return defaultValue;
                }

                return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase);
            }

            private static string ResolveString(Dictionary<string, string> options, string key, string defaultValue)
            {
                if (options == null || !options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    return defaultValue;
                }

                return value.Trim();
            }

            private static string ResolveProjectRoot(Dictionary<string, string> options)
            {
                if (options.TryGetValue("project-root", out var explicitRoot) && !string.IsNullOrWhiteSpace(explicitRoot))
                {
                    return Path.GetFullPath(explicitRoot);
                }

                var environmentRoot = Environment.GetEnvironmentVariable("UNITY_PROJECT_ROOT");
                if (!string.IsNullOrWhiteSpace(environmentRoot))
                {
                    return Path.GetFullPath(environmentRoot);
                }

                var unityRoot = FindUnityProjectRoot(Directory.GetCurrentDirectory());
                return string.IsNullOrWhiteSpace(unityRoot)
                    ? Directory.GetCurrentDirectory()
                    : unityRoot;
            }

            private static string ResolveSolutionPath(string projectRoot)
            {
                var solutions = Directory.GetFiles(projectRoot, "*.sln", SearchOption.TopDirectoryOnly);
                if (solutions.Length == 0)
                {
                    return null;
                }

                var projectName = new DirectoryInfo(projectRoot).Name;
                var matching = solutions.FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), projectName, StringComparison.OrdinalIgnoreCase));
                return matching ?? solutions.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }

            private static string FindUnityProjectRoot(string startDirectory)
            {
                var directory = startDirectory;
                while (!string.IsNullOrEmpty(directory))
                {
                    if (Directory.Exists(Path.Combine(directory, "Assets"))
                        && File.Exists(Path.Combine(directory, "ProjectSettings", "ProjectSettings.asset")))
                    {
                        return directory;
                    }

                    directory = Path.GetDirectoryName(directory);
                }

                return null;
            }
        }

        private sealed class CodeIndexDaemonExecutable
        {
            private static readonly string[] RequiredManagedFiles = new[]
            {
                DaemonAssemblyName + ".dll",
                DaemonAssemblyName + ".deps.json",
                DaemonAssemblyName + ".runtimeconfig.json",
                "Newtonsoft.Json.dll",
                "Microsoft.CodeAnalysis.dll",
                "Microsoft.CodeAnalysis.CSharp.dll",
                "Microsoft.CodeAnalysis.Workspaces.dll",
                "Microsoft.CodeAnalysis.CSharp.Workspaces.dll",
                "System.Composition.AttributedModel.dll",
                "System.Composition.Convention.dll",
                "System.Composition.Hosting.dll",
                "System.Composition.Runtime.dll",
                "System.Composition.TypedParts.dll"
            };

            private enum DaemonLaunchMode
            {
                Executable,
                Dll,
                SourceProject
            }

            private DaemonLaunchMode _mode;
            private string _path;
            private string[] _missingFiles;

            public bool CanStart { get { return !string.IsNullOrEmpty(_path) && !HasMissingFiles; } }
            public string DisplayPath { get { return _path; } }
            public string LaunchMode { get { return _mode.ToString(); } }
            public string[] MissingFiles { get { return _missingFiles ?? Array.Empty<string>(); } }
            public bool HasMissingFiles { get { return MissingFiles.Length > 0; } }
            public string DiagnosticMessage
            {
                get
                {
                    if (HasMissingFiles)
                    {
                        return "AIBridgeCodeIndex published directory is incomplete. Missing: " + string.Join(", ", MissingFiles);
                    }

                    return "AIBridgeCodeIndex daemon executable or project was not found.";
                }
            }

            public bool UseShellExecuteForDetachedLaunch
            {
                get
                {
                    return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                           && _mode == DaemonLaunchMode.Executable;
                }
            }

            public static CodeIndexDaemonExecutable Resolve()
            {
                var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? DaemonAssemblyName + ".exe"
                    : DaemonAssemblyName;
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var executablePath = Path.Combine(baseDirectory, DaemonDirectoryName, executableName);
                if (File.Exists(executablePath))
                {
                    return new CodeIndexDaemonExecutable
                    {
                        _mode = DaemonLaunchMode.Executable,
                        _path = executablePath,
                        _missingFiles = GetMissingPublishedFiles(Path.GetDirectoryName(executablePath), executableName)
                    };
                }

                var dllPath = Path.Combine(baseDirectory, DaemonDirectoryName, DaemonAssemblyName + ".dll");
                if (File.Exists(dllPath))
                {
                    return new CodeIndexDaemonExecutable
                    {
                        _mode = DaemonLaunchMode.Dll,
                        _path = dllPath,
                        _missingFiles = GetMissingPublishedFiles(Path.GetDirectoryName(dllPath), null)
                    };
                }

                var sourceProject = FindSourceProject(baseDirectory) ?? FindSourceProject(Directory.GetCurrentDirectory());
                return new CodeIndexDaemonExecutable { _mode = DaemonLaunchMode.SourceProject, _path = sourceProject };
            }

            public ProcessStartInfo CreateStartInfo()
            {
                if (_mode == DaemonLaunchMode.Executable)
                {
                    return new ProcessStartInfo { FileName = _path };
                }

                var startInfo = new ProcessStartInfo { FileName = "dotnet" };
                if (_mode == DaemonLaunchMode.Dll)
                {
                    startInfo.ArgumentList.Add(_path);
                }
                else
                {
                    startInfo.ArgumentList.Add("run");
                    startInfo.ArgumentList.Add("--project");
                    startInfo.ArgumentList.Add(_path);
                    startInfo.ArgumentList.Add("--no-launch-profile");
                    startInfo.ArgumentList.Add("--");
                }

                return startInfo;
            }

            public void AddLaunchArguments(ProcessStartInfo startInfo, CodeIndexContext context, string token)
            {
                startInfo.ArgumentList.Add("--project-root");
                startInfo.ArgumentList.Add(context.ProjectRoot);
                startInfo.ArgumentList.Add("--status-path");
                startInfo.ArgumentList.Add(context.StatusPath);
                startInfo.ArgumentList.Add("--token");
                startInfo.ArgumentList.Add(token);

                if (context.UnityPid > 0)
                {
                    startInfo.ArgumentList.Add("--unity-pid");
                    startInfo.ArgumentList.Add(context.UnityPid.ToString());
                }

                if (context.OwnerPid > 0)
                {
                    startInfo.ArgumentList.Add("--owner-pid");
                    startInfo.ArgumentList.Add(context.OwnerPid.ToString(CultureInfo.InvariantCulture));
                }

                if (context.OwnerStartTicks > 0L)
                {
                    startInfo.ArgumentList.Add("--owner-start-ticks");
                    startInfo.ArgumentList.Add(context.OwnerStartTicks.ToString(CultureInfo.InvariantCulture));
                }

                startInfo.ArgumentList.Add("--auto-refresh");
                startInfo.ArgumentList.Add(context.AutoRefresh ? "true" : "false");
                startInfo.ArgumentList.Add("--warmup-mode");
                startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(context.WarmupMode) ? "semantic" : context.WarmupMode);
            }

            public void AddSnapshotWorkerArguments(ProcessStartInfo startInfo, CodeIndexContext context, string inputPath, int workers)
            {
                startInfo.ArgumentList.Add("--worker");
                startInfo.ArgumentList.Add("snapshot");
                startInfo.ArgumentList.Add("--input");
                startInfo.ArgumentList.Add(inputPath);
                startInfo.ArgumentList.Add("--project-root");
                startInfo.ArgumentList.Add(context.ProjectRoot);
                startInfo.ArgumentList.Add("--priority");
                startInfo.ArgumentList.Add(context.ProcessPriority);

                if (context.OwnerPid > 0)
                {
                    startInfo.ArgumentList.Add("--owner-pid");
                    startInfo.ArgumentList.Add(context.OwnerPid.ToString(CultureInfo.InvariantCulture));
                }

                if (context.OwnerStartTicks > 0L)
                {
                    startInfo.ArgumentList.Add("--owner-start-ticks");
                    startInfo.ArgumentList.Add(context.OwnerStartTicks.ToString(CultureInfo.InvariantCulture));
                }

                if (workers > 0)
                {
                    startInfo.ArgumentList.Add("--workers");
                    startInfo.ArgumentList.Add(workers.ToString(CultureInfo.InvariantCulture));
                }
            }

            private static string FindSourceProject(string startDirectory)
            {
                var directory = startDirectory;
                while (!string.IsNullOrEmpty(directory))
                {
                    var candidate = Path.Combine(directory, "Tools~", "AIBridgeCodeIndex", "AIBridgeCodeIndex.csproj");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    if (string.Equals(Path.GetFileName(directory), "Tools~", StringComparison.OrdinalIgnoreCase))
                    {
                        candidate = Path.Combine(directory, "AIBridgeCodeIndex", "AIBridgeCodeIndex.csproj");
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }

                    directory = Path.GetDirectoryName(directory);
                }

                return null;
            }

            private static string[] GetMissingPublishedFiles(string directory, string executableName)
            {
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    missing.Add(DaemonDirectoryName);
                    return missing.ToArray();
                }

                if (!string.IsNullOrWhiteSpace(executableName) && !File.Exists(Path.Combine(directory, executableName)))
                {
                    missing.Add(executableName);
                }

                foreach (var fileName in RequiredManagedFiles)
                {
                    if (!File.Exists(Path.Combine(directory, fileName)))
                    {
                        missing.Add(fileName);
                    }
                }

                return missing.ToArray();
            }
        }

        private sealed class CodeIndexTextItem
        {
            public string kind { get; set; }
            public string name { get; set; }
            public string file { get; set; }
            public int line { get; set; }
            public int column { get; set; }
            public string preview { get; set; }
        }

        private sealed class CodeIndexQueryTimeouts
        {
            public int QueueTimeoutMs { get; set; }
            public int ExecuteTimeoutMs { get; set; }
            public int TransportTimeoutMs { get; set; }
        }
    }
}
