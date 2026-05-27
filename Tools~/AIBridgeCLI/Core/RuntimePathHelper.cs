using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Core
{
    public class RuntimeTargetInfo
    {
        public string targetId { get; set; }
        public string path { get; set; }
        public string heartbeatPath { get; set; }
        public string commandsPath { get; set; }
        public string resultsPath { get; set; }
        public string screenshotsPath { get; set; }
        public bool stale { get; set; }
        public double? ageSeconds { get; set; }
        public string lastHeartbeatUtc { get; set; }
        public JObject heartbeat { get; set; }
    }

    public static class RuntimePathHelper
    {
        public const string RuntimeDirectoryName = "runtime";
        public const string TargetsDirectoryName = "targets";
        public const string CommandsDirectoryName = "commands";
        public const string ResultsDirectoryName = "results";
        public const string ScreenshotsDirectoryName = "screenshots";
        public const string HeartbeatFileName = "heartbeat.json";
        public const string RuntimeDirEnvironment = "AIBRIDGE_RUNTIME_DIR";
        private static readonly TimeSpan StaleHeartbeatTimeout = TimeSpan.FromSeconds(15);

        public static string ResolveRuntimeDirectory(string overridePath)
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return Path.GetFullPath(overridePath);
            }

            var envPath = Environment.GetEnvironmentVariable(RuntimeDirEnvironment);
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                return Path.GetFullPath(envPath);
            }

            return Path.Combine(PathHelper.GetExchangeDirectory(), RuntimeDirectoryName);
        }

        public static List<RuntimeTargetInfo> ListTargets(string runtimeDirectory)
        {
            var results = new List<RuntimeTargetInfo>();
            var targetsDirectory = Path.Combine(runtimeDirectory, TargetsDirectoryName);
            if (!Directory.Exists(targetsDirectory))
            {
                return results;
            }

            foreach (var targetPath in Directory.GetDirectories(targetsDirectory))
            {
                var heartbeatPath = Path.Combine(targetPath, HeartbeatFileName);
                var heartbeat = ReadHeartbeat(heartbeatPath);
                var targetId = heartbeat?["targetId"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    targetId = Path.GetFileName(targetPath);
                }

                var lastHeartbeat = ParseHeartbeatTime(heartbeat);
                var ageSeconds = lastHeartbeat.HasValue
                    ? (double?)(DateTime.UtcNow - lastHeartbeat.Value).TotalSeconds
                    : null;

                results.Add(new RuntimeTargetInfo
                {
                    targetId = targetId,
                    path = targetPath,
                    heartbeatPath = heartbeatPath,
                    commandsPath = GetHeartbeatPathOrDefault(heartbeat, "commandsPath", targetPath, CommandsDirectoryName),
                    resultsPath = GetHeartbeatPathOrDefault(heartbeat, "resultsPath", targetPath, ResultsDirectoryName),
                    screenshotsPath = GetHeartbeatPathOrDefault(heartbeat, "screenshotsPath", targetPath, ScreenshotsDirectoryName),
                    stale = !lastHeartbeat.HasValue || DateTime.UtcNow - lastHeartbeat.Value > StaleHeartbeatTimeout,
                    ageSeconds = ageSeconds,
                    lastHeartbeatUtc = lastHeartbeat.HasValue ? lastHeartbeat.Value.ToString("o") : null,
                    heartbeat = heartbeat
                });
            }

            return results
                .OrderBy(t => t.stale)
                .ThenByDescending(t => t.lastHeartbeatUtc)
                .ThenBy(t => t.targetId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static RuntimeTargetInfo ResolveTarget(string runtimeDirectory, string target)
        {
            var targets = ListTargets(runtimeDirectory);
            if (targets.Count == 0)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(target) || string.Equals(target, "latest", StringComparison.OrdinalIgnoreCase))
            {
                return targets[0];
            }

            return targets.FirstOrDefault(t => string.Equals(t.targetId, target, StringComparison.OrdinalIgnoreCase));
        }

        public static bool TryResolveFreshHttpUrl(string runtimeDirectory, string target, out string url)
        {
            url = null;
            var targetInfo = ResolveTarget(runtimeDirectory, target);
            if (targetInfo == null || targetInfo.stale)
            {
                return false;
            }

            var heartbeatUrl = targetInfo.heartbeat?["httpUrl"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(heartbeatUrl))
            {
                return false;
            }

            url = heartbeatUrl.Trim().TrimEnd('/');
            return true;
        }

        public static string GetRuntimeAction(CommandRequest request)
        {
            if (request == null || request.@params == null)
            {
                return null;
            }

            return request.@params.TryGetValue("action", out var actionValue) ? actionValue?.ToString() : null;
        }

        public static string GetCommandPath(RuntimeTargetInfo targetInfo, string commandId)
        {
            return Path.Combine(targetInfo.commandsPath, commandId + ".json");
        }

        public static string GetResultPath(RuntimeTargetInfo targetInfo, string commandId)
        {
            return Path.Combine(targetInfo.resultsPath, commandId + ".json");
        }

        private static string GetHeartbeatPathOrDefault(JObject heartbeat, string heartbeatKey, string targetPath, string directoryName)
        {
            var value = heartbeat?[heartbeatKey]?.Value<string>();
            return string.IsNullOrWhiteSpace(value) ? Path.Combine(targetPath, directoryName) : value;
        }

        private static JObject ReadHeartbeat(string heartbeatPath)
        {
            if (!File.Exists(heartbeatPath))
            {
                return null;
            }

            try
            {
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(heartbeatPath))))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    return JObject.Load(reader);
                }
            }
            catch
            {
                return null;
            }
        }

        private static DateTime? ParseHeartbeatTime(JObject heartbeat)
        {
            var value = heartbeat?["lastHeartbeatUtc"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(value, out var parsedOffset))
            {
                return parsedOffset.UtcDateTime;
            }

            return null;
        }
    }
}
