using System;
using System.Collections.Generic;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    // 核心程序集拥有协议 DTO；可选适配器单向引用核心，未安装 Pipeline 时核心仍可编译。
    [Serializable]
    public sealed class PipelineCodeResult
    {
        public bool success;
        public string status = "failed";
        public string errorCode;
        public string error;
        public string errorDetails;
        public object resultValue;
        public string message;
        public string output;
        public long? executionTimeMs;
        public int httpStatus;
        public bool mayStillBeRunning;
        public List<PipelineCodeDiagnostic> diagnostics = new List<PipelineCodeDiagnostic>();
    }

    [Serializable]
    public sealed class PipelineCodeDiagnostic
    {
        public string severity;
        public string code;
        public string message;
        public string file;
        public int? line;
        public int? column;
    }

    /// <summary>Pipeline 0.10 eval 的受限协议，不提供任意命令转发。</summary>
    public static partial class PipelineCodeProtocol
    {
        public const string SupportedVersion = "0.10.0-exp.1";
        private const string Command = "script eval";
        private const string DryRunReceipt = "Compiled successfully (dry run; nothing was loaded or executed).";

        public static bool IsExecutionAuthorized()
        {
            var settings = AIBridgeProjectSettings.Instance;
            return settings.EnableCodeExecution && settings.CodeExecutionRiskAccepted;
        }

        public static string BuildEvalRequest(string code, int timeoutMs, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code is required.", nameof(code));
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            return AIBridgeJson.Serialize(new Dictionary<string, object>
            {
                { "command", Command },
                { "parameters", new Dictionary<string, object>
                    { { "code", code }, { "dry-run", dryRun }, { "auto-import", false },
                      { "internals", false }, { "timeout", timeoutMs / 1000.0 } } },
                { "timeout", timeoutMs }, { "verbose", true }, { "job", false }
            });
        }

        public static string BuildDryRunRequest(string code, int timeoutMs)
        {
            return BuildEvalRequest(code, timeoutMs, true);
        }

        public static PipelineCodeResult ParseEvalResponse(string json, int httpStatus, bool dryRun)
        {
            if (dryRun) return ParseDryRunResponse(json, httpStatus);
            try
            {
                var outer = AIBridgeJson.DeserializeObjectStrict(json);
                if (outer == null || !ValidEnvelopeFields(outer)) return InvalidResponse(httpStatus);
                var inner = Value(outer, "result") as Dictionary<string, object>;
                if (inner != null && !ValidEnvelopeFields(inner)) return InvalidResponse(httpStatus);
                if (!ConsistentOutcome(outer, inner, httpStatus)) return InvalidResponse(httpStatus);
                var result = ReadFailure(outer, inner, httpStatus);
                if (inner != null && !ReadDiagnostics(inner, result)) return InvalidResponse(httpStatus);
                if (httpStatus != 200 || !IsTrue(outer, "success") || inner == null || !IsTrue(inner, "success"))
                {
                    if (httpStatus == 401 || httpStatus == 403 || httpStatus == 429 || httpStatus == 503 || httpStatus == 504) return result;
                    var knownError = result.errorCode == "RUNTIME_EXCEPTION" || result.errorCode == "COMPILATION_ERROR"
                        || result.errorCode == "EVAL_INTERNAL_ERROR" || result.errorCode == "INVALID_COMMAND_ARGS";
                    if (!knownError || (httpStatus != 200 && httpStatus != 400 && httpStatus != 500)
                        || !(Value(outer, "success") is bool) || !(Value(inner, "success") is bool innerSuccess)
                        || innerSuccess || Text(outer, "command") != Command || Text(inner, "command") != Command)
                        return InvalidResponse(httpStatus);
                    result.success = false;
                    result.mayStillBeRunning = false;
                    result.status = result.errorCode == "COMPILATION_ERROR" ? "compile_failed"
                        : result.errorCode == "RUNTIME_EXCEPTION" ? "execution_failed" : "rejected";
                    return result;
                }

                if (Text(outer, "command") != Command || Text(inner, "command") != Command
                    || !inner.ContainsKey("result") || !inner.ContainsKey("output") || !inner.ContainsKey("diagnostics")
                    || Text(inner, "message") == DryRunReceipt
                    || !string.IsNullOrEmpty(Text(outer, "error")) || !string.IsNullOrEmpty(Text(inner, "error"))
                    || !string.IsNullOrEmpty(Text(outer, "errorCode")) || !string.IsNullOrEmpty(Text(inner, "errorCode")))
                    return InvalidResponse(httpStatus);
                if (result.diagnostics.Exists(d => string.Equals(d.severity, "error", StringComparison.OrdinalIgnoreCase)))
                    return InvalidResponse(httpStatus);
                result.success = true;
                result.status = "executed";
                result.mayStillBeRunning = false;
                result.resultValue = Value(inner, "result");
                result.message = Text(inner, "message");
                result.output = Text(inner, "output");
                result.error = null;
                result.errorCode = null;
                result.errorDetails = null;
                result.executionTimeMs = ReadLong(inner, "executionTimeMs");
                return result;
            }
            catch { return InvalidResponse(httpStatus); }
        }

        private static bool ConsistentOutcome(Dictionary<string, object> outer, Dictionary<string, object> inner, int httpStatus)
        {
            var outerCode = Text(outer, "errorCode");
            var innerCode = Text(inner, "errorCode");
            // 外层 timeout 不得被内层编译错误遮盖；不一致的双层结果不能证明远端已停止。
            if (!string.IsNullOrEmpty(outerCode) && !string.IsNullOrEmpty(innerCode) && outerCode != innerCode) return false;
            if ((outerCode == "TIMEOUT" || innerCode == "TIMEOUT")
                && (IsTrue(outer, "success") || IsTrue(inner, "success"))) return false;
            if (httpStatus == 401 || httpStatus == 403 || httpStatus == 429 || httpStatus == 503 || httpStatus == 504)
            {
                if (!(Value(outer, "success") is bool success) || success || IsTrue(inner, "success")) return false;
                if (inner != null && !(Value(inner, "success") is bool)) return false;
                if (httpStatus != 504 && (outerCode == "TIMEOUT" || innerCode == "TIMEOUT")) return false;
                // 拒绝形状只能带拒绝错误；携带执行异常/编译结果表示协议冲突。
                if (inner != null && (innerCode == "COMPILATION_ERROR" || innerCode == "RUNTIME_EXCEPTION"
                    || innerCode == "EVAL_INTERNAL_ERROR")) return false;
            }
            return true;
        }

        private static bool ValidEnvelopeFields(Dictionary<string, object> data)
        {
            // as string 会把错误类型吞成 null；确认终态前必须验证字段类型，不能据此清除占位。
            foreach (var key in new[] { "command", "error", "errorCode", "errorDetails", "message", "output" })
                if (data.TryGetValue(key, out var value) && value != null && !(value is string)) return false;
            if (data.TryGetValue("success", out var success) && !(success is bool)) return false;
            if (data.TryGetValue("executionTimeMs", out var time) && time != null
                && (!(time is long milliseconds) || milliseconds < 0)) return false;
            return true;
        }

        private static long? ReadLong(Dictionary<string, object> data, string key)
        {
            var value = Value(data, key);
            return value is long number && number >= 0 ? (long?)number : null;
        }

        public static PipelineCodeResult ParseDryRunResponse(string json, int httpStatus)
        {
            try
            {
                var outer = AIBridgeJson.DeserializeObjectStrict(json);
                if (outer == null || !ValidEnvelopeFields(outer)) return InvalidResponse(httpStatus);
                var inner = Value(outer, "result") as Dictionary<string, object>;
                if (inner != null && !ValidEnvelopeFields(inner)) return InvalidResponse(httpStatus);
                if (!ConsistentOutcome(outer, inner, httpStatus)) return InvalidResponse(httpStatus);
                var result = ReadFailure(outer, inner, httpStatus);
                if (inner != null && !ReadDiagnostics(inner, result)) return InvalidResponse(httpStatus);
                if (httpStatus != 200 || !IsTrue(outer, "success") || inner == null || !IsTrue(inner, "success"))
                {
                    if (httpStatus == 401 || httpStatus == 403 || httpStatus == 429 || httpStatus == 503 || httpStatus == 504) return result;
                    var knownFailure = result.errorCode == "COMPILATION_ERROR" || result.errorCode == "EVAL_INTERNAL_ERROR"
                        || result.errorCode == "INVALID_COMMAND_ARGS";
                    if (knownFailure && (httpStatus == 200 || httpStatus == 400 || httpStatus == 500)
                        && Value(outer, "success") is bool && Value(inner, "success") is bool innerSuccess
                        && !innerSuccess && Text(outer, "command") == Command && Text(inner, "command") == Command) return result;
                    return InvalidResponse(httpStatus);
                }

                // 上游没有 dryRun 布尔回执。只接受已核实版本的完整 compile-only 回执，缺字段不得默认为成功。
                if (Text(outer, "command") != Command || Text(inner, "command") != Command
                    || Text(inner, "message") != DryRunReceipt
                    || !inner.ContainsKey("result") || Value(inner, "result") != null
                    || !inner.ContainsKey("output") || Value(inner, "output") != null
                    || !inner.ContainsKey("diagnostics")
                    || !string.IsNullOrEmpty(Text(outer, "error")) || !string.IsNullOrEmpty(Text(inner, "error"))
                    || !string.IsNullOrEmpty(Text(outer, "errorCode")) || !string.IsNullOrEmpty(Text(inner, "errorCode")))
                    return InvalidResponse(httpStatus);
                if (result.diagnostics.Exists(d => string.Equals(d.severity, "error", StringComparison.OrdinalIgnoreCase)))
                    return InvalidResponse(httpStatus);
                result.success = true;
                result.mayStillBeRunning = false;
                result.status = "validated";
                result.error = null;
                result.errorCode = null;
                result.errorDetails = null;
                return result;
            }
            catch (Exception)
            {
                return InvalidResponse(httpStatus);
            }
        }

        private static PipelineCodeResult ReadFailure(Dictionary<string, object> outer, Dictionary<string, object> inner, int httpStatus)
        {
            var result = new PipelineCodeResult
            {
                httpStatus = httpStatus,
                error = Text(inner, "error") ?? Text(outer, "error") ?? "Pipeline did not confirm compile-only validation.",
                errorCode = Text(inner, "errorCode") ?? Text(outer, "errorCode") ?? "pipelineResponseInvalid",
                errorDetails = Text(inner, "errorDetails") ?? Text(outer, "errorDetails")
            };
            if (result.errorCode == "COMPILATION_ERROR") result.status = "compile_failed";
            if (httpStatus == 401) result.errorCode = "pipelineAuthenticationFailed";
            if (httpStatus == 403) result.errorCode = "pipelineAccessDenied";
            if (httpStatus == 429 || httpStatus == 503)
            {
                result.status = "unavailable";
                return result;
            }
            if (httpStatus == 504 || result.errorCode == "TIMEOUT" || result.errorCode == "pipelineResponseInvalid")
            {
                result.status = "unknown";
                result.mayStillBeRunning = true;
            }
            return result;
        }

        private static bool ReadDiagnostics(Dictionary<string, object> inner, PipelineCodeResult result)
        {
            var items = Value(inner, "diagnostics") as List<object>;
            if (items == null) return !inner.ContainsKey("diagnostics");
            if (items.Count > 512) return false;
            foreach (var item in items)
            {
                var data = item as Dictionary<string, object>;
                if (data == null || string.IsNullOrEmpty(Text(data, "severity")) || Text(data, "message") == null) return false;
                var severity = Text(data, "severity");
                if (!string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(severity, "info", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(severity, "hidden", StringComparison.OrdinalIgnoreCase)) return false;
                foreach (var key in new[] { "id", "file" })
                    if (data.TryGetValue(key, out var text) && text != null && !(text is string)) return false;
                foreach (var key in new[] { "line", "column", "fileLine" })
                    if (data.TryGetValue(key, out var position) && position != null
                        && (!(position is long number) || number < 0 || number >= int.MaxValue
                            || (key == "fileLine" && number == 0))) return false;
                var line = Coordinate(data, "line", true);
                var column = Coordinate(data, "column", true);
                var fileLine = Coordinate(data, "fileLine", false);
                result.diagnostics.Add(new PipelineCodeDiagnostic
                {
                    severity = Text(data, "severity"), code = Text(data, "id"),
                    message = Text(data, "message"), file = Text(data, "file"),
                    line = fileLine ?? line, column = column
                });
            }
            return true;
        }

        private static int? Coordinate(Dictionary<string, object> data, string key, bool zeroBased)
        {
            var value = Value(data, key);
            if (!(value is long number) || number < 0 || number >= int.MaxValue) return null;
            return (int)number + (zeroBased ? 1 : 0);
        }

        private static PipelineCodeResult InvalidResponse(int httpStatus)
        {
            return new PipelineCodeResult
            {
                status = "unknown", httpStatus = httpStatus, mayStillBeRunning = true,
                errorCode = "pipelineResponseInvalid",
                error = "Pipeline returned an invalid, conflicting or unrecognized response; do not replay automatically."
            };
        }

        private static object Value(Dictionary<string, object> data, string key)
        {
            return data != null && data.TryGetValue(key, out var value) ? value : null;
        }

        private static string Text(Dictionary<string, object> data, string key) { return Value(data, key) as string; }
        private static bool IsTrue(Dictionary<string, object> data, string key) { return Value(data, key) is bool value && value; }
    }
}
