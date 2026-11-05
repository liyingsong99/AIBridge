using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    public static partial class PipelineCodeProtocol
    {
        private const string RunCommand = "script run";
        private const int RunMinTimeoutMs = 1000;
        private const int RunMaxTimeoutMs = 60000;
        private const int RunMaxArgumentsChars = 32768;
        private const int RunArgumentsDepth = 16;
        private static readonly Regex RunEntryPattern = new Regex(
            @"\A[A-Za-z_][A-Za-z0-9_]*(?:[.+][A-Za-z_][A-Za-z0-9_]*)*\.[A-Za-z_][A-Za-z0-9_]*\z",
            RegexOptions.CultureInvariant);

        /// <summary>仅构造具名静态入口的 ephemeral 请求；源快照与执行授权由适配器负责。</summary>
        public static string BuildRunRequest(string file, string entry, string argumentsJson, int timeoutMs, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(file) || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || file.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                throw new ArgumentException("A .cs source path is required.", nameof(file));
            if (string.IsNullOrWhiteSpace(entry) || entry.Length > 256 || !RunEntryPattern.IsMatch(entry))
                throw new ArgumentException("An explicit Type.Method entry is required.", nameof(entry));
            if (timeoutMs < RunMinTimeoutMs || timeoutMs > RunMaxTimeoutMs)
                throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            var arguments = ReadRunArguments(argumentsJson);
            return AIBridgeJson.Serialize(new Dictionary<string, object>
            {
                { "command", RunCommand },
                { "parameters", new Dictionary<string, object>
                    { { "file", file }, { "entry", entry }, { "args", arguments }, { "mode", "ephemeral" },
                      { "pdb", true }, { "timeout", timeoutMs / 1000.0 }, { "dry-run", dryRun } } },
                { "timeout", timeoutMs }, { "verbose", true }, { "job", false }
            });
        }

        private static object ReadRunArguments(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > RunMaxArgumentsChars)
                throw new ArgumentException("args must be a bounded JSON array.", nameof(json));
            // 包装层也计入深度；精确检查单个 args，阻止闭合包装后注入额外请求字段。
            var wrapper = AIBridgeJson.DeserializeObjectStrict("{\"args\":" + json + "}", RunArgumentsDepth);
            if (wrapper == null || wrapper.Count != 1 || !(Value(wrapper, "args") is List<object> arguments))
                throw new ArgumentException("args must be one JSON array.", nameof(json));
            return AIBridgeJson.PreserveObjectNulls(arguments);
        }

        /// <summary>解释已核实版本的双层 run 回执；不确定结果始终保留远端执行占位。</summary>
        public static PipelineCodeResult ParseRunResponse(string json, int httpStatus, bool dryRun)
        {
            try
            {
                var outer = AIBridgeJson.DeserializeObjectStrict(json);
                if (outer == null || !ValidRunEnvelopeFields(outer) || !ValidRunOuterFields(outer))
                    return InvalidResponse(httpStatus);
                var payload = Value(outer, "result");
                if (payload == null) return ReadRunOuterFailure(outer, httpStatus);
                if (!(payload is Dictionary<string, object> inner) || !ValidRunPayload(inner))
                    return InvalidResponse(httpStatus);
                var result = new PipelineCodeResult { httpStatus = httpStatus };
                if (!ReadDiagnostics(inner, result)) return InvalidResponse(httpStatus);
                // RunScriptResponse.Fail 不请求外层失败：HTTP 200 + outer=true + inner=false 是正常失败。
                if (httpStatus != 200 || !IsTrue(outer, "success") || Text(outer, "command") != RunCommand
                    || !RunHasNoError(outer) || !RunHasNoBusyMarker(outer) || !string.IsNullOrEmpty(Text(outer, "message")))
                    return InvalidResponse(httpStatus);
                if (IsTrue(inner, "success")) return ReadRunSuccess(inner, result, dryRun);
                return ReadRunFailure(inner, result, dryRun);
            }
            catch (Exception)
            {
                return InvalidResponse(httpStatus);
            }
        }

        private static bool ValidRunEnvelopeFields(Dictionary<string, object> data)
        {
            if (!ValidEnvelopeFields(data) || data.ContainsKey("output")) return false;
            foreach (var key in new[] { "compileMs", "executeMs" })
                if (data.TryGetValue(key, out var duration) && (!(duration is long number) || number < 0)) return false;
            foreach (var key in new[] { "assemblyName", "status", "busyReason" })
                if (data.TryGetValue(key, out var text) && text != null && !(text is string)) return false;
            if (data.TryGetValue("retryable", out var retryable) && retryable != null && !(retryable is bool)) return false;
            return true;
        }

        private static bool ValidRunOuterFields(Dictionary<string, object> outer)
        {
            foreach (var key in new[] { "compileMs", "executeMs", "assemblyName", "diagnostics" })
                if (outer.ContainsKey(key)) return false;
            return true;
        }

        private static bool ValidRunPayload(Dictionary<string, object> inner)
        {
            if (!ValidRunEnvelopeFields(inner) || Text(inner, "command") != RunCommand
                || !(Value(inner, "success") is bool) || !string.IsNullOrEmpty(Text(inner, "errorCode"))
                || !RunHasNoBusyMarker(inner)) return false;
            foreach (var key in new[] { "result", "message", "error", "errorDetails", "compileMs", "executeMs", "assemblyName" })
                if (!inner.ContainsKey(key)) return false;
            // verbose 的 run 内层始终包含 diagnostics；null 不是合法的诊断列表。
            return Value(inner, "diagnostics") is List<object>;
        }

        private static bool RunHasNoError(Dictionary<string, object> data)
        {
            return string.IsNullOrEmpty(Text(data, "error")) && string.IsNullOrEmpty(Text(data, "errorCode"))
                && string.IsNullOrEmpty(Text(data, "errorDetails"));
        }

        private static bool RunHasNoBusyMarker(Dictionary<string, object> data)
        {
            return Value(data, "status") == null && Value(data, "retryable") == null && Value(data, "busyReason") == null;
        }

        private static bool RunHasErrorDiagnostic(PipelineCodeResult result)
        {
            return result.diagnostics.Exists(d => string.Equals(d.severity, "error", StringComparison.OrdinalIgnoreCase));
        }

        private static PipelineCodeResult ReadRunSuccess(Dictionary<string, object> inner, PipelineCodeResult result, bool dryRun)
        {
            if (!RunHasNoError(inner) || RunHasErrorDiagnostic(result)) return InvalidResponse(result.httpStatus);
            if (dryRun)
            {
                // 仅这一组精确回执能证明未加载程序集、未调用入口；编译成功不代表入口或参数有效。
                if (Text(inner, "message") != DryRunReceipt || Value(inner, "result") != null
                    || Value(inner, "assemblyName") != null || ReadLong(inner, "executeMs") != 0)
                    return InvalidResponse(result.httpStatus);
            }
            else if (Text(inner, "message") == DryRunReceipt || string.IsNullOrWhiteSpace(Text(inner, "assemblyName")))
                return InvalidResponse(result.httpStatus);
            result.success = true;
            result.status = dryRun ? "validated" : "executed";
            result.mayStillBeRunning = false;
            result.resultValue = AIBridgeJson.PreserveObjectNulls(Value(inner, "result"));
            result.message = Text(inner, "message");
            result.executionTimeMs = ReadLong(inner, "executionTimeMs");
            return result;
        }

        private static PipelineCodeResult ReadRunFailure(Dictionary<string, object> inner, PipelineCodeResult result, bool dryRun)
        {
            if (Value(inner, "result") != null || !string.IsNullOrEmpty(Text(inner, "message"))
                || string.IsNullOrWhiteSpace(Text(inner, "errorDetails"))) return InvalidResponse(result.httpStatus);
            var error = Text(inner, "error");
            var assembly = Text(inner, "assemblyName");
            var executeMs = ReadLong(inner, "executeMs");
            switch (error)
            {
                case "Compilation Failed":
                    if (string.IsNullOrWhiteSpace(assembly) || executeMs != 0) return InvalidResponse(result.httpStatus);
                    return CompleteRunFailure(inner, result, "compile_failed", "pipelineRunCompilationFailed");
                case "Runtime Error":
                    if (dryRun || string.IsNullOrWhiteSpace(assembly) || RunHasErrorDiagnostic(result))
                        return InvalidResponse(result.httpStatus);
                    return CompleteRunFailure(inner, result, "execution_failed", "pipelineRunRuntimeError");
                case "Entry Point Not Found":
                    if (dryRun || string.IsNullOrWhiteSpace(assembly) || !ValidRunBeforeInvocation(inner, result))
                        return InvalidResponse(result.httpStatus);
                    return CompleteRunFailure(inner, result, "rejected", "pipelineRunEntryPointNotFound");
                case "Bad Request":
                    if (!ValidRunBeforeInvocation(inner, result) || (dryRun && assembly != null)
                        || (assembly == null && ReadLong(inner, "compileMs") != 0)
                        || (assembly != null && string.IsNullOrWhiteSpace(assembly))) return InvalidResponse(result.httpStatus);
                    return CompleteRunFailure(inner, result, "rejected", "pipelineRunBadRequest");
                case "File Not Found":
                    if (assembly != null || ReadLong(inner, "compileMs") != 0 || !ValidRunBeforeInvocation(inner, result))
                        return InvalidResponse(result.httpStatus);
                    return CompleteRunFailure(inner, result, "rejected", "pipelineRunFileNotFound");
                default:
                    // Timeout 的 Task 可继续运行；Execution Failed 也可能发生在入口执行之后，不能据此释放占位。
                    result.status = "unknown";
                    result.mayStillBeRunning = true;
                    result.error = error;
                    result.errorDetails = Text(inner, "errorDetails");
                    result.errorCode = error == "Timeout" ? "pipelineRunTimeout" : "pipelineResponseInvalid";
                    return result;
            }
        }

        private static bool ValidRunBeforeInvocation(Dictionary<string, object> inner, PipelineCodeResult result)
        {
            return ReadLong(inner, "executeMs") == 0 && !RunHasErrorDiagnostic(result);
        }

        private static PipelineCodeResult CompleteRunFailure(Dictionary<string, object> inner, PipelineCodeResult result,
            string status, string code)
        {
            result.status = status;
            result.errorCode = code;
            result.error = Text(inner, "error");
            result.errorDetails = Text(inner, "errorDetails");
            result.executionTimeMs = ReadLong(inner, "executionTimeMs");
            result.mayStillBeRunning = false;
            return result;
        }

        private static PipelineCodeResult ReadRunOuterFailure(Dictionary<string, object> outer, int httpStatus)
        {
            if (!ValidRunRefusalShape(outer) || (httpStatus >= 200 && httpStatus < 300)
                || (httpStatus != 503 && !RunHasNoBusyMarker(outer))) return InvalidResponse(httpStatus);
            var error = Text(outer, "error");
            var code = Text(outer, "errorCode");
            switch (httpStatus)
            {
                case 401:
                    if (error == "Unauthorized" && string.IsNullOrEmpty(code))
                        return CompleteRunRefusal(outer, httpStatus, "failed", "pipelineAuthenticationFailed");
                    break;
                case 403:
                    if (error == "Forbidden" && string.IsNullOrEmpty(code))
                        return CompleteRunRefusal(outer, httpStatus, "failed", "pipelineAccessDenied");
                    break;
                case 429:
                    if (ValidRunCommandRefusal(outer) && error == "Too Many Queued Commands" && string.IsNullOrEmpty(code))
                        return CompleteRunRefusal(outer, httpStatus, "unavailable", "pipelineUnavailable");
                    break;
                case 503:
                    if (ValidRunCommandRefusal(outer) && error == "Server Busy" && string.IsNullOrEmpty(code)
                        && Text(outer, "status") == "busy" && IsTrue(outer, "retryable") && ValidRunBusyReason(outer))
                        return CompleteRunRefusal(outer, httpStatus, "unavailable", "pipelineUnavailable");
                    break;
                case 400:
                    if (ValidRunPreflightFailure(outer))
                        return CompleteRunRefusal(outer, httpStatus, "rejected", code ?? "pipelineRunRejected");
                    break;
                case 413:
                    if (error == "Payload Too Large" && string.IsNullOrEmpty(code))
                        return CompleteRunRefusal(outer, httpStatus, "rejected", "pipelineRunRejected");
                    break;
            }
            // HTTP 504 从不证明远端停止；非已知、完整且自洽的拒绝形状也不能清除执行占位。
            return InvalidResponse(httpStatus);
        }

        private static bool ValidRunRefusalShape(Dictionary<string, object> outer)
        {
            if (outer.TryGetValue("success", out var success) && (!(success is bool flag) || flag)) return false;
            if (outer.TryGetValue("command", out var command) && command != null && !Equals(command, RunCommand)) return false;
            if (string.IsNullOrWhiteSpace(Text(outer, "error")) || !string.IsNullOrEmpty(Text(outer, "message"))) return false;
            return Value(outer, "executionTimeMs") == null;
        }

        private static bool ValidRunCommandRefusal(Dictionary<string, object> outer)
        {
            return Value(outer, "success") is bool success && !success && Text(outer, "command") == RunCommand;
        }

        private static bool ValidRunBusyReason(Dictionary<string, object> outer)
        {
            var reason = Text(outer, "busyReason");
            return reason == null || reason == "settling" || reason == "blocked_by_dialog";
        }

        private static bool ValidRunPreflightFailure(Dictionary<string, object> outer)
        {
            var error = Text(outer, "error");
            var code = Text(outer, "errorCode");
            if (code == "INVALID_COMMAND_ARGS")
                return ValidRunCommandRefusal(outer) && error == "Invalid Command Arguments";
            if (!string.IsNullOrEmpty(code)) return false;
            if (error == "Parameter Validation Failed" || error == "Command Not Found")
                return ValidRunCommandRefusal(outer);
            return error == "Bad Request" || error == "Invalid JSON" || error == "Invalid Request";
        }

        private static PipelineCodeResult CompleteRunRefusal(Dictionary<string, object> outer, int httpStatus,
            string status, string code)
        {
            return new PipelineCodeResult
            {
                httpStatus = httpStatus, status = status, errorCode = code,
                error = Text(outer, "error"), errorDetails = Text(outer, "errorDetails"), mayStillBeRunning = false
            };
        }
    }
}
