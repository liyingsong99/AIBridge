using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace AIBridgeCLI.Workflow
{
    public class WorkflowCleanOptions
    {
        public string OlderThan { get; set; } = "30d";
        public bool DryRun { get; set; } = true;
        public bool KeepFailed { get; set; } = true;
        public int KeepLatest { get; set; }
        public int MaxDeletePerRun { get; set; } = 100;
    }

    public class WorkflowCleanResult
    {
        [JsonProperty("dryRun")]
        public bool DryRun { get; set; }

        [JsonProperty("olderThan")]
        public string OlderThan { get; set; }

        [JsonProperty("keepFailed")]
        public bool KeepFailed { get; set; }

        [JsonProperty("keepLatest")]
        public int KeepLatest { get; set; }

        [JsonProperty("maxDeletePerRun")]
        public int MaxDeletePerRun { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("deletedCount")]
        public int DeletedCount { get; set; }

        [JsonProperty("candidates")]
        public List<WorkflowCleanCandidate> Candidates { get; set; } = new List<WorkflowCleanCandidate>();
    }

    public class WorkflowCleanCandidate
    {
        [JsonProperty("runId")]
        public string RunId { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("lastWriteTimeUtc")]
        public string LastWriteTimeUtc { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("deleted")]
        public bool Deleted { get; set; }
    }

    public static class WorkflowCleaner
    {
        public static WorkflowCleanResult Clean(WorkflowCleanOptions options)
        {
            options = NormalizeOptions(options);
            var result = new WorkflowCleanResult
            {
                DryRun = options.DryRun,
                OlderThan = options.OlderThan,
                KeepFailed = options.KeepFailed,
                KeepLatest = options.KeepLatest,
                MaxDeletePerRun = options.MaxDeletePerRun
            };

            var runsDirectory = WorkflowPathHelper.GetRunsDirectory();
            if (!Directory.Exists(runsDirectory))
            {
                return result;
            }

            var cutoff = DateTime.UtcNow - ParseAge(options.OlderThan);
            var runInfos = Directory.GetDirectories(runsDirectory)
                .Select(path => new RunInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ToList();

            var protectedLatest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (options.KeepLatest > 0)
            {
                foreach (var info in runInfos.Take(options.KeepLatest))
                {
                    protectedLatest.Add(info.RunId);
                }
            }

            foreach (var info in runInfos.OrderBy(item => item.LastWriteTimeUtc))
            {
                if (info.LastWriteTimeUtc > cutoff)
                {
                    continue;
                }

                if (protectedLatest.Contains(info.RunId))
                {
                    continue;
                }

                if (options.KeepFailed && IsFailedStatus(info.Status))
                {
                    continue;
                }

                if (!options.DryRun && result.DeletedCount >= options.MaxDeletePerRun)
                {
                    break;
                }

                var candidate = new WorkflowCleanCandidate
                {
                    RunId = info.RunId,
                    Path = WorkflowPathHelper.ToDisplayPath(info.Path),
                    Status = info.Status,
                    LastWriteTimeUtc = info.LastWriteTimeUtc.ToString("o"),
                    Reason = "older-than " + options.OlderThan
                };

                if (!options.DryRun)
                {
                    RemoveRunDirectory(runsDirectory, info.Path);
                    candidate.Deleted = true;
                    result.DeletedCount++;
                }

                result.Candidates.Add(candidate);
            }

            result.Count = result.Candidates.Count;
            return result;
        }

        public static WorkflowCleanResult AutoClean()
        {
            var settings = WorkflowSettings.Load();
            if (!settings.AutoCleanEnabled)
            {
                return null;
            }

            return Clean(new WorkflowCleanOptions
            {
                OlderThan = settings.AutoCleanOlderThan,
                DryRun = false,
                KeepFailed = settings.KeepFailed,
                KeepLatest = settings.KeepLatest,
                MaxDeletePerRun = settings.MaxDeletePerRun
            });
        }

        public static TimeSpan ParseAge(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return TimeSpan.FromDays(30);
            }

            value = value.Trim();
            var unit = value[value.Length - 1];
            var numberText = char.IsLetter(unit) ? value.Substring(0, value.Length - 1) : value;
            if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return TimeSpan.FromDays(30);
            }

            if (number <= 0)
            {
                return TimeSpan.FromDays(30);
            }

            switch (char.ToLowerInvariant(unit))
            {
                case 'h':
                    return TimeSpan.FromHours(number);
                case 'm':
                    return TimeSpan.FromMinutes(number);
                case 'd':
                default:
                    return TimeSpan.FromDays(number);
            }
        }

        private static WorkflowCleanOptions NormalizeOptions(WorkflowCleanOptions options)
        {
            options = options ?? new WorkflowCleanOptions();
            if (string.IsNullOrWhiteSpace(options.OlderThan))
            {
                options.OlderThan = "30d";
            }

            if (options.MaxDeletePerRun <= 0)
            {
                options.MaxDeletePerRun = 100;
            }

            if (options.KeepLatest < 0)
            {
                options.KeepLatest = 0;
            }

            return options;
        }

        private static bool IsFailedStatus(string status)
        {
            return string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase);
        }

        private static void RemoveRunDirectory(string runsDirectory, string runDirectory)
        {
            var root = Path.GetFullPath(runsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(runDirectory);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove outside workflow runs: " + target);
            }

            Directory.Delete(target, true);
        }

        private class RunInfo
        {
            public RunInfo(string path)
            {
                Path = path;
                RunId = System.IO.Path.GetFileName(path);
                LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(path);
                Status = ReadStatus(path);
            }

            public string Path { get; }
            public string RunId { get; }
            public DateTime LastWriteTimeUtc { get; }
            public string Status { get; }

            private static string ReadStatus(string runDirectory)
            {
                var manifestPath = System.IO.Path.Combine(runDirectory, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    return null;
                }

                try
                {
                    var manifest = JsonConvert.DeserializeObject<WorkflowRunManifest>(File.ReadAllText(manifestPath));
                    return manifest == null ? null : manifest.Status;
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
