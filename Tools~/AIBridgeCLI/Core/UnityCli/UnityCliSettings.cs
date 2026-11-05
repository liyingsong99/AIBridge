using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AIBridgeCLI.Core.UnityCli
{
    /// <summary>可选配置只由新的诊断命令读取，不改变现有 CommandSender。</summary>
    public sealed class UnityCliSettings
    {
        public const int DefaultProbeTimeoutMs = 5000;
        public const int MaxProbeTimeoutMs = 60000;
        public const string RelativeSettingsPath = ".aibridge/unity-cli.json";
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = 1;
        [JsonProperty("backend")] public string Backend { get; set; } = "native";
        [JsonProperty("executablePath")] public string ExecutablePath { get; set; }
        [JsonProperty("probeTimeoutMs")] public int ProbeTimeoutMs { get; set; } = DefaultProbeTimeoutMs;
        [JsonIgnore] public string ProjectRoot { get; private set; }
        [JsonIgnore] public string ConfigPath { get; private set; }

        public static UnityCliSettings Load(IDictionary<string, string> options)
        {
            var root = PathHelper.TryGetUnityProjectRoot() ?? Directory.GetCurrentDirectory();
            var configPath = ReadOption(options, "config") ?? RelativeSettingsPath;
            configPath = ResolvePath(root, configPath);
            var settings = new UnityCliSettings();
            if (File.Exists(configPath))
            {
                settings = JsonConvert.DeserializeObject<UnityCliSettings>(File.ReadAllText(configPath))
                    ?? throw new ArgumentException("Unity CLI config must contain a JSON object.");
            }
            else if (ReadOption(options, "config") != null)
            {
                throw new ArgumentException("Unity CLI config does not exist: " + configPath);
            }
            settings.ProjectRoot = root;
            settings.ConfigPath = configPath;
            settings.Backend = ReadOption(options, "backend")
                ?? Environment.GetEnvironmentVariable("AIBRIDGE_UNITY_CLI_BACKEND") ?? settings.Backend;
            settings.ExecutablePath = ReadOption(options, "unity-cli-path")
                ?? Environment.GetEnvironmentVariable("AIBRIDGE_UNITY_CLI_PATH") ?? settings.ExecutablePath;
            var timeout = ReadOption(options, "timeout");
            if (timeout != null)
            {
                if (!int.TryParse(timeout, out var parsed)) throw new ArgumentException("--timeout must be an integer.");
                settings.ProbeTimeoutMs = parsed;
            }
            settings.Backend = (settings.Backend ?? "native").Trim().ToLowerInvariant();
            if (settings.SchemaVersion != 1) throw new ArgumentException("Unsupported Unity CLI config schemaVersion.");
            if (settings.Backend != "native" && settings.Backend != "auto" && settings.Backend != "unity-cli")
                throw new ArgumentException("--backend must be native, auto, or unity-cli.");
            if (settings.ProbeTimeoutMs < 1 || settings.ProbeTimeoutMs > MaxProbeTimeoutMs)
                throw new ArgumentException("Unity CLI probe timeout must be between 1 and 60000ms.");
            return settings;
        }

        internal static string ReadOption(IDictionary<string, string> options, string name)
        {
            return options != null && options.TryGetValue(name, out var value) ? value : null;
        }

        internal static string ResolvePath(string root, string path)
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        }
    }
}
