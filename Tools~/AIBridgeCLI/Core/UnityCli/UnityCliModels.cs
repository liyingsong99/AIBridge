using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Core.UnityCli
{
    /// <summary>子进程原始输出与结构化记录；stderr 不参与 JSON 解析。</summary>
    public sealed class UnityCliProcessResult
    {
        public bool success { get; set; }
        public bool started { get; set; }
        public bool timedOut { get; set; }
        public bool cancelled { get; set; }
        public bool retryable { get; set; }
        public int? exitCode { get; set; }
        public string errorCode { get; set; }
        public string error { get; set; }
        public string stdout { get; set; } = string.Empty;
        public string stderr { get; set; } = string.Empty;
        public bool outputTruncated { get; set; }
        public long durationMs { get; set; }
        public List<JToken> records { get; } = new List<JToken>();
    }

    public enum UnityCliOutputFormat { Text, Json, Ndjson }

    /// <summary>调用方提供真实 CLI argv；执行器不猜测 JSON 开关。</summary>
    public sealed class UnityCliProcessRequest
    {
        public string ExecutablePath { get; set; }
        public string WorkingDirectory { get; set; }
        public IReadOnlyList<string> Arguments { get; set; } = new string[0];
        public int TimeoutMs { get; set; } = UnityCliSettings.DefaultProbeTimeoutMs;
        public int MaxOutputChars { get; set; } = 262144;
        public int MaxRecords { get; set; } = 1024;
        public UnityCliOutputFormat OutputFormat { get; set; }
    }

    public sealed class UnityCliDiagnostic
    {
        public bool available { get; set; }
        public bool retryable { get; set; }
        public string errorCode { get; set; }
        public string error { get; set; }
        public string executablePath { get; set; }
        public string source { get; set; }
        public string version { get; set; }
        public bool identityConfirmed { get; set; }
        public bool? pipelineAvailable { get; set; }
        public List<string> capabilities { get; } = new List<string>();
        public List<string> warnings { get; } = new List<string>();
        public UnityCliProcessResult versionProbe { get; set; }
        public UnityCliProcessResult helpProbe { get; set; }
        public UnityCliProcessResult pipelineProbe { get; set; }
    }

    public sealed class UnityCliBackendSelection
    {
        public bool success { get; set; }
        public string requested { get; set; }
        public string selected { get; set; }
        public bool available { get; set; }
        public bool retryable { get; set; }
        public string errorCode { get; set; }
        public string reason { get; set; }
    }
}
