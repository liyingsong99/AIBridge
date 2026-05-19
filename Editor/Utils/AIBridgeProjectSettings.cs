using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditorInternal;
using UnityEngine;

namespace AIBridge.Editor
{
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

        public const int CurrentDataVersion = 3;
        public const int DefaultGifFrameCount = 50;
        public const int DefaultGifFps = 20;
        public const float DefaultGifScale = 0.5f;
        public const int DefaultGifColorCount = 128;
        public const float DefaultGifStartDelay = 0.1f;
        public const int DefaultLogRetrievalCount = 50;
        public const string DefaultLogRetrievalType = "all";
        public const string DefaultScriptDirectory = "Assets/AIBridgeScripts";
        public static readonly string[] SupportedLogRetrievalTypes = { "all", "Log", "Warning", "Error" };

        [SerializeField] private int dataVersion = CurrentDataVersion;
        [SerializeField] private bool bridgeEnabled = true;
        [SerializeField] private bool debugLogging;
        [SerializeField] private string scriptDirectory = DefaultScriptDirectory;
        [SerializeField] private GifRecorderSettingsData gifRecorder = new GifRecorderSettingsData();
        [SerializeField] private LogRetrievalSettingsData logRetrieval = new LogRetrievalSettingsData();
        [SerializeField] private List<AssistantSelectionEntry> assistantSelections = new List<AssistantSelectionEntry>();
        [SerializeField] private List<AssistantSkillRootDirectoryEntry> assistantSkillRootDirectories = new List<AssistantSkillRootDirectoryEntry>();
        [SerializeField] private bool legacyGifMigrated;
        [SerializeField] private bool legacyScriptDirectoryMigrated;
        [SerializeField] private bool autoInstallSkills = true;

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

        public string ScriptDirectory
        {
            get { return string.IsNullOrEmpty(scriptDirectory) ? DefaultScriptDirectory : scriptDirectory; }
            set { scriptDirectory = string.IsNullOrEmpty(value) ? DefaultScriptDirectory : value; }
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

        /// <summary>
        /// 从 ProjectSettings 读取配置；首次没有配置文件时创建默认实例。
        /// </summary>
        public static AIBridgeProjectSettings LoadOrCreate()
        {
            var objects = InternalEditorUtility.LoadSerializedFileAndForget(SettingsFilePath);
            var loadedSettings = objects.Length > 0 ? objects[0] as AIBridgeProjectSettings : null;
            _instance = loadedSettings ?? _instance ?? CreateInstance<AIBridgeProjectSettings>();
            return _instance;
        }

        /// <summary>
        /// 手动序列化保存到 ProjectSettings，替代 Unity 2019 下不可访问的 FilePathAttribute。
        /// </summary>
        public void SaveSettings()
        {
            if (dataVersion != CurrentDataVersion)
            {
                dataVersion = CurrentDataVersion;
            }

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
    }
}
