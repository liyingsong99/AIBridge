using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace AIBridgeCLI.Workflow
{
    public class WorkflowSettings
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonProperty("autoCleanEnabled")]
        public bool AutoCleanEnabled { get; set; }

        [JsonProperty("autoCleanOlderThan")]
        public string AutoCleanOlderThan { get; set; } = "30d";

        [JsonProperty("keepFailed")]
        public bool KeepFailed { get; set; } = true;

        [JsonProperty("keepLatest")]
        public int KeepLatest { get; set; } = 20;

        [JsonProperty("maxDeletePerRun")]
        public int MaxDeletePerRun { get; set; } = 100;

        public static string GetSettingsPath()
        {
            return Path.Combine(WorkflowPathHelper.GetWorkflowRootDirectory(), "settings.json");
        }

        public static WorkflowSettings Load()
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return new WorkflowSettings();
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var settings = JsonConvert.DeserializeObject<WorkflowSettings>(json);
            return settings ?? new WorkflowSettings();
        }

        public void Save()
        {
            var path = GetSettingsPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
        }
    }
}
