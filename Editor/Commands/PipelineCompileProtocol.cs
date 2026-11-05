using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    [Serializable]
    public sealed class PipelineCompileResult
    {
        public bool valid;
        public string acknowledgement;
        public int httpStatus;
        public object data;
        public string errorCode;
        public string error;
    }

    /// <summary>捕获固定目标的单次提交会话；Release 只释放本地资源，不停止远端编译。</summary>
    public abstract class PipelineCompileSession
    {
        public abstract string TargetIdentity { get; }
        public abstract string Generation { get; }
        public abstract Task<PipelineCompileResult> SubmitAsync();
        public abstract void Release();
    }

    /// <summary>只确认本次 Refresh 应答，任何应答都不能证明本次编译成功。</summary>
    public static class PipelineCompileProtocol
    {
        public const string SupportedVersion = PipelineCodeProtocol.SupportedVersion;
        public const int MinTimeoutMs = 1000;
        public const int MaxTimeoutMs = 60000;
        public const int MaxJsonBytes = 1024 * 1024;
        private const int MaxText = 4096;
        private const int MaxWarnings = 32;
        private const string Command = "script recompile";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly string[] OuterFields =
        { "success", "command", "result", "executionTimeMs", "message", "error", "errorDetails", "executedAt" };
        private static readonly string[] ResultFields = { "status", "message" };
        private static readonly string[] DataFields =
        { "acknowledgement", "executedAt", "warnings", "compilationSuccess", "errorCount", "warningCount", "duration", "operationCorrelation" };

        public static string BuildRequest(int timeoutMs)
        {
            if (timeoutMs < MinTimeoutMs || timeoutMs > MaxTimeoutMs) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            return AIBridgeJson.Serialize(new Dictionary<string, object>
            {
                { "command", Command }, { "parameters", new Dictionary<string, object> { { "focus", false } } },
                { "timeout", timeoutMs }, { "verbose", true }, { "omitNulls", false }, { "job", false }
            });
        }

        public static PipelineCompileResult ParseResponse(string json, int httpStatus)
        {
            if (httpStatus != 200) return Unknown(httpStatus);
            try
            {
                if (string.IsNullOrEmpty(json) || json.Length > MaxJsonBytes || StrictUtf8.GetByteCount(json) > MaxJsonBytes)
                    return Unknown(httpStatus);
                var outer = AIBridgeJson.DeserializeObjectStrict(json, 16);
                if (!ValidOuter(outer)) return Unknown(httpStatus);
                var result = outer["result"] as Dictionary<string, object>;
                if (!ExactFields(result, ResultFields) || !IsAcknowledgement(result["status"] as string)
                    || !BoundedText(result["message"], false)) return Unknown(httpStatus);
                return Acknowledged(result["status"] as string, outer, httpStatus);
            }
            catch (Exception) { return Unknown(httpStatus); }
        }

        private static bool ValidOuter(Dictionary<string, object> data)
        {
            if (data == null) return false;
            foreach (var field in OuterFields) if (!data.ContainsKey(field)) return false;
            foreach (var field in data.Keys)
                if (Array.IndexOf(OuterFields, field) < 0 && field != "warnings" && field != "errorCode") return false;
            if (!Equals(data["success"], true) || !Equals(data["command"], Command)) return false;
            foreach (var field in new[] { "executionTimeMs", "message", "error", "errorDetails", "errorCode" })
                if (Value(data, field) != null) return false;
            return ValidUtc(data["executedAt"], out _) && ValidWarnings(Value(data, "warnings"));
        }

        private static PipelineCompileResult Acknowledged(string acknowledgement, Dictionary<string, object> outer, int httpStatus)
        {
            var data = new Dictionary<string, object>
            {
                { "acknowledgement", acknowledgement }, { "executedAt", outer["executedAt"] },
                { "warnings", Value(outer, "warnings") }, { "compilationSuccess", null }, { "errorCount", null },
                { "warningCount", null }, { "duration", null }, { "operationCorrelation", false }
            };
            return new PipelineCompileResult
            {
                valid = true, acknowledgement = acknowledgement, httpStatus = httpStatus,
                data = AIBridgeJson.PreserveObjectNulls(data),
                errorCode = acknowledgement == "failed" ? "pipelineCompileFailed" : null,
                error = acknowledgement == "failed" ? "Pipeline Refresh acknowledged compile errors; this is not a correlated compilation result." : null
            };
        }

        internal static bool ValidAcknowledgedData(object value, string acknowledgement)
        {
            var data = value as Dictionary<string, object>;
            if (!ExactFields(data, DataFields) || !IsAcknowledgement(acknowledgement)
                || !Equals(data["acknowledgement"], acknowledgement) || !ValidUtc(data["executedAt"], out _)
                || !ValidWarnings(data["warnings"]) || !Equals(data["operationCorrelation"], false)) return false;
            // 缺失或擅自补零/true 的统计都不属于本次触发的证据。
            foreach (var field in new[] { "compilationSuccess", "errorCount", "warningCount", "duration" })
                if (data[field] != null) return false;
            return true;
        }

        public static PipelineCompileResult Unknown(int httpStatus = 0, string code = "pipelineCompileOutcomeUnknown")
        {
            return new PipelineCompileResult { httpStatus = httpStatus, errorCode = code,
                error = "Pipeline compile acknowledgement is unknown; query the original request locally and do not replay." };
        }

        internal static bool IsAcknowledgement(string value)
        { return value == "up_to_date" || value == "compiling" || value == "failed"; }

        internal static bool ValidUtc(object value, out DateTimeOffset time)
        { return PipelineTestProtocol.ValidUtc(value, out time); }

        private static bool ValidWarnings(object value)
        {
            if (value == null) return true;
            if (!(value is List<object> warnings) || warnings.Count > MaxWarnings) return false;
            foreach (var warning in warnings) if (!BoundedText(warning, true)) return false;
            return true;
        }

        private static bool BoundedText(object value, bool emptyAllowed)
        {
            if (!(value is string text) || text.Length > MaxText || (!emptyAllowed && string.IsNullOrWhiteSpace(text))) return false;
            try { StrictUtf8.GetByteCount(text); return true; }
            catch (EncoderFallbackException) { return false; }
        }

        private static bool ExactFields(Dictionary<string, object> data, string[] fields)
        {
            if (data == null || data.Count != fields.Length) return false;
            foreach (var field in fields) if (!data.ContainsKey(field)) return false;
            return true;
        }

        private static object Value(Dictionary<string, object> data, string key)
        { return data.TryGetValue(key, out var value) ? value : null; }
    }
}
