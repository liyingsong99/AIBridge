using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    [Serializable]
    public sealed class PipelineTestResult
    {
        public bool valid;
        public bool success;
        public bool executionEnded;
        public string status = "unknown";
        public int httpStatus;
        public string errorCode;
        public string error;
        public object data;
    }

    /// <summary>固定版本 test run 的同步回执；不使用无归属的上游全局 status/cancel。</summary>
    public static class PipelineTestProtocol
    {
        public const string SupportedVersion = PipelineCodeProtocol.SupportedVersion;
        public const int MaxJsonBytes = 1024 * 1024;
        private const int MaxTests = 65536;
        private const int MaxText = 16384;
        private static readonly string[] BaseFields =
        { "success", "command", "result", "executionTimeMs", "message", "error", "errorDetails", "executedAt" };
        private static readonly string[] RunFields = { "Summary", "Results", "Duration", "StatusPath", "Mode", "FilterApplied" };
        private static readonly string[] SummaryFields = { "Total", "Passed", "Failed", "Skipped", "Inconclusive" };
        private static readonly string[] ResultFields = { "FullName", "Status", "Duration", "Message", "StackTrace" };
        private static readonly string[] UtcFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
        };

        public static string BuildRequest(PipelineTestOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            return AIBridgeJson.Serialize(new Dictionary<string, object>
            {
                { "command", "test run" },
                { "parameters", new Dictionary<string, object>
                    { { "mode", "editor" }, { "filter", options.Filter }, { "filter-type", options.FilterType },
                      { "include-explicit", false }, { "async-tests", false }, { "timeout", options.TimeoutMs / 1000.0 } } },
                { "timeout", options.TimeoutMs }, { "verbose", true }, { "omitNulls", false }, { "job", false }
            });
        }

        public static PipelineTestResult ParseResponse(string json, int httpStatus, PipelineTestOptions options)
        {
            if (httpStatus != 200 || options == null) return Unknown(httpStatus);
            try
            {
                if (string.IsNullOrEmpty(json) || json.Length > MaxJsonBytes || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
                    return Unknown(httpStatus);
                var outer = AIBridgeJson.DeserializeObjectStrict(json, 16);
                if (!ValidEnvelope(outer, false, out var outerTime)) return Unknown(httpStatus);
                var inner = outer["result"] as Dictionary<string, object>;
                if (!ValidEnvelope(inner, true, out var innerTime) || innerTime > outerTime) return Unknown(httpStatus);
                if (!Equals(inner["Mode"], "EditMode") || inner["StatusPath"] != null
                    || !Equals(inner["FilterApplied"], options.ExpectedFilterApplied) || !FiniteNonNegative(inner["Duration"]))
                    return Unknown(httpStatus);
                if (!ConsistentDuration(inner["Duration"], inner["executionTimeMs"])) return Unknown(httpStatus);
                var counts = ReadCounts(inner["Summary"] as Dictionary<string, object>);
                var items = inner["Results"] as List<object>;
                if (counts == null || !ValidResults(items, counts)) return Unknown(httpStatus);
                return Completed(inner, outer, counts, httpStatus);
            }
            catch (Exception) { return Unknown(httpStatus); }
        }

        private static bool ValidEnvelope(Dictionary<string, object> data, bool inner, out DateTimeOffset time)
        {
            time = default;
            if (!HasFields(data, BaseFields) || (inner && !HasFields(data, RunFields))) return false;
            foreach (var key in data.Keys)
                if (Array.IndexOf(BaseFields, key) < 0 && key != "warnings" && key != "errorCode"
                    && (!inner || Array.IndexOf(RunFields, key) < 0)) return false;
            if (!Equals(data["success"], true) || !Equals(data["command"], "test run")) return false;
            foreach (var key in new[] { "message", "error", "errorDetails", "errorCode" })
                if (Value(data, key) != null) return false;
            if (inner)
            {
                if (data["result"] != null || !(data["executionTimeMs"] is long elapsed) || elapsed < 0) return false;
            }
            else if (!(data["result"] is Dictionary<string, object>) || data["executionTimeMs"] != null) return false;
            return ValidUtc(data["executedAt"], out time) && ValidWarnings(Value(data, "warnings"));
        }

        private static int[] ReadCounts(Dictionary<string, object> data)
        {
            if (!ExactFields(data, SummaryFields)) return null;
            var counts = new int[SummaryFields.Length];
            var sum = 0;
            for (var i = 0; i < counts.Length; i++)
            {
                if (!(data[SummaryFields[i]] is long value) || value < 0 || value > MaxTests) return null;
                counts[i] = checked((int)value);
                if (i > 0) sum = checked(sum + counts[i]);
            }
            return sum == counts[0] ? counts : null;
        }

        private static bool ValidResults(List<object> results, int[] expected)
        {
            if (results == null || results.Count != expected[0]) return false;
            var counts = new int[expected.Length];
            foreach (var value in results)
            {
                var data = value as Dictionary<string, object>;
                if (!ExactFields(data, ResultFields) || !Text(data["FullName"], false)
                    || !Text(data["Message"], true) || !Text(data["StackTrace"], true) || !FiniteNonNegative(data["Duration"])) return false;
                var index = Array.IndexOf(SummaryFields, data["Status"] as string);
                if (index < 1) return false;
                counts[index] = checked(counts[index] + 1);
            }
            for (var i = 1; i < counts.Length; i++) if (counts[i] != expected[i]) return false;
            return true;
        }

        private static PipelineTestResult Completed(Dictionary<string, object> inner, Dictionary<string, object> outer, int[] counts, int httpStatus)
        {
            string status;
            if (counts[2] > 0) status = "failed";
            else if (counts[0] == 0) status = "no_tests";
            else if (counts[4] > 0) status = "inconclusive";
            else if (counts[1] == 0) status = "skipped";
            else status = "passed";
            var data = new Dictionary<string, object>
            {
                { "mode", "EditMode" }, { "filterApplied", inner["FilterApplied"] },
                { "total", (long)counts[0] }, { "passed", (long)counts[1] }, { "failed", (long)counts[2] },
                { "skipped", (long)counts[3] }, { "inconclusive", (long)counts[4] }, { "duration", inner["Duration"] },
                { "executionTimeMs", inner["executionTimeMs"] }, { "results", inner["Results"] },
                { "executedAt", outer["executedAt"] }, { "innerExecutedAt", inner["executedAt"] },
                { "warnings", Value(outer, "warnings") }, { "innerWarnings", Value(inner, "warnings") }
            };
            return new PipelineTestResult
            {
                valid = true, success = status == "passed", executionEnded = true, status = status, httpStatus = httpStatus,
                data = AIBridgeJson.PreserveObjectNulls(data),
                errorCode = status == "passed" ? null : "pipelineTest_" + status,
                error = status == "passed" ? null : "Pipeline test run ended with " + status + "; inspect the recorded summary."
            };
        }

        internal static bool ValidCompletedData(object value, PipelineTestOptions options, string expectedStatus)
        {
            var data = value as Dictionary<string, object>;
            var fields = new[] { "mode", "filterApplied", "total", "passed", "failed", "skipped", "inconclusive",
                "duration", "executionTimeMs", "results", "executedAt", "innerExecutedAt", "warnings", "innerWarnings" };
            if (!ExactFields(data, fields) || options == null || !Equals(data["mode"], "EditMode")
                || !Equals(data["filterApplied"], options.ExpectedFilterApplied) || !FiniteNonNegative(data["duration"])
                || !ConsistentDuration(data["duration"], data["executionTimeMs"])
                || !ValidUtc(data["executedAt"], out var outer) || !ValidUtc(data["innerExecutedAt"], out var inner) || inner > outer
                || !ValidWarnings(data["warnings"]) || !ValidWarnings(data["innerWarnings"])) return false;
            var summary = new Dictionary<string, object>();
            for (var i = 0; i < SummaryFields.Length; i++) summary.Add(SummaryFields[i], data[SummaryFields[i].ToLowerInvariant()]);
            var counts = ReadCounts(summary);
            if (counts == null || !ValidResults(data["results"] as List<object>, counts)) return false;
            string status;
            if (counts[2] > 0) status = "failed";
            else if (counts[0] == 0) status = "no_tests";
            else if (counts[4] > 0) status = "inconclusive";
            else if (counts[1] == 0) status = "skipped";
            else status = "passed";
            return status == expectedStatus;
        }

        public static PipelineTestResult Unknown(int httpStatus = 0, string code = "pipelineTestOutcomeUnknown")
        {
            return new PipelineTestResult { httpStatus = httpStatus, errorCode = code,
                error = "Pipeline test outcome is unknown; query the original request locally and do not replay." };
        }

        private static bool ConsistentDuration(object duration, object elapsed)
        {
            if (!FiniteNonNegative(duration) || !(elapsed is long milliseconds) || milliseconds < 0) return false;
            // 上游 Duration 四舍五入到 2 位，executionTimeMs 截断毫秒；最大舍入差 6ms。
            return Math.Abs(Convert.ToDouble(duration, CultureInfo.InvariantCulture) * 1000.0 - milliseconds) <= 6.0;
        }

        private static bool FiniteNonNegative(object value)
        {
            if (value is long integer) return integer >= 0;
            return value is double number && !double.IsNaN(number) && !double.IsInfinity(number) && number >= 0;
        }

        private static bool Text(object value, bool nullable)
        {
            if (value == null) return nullable;
            return value is string text && text.Length <= MaxText && (nullable || !string.IsNullOrWhiteSpace(text));
        }

        private static bool ValidWarnings(object value)
        {
            if (value == null) return true;
            if (!(value is List<object> list) || list.Count > 32) return false;
            foreach (var item in list) if (!(item is string text) || text.Length > 4096) return false;
            return true;
        }

        internal static bool ValidUtc(object value, out DateTimeOffset time)
        {
            time = default;
            if (!(value is string text)) return false;
            var suffix = text.EndsWith("Z", StringComparison.Ordinal) ? 1 : 0;
            if (suffix == 0 && text.EndsWith("+00:00", StringComparison.Ordinal)) suffix = 6;
            if (suffix == 0) return false;
            var length = text.Length - suffix;
            if (length != 19)
            {
                if (length < 21 || length > 27 || text[19] != '.') return false;
                for (var i = 20; i < length; i++) if (text[i] < '0' || text[i] > '9') return false;
            }
            return DateTimeOffset.TryParseExact(text, UtcFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time)
                && time.Offset == TimeSpan.Zero;
        }

        private static bool HasFields(Dictionary<string, object> data, string[] fields)
        {
            if (data == null) return false;
            foreach (var field in fields) if (!data.ContainsKey(field)) return false;
            return true;
        }
        private static bool ExactFields(Dictionary<string, object> data, string[] fields)
        { return data != null && data.Count == fields.Length && HasFields(data, fields); }
        private static object Value(Dictionary<string, object> data, string key)
        { return data.TryGetValue(key, out var value) ? value : null; }
    }
}
