using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    [Serializable]
    public class WorkflowJobState
    {
        public string jobId;
        public string jobType;
        public string status;
        public string phase;
        public string error;
        public string startedAtUtc;
        public string updatedAtUtc;
        public string completedAtUtc;
        public Dictionary<string, object> inputs = new Dictionary<string, object>();
        public Dictionary<string, object> result = new Dictionary<string, object>();
    }

    public static class WorkflowJobCacheManager
    {
        private static readonly TimeSpan JobRetention = TimeSpan.FromDays(1);

        public static string JobsDirectory => Path.Combine(AIBridge.BridgeDirectory, "workflow-jobs");

        public static void EnsureDirectoryExists()
        {
            if (!Directory.Exists(JobsDirectory))
            {
                Directory.CreateDirectory(JobsDirectory);
            }

            var gitignorePath = Path.Combine(JobsDirectory, ".gitignore");
            if (!File.Exists(gitignorePath))
            {
                File.WriteAllText(gitignorePath, "*\n!.gitignore\n");
            }
        }

        public static void CleanupOldJobs()
        {
            EnsureDirectoryExists();

            foreach (var file in Directory.GetFiles(JobsDirectory, "*.json"))
            {
                var info = new FileInfo(file);
                if (DateTime.UtcNow - info.LastWriteTimeUtc > JobRetention)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public static WorkflowJobState Load(string jobId)
        {
            EnsureDirectoryExists();
            var path = GetStatePath(jobId);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var data = AIBridgeJson.DeserializeObject(json);
            if (data == null)
            {
                return null;
            }

            return new WorkflowJobState
            {
                jobId = GetString(data, "jobId"),
                jobType = GetString(data, "jobType"),
                status = GetString(data, "status"),
                phase = GetString(data, "phase"),
                error = GetString(data, "error"),
                startedAtUtc = GetString(data, "startedAtUtc"),
                updatedAtUtc = GetString(data, "updatedAtUtc"),
                completedAtUtc = GetString(data, "completedAtUtc"),
                inputs = GetDictionary(data, "inputs"),
                result = GetDictionary(data, "result")
            };
        }

        public static void Save(WorkflowJobState state)
        {
            EnsureDirectoryExists();
            state.updatedAtUtc = DateTime.UtcNow.ToString("O");
            WriteAtomic(GetStatePath(state.jobId), AIBridgeJson.Serialize(state, pretty: true));
            WriteAtomic(GetLastPath(), AIBridgeJson.Serialize(new Dictionary<string, object>
            {
                ["jobId"] = state.jobId,
                ["jobType"] = state.jobType,
                ["updatedAtUtc"] = state.updatedAtUtc
            }, pretty: true));
        }

        public static WorkflowJobState LoadLast()
        {
            var lastPath = GetLastPath();
            if (!File.Exists(lastPath))
            {
                return null;
            }

            var json = File.ReadAllText(lastPath);
            var data = AIBridgeJson.DeserializeObject(json);
            if (data == null)
            {
                return null;
            }

            var jobId = GetString(data, "jobId");
            return string.IsNullOrEmpty(jobId) ? null : Load(jobId);
        }

        public static WorkflowJobState LoadActive(string jobType)
        {
            var activePath = GetActivePath(jobType);
            if (!File.Exists(activePath))
            {
                return null;
            }

            var json = File.ReadAllText(activePath);
            var data = AIBridgeJson.DeserializeObject(json);
            var jobId = data != null ? GetString(data, "jobId") : null;
            return string.IsNullOrEmpty(jobId) ? null : Load(jobId);
        }

        public static void SaveActive(string jobType, string jobId)
        {
            EnsureDirectoryExists();
            WriteAtomic(GetActivePath(jobType), AIBridgeJson.Serialize(new Dictionary<string, object>
            {
                ["jobType"] = jobType,
                ["jobId"] = jobId,
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("O")
            }, pretty: true));
        }

        public static void ClearActive(string jobType)
        {
            var activePath = GetActivePath(jobType);
            if (File.Exists(activePath))
            {
                File.Delete(activePath);
            }
        }

        public static WorkflowJobState LoadGlobalActive()
        {
            var activePath = GetGlobalActivePath();
            if (!File.Exists(activePath))
            {
                return null;
            }

            var json = File.ReadAllText(activePath);
            var data = AIBridgeJson.DeserializeObject(json);
            var jobId = data != null ? GetString(data, "jobId") : null;
            return string.IsNullOrEmpty(jobId) ? null : Load(jobId);
        }

        public static void SaveGlobalActive(string jobType, string jobId)
        {
            EnsureDirectoryExists();
            WriteAtomic(GetGlobalActivePath(), AIBridgeJson.Serialize(new Dictionary<string, object>
            {
                ["jobType"] = jobType,
                ["jobId"] = jobId,
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("O")
            }, pretty: true));
        }

        public static void ClearGlobalActive(string jobId = null)
        {
            var activePath = GetGlobalActivePath();
            if (!File.Exists(activePath))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(jobId))
            {
                File.Delete(activePath);
                return;
            }

            var json = File.ReadAllText(activePath);
            var data = AIBridgeJson.DeserializeObject(json);
            var activeJobId = data != null ? GetString(data, "jobId") : null;
            if (string.Equals(activeJobId, jobId, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(activePath);
            }
        }

        private static string GetStatePath(string jobId)
        {
            return Path.Combine(JobsDirectory, jobId + ".json");
        }

        private static string GetLastPath()
        {
            return Path.Combine(JobsDirectory, "last.json");
        }

        private static string GetActivePath(string jobType)
        {
            return Path.Combine(JobsDirectory, "active-" + jobType + ".json");
        }

        private static string GetGlobalActivePath()
        {
            return Path.Combine(JobsDirectory, "active-global.json");
        }

        private static void WriteAtomic(string path, string content)
        {
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, content);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value.ToString();
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var value) || value == null)
            {
                return new Dictionary<string, object>();
            }

            return value as Dictionary<string, object> ?? new Dictionary<string, object>();
        }
    }
}
