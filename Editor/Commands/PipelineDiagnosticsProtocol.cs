using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    [Serializable]
    internal sealed class PipelineDiagnosticsResult
    {
        public bool valid;
        public string errorCode;
        public string error;
        public object data;
    }

    /// <summary>只读编译状态及 EditMode 测试目录的纯协议；不访问 Editor、连接或凭据。</summary>
    internal static class PipelineDiagnosticsProtocol
    {
        internal const string SupportedVersion = PipelineCodeProtocol.SupportedVersion;
        private const int MaxJsonBytes = 1024 * 1024;
        private const int MaxDepth = 16;
        private const int MaxDiagnostics = 4096;
        private const int MaxDiagnosticLength = 32768;
        private const int MaxTests = 65536;
        private const int MaxNameLength = 4096;
        private const int MaxCategories = 128;
        private const int MaxCategoryLength = 1024;
        private const int MaxWarnings = 32;
        private const int MaxWarningLength = 4096;
        private static readonly string[] BaseFields =
        {
            "success", "command", "result", "executionTimeMs", "message", "error", "errorDetails", "executedAt"
        };
        private static readonly string[] CompileFields = { "status", "failed", "errors", "compilationFailed" };
        private static readonly string[] ListFields = { "Mode", "Count", "Tests" };
        private static readonly string[] ItemFields = { "FullName", "Mode", "Assembly", "Categories", "Explicit" };
        private static readonly string[] UtcFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
        };

        internal static string BuildRequest(string action, int timeoutMs)
        {
            var command = CommandFor(action);
            if (command == null) throw new ArgumentException("Unsupported Pipeline diagnostic action.", nameof(action));
            if (timeoutMs < 1000 || timeoutMs > 60000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            var parameters = new Dictionary<string, object>();
            if (action == "test_list") parameters.Add("mode", "editor");
            return AIBridgeJson.Serialize(new Dictionary<string, object>
            {
                { "command", command }, { "parameters", parameters }, { "timeout", timeoutMs },
                { "verbose", true }, { "omitNulls", false }, { "job", false }
            });
        }

        internal static PipelineDiagnosticsResult ParseResponse(string action, string json, int httpStatus)
        {
            var command = CommandFor(action);
            if (command == null) return Unavailable(action, null, httpStatus, "pipelineDiagnosticsActionUnsupported");
            if (httpStatus != 200)
                return HttpUnavailable(httpStatus) ? Unavailable(action, command, httpStatus, "pipelineDiagnosticsUnavailable")
                    : Unknown(action, command, httpStatus);
            try
            {
                var outer = ParseObject(json);
                if (!ValidEnvelope(outer, command, null, false)) return Unknown(action, command, httpStatus);
                var payload = outer["result"] as Dictionary<string, object>;
                var data = Observation(action, command, httpStatus, outer);
                var valid = action == "compile_status" ? ReadCompile(payload, data) : ReadList(payload, data);
                if (!valid) return Unknown(action, command, httpStatus);
                return new PipelineDiagnosticsResult { valid = true, data = AIBridgeJson.PreserveObjectNulls(data) };
            }
            catch (Exception) { return Unknown(action, command, httpStatus); }
        }

        private static Dictionary<string, object> ParseObject(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaxJsonBytes
                || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) return null;
            return AIBridgeJson.DeserializeObjectStrict(json, MaxDepth);
        }

        private static bool ValidEnvelope(Dictionary<string, object> data, string command, string[] extraFields, bool inner)
        {
            if (!HasFields(data, BaseFields)) return false;
            if (extraFields != null && !HasFields(data, extraFields)) return false;
            foreach (var key in data.Keys)
                if (Array.IndexOf(BaseFields, key) < 0 && key != "warnings" && key != "errorCode"
                    && (extraFields == null || Array.IndexOf(extraFields, key) < 0)) return false;
            if (!Equals(data["success"], true) || !Equals(data["command"], command)) return false;
            // 本机固定版本 CmdSuccess 没有计时；内层 TestListResponse 也不填写 result/duration。
            foreach (var key in new[] { "executionTimeMs", "error", "errorDetails", "errorCode" })
                if (Value(data, key) != null) return false;
            if (inner ? data["result"] != null : !(data["result"] is Dictionary<string, object>)) return false;
            if (!inner && data["message"] != null) return false;
            return ValidUtc(data["executedAt"]) && ValidStrings(Value(data, "warnings"), MaxWarnings, MaxWarningLength, true);
        }

        private static bool ReadCompile(Dictionary<string, object> payload, Dictionary<string, object> data)
        {
            if (!HasExactFields(payload, CompileFields) || !(payload["failed"] is bool failed)
                || !(payload["compilationFailed"] is bool compilationFailed)
                || !ValidStrings(payload["errors"], MaxDiagnostics, MaxDiagnosticLength, false)) return false;
            var state = payload["status"] as string;
            var errors = (List<object>)payload["errors"];
            if (!ValidCompileStage(state, failed, compilationFailed, errors.Count)) return false;
            data.Add("compileStatus", state);
            data.Add("failed", failed);
            data.Add("compilationFailed", compilationFailed);
            data.Add("errors", errors);
            data.Add("observedDiagnosticCount", errors.Count);
            // 全局状态不关联本次操作，也不提供完整计数或编译耗时；不能缺字段补零。
            data.Add("errorCount", null);
            data.Add("warningCount", null);
            data.Add("duration", null);
            return true;
        }

        private static bool ValidCompileStage(string state, bool failed, bool compilationFailed, int errors)
        {
            switch (state)
            {
                case "completed":
                    // 修复完成但 reload 尚未清除原生失败标志时 false/true 是合法组合。
                    return failed || errors == 0;
                case "idle":
                case "up_to_date":
                    return !failed && !compilationFailed && errors == 0;
                case "triggered":
                case "compiling":
                    return !failed && errors == 0;
                default:
                    return false;
            }
        }

        private static bool ReadList(Dictionary<string, object> payload, Dictionary<string, object> data)
        {
            if (!ValidEnvelope(payload, "test list", ListFields, true) || !Equals(payload["Mode"], "EditMode")
                || !(payload["Count"] is long count) || count < 0 || count > MaxTests
                || !(payload["Tests"] is List<object> tests) || tests.Count != count) return false;
            if (!Equals(payload["message"], "Found " + count.ToString(CultureInfo.InvariantCulture) + " test(s).")) return false;
            foreach (var test in tests) if (!ValidTestItem(test as Dictionary<string, object>)) return false;
            data.Add("mode", payload["Mode"]);
            data.Add("count", count);
            data.Add("tests", tests);
            data.Add("listingIsExecution", false);
            data.Add("innerExecutedAt", payload["executedAt"]);
            data.Add("innerWarnings", Value(payload, "warnings"));
            return true;
        }

        private static bool ValidTestItem(Dictionary<string, object> data)
        {
            return HasExactFields(data, ItemFields) && IsName(data["FullName"], false)
                && Equals(data["Mode"], "EditMode") && IsName(data["Assembly"], true)
                && data["Explicit"] is bool && ValidStrings(data["Categories"], MaxCategories, MaxCategoryLength, false);
        }

        private static bool IsName(object value, bool nullable)
        {
            if (value == null) return nullable;
            return value is string text && !string.IsNullOrWhiteSpace(text) && text.Length <= MaxNameLength;
        }

        private static bool ValidStrings(object value, int maxCount, int maxLength, bool nullable)
        {
            if (value == null) return nullable;
            if (!(value is List<object> list) || list.Count > maxCount) return false;
            foreach (var item in list) if (!(item is string text) || text.Length > maxLength) return false;
            return true;
        }

        private static bool ValidUtc(object value)
        {
            if (!(value is string text)) return false;
            var suffix = text.EndsWith("Z", StringComparison.Ordinal) ? 1 : 0;
            if (suffix == 0 && text.EndsWith("+00:00", StringComparison.Ordinal)) suffix = 6;
            if (suffix == 0) return false;
            var dateLength = text.Length - suffix;
            if (dateLength != 19)
            {
                if (dateLength < 21 || dateLength > 27 || text[19] != '.') return false;
                for (var i = 20; i < dateLength; i++) if (text[i] < '0' || text[i] > '9') return false;
            }
            return DateTimeOffset.TryParseExact(text, UtcFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var time) && time.Offset == TimeSpan.Zero;
        }

        private static Dictionary<string, object> Observation(string action, string command, int httpStatus,
            Dictionary<string, object> outer)
        {
            var data = State("observed", action, command, httpStatus);
            data.Add("operationCorrelation", false);
            data.Add("executedAt", outer["executedAt"]);
            data.Add("warnings", Value(outer, "warnings"));
            return data;
        }

        private static Dictionary<string, object> State(string status, string action, string command, int httpStatus)
        {
            return new Dictionary<string, object>
            {
                { "status", status }, { "backend", "pipeline" }, { "action", action },
                { "command", command }, { "httpStatus", httpStatus }
            };
        }

        private static PipelineDiagnosticsResult Unknown(string action, string command, int httpStatus)
        {
            return Failure("unknown", action, command, httpStatus, "pipelineDiagnosticsResponseInvalid",
                "Pipeline diagnostic observation could not be confirmed.");
        }

        private static PipelineDiagnosticsResult Unavailable(string action, string command, int httpStatus, string code)
        {
            return Failure("unavailable", action, command, httpStatus, code, "Pipeline diagnostic observation is unavailable.");
        }

        private static PipelineDiagnosticsResult Failure(string status, string action, string command, int httpStatus,
            string code, string error)
        {
            var data = State(status, CommandFor(action) == null ? null : action, command, httpStatus);
            data.Add("operationCorrelation", false);
            data.Add("errorCode", code);
            return new PipelineDiagnosticsResult
            {
                errorCode = code, error = error, data = AIBridgeJson.PreserveObjectNulls(data)
            };
        }

        private static bool HttpUnavailable(int status)
        {
            return status == 401 || status == 403 || status == 404 || status == 405
                || status == 429 || status == 501 || status == 503;
        }

        private static string CommandFor(string action)
        {
            if (action == "compile_status") return "script recompile_status";
            if (action == "test_list") return "test list";
            return null;
        }

        private static bool HasFields(Dictionary<string, object> data, string[] fields)
        {
            if (data == null) return false;
            foreach (var field in fields) if (!data.ContainsKey(field)) return false;
            return true;
        }

        private static bool HasExactFields(Dictionary<string, object> data, string[] fields)
        {
            return data != null && data.Count == fields.Length && HasFields(data, fields);
        }

        private static object Value(Dictionary<string, object> data, string key)
        {
            return data.TryGetValue(key, out var value) ? value : null;
        }
    }
}
