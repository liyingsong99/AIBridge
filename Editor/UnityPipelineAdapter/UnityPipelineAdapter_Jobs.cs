using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace AIBridge.Editor.UnityPipelineAdapter
{
    public static partial class UnityPipelineAdapter
    {
        // 内部观察入口，不提交/取消作业，不改变 code 的 busy/unknown 占位。
        internal static Task<PipelineJobObservation> ObserveJobAsync(string jobId, string command, int timeoutMs)
        {
            if (!PipelineCodeProtocol.IsExecutionAuthorized())
                return Task.FromResult(UnknownJob("pipelineAuthorizationDenied"));
            if (!PipelineJobProtocol.IsJobId(jobId) || (command != "script eval" && command != "script run"))
                return Task.FromResult(UnknownJob("pipelineJobInputInvalid"));
            if (timeoutMs < 1000 || timeoutMs > 60000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            var descriptorPath = GetPipelineDescriptorPath();
            if (!TryReadPortDescriptor(descriptorPath, out var descriptor))
                return Task.FromResult(UnknownJob("pipelineDescriptorMismatch"));
            var package = FindPipelinePackage();
            if (package == null || package.Version != PipelineCodeProtocol.SupportedVersion)
                return Task.FromResult(UnknownJob("pipelineVersionUnsupported"));
            // 路径和 ID 只来自固定模板及已验证32位hex；不接受调用方URL/凭据/任意命令。
            return Task.Run(() => ReadJobOnce(descriptor, descriptorPath, jobId, command, timeoutMs));
        }

        private static PipelineJobObservation ReadJobOnce(PipelineServerDescriptor descriptor, string descriptorPath,
            string jobId, string command, int timeoutMs)
        {
            try
            {
                if (!DescriptorIsUnchanged(descriptorPath, descriptor)) return UnknownJob("pipelineTargetChanged");
                var request = CreateAuthenticatedRequest(descriptor, "/api/job?id=" + jobId + "&omit_nulls=false", timeoutMs);
                using (var response = (HttpWebResponse)request.GetResponse())
                    return ReadJobResponse(response, descriptor, descriptorPath, jobId, command);
            }
            catch (WebException exception)
            {
                using (var response = exception.Response as HttpWebResponse)
                {
                    if (response != null) return ReadJobResponse(response, descriptor, descriptorPath, jobId, command);
                }
                return UnknownJob("pipelineJobTransportUnknown");
            }
            catch (Exception) { return UnknownJob("pipelineJobTransportUnknown"); }
        }

        private static PipelineJobObservation ReadJobResponse(HttpWebResponse response, PipelineServerDescriptor descriptor,
            string descriptorPath, string jobId, string command)
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
                    if (count > MaxResponseChars) return UnknownJob("pipelineJobResponseLimit");
                    var observation = PipelineJobProtocol.ParseStatus(new string(buffer, 0, count),
                        (int)response.StatusCode, jobId, command);
                    RedactJobObservation(observation, descriptor.evalToken);
                    if (!DescriptorIsUnchanged(descriptorPath, descriptor)) return UnknownJob("pipelineTargetChanged");
                    return observation;
                }
            }
            catch (Exception) { return UnknownJob("pipelineJobResponseInvalid"); }
        }

        private static void RedactJobObservation(PipelineJobObservation observation, string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            observation.error = Redact(observation.error, token);
            observation.errorCode = Redact(observation.errorCode, token);
            observation.errorDetails = Redact(observation.errorDetails, token);
            observation.resultValue = global::AIBridge.Internal.Json.AIBridgeJson.PreserveObjectNulls(RedactValue(observation.resultValue, token));
            observation.progress = global::AIBridge.Internal.Json.AIBridgeJson.PreserveObjectNulls(RedactValue(observation.progress, token));
            if (observation.warnings != null)
                for (var i = 0; i < observation.warnings.Count; i++) observation.warnings[i] = Redact(observation.warnings[i], token);
        }

        private static PipelineJobObservation UnknownJob(string code)
        {
            return new PipelineJobObservation
            {
                status = "unknown", errorCode = code,
                error = "Pipeline did not establish a job observation; do not replay or assume execution stopped."
            };
        }
    }
}
