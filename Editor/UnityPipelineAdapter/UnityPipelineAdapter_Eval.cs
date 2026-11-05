using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;

namespace AIBridge.Editor.UnityPipelineAdapter
{
    public static partial class UnityPipelineAdapter
    {
        private static readonly object EvalLock = new object();
        private static bool s_EvalRequestActive;
        private const int TransportGraceMs = 2000;
        private const int MaxResponseChars = 1024 * 1024;

        internal static Task<PipelineCodeResult> EvaluateAsync(string code, int timeoutMs, bool dryRun, bool confirmed)
        {
            // 可选入口也复核设置和单次确认；直接反射调用不能省略执行意图。
            if (!PipelineCodeProtocol.IsExecutionAuthorized())
                return RejectedSubmission("pipelineAuthorizationDenied", "Code execution is disabled.");
            if (!dryRun && !confirmed)
                return RejectedSubmission("pipelineConfirmationRequired", "Pipeline execution requires per-request confirmation.");
            if (timeoutMs < 1000 || timeoutMs > 60000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            var body = PipelineCodeProtocol.BuildEvalRequest(code, timeoutMs, dryRun);
            var descriptorPath = GetPipelineDescriptorPath();
            if (!TryReadPortDescriptor(descriptorPath, out var descriptor))
                return RejectedSubmission("pipelineDescriptorMismatch", "Pipeline descriptor does not match the current Editor.");
            var package = FindPipelinePackage();
            if (package == null || package.Version != PipelineCodeProtocol.SupportedVersion)
                return RejectedSubmission("pipelineVersionUnsupported", "Pipeline dry-run protocol version is unsupported.");
            lock (EvalLock)
            {
                if (s_EvalRequestActive) return RejectedSubmission("pipelineRequestBusy", "A Pipeline eval request is already active.");
                s_EvalRequestActive = true;
            }
            // 只捕获已验证的目标和源码快照；worker 不访问 Unity API，不重读新目标，不重试 POST。
            try { return Task.Run(() => ExecuteEvalDryRunRequest(descriptor, descriptorPath, body, timeoutMs, dryRun)); }
            catch { lock (EvalLock) { s_EvalRequestActive = false; } throw; }
        }

        private static PipelineCodeResult ExecuteEvalDryRunRequest(PipelineServerDescriptor descriptor, string descriptorPath, string body, int timeoutMs, bool dryRun, bool run = false)
        {
            try
            {
                if (!DescriptorIsUnchanged(descriptorPath, descriptor))
                    return new PipelineCodeResult { status = "not_submitted", errorCode = "pipelineTargetChanged", error = "Pipeline target changed before submission." };
                var request = CreateAuthenticatedRequest(descriptor, "/api/exec", timeoutMs + TransportGraceMs);
                request.Method = "POST";
                request.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bytes.Length;
                using (var stream = request.GetRequestStream()) { stream.Write(bytes, 0, bytes.Length); }
                using (var response = (HttpWebResponse)request.GetResponse())
                    return ReadEvalResponse(response, descriptor, descriptorPath, dryRun, run);
            }
            catch (WebException exception)
            {
                using (var response = exception.Response as HttpWebResponse)
                {
                    if (response != null) return ReadEvalResponse(response, descriptor, descriptorPath, dryRun, run);
                }
                return UnknownTransport(exception.Status.ToString());
            }
            catch (Exception exception) { return UnknownTransport(exception.GetType().Name); }
            finally { lock (EvalLock) { s_EvalRequestActive = false; } }
        }

        private static PipelineCodeResult ReadEvalResponse(HttpWebResponse response, PipelineServerDescriptor descriptor, string descriptorPath, bool dryRun, bool run)
        {
            try
            {
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var buffer = new char[MaxResponseChars + 1];
                    var count = 0;
                    while (count < buffer.Length)
                    {
                        var read = reader.Read(buffer, count, buffer.Length - count);
                        if (read == 0) break;
                        count += read;
                    }
                    if (count > MaxResponseChars) return UnknownTransport("responseLimitExceeded");
                    var json = new string(buffer, 0, count);
                    // 先解析再脱敏：原文替换会把不同 JSON 键合并，绕过后续碰撞检测。
                    // 解析错误只回传固定错误类型，原始响应不写日志或持久化。
                    var result = run
                        ? PipelineCodeProtocol.ParseRunResponse(json, (int)response.StatusCode, dryRun)
                        : PipelineCodeProtocol.ParseEvalResponse(json, (int)response.StatusCode, dryRun);
                    RedactResult(result, descriptor.evalToken);
                    if (!DescriptorIsUnchanged(descriptorPath, descriptor)) return UnknownTransport("pipelineTargetChanged");
                    return result;
                }
            }
            catch (Exception exception) { return UnknownTransport(exception.GetType().Name); }
        }

        private static void RedactResult(PipelineCodeResult result, string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            result.error = Redact(result.error, token);
            result.errorCode = Redact(result.errorCode, token);
            result.errorDetails = Redact(result.errorDetails, token);
            result.message = Redact(result.message, token);
            result.output = Redact(result.output, token);
            result.resultValue = global::AIBridge.Internal.Json.AIBridgeJson.PreserveObjectNulls(RedactValue(result.resultValue, token));
            foreach (var diagnostic in result.diagnostics)
            {
                diagnostic.file = Redact(diagnostic.file, token);
                diagnostic.message = Redact(diagnostic.message, token);
                diagnostic.code = Redact(diagnostic.code, token);
                diagnostic.severity = Redact(diagnostic.severity, token);
            }
        }

        private static object RedactValue(object value, string token)
        {
            if (value is string text) return Redact(text, token);
            if (value is System.Collections.IDictionary dictionary)
            {
                var cleaned = new System.Collections.Generic.Dictionary<string, object>(StringComparer.Ordinal);
                foreach (System.Collections.DictionaryEntry item in dictionary)
                {
                    // JSON 转义会在解析后成为键名；键和值都脱敏，碰撞时拒绝而非静默丢失结果。
                    if (!(item.Key is string key)) throw new InvalidDataException("Invalid result key.");
                    key = Redact(key, token);
                    if (cleaned.ContainsKey(key)) throw new InvalidDataException("Redacted result key collision.");
                    cleaned.Add(key, RedactValue(item.Value, token));
                }
                return cleaned;
            }
            if (value is System.Collections.IList list)
            {
                var cleaned = new System.Collections.ArrayList();
                foreach (var item in list) cleaned.Add(RedactValue(item, token));
                return cleaned;
            }
            return value;
        }

        private static string Redact(string text, string token)
        {
            return text == null ? null : text.Replace(token, "[redacted]");
        }

        private static Task<PipelineCodeResult> RejectedSubmission(string code, string message)
        {
            return Task.FromResult(new PipelineCodeResult { status = "not_submitted", errorCode = code, error = message });
        }

        private static PipelineCodeResult UnknownTransport(string detail)
        {
            return new PipelineCodeResult
            {
                status = "unknown", errorCode = "pipelineTransportUnknown", mayStillBeRunning = true,
                error = "Pipeline did not confirm the result; do not replay automatically.", errorDetails = detail
            };
        }

        private static HttpWebRequest CreateAuthenticatedRequest(PipelineServerDescriptor descriptor, string path, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + descriptor.port + path);
            request.Method = "GET";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.Proxy = null;
            request.AllowAutoRedirect = false;
            request.KeepAlive = false;
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + descriptor.evalToken;
            return request;
        }

        private static bool DescriptorIsUnchanged(string path, PipelineServerDescriptor expected)
        {
            try
            {
                var current = global::AIBridge.Internal.Json.AIBridgeJson.DeserializeObject(File.ReadAllText(path));
                return current != null
                    && Convert.ToInt32(current["pid"]) == expected.pid
                    && Convert.ToInt32(current["port"]) == expected.port
                    && IsSamePath(current["projectPath"] as string, expected.projectPath)
                    && string.Equals(current["unityVersion"] as string, expected.unityVersion, StringComparison.Ordinal)
                    && string.Equals(current["startedAt"] as string, expected.startedAt, StringComparison.Ordinal)
                    && string.Equals(current["evalToken"] as string, expected.evalToken, StringComparison.Ordinal);
            }
            catch { return false; }
        }
    }
}
