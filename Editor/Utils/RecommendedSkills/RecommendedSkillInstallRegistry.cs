using System;
using System.IO;
using System.Linq;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    internal static class RecommendedSkillInstallRegistry
    {
        private const string InstalledRegistryRelativePath = ".aibridge/skill-library/installed.json";

        public static InstalledSkillRecordList Load(string projectRoot)
        {
            var registryPath = GetRegistryPath(projectRoot);
            if (!File.Exists(registryPath))
            {
                return new InstalledSkillRecordList();
            }

            try
            {
                var json = File.ReadAllText(registryPath);
                var payload = AIBridgeJson.DeserializeObject(json);
                return ConvertFromJson(payload);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("[RecommendedSkillInstallRegistry] Failed to read installed registry: " + ex.Message);
                return new InstalledSkillRecordList();
            }
        }

        public static void Save(string projectRoot, InstalledSkillRecordList records)
        {
            var registryPath = GetRegistryPath(projectRoot);
            var directory = Path.GetDirectoryName(registryPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(registryPath, AIBridgeJson.Serialize(records, pretty: true));
        }

        public static void Upsert(string projectRoot, InstalledSkillRecord record)
        {
            var records = Load(projectRoot);
            for (var i = records.Records.Count - 1; i >= 0; i--)
            {
                if (string.Equals(records.Records[i].Name, record.Name, StringComparison.OrdinalIgnoreCase))
                {
                    records.Records.RemoveAt(i);
                }
            }

            records.Records.Add(record);
            records.Records = records.Records
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Save(projectRoot, records);
        }

        public static InstalledSkillRecord Find(string projectRoot, string skillName)
        {
            return Load(projectRoot).Records.FirstOrDefault(record =>
                string.Equals(record.Name, skillName, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetRegistryPath(string projectRoot)
        {
            return Path.Combine(projectRoot, InstalledRegistryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static InstalledSkillRecordList ConvertFromJson(object value)
        {
            var result = new InstalledSkillRecordList();
            var root = value as System.Collections.Generic.Dictionary<string, object>;
            if (root == null)
            {
                return result;
            }

            object recordsValue;
            if (!root.TryGetValue("Records", out recordsValue))
            {
                root.TryGetValue("records", out recordsValue);
            }

            var records = recordsValue as System.Collections.Generic.List<object>;
            if (records == null)
            {
                return result;
            }

            foreach (var item in records)
            {
                var map = item as System.Collections.Generic.Dictionary<string, object>;
                if (map == null)
                {
                    continue;
                }

                result.Records.Add(new InstalledSkillRecord
                {
                    Name = GetString(map, "Name", "name"),
                    RepositoryId = GetString(map, "RepositoryId", "repositoryId"),
                    RepositoryUrl = GetString(map, "RepositoryUrl", "repositoryUrl"),
                    SourceRelativePath = GetString(map, "SourceRelativePath", "sourceRelativePath"),
                    BranchOrTag = GetString(map, "BranchOrTag", "branchOrTag"),
                    Commit = GetString(map, "Commit", "commit"),
                    InstalledAtUtcTicks = GetLong(map, "InstalledAtUtcTicks", "installedAtUtcTicks")
                });
            }

            return result;
        }

        private static string GetString(System.Collections.Generic.Dictionary<string, object> map, string key, string fallbackKey)
        {
            object value;
            if (!map.TryGetValue(key, out value))
            {
                map.TryGetValue(fallbackKey, out value);
            }

            return value as string ?? string.Empty;
        }

        private static long GetLong(System.Collections.Generic.Dictionary<string, object> map, string key, string fallbackKey)
        {
            object value;
            if (!map.TryGetValue(key, out value))
            {
                map.TryGetValue(fallbackKey, out value);
            }

            if (value is long)
            {
                return (long)value;
            }

            if (value is double)
            {
                return Convert.ToInt64((double)value);
            }

            return 0;
        }
    }
}
