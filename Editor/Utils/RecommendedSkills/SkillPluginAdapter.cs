using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    internal static class SkillPluginAdapter
    {
        private const string AIBridgePluginName = "aibridge-skills";
        private const string CodexPluginContainerDirectoryName = "plugins";

        public static void GenerateAll(string projectRoot)
        {
            GenerateClaudePlugin(projectRoot);
            GenerateCodexPlugin(projectRoot);
            GenerateCodexMarketplace(projectRoot);
        }

        public static void GenerateForTargets(string projectRoot, IEnumerable<AssistantIntegrationTarget> targets)
        {
            var generatedCodex = false;
            foreach (var target in targets)
            {
                if (target == null)
                {
                    continue;
                }

                if (string.Equals(target.Id, "claude", StringComparison.OrdinalIgnoreCase))
                {
                    GenerateClaudePlugin(projectRoot);
                }
                else if (string.Equals(target.Id, "codex", StringComparison.OrdinalIgnoreCase) && !generatedCodex)
                {
                    GenerateCodexPlugin(projectRoot);
                    GenerateCodexMarketplace(projectRoot);
                    generatedCodex = true;
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

                if (string.Equals(target.Id, "claude", StringComparison.OrdinalIgnoreCase))
                {
                    CleanupClaudePlugin(projectRoot);
                }
                else if (string.Equals(target.Id, "codex", StringComparison.OrdinalIgnoreCase))
                {
                    CleanupCodexPlugin(projectRoot);
                    CleanupCodexMarketplace(projectRoot);
                }
            }
        }

        private static void GenerateClaudePlugin(string projectRoot)
        {
            var pluginRoot = Path.Combine(projectRoot, ".claude-plugin");
            Directory.CreateDirectory(pluginRoot);
            var pluginJsonPath = Path.Combine(pluginRoot, "plugin.json");
            var payload = LoadPluginPayload(pluginJsonPath);
            ApplyPluginPayload(
                payload,
                AIBridgePluginName,
                "AIBridge Skills",
                "Expose project-root skills for Claude-compatible plugin discovery.",
                "./" + AIBridgeProjectSettings.Instance.SkillRootDirectory + "/");
            File.WriteAllText(pluginJsonPath, AIBridgeJson.Serialize(payload, pretty: true));
        }

        private static void GenerateCodexPlugin(string projectRoot)
        {
            var pluginRoot = Path.Combine(projectRoot, CodexPluginContainerDirectoryName, AIBridgePluginName);
            var manifestDirectory = Path.Combine(pluginRoot, ".codex-plugin");
            Directory.CreateDirectory(manifestDirectory);
            var pluginJsonPath = Path.Combine(manifestDirectory, "plugin.json");
            var payload = LoadPluginPayload(pluginJsonPath);
            ApplyPluginPayload(
                payload,
                AIBridgePluginName,
                "AIBridge Skills",
                "Expose project-root skills for Codex plugin discovery.",
                "./../../" + AIBridgeProjectSettings.Instance.SkillRootDirectory + "/");
            File.WriteAllText(pluginJsonPath, AIBridgeJson.Serialize(payload, pretty: true));
        }

        private static void GenerateCodexMarketplace(string projectRoot)
        {
            var marketplacePath = Path.Combine(projectRoot, ".agents", "plugins", "marketplace.json");
            var directory = Path.GetDirectoryName(marketplacePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = LoadMarketplacePayload(marketplacePath);
            payload["name"] = GetString(payload, "name", "aibridge-project");
            var interfacePayload = GetOrCreateObject(payload, "interface");
            if (!interfacePayload.ContainsKey("displayName"))
            {
                interfacePayload["displayName"] = "AIBridge Project Plugins";
            }

            var plugins = GetOrCreatePluginList(payload);
            UpsertMarketplacePlugin(plugins);

            File.WriteAllText(marketplacePath, AIBridgeJson.Serialize(payload, pretty: true));
        }

        private static void CleanupClaudePlugin(string projectRoot)
        {
            var pluginRoot = Path.Combine(projectRoot, ".claude-plugin");
            var pluginJsonPath = Path.Combine(pluginRoot, "plugin.json");
            if (!File.Exists(pluginJsonPath))
            {
                return;
            }

            var payload = LoadPluginPayload(pluginJsonPath);
            if (!string.Equals(GetString(payload, "name", string.Empty), AIBridgePluginName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            File.Delete(pluginJsonPath);
            TryDeleteDirectoryIfEmpty(pluginRoot);
        }

        private static void CleanupCodexPlugin(string projectRoot)
        {
            var pluginRoot = Path.Combine(projectRoot, CodexPluginContainerDirectoryName, AIBridgePluginName);
            if (Directory.Exists(pluginRoot))
            {
                Directory.Delete(pluginRoot, true);
            }
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

        private static List<object> GetOrCreatePluginList(Dictionary<string, object> payload)
        {
            object pluginsValue;
            if (!payload.TryGetValue("plugins", out pluginsValue) || !(pluginsValue is List<object>))
            {
                var newList = new List<object>();
                payload["plugins"] = newList;
                return newList;
            }

            return (List<object>)pluginsValue;
        }

        private static List<object> GetPluginList(Dictionary<string, object> payload)
        {
            object pluginsValue;
            return payload.TryGetValue("plugins", out pluginsValue) && pluginsValue is List<object>
                ? (List<object>)pluginsValue
                : null;
        }

        private static void UpsertMarketplacePlugin(List<object> plugins)
        {
            for (var i = plugins.Count - 1; i >= 0; i--)
            {
                var map = plugins[i] as Dictionary<string, object>;
                if (map != null && string.Equals(GetString(map, "name", string.Empty), AIBridgePluginName, StringComparison.OrdinalIgnoreCase))
                {
                    plugins.RemoveAt(i);
                }
            }

            plugins.Add(
                new Dictionary<string, object>
                {
                    { "name", AIBridgePluginName },
                    {
                        "source",
                        new Dictionary<string, object>
                        {
                            { "source", "local" },
                            { "path", "./plugins/" + AIBridgePluginName }
                        }
                    },
                    {
                        "policy",
                        new Dictionary<string, object>
                        {
                            { "installation", "INSTALLED_BY_DEFAULT" },
                            { "authentication", "ON_INSTALL" }
                        }
                    },
                    { "category", "Productivity" }
                });
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

        private static void ApplyPluginPayload(Dictionary<string, object> payload, string name, string displayName, string description, string skillsPath)
        {
            payload["name"] = name;
            payload["version"] = GetString(payload, "version", "1.0.0");
            payload["description"] = description;
            if (!payload.ContainsKey("author"))
            {
                payload["author"] = new Dictionary<string, object>
                {
                    { "name", "AIBridge" },
                    { "url", "https://github.com/liyingsong99/AIBridge" }
                };
            }

            payload["license"] = GetString(payload, "license", "MIT");
            payload["keywords"] = new List<object> { "aibridge", "unity", "skills" };
            payload["skills"] = skillsPath;

            var interfacePayload = GetOrCreateObject(payload, "interface");
            interfacePayload["displayName"] = displayName;
            interfacePayload["shortDescription"] = description;
            interfacePayload["developerName"] = GetString(interfacePayload, "developerName", "AIBridge");
            interfacePayload["category"] = GetString(interfacePayload, "category", "Productivity");
            if (!interfacePayload.ContainsKey("capabilities"))
            {
                interfacePayload["capabilities"] = new List<object> { "Write", "Interactive" };
            }
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
