using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    /// <summary>仅描述一次受限 job 观察；不表示脚本业务成功，也不携带连接凭据。</summary>
    [Serializable]
    public sealed class PipelineJobObservation
    {
        public bool valid;
        public int httpStatus;
        public string status = "unknown";
        public string errorCode;
        public string error;
        public string errorDetails;
        public string jobId;
        public string command;
        public string state;
        public bool cancellationRequested;
        public string enqueuedAt;
        public string startedAt;
        public string completedAt;
        public object resultValue;
        public object progress;
        public List<string> warnings;
        public bool executionEnded;
        public bool notStarted;
        public bool sideEffectsUnknown = true;
    }

    /// <summary>Pipeline 固定脚本 job 的纯协议层；没有 HTTP、持久化、授权或 CLI 路由。</summary>
    public static class PipelineJobProtocol
    {
        public const string SupportedVersion = PipelineCodeProtocol.SupportedVersion;
        private const int MaxJsonBytes = 1024 * 1024;
        private const int MaxJsonDepth = 64;
        private const int MaxWarnings = 32;
        private const int MaxWarningLength = 4096;
        private const string InvalidCode = "pipelineJobResponseInvalid";
        private const string InvalidText = "Pipeline job state could not be confirmed; do not replay automatically.";
        private static readonly string[] StatusFields =
        {
            "jobId", "command", "state", "cancellationRequested", "enqueuedAt", "startedAt", "completedAt",
            "result", "error", "errorDetails", "errorCode", "progress", "warnings"
        };
        private static readonly string[] SubmissionFields =
        {
            "success", "command", "result", "executionTimeMs", "message", "error", "errorDetails", "executedAt"
        };
        private static readonly string[] ProgressFields = { "title", "info", "current", "total", "pct" };
        private static readonly string[] UtcFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
        };

        /// <summary>只接受固定 verbose、保留 null 的 HTTP200 detached 提交回执。</summary>
        public static PipelineJobObservation ParseSubmission(string json, int httpStatus, string expectedCommand)
        {
            if (httpStatus != 200 || !IsScriptCommand(expectedCommand)) return Invalid(httpStatus);
            try
            {
                var data = ParseObject(json);
                if (!ValidSubmission(data, expectedCommand, out var handle)) return Invalid(httpStatus);
                return new PipelineJobObservation
                {
                    valid = true, httpStatus = httpStatus, status = "observed",
                    jobId = (string)handle["jobId"], command = expectedCommand, state = "queued",
                    warnings = ReadWarnings(Value(data, "warnings"))
                };
            }
            catch (Exception) { return Invalid(httpStatus); }
        }

        /// <summary>只接受固定 /api/job 的裸 DTO；终态与脚本业务结果由不同协议判定。</summary>
        public static PipelineJobObservation ParseStatus(string json, int httpStatus, string expectedJobId, string expectedCommand)
        {
            if (httpStatus != 200 || !IsJobId(expectedJobId) || !IsScriptCommand(expectedCommand)) return Invalid(httpStatus);
            try
            {
                var data = ParseObject(json);
                if (!ValidStatusFields(data, expectedJobId, expectedCommand)
                    || !ValidStage(data)) return Invalid(httpStatus);
                return Observe(data, httpStatus);
            }
            catch (Exception) { return Invalid(httpStatus); }
        }

        /// <summary>只构造固定 cancel body；调用者仍须独立验证已登记 job 和服务身份。</summary>
        public static string BuildCancelRequest(string jobId)
        {
            if (!IsJobId(jobId)) throw new ArgumentException("A 32-character hexadecimal job ID is required.", nameof(jobId));
            return AIBridgeJson.Serialize(new Dictionary<string, object> { { "id", jobId } });
        }

        public static bool IsJobId(string value)
        {
            if (value == null || value.Length != 32) return false;
            foreach (var c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        private static Dictionary<string, object> ParseObject(string json)
        {
            // 先用字符下界挡住超大输入，再校验 UTF-8 字节数；不得记录正文或透传解析异常。
            if (json == null || json.Length > MaxJsonBytes || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) return null;
            return AIBridgeJson.DeserializeObjectStrict(json, MaxJsonDepth);
        }

        private static bool ValidSubmission(Dictionary<string, object> data, string expectedCommand,
            out Dictionary<string, object> handle)
        {
            handle = null;
            if (!HasRequiredFields(data, SubmissionFields)) return false;
            foreach (var key in data.Keys)
                if (Array.IndexOf(SubmissionFields, key) < 0 && key != "errorCode" && key != "warnings") return false;
            if (!(data["success"] is bool success) || !success || !Equals(data["command"], expectedCommand)) return false;
            // CmdSuccess 没有 duration/message/error；接受非 null 值会把同步结果或失败元数据错认成提交。
            foreach (var key in new[] { "executionTimeMs", "message", "error", "errorDetails", "errorCode" })
                if (Value(data, key) != null) return false;
            if (!TryUtc(data["executedAt"], out _) || !ValidWarnings(Value(data, "warnings"))) return false;
            handle = data["result"] as Dictionary<string, object>;
            return HasOnlyFields(handle, new[] { "jobId", "state" })
                && IsJobId(handle["jobId"] as string) && Equals(handle["state"], "queued");
        }

        private static bool ValidStatusFields(Dictionary<string, object> data, string expectedJobId, string expectedCommand)
        {
            // omit_nulls=false 是固定适配契约。缺字段与显式 null 不等价，未知顶层字段也不能静默吞掉。
            if (!HasOnlyFields(data, StatusFields) || !Equals(data["jobId"], expectedJobId)
                || !Equals(data["command"], expectedCommand) || !(data["cancellationRequested"] is bool)) return false;
            foreach (var key in new[] { "error", "errorDetails", "errorCode" })
                if (data[key] != null && !(data[key] is string)) return false;
            if (data["result"] != null && !(data["result"] is Dictionary<string, object>)) return false;
            if (!ValidWarnings(data["warnings"])) return false;
            if (!TryUtc(data["enqueuedAt"], out var enqueued)) return false;
            if (data["startedAt"] != null && (!TryUtc(data["startedAt"], out var started) || started < enqueued)) return false;
            if (data["completedAt"] == null) return true;
            if (!TryUtc(data["completedAt"], out var completed) || completed < enqueued) return false;
            return data["startedAt"] == null || (TryUtc(data["startedAt"], out var start) && completed >= start);
        }

        private static bool ValidStage(Dictionary<string, object> data)
        {
            var state = data["state"] as string;
            var started = data["startedAt"] != null;
            var completed = data["completedAt"] != null;
            var noErrors = data["error"] == null && data["errorDetails"] == null && data["errorCode"] == null;
            switch (state)
            {
                case "queued":
                    return !started && !completed && noErrors && data["result"] == null && data["progress"] == null;
                case "running":
                    return started && !completed && noErrors && data["result"] == null && ValidProgress(data["progress"]);
                case "completed":
                    return started && completed && noErrors && data["progress"] == null;
                case "failed":
                    // 参数/队列错误可能尚未启动；失败可保留 eval 的完整 payload，不能据其内层布尔值改写 state。
                    return completed && data["progress"] == null
                        && data["error"] is string error && !string.IsNullOrWhiteSpace(error);
                case "canceled":
                    // 合作取消可发生在全部副作用之后；不能从 cancellationRequested 或结果被丢弃推导回滚。
                    return completed && noErrors && data["result"] == null && data["progress"] == null;
                default:
                    return false;
            }
        }

        private static bool ValidProgress(object value)
        {
            if (value == null) return true;
            var data = value as Dictionary<string, object>;
            if (!HasOnlyFields(data, ProgressFields)) return false;
            foreach (var key in new[] { "title", "info" })
                if (data[key] != null && !(data[key] is string)) return false;
            return NonnegativeNumberOrNull(data["current"], false)
                && NonnegativeNumberOrNull(data["total"], false) && NonnegativeNumberOrNull(data["pct"], true);
        }

        private static bool NonnegativeNumberOrNull(object value, bool fraction)
        {
            if (value == null) return true;
            double number;
            if (value is long integer) number = integer;
            else if (value is double real) number = real;
            else return false;
            return !double.IsNaN(number) && !double.IsInfinity(number) && number >= 0 && (!fraction || number <= 1);
        }

        private static bool ValidWarnings(object value)
        {
            if (value == null) return true;
            var list = value as List<object>;
            if (list == null || list.Count > MaxWarnings) return false;
            foreach (var item in list)
                if (!(item is string text) || text.Length > MaxWarningLength) return false;
            return true;
        }

        private static List<string> ReadWarnings(object value)
        {
            if (value == null) return null;
            var result = new List<string>();
            foreach (var item in (List<object>)value) result.Add((string)item);
            return result;
        }

        private static bool TryUtc(object value, out DateTimeOffset time)
        {
            time = default;
            var text = value as string;
            if (text == null) return false;
            var suffix = 0;
            if (text.EndsWith("Z", StringComparison.Ordinal)) suffix = 1;
            else if (text.EndsWith("+00:00", StringComparison.Ordinal)) suffix = 6;
            if (suffix == 0) return false;
            var dateLength = text.Length - suffix;
            if (dateLength != 19)
            {
                if (dateLength < 21 || dateLength > 27 || text[19] != '.') return false;
                for (var i = 20; i < dateLength; i++) if (text[i] < '0' || text[i] > '9') return false;
            }
            return DateTimeOffset.TryParseExact(text, UtcFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out time) && time.Offset == TimeSpan.Zero;
        }

        private static PipelineJobObservation Observe(Dictionary<string, object> data, int httpStatus)
        {
            var state = (string)data["state"];
            return new PipelineJobObservation
            {
                valid = true, httpStatus = httpStatus, status = "observed",
                jobId = (string)data["jobId"], command = (string)data["command"], state = state,
                cancellationRequested = (bool)data["cancellationRequested"],
                enqueuedAt = (string)data["enqueuedAt"], startedAt = (string)data["startedAt"], completedAt = (string)data["completedAt"],
                resultValue = AIBridgeJson.PreserveObjectNulls(data["result"]),
                progress = AIBridgeJson.PreserveObjectNulls(data["progress"]), warnings = ReadWarnings(data["warnings"]),
                error = (string)data["error"], errorDetails = (string)data["errorDetails"], errorCode = (string)data["errorCode"],
                executionEnded = state == "completed" || state == "failed" || state == "canceled",
                notStarted = state == "canceled" && data["startedAt"] == null
            };
        }

        private static bool HasRequiredFields(Dictionary<string, object> data, string[] fields)
        {
            if (data == null) return false;
            foreach (var field in fields) if (!data.ContainsKey(field)) return false;
            return true;
        }

        private static bool HasOnlyFields(Dictionary<string, object> data, string[] fields)
        {
            return data != null && data.Count == fields.Length && HasRequiredFields(data, fields);
        }

        private static object Value(Dictionary<string, object> data, string key)
        {
            return data != null && data.TryGetValue(key, out var value) ? value : null;
        }

        private static bool IsScriptCommand(string value) { return value == "script eval" || value == "script run"; }

        private static PipelineJobObservation Invalid(int httpStatus)
        {
            // 所有非200、身份/形状冲突及解析错误统一 unknown；不复制正文、异常、远端错误或候选句柄。
            return new PipelineJobObservation { httpStatus = httpStatus, errorCode = InvalidCode, error = InvalidText };
        }
    }
}
