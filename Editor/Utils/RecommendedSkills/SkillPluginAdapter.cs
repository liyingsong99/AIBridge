using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    /// <summary>
    /// 为已选 plugin 工具写项目根 plugin.json。实测不可靠，工具无法稳定识别，保留仅作备用。
    /// </summary>
    internal static class SkillPluginAdapter
    {
        private const string AIBridgePluginName = "aibridge-skills";
        private const string PluginJsonFileName = "plugin.json";
        private const string SkillsFieldName = "skills";
        private const string LegacyCodexPluginContainerDirectoryName = "plugins";

        public static void GenerateAll(string projectRoot)
        {
            GenerateForTargets(projectRoot, AssistantIntegrationRegistry.GetTargets());
        }

        public static void GenerateForTargets(string projectRoot, IEnumerable<AssistantIntegrationTarget> targets)
        {
            foreach (var target in targets)
            {
                if (target == null || !target.SupportsPluginManifest || string.IsNullOrEmpty(target.PluginManifestDirectoryName))
                {
                    continue;
                }

                GeneratePluginManifest(
                    projectRoot,
                    target.PluginManifestDirectoryName,
                    "Expose project-root skills for " + target.DisplayName + " plugin discovery.",
                    target.UsesCursorPluginPayload);
                if (string.Equals(target.Id, "codex", StringComparison.OrdinalIgnoreCase))
                {
                    CleanupLegacyCodexPlugin(projectRoot);
                }
            }
        }

        public static void GenerateSelected(string projectRoot)
        {
            GenerateForTargets(projectRoot, SkillInstaller.GetSelectedTargetsForPluginGeneration(projectRoot));
        }

        public static void CleanupForTargets(string projectRoot, IEnumerable<AssistantIntegrationTarget> targets)
        {
            foreach (var target in targets)
            {
                if (target == null)
                {
                    continue;
                }

                if (!target.SupportsPluginManifest || string.IsNullOrEmpty(target.PluginManifestDirectoryName))
                {
                    continue;
                }

                CleanupPluginManifest(projectRoot, target.PluginManifestDirectoryName);
                if (string.Equals(target.Id, "codex", StringComparison.OrdinalIgnoreCase))
                {
                    CleanupLegacyCodexPlugin(projectRoot);
                }
            }
        }

        public static void CleanupSkillRootForTargets(string projectRoot, IEnumerable<AssistantIntegrationTarget> targets, string skillRootDirectory)
        {
            var skillPluginPath = GetSkillPluginPath(skillRootDirectory);
            if (string.IsNullOrEmpty(skillPluginPath))
            {
                return;
            }

            foreach (var target in targets)
            {
                if (target == null)
                {
                    continue;
                }

                if (!target.SupportsPluginManifest || string.IsNullOrEmpty(target.PluginManifestDirectoryName))
                {
                    continue;
                }

                CleanupPluginManifest(projectRoot, target.PluginManifestDirectoryName, skillPluginPath);
                if (string.Equals(target.Id, "codex", StringComparison.OrdinalIgnoreCase))
                {
                    CleanupLegacyCodexPlugin(projectRoot);
                }
            }
        }

        private static void GeneratePluginManifest(string projectRoot, string pluginDirectoryName, string description, bool cursorManifest)
        {
            var pluginRoot = Path.Combine(projectRoot, pluginDirectoryName);
            Directory.CreateDirectory(pluginRoot);
            var pluginJsonPath = Path.Combine(pluginRoot, PluginJsonFileName);
            var payload = LoadPluginPayload(pluginJsonPath);
            var skillsPath = GetCustomSkillPluginPath();

            if (payload.Count == 0 || IsAIBridgePluginPayload(payload))
            {
                if (cursorManifest)
                {
                    ApplyCursorPluginPayload(payload, description, skillsPath);
                }
                else
                {
                    ApplyStandardPluginPayload(payload, description, skillsPath);
                }
            }
            else
            {
                // 已存在的第三方插件清单不能被 AIBridge 覆盖，只追加自定义 Skill 根目录索引。
                UpsertSkillPath(payload, skillsPath);
            }

            File.WriteAllText(pluginJsonPath, AIBridgeJson.Serialize(payload, pretty: true));
        }

        private static void CleanupPluginManifest(string projectRoot, string pluginDirectoryName)
        {
            CleanupPluginManifest(projectRoot, pluginDirectoryName, GetCustomSkillPluginPath());
        }

        private static void CleanupPluginManifest(string projectRoot, string pluginDirectoryName, string skillPluginPath)
        {
            var pluginRoot = Path.Combine(projectRoot, pluginDirectoryName);
            var pluginJsonPath = Path.Combine(pluginRoot, PluginJsonFileName);
            if (!File.Exists(pluginJsonPath))
            {
                return;
            }

            var payload = LoadPluginPayload(pluginJsonPath);
            if (IsAIBridgePluginPayload(payload))
            {
                File.Delete(pluginJsonPath);
                TryDeleteDirectoryIfEmpty(pluginRoot);
                return;
            }

            if (RemoveSkillPath(payload, skillPluginPath))
            {
                File.WriteAllText(pluginJsonPath, AIBridgeJson.Serialize(payload, pretty: true));
            }
        }

        private static void CleanupLegacyCodexPlugin(string projectRoot)
        {
            // 兼容清理旧实现生成的 plugins/aibridge-skills，新的 Codex 索引固定在项目根 .codex-plugin。
            var pluginRoot = Path.Combine(projectRoot, LegacyCodexPluginContainerDirectoryName, AIBridgePluginName);
            if (Directory.Exists(pluginRoot))
            {
                Directory.Delete(pluginRoot, true);
            }

            CleanupCodexMarketplace(projectRoot);
        }

        private static void CleanupCodexMarketplace(string projectRoot)
        {
            var marketplacePath = Path.Combine(projectRoot, ".agents", "plugins", "marketplace.json");
            if (!File.Exists(marketplacePath))
            {
                return;
            }

            var payload = LoadMarketplacePayload(marketplacePath);
            var plugins = GetPluginList(payload);
            if (plugins == null)
            {
                return;
            }

            if (RemoveMarketplacePlugin(plugins))
            {
                File.WriteAllText(marketplacePath, AIBridgeJson.Serialize(payload, pretty: true));
            }
        }

        private static Dictionary<string, object> LoadMarketplacePayload(string marketplacePath)
        {
            if (!File.Exists(marketplacePath))
            {
                return new Dictionary<string, object>();
            }

            try
            {
                var payload = AIBridgeJson.DeserializeObject(File.ReadAllText(marketplacePath));
                return payload ?? new Dictionary<string, object>();
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("[SkillPluginAdapter] Failed to read Codex marketplace, recreating: " + ex.Message);
                return new Dictionary<string, object>();
            }
        }

        private static List<object> GetPluginList(Dictionary<string, object> payload)
        {
            object pluginsValue;
            return payload.TryGetValue("plugins", out pluginsValue) && pluginsValue is List<object>
                ? (List<object>)pluginsValue
                : null;
        }

        private static bool RemoveMarketplacePlugin(List<object> plugins)
        {
            var changed = false;
            for (var i = plugins.Count - 1; i >= 0; i--)
            {
                var map = plugins[i] as Dictionary<string, object>;
                if (map != null && string.Equals(GetString(map, "name", string.Empty), AIBridgePluginName, StringComparison.OrdinalIgnoreCase))
                {
                    plugins.RemoveAt(i);
                    changed = true;
                }
            }

            return changed;
        }

        private static Dictionary<string, object> LoadPluginPayload(string pluginJsonPath)
        {
            if (!File.Exists(pluginJsonPath))
            {
                return new Dictionary<string, object>();
            }

            try
            {
                var payload = AIBridgeJson.DeserializeObject(File.ReadAllText(pluginJsonPath));
                return payload ?? new Dictionary<string, object>();
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("[SkillPluginAdapter] Failed to read plugin manifest, recreating: " + ex.Message);
                return new Dictionary<string, object>();
            }
        }

        private static void ApplyStandardPluginPayload(Dictionary<string, object> payload, string description, string skillsPath)
        {
            payload["name"] = AIBridgePluginName;
            payload["version"] = GetString(payload, "version", "1.0.0");
            payload["description"] = description;
            ApplyCommonPluginMetadata(payload, skillsPath);

            var interfacePayload = GetOrCreateObject(payload, "interface");
            interfacePayload["displayName"] = "AIBridge Skills";
            interfacePayload["shortDescription"] = description;
            interfacePayload["developerName"] = GetString(interfacePayload, "developerName", "AIBridge");
            interfacePayload["category"] = GetString(interfacePayload, "category", "Productivity");
            if (!interfacePayload.ContainsKey("capabilities"))
            {
                interfacePayload["capabilities"] = new List<object> { "Write", "Interactive" };
            }
        }

        private static void ApplyCursorPluginPayload(Dictionary<string, object> payload, string description, string skillsPath)
        {
            payload["name"] = AIBridgePluginName;
            payload["displayName"] = "AIBridge Skills";
            payload["description"] = description;
            payload["version"] = GetString(payload, "version", "1.0.0");
            ApplyCommonPluginMetadata(payload, skillsPath);
        }

        private static void ApplyCommonPluginMetadata(Dictionary<string, object> payload, string skillsPath)
        {
            if (!payload.ContainsKey("author"))
            {
                payload["author"] = new Dictionary<string, object>
                {
                    { "name", "AIBridge" },
                    { "url", "https://github.com/liyingsong99/AIBridge" }
                };
            }

            payload["homepage"] = GetString(payload, "homepage", "https://github.com/liyingsong99/AIBridge");
            payload["repository"] = GetString(payload, "repository", "https://github.com/liyingsong99/AIBridge");
            payload["license"] = GetString(payload, "license", "MIT");
            payload["keywords"] = new List<object> { "aibridge", "unity", "skills" };
            payload[SkillsFieldName] = skillsPath;
        }

        private static bool IsAIBridgePluginPayload(Dictionary<string, object> payload)
        {
            return string.Equals(GetString(payload, "name", string.Empty), AIBridgePluginName, StringComparison.OrdinalIgnoreCase);
        }

        private static void UpsertSkillPath(Dictionary<string, object> payload, string skillPath)
        {
            object skillsValue;
            if (!payload.TryGetValue(SkillsFieldName, out skillsValue))
            {
                payload[SkillsFieldName] = skillPath;
                return;
            }

            var skillsString = skillsValue as string;
            if (skillsString != null)
            {
                if (IsSamePluginPath(skillsString, skillPath))
                {
                    return;
                }

                payload[SkillsFieldName] = new List<object> { skillsString, skillPath };
                return;
            }

            var skillsList = skillsValue as List<object>;
            if (skillsList == null)
            {
                payload[SkillsFieldName] = skillPath;
                return;
            }

            foreach (var entry in skillsList)
            {
                var entryString = entry as string;
                if (entryString != null && IsSamePluginPath(entryString, skillPath))
                {
                    return;
                }
            }

            skillsList.Add(skillPath);
        }

        private static bool RemoveSkillPath(Dictionary<string, object> payload, string skillPath)
        {
            object skillsValue;
            if (!payload.TryGetValue(SkillsFieldName, out skillsValue))
            {
                return false;
            }

            var skillsString = skillsValue as string;
            if (skillsString != null)
            {
                if (!IsSamePluginPath(skillsString, skillPath))
                {
                    return false;
                }

                payload.Remove(SkillsFieldName);
                return true;
            }

            var skillsList = skillsValue as List<object>;
            if (skillsList == null)
            {
                return false;
            }

            var changed = false;
            for (var i = skillsList.Count - 1; i >= 0; i--)
            {
                var entryString = skillsList[i] as string;
                if (entryString != null && IsSamePluginPath(entryString, skillPath))
                {
                    skillsList.RemoveAt(i);
                    changed = true;
                }
            }

            if (changed && skillsList.Count == 0)
            {
                payload.Remove(SkillsFieldName);
            }

            return changed;
        }

        private static bool IsSamePluginPath(string firstPath, string secondPath)
        {
            return string.Equals(NormalizePluginPath(firstPath), NormalizePluginPath(secondPath), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePluginPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var normalized = path.Replace('\\', '/').Trim().TrimEnd('/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(2);
            }

            return normalized;
        }

        private static string GetCustomSkillPluginPath()
        {
            return GetSkillPluginPath(AIBridgeProjectSettings.ResolveSharedSkillRootDirectory());
        }

        private static string GetSkillPluginPath(string skillRootDirectory)
        {
            if (string.IsNullOrEmpty(skillRootDirectory))
            {
                skillRootDirectory = AIBridgeProjectSettings.CanonicalSharedSkillRootDirectory;
            }

            skillRootDirectory = skillRootDirectory.Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(skillRootDirectory))
            {
                skillRootDirectory = AIBridgeProjectSettings.CanonicalSharedSkillRootDirectory.Trim('/');
            }

            return "./" + skillRootDirectory + "/";
        }

        private static string GetString(Dictionary<string, object> payload, string key, string defaultValue)
        {
            object value;
            return payload.TryGetValue(key, out value) && value is string
                ? (string)value
                : defaultValue;
        }

        private static Dictionary<string, object> GetOrCreateObject(Dictionary<string, object> payload, string key)
        {
            object value;
            if (payload.TryGetValue(key, out value) && value is Dictionary<string, object>)
            {
                return (Dictionary<string, object>)value;
            }

            var created = new Dictionary<string, object>();
            payload[key] = created;
            return created;
        }

        private static void TryDeleteDirectoryIfEmpty(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            if (Directory.GetFileSystemEntries(directory).Length == 0)
            {
                Directory.Delete(directory);
            }
        }
    }
}
