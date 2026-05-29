using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditorInternal;
using UnityEngine;

namespace AIBridge.Editor
{
    internal enum AIBridgeEditorLanguage
    {
        English,
        SimplifiedChinese
    }

    /// <summary>
    /// AIBridge 项目级编辑器配置，兼容 Unity 2019 的手动序列化落盘方式。
    /// </summary>
    internal sealed class AIBridgeProjectSettings : ScriptableObject
    {
        private const string SettingsFilePath = "ProjectSettings/AIBridgeSettings.asset";
        private static AIBridgeProjectSettings _instance;

        [Serializable]
        internal sealed class GifRecorderSettingsData
        {
            public int FrameCount = DefaultGifFrameCount;
            public int Fps = DefaultGifFps;
            public float Scale = DefaultGifScale;
            public int ColorCount = DefaultGifColorCount;
            public float StartDelay = DefaultGifStartDelay;
        }

        [Serializable]
        internal sealed class LogRetrievalSettingsData
        {
            public int Count = DefaultLogRetrievalCount;
            public string LogType = DefaultLogRetrievalType;
            public bool RegexFilterEnabled;
            public string RegexPattern = string.Empty;
        }

        [Serializable]
        internal sealed class AssistantSelectionEntry
        {
            public string TargetId;
            public bool Selected;
        }

        [Serializable]
        internal sealed class AssistantSkillRootDirectoryEntry
        {
            public string TargetId;
            public string SkillRootDirectory;
        }

        [Serializable]
        internal sealed class RuntimeBridgeSettingsData
        {
            public bool EnableRuntimeBridge = DefaultRuntimeBridgeEnabled;
            public bool AutoInjectRuntimeBridgeInEditorPlayMode = DefaultRuntimeBridgeAutoInjectInEditorPlayMode;
            public bool AutoInjectRuntimeBridgeInDevelopmentBuild = DefaultRuntimeBridgeAutoInjectInDevelopmentBuild;
            public bool AllowInReleaseBuild = DefaultRuntimeBridgeAllowInReleaseBuild;
            public string ExchangeDirectory = DefaultRuntimeBridgeExchangeDirectory;
            public string TargetId = DefaultRuntimeBridgeTargetId;
            public string AuthToken = string.Empty;
            public string AllowedActions = string.Empty;
            public bool EnableRuntimeCodeExecution = DefaultRuntimeBridgeCodeExecutionEnabled;
            public float HeartbeatIntervalSeconds = DefaultRuntimeBridgeHeartbeatIntervalSeconds;
            public int LogBufferSize = DefaultRuntimeBridgeLogBufferSize;
            public int MaxResultBytes = DefaultRuntimeBridgeMaxResultBytes;
            public bool KeepRunningInBackground = DefaultRuntimeBridgeKeepRunningInBackground;
            public bool EnableHttpTransport = DefaultRuntimeBridgeEnableHttpTransport;
            public string HttpBindAddress = DefaultRuntimeBridgeHttpBindAddress;
            public int HttpPort = DefaultRuntimeBridgeHttpPort;
            public bool EnableLanDiscovery = DefaultRuntimeBridgeEnableLanDiscovery;
            public int DiscoveryUdpPort = DefaultRuntimeBridgeDiscoveryUdpPort;

            public bool AllowRuntimeBridgeInReleaseBuild
            {
                get { return AllowInReleaseBuild; }
                set { AllowInReleaseBuild = value; }
            }
        }

        [Serializable]
        internal sealed class CodeIndexSettingsData
        {
            public bool EnableCodeIndex = DefaultCodeIndexEnabled;
            public bool PrewarmOnUnityStartup = DefaultCodeIndexPrewarmOnUnityStartup;
            public int WarmupDelaySeconds = DefaultCodeIndexWarmupDelaySeconds;
            public string WarmupMode = DefaultCodeIndexWarmupMode;
            public bool AutoRefreshOnFileChange = DefaultCodeIndexAutoRefreshOnFileChange;
            public bool FallbackToTextSearch = DefaultCodeIndexFallbackToTextSearch;
            public string CleanupModeOnQuit = DefaultCodeIndexCleanupModeOnQuit;
            public bool IncludePackageCacheSourceAssemblies = DefaultCodeIndexIncludePackageCacheSourceAssemblies;
            public string IgnoredAssemblyPatterns = DefaultCodeIndexIgnoredAssemblyPatterns;
            public string IgnoredSourcePathPatterns = DefaultCodeIndexIgnoredSourcePathPatterns;
        }

        public const int CurrentDataVersion = 16;
        public const string DefaultEditorLanguage = "English";
        public const string LegacySharedSkillRootDirectory = ".skills";
        public const string DefaultSkillRootDirectory = "";
        public const int DefaultGifFrameCount = 50;
        public const int DefaultGifFps = 20;
        public const float DefaultGifScale = 0.5f;
        public const int DefaultGifColorCount = 128;
        public const float DefaultGifStartDelay = 0.1f;
        public const int DefaultLogRetrievalCount = 50;
        public const string DefaultLogRetrievalType = "all";
        public const string DefaultScriptDirectory = "Assets/AIBridgeScripts";
        public const bool DefaultEnableCodeExecution = true;
        public const bool DefaultCodeExecutionRiskAccepted = true;
        public const bool DefaultRuntimeBridgeEnabled = true;
        public const bool DefaultRuntimeBridgeAutoInjectInEditorPlayMode = true;
        public const bool DefaultRuntimeBridgeAutoInjectInDevelopmentBuild = true;
        public const bool DefaultRuntimeBridgeAllowInReleaseBuild = false;
        public const string DefaultRuntimeBridgeExchangeDirectory = "";
        public const string DefaultRuntimeBridgeTargetId = "";
        public const bool DefaultRuntimeBridgeCodeExecutionEnabled = true;
        public const float DefaultRuntimeBridgeHeartbeatIntervalSeconds = 1f;
        public const int DefaultRuntimeBridgeLogBufferSize = 500;
        public const int DefaultRuntimeBridgeMaxResultBytes = 1048576;
        public const bool DefaultRuntimeBridgeKeepRunningInBackground = true;
        public const bool DefaultRuntimeBridgeEnableHttpTransport = true;
        public const string DefaultRuntimeBridgeHttpBindAddress = "0.0.0.0";
        public const int DefaultRuntimeBridgeHttpPort = 27182;
        public const bool DefaultRuntimeBridgeEnableLanDiscovery = true;
        public const int DefaultRuntimeBridgeDiscoveryUdpPort = 27183;
        public const bool DefaultCodeIndexEnabled = false;
        public const bool DefaultCodeIndexPrewarmOnUnityStartup = true;
        public const int DefaultCodeIndexWarmupDelaySeconds = 10;
        public const string DefaultCodeIndexWarmupMode = "light";
        public const bool DefaultCodeIndexAutoRefreshOnFileChange = true;
        public const bool DefaultCodeIndexFallbackToTextSearch = true;
        public const string DefaultCodeIndexCleanupModeOnQuit = "processOnly";
        public const bool DefaultCodeIndexIncludePackageCacheSourceAssemblies = false;
        public const string DefaultCodeIndexIgnoredAssemblyPatterns = "";
        public const string DefaultCodeIndexIgnoredSourcePathPatterns = "";
        public static readonly string[] SupportedCodeIndexCleanupModes = { "processOnly", "processAndTemp", "fullCleanup" };
        public static readonly string[] SupportedLogRetrievalTypes = { "all", "Log", "Warning", "Error" };

        [SerializeField] private int dataVersion = CurrentDataVersion;
        [SerializeField] private bool bridgeEnabled = true;
        [SerializeField] private bool debugLogging;
        [SerializeField] private string editorLanguage = DefaultEditorLanguage;
        [SerializeField] private bool editorLanguageInitialized;
        [SerializeField] private string skillRootDirectory = DefaultSkillRootDirectory;
        [SerializeField] private string scriptDirectory = DefaultScriptDirectory;
        [SerializeField] private GifRecorderSettingsData gifRecorder = new GifRecorderSettingsData();
        [SerializeField] private LogRetrievalSettingsData logRetrieval = new LogRetrievalSettingsData();
        [SerializeField] private List<AssistantSelectionEntry> assistantSelections = new List<AssistantSelectionEntry>();
        [SerializeField] private List<AssistantSkillRootDirectoryEntry> assistantSkillRootDirectories = new List<AssistantSkillRootDirectoryEntry>();
        [SerializeField] private bool legacyGifMigrated;
        [SerializeField] private bool legacyScriptDirectoryMigrated;
        [SerializeField] private bool autoInstallSkills = true;
        [SerializeField] private bool enableCodeExecution = DefaultEnableCodeExecution;
        [SerializeField] private bool codeExecutionRiskAccepted = DefaultCodeExecutionRiskAccepted;
        [SerializeField] private RuntimeBridgeSettingsData runtimeBridge = new RuntimeBridgeSettingsData();
        [SerializeField] private CodeIndexSettingsData codeIndex = new CodeIndexSettingsData();

        public static AIBridgeProjectSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    LoadOrCreate();
                }

                return _instance;
            }
        }

        public int DataVersion
        {
            get { return dataVersion; }
            set { dataVersion = value; }
        }

        public bool BridgeEnabled
        {
            get { return bridgeEnabled; }
            set { bridgeEnabled = value; }
        }

        public bool DebugLogging
        {
            get { return debugLogging; }
            set { debugLogging = value; }
        }

        public AIBridgeEditorLanguage EditorLanguage
        {
            get { return NormalizeEditorLanguage(editorLanguage); }
            set { editorLanguage = value.ToString(); }
        }

        public bool EditorLanguageInitialized
        {
            get { return editorLanguageInitialized; }
            set { editorLanguageInitialized = value; }
        }

        public string ScriptDirectory
        {
            get { return string.IsNullOrEmpty(scriptDirectory) ? DefaultScriptDirectory : scriptDirectory; }
            set { scriptDirectory = string.IsNullOrEmpty(value) ? DefaultScriptDirectory : value; }
        }

        public string SkillRootDirectory
        {
            get
            {
                var normalized = NormalizeSkillRootDirectory(skillRootDirectory);
                if (string.IsNullOrEmpty(normalized))
                {
                    return DefaultSkillRootDirectory;
                }

                return IsValidSkillRootDirectory(normalized) ? normalized : DefaultSkillRootDirectory;
            }
            set
            {
                var normalized = NormalizeSkillRootDirectory(value);
                if (string.IsNullOrEmpty(normalized))
                {
                    skillRootDirectory = DefaultSkillRootDirectory;
                    return;
                }

                skillRootDirectory = IsValidSkillRootDirectory(normalized) ? normalized : DefaultSkillRootDirectory;
            }
        }

        public GifRecorderSettingsData GifRecorder
        {
            get
            {
                if (gifRecorder == null)
                {
                    gifRecorder = new GifRecorderSettingsData();
                }

                return gifRecorder;
            }
        }

        public LogRetrievalSettingsData LogRetrieval
        {
            get
            {
                if (logRetrieval == null)
                {
                    logRetrieval = new LogRetrievalSettingsData();
                }

                if (logRetrieval.Count <= 0)
                {
                    logRetrieval.Count = DefaultLogRetrievalCount;
                }

                if (string.IsNullOrEmpty(logRetrieval.LogType))
                {
                    logRetrieval.LogType = DefaultLogRetrievalType;
                }
                else
                {
                    logRetrieval.LogType = NormalizeLogRetrievalType(logRetrieval.LogType);
                }

                return logRetrieval;
            }
        }

        public static string NormalizeLogRetrievalType(string logType)
        {
            if (string.IsNullOrEmpty(logType))
            {
                return DefaultLogRetrievalType;
            }

            for (var i = 0; i < SupportedLogRetrievalTypes.Length; i++)
            {
                var supportedType = SupportedLogRetrievalTypes[i];
                if (string.Equals(supportedType, logType, StringComparison.OrdinalIgnoreCase))
                {
                    return supportedType;
                }
            }

            return DefaultLogRetrievalType;
        }

        public static AIBridgeEditorLanguage NormalizeEditorLanguage(string value)
        {
            if (string.Equals(value, AIBridgeEditorLanguage.SimplifiedChinese.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return AIBridgeEditorLanguage.SimplifiedChinese;
            }

            return AIBridgeEditorLanguage.English;
        }

        public List<AssistantSelectionEntry> AssistantSelections
        {
            get
            {
                if (assistantSelections == null)
                {
                    assistantSelections = new List<AssistantSelectionEntry>();
                }

                return assistantSelections;
            }
        }

        public List<AssistantSkillRootDirectoryEntry> AssistantSkillRootDirectories
        {
            get
            {
                if (assistantSkillRootDirectories == null)
                {
                    assistantSkillRootDirectories = new List<AssistantSkillRootDirectoryEntry>();
                }

                return assistantSkillRootDirectories;
            }
        }

        public bool LegacyGifMigrated
        {
            get { return legacyGifMigrated; }
            set { legacyGifMigrated = value; }
        }

        public bool LegacyScriptDirectoryMigrated
        {
            get { return legacyScriptDirectoryMigrated; }
            set { legacyScriptDirectoryMigrated = value; }
        }

        public bool AutoInstallSkills
        {
            get { return autoInstallSkills; }
            set { autoInstallSkills = value; }
        }

        public bool EnableCodeExecution
        {
            get { return enableCodeExecution; }
            set { enableCodeExecution = value; }
        }

        public bool CodeExecutionRiskAccepted
        {
            get { return codeExecutionRiskAccepted; }
            set { codeExecutionRiskAccepted = value; }
        }

        public RuntimeBridgeSettingsData RuntimeBridge
        {
            get
            {
                if (runtimeBridge == null)
                {
                    runtimeBridge = new RuntimeBridgeSettingsData();
                }

                if (runtimeBridge.HeartbeatIntervalSeconds <= 0f)
                {
                    runtimeBridge.HeartbeatIntervalSeconds = DefaultRuntimeBridgeHeartbeatIntervalSeconds;
                }

                if (runtimeBridge.LogBufferSize <= 0)
                {
                    runtimeBridge.LogBufferSize = DefaultRuntimeBridgeLogBufferSize;
                }

                if (runtimeBridge.MaxResultBytes <= 0)
                {
                    runtimeBridge.MaxResultBytes = DefaultRuntimeBridgeMaxResultBytes;
                }

                if (string.IsNullOrWhiteSpace(runtimeBridge.HttpBindAddress))
                {
                    runtimeBridge.HttpBindAddress = DefaultRuntimeBridgeHttpBindAddress;
                }

                if (runtimeBridge.HttpPort <= 0)
                {
                    runtimeBridge.HttpPort = DefaultRuntimeBridgeHttpPort;
                }

                if (runtimeBridge.DiscoveryUdpPort <= 0)
                {
                    runtimeBridge.DiscoveryUdpPort = DefaultRuntimeBridgeDiscoveryUdpPort;
                }

                return runtimeBridge;
            }
        }

        public CodeIndexSettingsData CodeIndex
        {
            get
            {
                if (codeIndex == null)
                {
                    codeIndex = new CodeIndexSettingsData();
                }

                if (codeIndex.WarmupDelaySeconds < 0)
                {
                    codeIndex.WarmupDelaySeconds = DefaultCodeIndexWarmupDelaySeconds;
                }

                if (string.IsNullOrWhiteSpace(codeIndex.WarmupMode))
                {
                    codeIndex.WarmupMode = DefaultCodeIndexWarmupMode;
                }

                codeIndex.CleanupModeOnQuit = NormalizeCodeIndexCleanupMode(codeIndex.CleanupModeOnQuit);
                if (codeIndex.IgnoredAssemblyPatterns == null)
                {
                    codeIndex.IgnoredAssemblyPatterns = DefaultCodeIndexIgnoredAssemblyPatterns;
                }

                if (codeIndex.IgnoredSourcePathPatterns == null)
                {
                    codeIndex.IgnoredSourcePathPatterns = DefaultCodeIndexIgnoredSourcePathPatterns;
                }

                return codeIndex;
            }
        }

        public static string NormalizeCodeIndexCleanupMode(string cleanupMode)
        {
            if (string.IsNullOrEmpty(cleanupMode))
            {
                return DefaultCodeIndexCleanupModeOnQuit;
            }

            for (var i = 0; i < SupportedCodeIndexCleanupModes.Length; i++)
            {
                var supportedMode = SupportedCodeIndexCleanupModes[i];
                if (string.Equals(supportedMode, cleanupMode, StringComparison.OrdinalIgnoreCase))
                {
                    return supportedMode;
                }
            }

            return DefaultCodeIndexCleanupModeOnQuit;
        }

        public bool TryGetAssistantSelection(string targetId, out bool selected)
        {
            selected = false;
            if (string.IsNullOrEmpty(targetId))
            {
                return false;
            }

            var entries = AssistantSelections;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry != null && entry.TargetId == targetId)
                {
                    selected = entry.Selected;
                    return true;
                }
            }

            return false;
        }

        public bool SetAssistantSelection(string targetId, bool selected)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return false;
            }

            var entries = AssistantSelections;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry != null && entry.TargetId == targetId)
                {
                    if (entry.Selected == selected)
                    {
                        return false;
                    }

                    entry.Selected = selected;
                    return true;
                }
            }

            entries.Add(new AssistantSelectionEntry
            {
                TargetId = targetId,
                Selected = selected
            });
            return true;
        }

        public bool ClearAssistantSelection(string targetId)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return false;
            }

            var entries = AssistantSelections;
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (entry != null && entry.TargetId == targetId)
                {
                    entries.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        public bool TryGetAssistantSkillRootDirectory(string targetId, out string skillRootDirectory)
        {
            skillRootDirectory = null;
            if (string.IsNullOrEmpty(targetId))
            {
                return false;
            }

            var entries = AssistantSkillRootDirectories;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry != null && entry.TargetId == targetId && !string.IsNullOrEmpty(entry.SkillRootDirectory))
                {
                    skillRootDirectory = entry.SkillRootDirectory;
                    return true;
                }
            }

            return false;
        }

        public bool SetAssistantSkillRootDirectory(string targetId, string skillRootDirectory)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return false;
            }

            var normalized = NormalizeSkillRootDirectory(skillRootDirectory);
            if (string.IsNullOrEmpty(normalized))
            {
                return ClearAssistantSkillRootDirectory(targetId);
            }

            if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part == ".."))
            {
                return false;
            }

            var entries = AssistantSkillRootDirectories;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry != null && entry.TargetId == targetId)
                {
                    if (entry.SkillRootDirectory == normalized)
                    {
                        return false;
                    }

                    entry.SkillRootDirectory = normalized;
                    return true;
                }
            }

            entries.Add(new AssistantSkillRootDirectoryEntry
            {
                TargetId = targetId,
                SkillRootDirectory = normalized
            });
            return true;
        }

        public bool ClearAssistantSkillRootDirectory(string targetId)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return false;
            }

            var entries = AssistantSkillRootDirectories;
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (entry != null && entry.TargetId == targetId)
                {
                    entries.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeSkillRootDirectory(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim().Replace('\\', '/').Trim('/');
        }

        private static bool IsValidSkillRootDirectory(string value)
        {
            return !string.IsNullOrEmpty(value)
                && !Path.IsPathRooted(value)
                && !value.Split('/').Any(part => part == "..");
        }

        /// <summary>
        /// 从 ProjectSettings 读取配置；首次没有配置文件时创建默认实例。
        /// </summary>
        public static AIBridgeProjectSettings LoadOrCreate()
        {
            var objects = InternalEditorUtility.LoadSerializedFileAndForget(SettingsFilePath);
            var loadedSettings = objects.Length > 0 ? objects[0] as AIBridgeProjectSettings : null;
            _instance = loadedSettings ?? _instance ?? CreateInstance<AIBridgeProjectSettings>();
            _instance.MigrateDataIfNeeded();
            return _instance;
        }

        /// <summary>
        /// 手动序列化保存到 ProjectSettings，替代 Unity 2019 下不可访问的 FilePathAttribute。
        /// </summary>
        public void SaveSettings()
        {
            MigrateDataIfNeeded();

            editorLanguage = EditorLanguage.ToString();
            skillRootDirectory = SkillRootDirectory;

            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            InternalEditorUtility.SaveToSerializedFileAndForget(
                new UnityEngine.Object[] { this },
                SettingsFilePath,
                true);
        }

        private void MigrateDataIfNeeded()
        {
            if (dataVersion < 7
                && string.Equals(NormalizeSkillRootDirectory(skillRootDirectory), LegacySharedSkillRootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                skillRootDirectory = DefaultSkillRootDirectory;
            }

            if (dataVersion < 8)
            {
                enableCodeExecution = DefaultEnableCodeExecution;
                codeExecutionRiskAccepted = DefaultCodeExecutionRiskAccepted;
            }

            if (runtimeBridge == null)
            {
                runtimeBridge = new RuntimeBridgeSettingsData();
            }

            if (codeIndex == null)
            {
                codeIndex = new CodeIndexSettingsData();
            }

            if (dataVersion < 10)
            {
                runtimeBridge.AutoInjectRuntimeBridgeInDevelopmentBuild = DefaultRuntimeBridgeAutoInjectInDevelopmentBuild;
            }

            if (dataVersion < 11)
            {
                runtimeBridge.KeepRunningInBackground = DefaultRuntimeBridgeKeepRunningInBackground;
            }

            if (dataVersion < 12)
            {
                runtimeBridge.AutoInjectRuntimeBridgeInEditorPlayMode = DefaultRuntimeBridgeAutoInjectInEditorPlayMode;
            }

            if (dataVersion < 13)
            {
                runtimeBridge.EnableHttpTransport = DefaultRuntimeBridgeEnableHttpTransport;
                runtimeBridge.HttpBindAddress = DefaultRuntimeBridgeHttpBindAddress;
                runtimeBridge.HttpPort = DefaultRuntimeBridgeHttpPort;
                runtimeBridge.EnableLanDiscovery = DefaultRuntimeBridgeEnableLanDiscovery;
                runtimeBridge.DiscoveryUdpPort = DefaultRuntimeBridgeDiscoveryUdpPort;
            }

            if (dataVersion < 14)
            {
                runtimeBridge.EnableRuntimeCodeExecution = DefaultRuntimeBridgeCodeExecutionEnabled;
            }

            if (dataVersion < 15)
            {
                codeIndex.EnableCodeIndex = DefaultCodeIndexEnabled;
                codeIndex.PrewarmOnUnityStartup = DefaultCodeIndexPrewarmOnUnityStartup;
                codeIndex.WarmupDelaySeconds = DefaultCodeIndexWarmupDelaySeconds;
                codeIndex.WarmupMode = DefaultCodeIndexWarmupMode;
                codeIndex.AutoRefreshOnFileChange = DefaultCodeIndexAutoRefreshOnFileChange;
                codeIndex.FallbackToTextSearch = DefaultCodeIndexFallbackToTextSearch;
                codeIndex.CleanupModeOnQuit = DefaultCodeIndexCleanupModeOnQuit;
            }

            if (dataVersion < 16)
            {
                codeIndex.IncludePackageCacheSourceAssemblies = DefaultCodeIndexIncludePackageCacheSourceAssemblies;
                codeIndex.IgnoredAssemblyPatterns = DefaultCodeIndexIgnoredAssemblyPatterns;
                codeIndex.IgnoredSourcePathPatterns = DefaultCodeIndexIgnoredSourcePathPatterns;
            }

            if (dataVersion != CurrentDataVersion)
            {
                dataVersion = CurrentDataVersion;
            }
        }
    }
}
