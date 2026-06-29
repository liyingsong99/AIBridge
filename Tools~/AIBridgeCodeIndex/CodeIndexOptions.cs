using System;
using System.Collections.Generic;
using System.IO;

namespace AIBridgeCodeIndex
{
    internal sealed class CodeIndexOptions
    {
        public string ProjectRoot { get; set; }
        public string StatusPath { get; set; }
        public string Token { get; set; }
        public int UnityPid { get; set; }
        public int OwnerPid { get; set; }
        public long OwnerStartTicks { get; set; }
        public bool AutoRefresh { get; set; }
        public string WarmupMode { get; set; }
        public string Worker { get; set; }
        public string InputPath { get; set; }
        public string Priority { get; set; }
        public int WorkerCount { get; set; }

        public static CodeIndexOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var key = arg.Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    values[key] = args[i + 1];
                    i++;
                }
                else
                {
                    values[key] = "true";
                }
            }

            values.TryGetValue("project-root", out var projectRoot);
            values.TryGetValue("status-path", out var statusPath);
            values.TryGetValue("token", out var token);
            values.TryGetValue("worker", out var worker);
            values.TryGetValue("input", out var inputPath);
            values.TryGetValue("priority", out var priority);
            values.TryGetValue("warmup-mode", out var warmupMode);

            var unityPid = 0;
            if (values.TryGetValue("unity-pid", out var unityPidText))
            {
                int.TryParse(unityPidText, out unityPid);
            }

            var ownerPid = 0;
            if (values.TryGetValue("owner-pid", out var ownerPidText))
            {
                int.TryParse(ownerPidText, out ownerPid);
            }

            var ownerStartTicks = 0L;
            if (values.TryGetValue("owner-start-ticks", out var ownerStartTicksText))
            {
                long.TryParse(ownerStartTicksText, out ownerStartTicks);
            }

            if (ownerPid <= 0 && unityPid > 0)
            {
                ownerPid = unityPid;
            }

            var autoRefresh = true;
            if (values.TryGetValue("auto-refresh", out var autoRefreshText))
            {
                autoRefresh = !string.Equals(autoRefreshText, "false", StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(autoRefreshText, "0", StringComparison.OrdinalIgnoreCase);
            }

            var workerCount = 0;
            if (values.TryGetValue("workers", out var workersText))
            {
                int.TryParse(workersText, out workerCount);
            }

            if (string.IsNullOrWhiteSpace(statusPath) && !string.IsNullOrWhiteSpace(projectRoot))
            {
                statusPath = Path.Combine(projectRoot, ".aibridge", "code-index", "status.json");
            }

            return new CodeIndexOptions
            {
                ProjectRoot = projectRoot,
                StatusPath = statusPath,
                Token = string.IsNullOrWhiteSpace(token) ? Guid.NewGuid().ToString("N") : token,
                UnityPid = unityPid,
                OwnerPid = ownerPid,
                OwnerStartTicks = ownerStartTicks,
                AutoRefresh = autoRefresh,
                WarmupMode = string.IsNullOrWhiteSpace(warmupMode) ? "semantic" : warmupMode,
                Worker = worker,
                InputPath = inputPath,
                Priority = string.IsNullOrWhiteSpace(priority) ? "normal" : priority,
                WorkerCount = workerCount
            };
        }
    }
}
