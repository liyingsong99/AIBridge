using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using AIBridge.Internal.Json;

namespace AIBridge.Editor.UnityPipelineAdapter
{
    public static partial class UnityPipelineAdapter
    {
        private static bool s_DiagnosticActive;

        // 只开放两个只读命令；主线程捕获身份，worker 不访问 Unity API。
        internal static Task<CommandResult> QueryDiagnosticsAsync(string requestId, string action, int timeoutMs)
        {
            var body = PipelineDiagnosticsProtocol.BuildRequest(action, timeoutMs);
            if (!IsEditorSupported() || EditorApplication.isPlayingOrWillChangePlaymode)
                return Task.FromResult(DiagnosticFailure(requestId, "unsupported", "Pipeline diagnostics require an Edit Mode Unity 6 Editor."));
#if UNITY_2020_2_OR_NEWER
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return Task.FromResult(DiagnosticFailure(requestId, "unsupported", "Asset import workers cannot query Pipeline."));
#endif
            var package = FindPipelinePackage();
            if (package == null || package.Version != PipelineCodeProtocol.SupportedVersion)
                return Task.FromResult(DiagnosticFailure(requestId, "unsupported", "Pipeline package protocol version is unsupported."));
            var path = GetPipelineDescriptorPath();
            if (!TryReadPortDescriptor(path, out var descriptor))
                return Task.FromResult(DiagnosticFailure(requestId, "unavailable", "Pipeline descriptor does not match the current Editor."));
            lock (EvalLock)
            {
                if (s_DiagnosticActive)
                    return Task.FromResult(DiagnosticFailure(requestId, "busy", "A Pipeline diagnostic query is active."));
                s_DiagnosticActive = true;
            }
            try { return Task.Run(() => ExecuteDiagnosticQuery(requestId, action, body, timeoutMs, descriptor, path)); }
            catch { lock (EvalLock) { s_DiagnosticActive = false; } throw; }
        }

        private static CommandResult ExecuteDiagnosticQuery(string id, string action, string body, int timeout,
            PipelineServerDescriptor descriptor, string path)
        {
            try
            {
                if (!DescriptorIsUnchanged(path, descriptor))
                    return DiagnosticFailure(id, "unknown", "Pipeline target changed before the query.");
                var request = CreateAuthenticatedRequest(descriptor, "/api/exec", timeout + TransportGraceMs);
                request.Method = "POST"; request.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body); request.ContentLength = bytes.Length;
                using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                        return ReadDiagnosticResponse(id, action, response, descriptor, path);
                }
                catch (WebException ex)
                {
                    using (var response = ex.Response as HttpWebResponse)
                        return response == null ? DiagnosticFailure(id, "unknown", "Pipeline diagnostic transport did not confirm a response.")
                            : ReadDiagnosticResponse(id, action, response, descriptor, path);
                }
            }
            catch (Exception) { return DiagnosticFailure(id, "unknown", "Pipeline diagnostic query could not be verified."); }
            finally { lock (EvalLock) { s_DiagnosticActive = false; } }
        }

        private static CommandResult ReadDiagnosticResponse(string id, string action, HttpWebResponse response,
            PipelineServerDescriptor descriptor, string path)
        {
            using (var reader = new StreamReader(response.GetResponseStream(), new UTF8Encoding(false, true)))
            {
                var buffer = new char[MaxResponseChars + 1]; var count = 0;
                while (count < buffer.Length)
                {
                    var read = reader.Read(buffer, count, buffer.Length - count);
                    if (read == 0) break;
                    count += read;
                }
                if (count > MaxResponseChars) return DiagnosticFailure(id, "unknown", "Pipeline diagnostic response exceeded its bound.");
                var parsed = PipelineDiagnosticsProtocol.ParseResponse(action, new string(buffer, 0, count), (int)response.StatusCode);
                // 失败分支也保留纯协议产生的 unavailable/httpStatus，不透传远端正文。
                // 脱敏发生在严格解析之后，复用键碰撞检测，并保留显式 null。
                var data = AIBridgeJson.PreserveObjectNulls(RedactValue(parsed.data, descriptor.evalToken));
                if (!DescriptorIsUnchanged(path, descriptor))
                    return DiagnosticFailure(id, "unknown", "Pipeline target changed during the query.");
                return DiagnosticResult(id, parsed.valid, parsed.error, data);
            }
        }

        private static CommandResult DiagnosticResult(string id, bool valid, string error, object safeData)
        {
            if (valid) return CommandResult.Success(id, safeData);
            var failed = CommandResult.Failure(id, error ?? "Pipeline diagnostic response is unavailable.");
            failed.data = safeData;
            return failed;
        }

        private static CommandResult DiagnosticFailure(string id, string status, string message)
        {
            var result = CommandResult.Failure(id, message);
            result.data = new { backend = "pipeline", status, operationCorrelation = false };
            return result;
        }
    }
}
