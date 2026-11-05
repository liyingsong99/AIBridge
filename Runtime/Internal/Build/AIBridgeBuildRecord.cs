using System;
using System.Collections.Generic;

namespace AIBridge.Runtime.Internal
{
    /// <summary>纯托管构建快照；未知结果通过显式 null 表达，不依赖 Unity 序列化。</summary>
    [Serializable]
    public sealed class AIBridgeBuildRecord
    {
        public int schemaVersion = 1;
        public string requestId;
        public string buildId;
        public string projectPath;
        public string unityVersion;
        public string generation;
        public string status;
        public string target;
        public string outputPath;
        public string queuedAt;
        public string startedAt;
        public string endedAt;
        public string errorCode;
        public string error;
        public int editorPid;
        public bool? success;
        public bool busy;
        public bool released;
        public bool invocationStarted;
        public bool development;
        public List<string> scenes;
        public AIBridgeBuildReport report;
    }

    [Serializable]
    public sealed class AIBridgeBuildReport
    {
        public string result;
        public string target;
        public string outputPath;
        public long totalSizeBytes;
        public long durationMs;
        public int totalErrors;
        public int totalWarnings;
        public int fileCount;
        public int stepCount;
        public bool truncated;
        public List<AIBridgeBuildFile> files;
        public List<AIBridgeBuildStep> steps;
    }

    [Serializable]
    public sealed class AIBridgeBuildFile
    {
        public string path;
        public string role;
        public long sizeBytes;
    }

    [Serializable]
    public sealed class AIBridgeBuildStep
    {
        public string name;
        public int depth;
        public long durationMs;
        public List<AIBridgeBuildMessage> messages;
    }

    [Serializable]
    public sealed class AIBridgeBuildMessage
    {
        public string type;
        public string message;
    }
}
