using System;
using System.Collections.Generic;
using System.IO;
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
        internal sealed class AssistantSelectionEntry
        {
            public string TargetId;
            public bool Selected;
        }

        public const int CurrentDataVersion = 1;
        public const int DefaultGifFrameCount = 50;
        public const int DefaultGifFps = 20;
        public const float DefaultGifScale = 0.5f;
        public const int DefaultGifColorCount = 128;
        public const float DefaultGifStartDelay = 0.1f;
        public const string DefaultScriptDirectory = "Assets/AIBridgeScripts";

        [SerializeField] private int dataVersion = CurrentDataVersion;
        [SerializeField] private bool bridgeEnabled = true;
        [SerializeField] private bool debugLogging;
        [SerializeField] private string scriptDirectory = DefaultScriptDirectory;
        [SerializeField] private GifRecorderSettingsData gifRecorder = new GifRecorderSettingsData();
        [SerializeField] private List<AssistantSelectionEntry> assistantSelections = new List<AssistantSelectionEntry>();
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
