using System.Threading.Tasks;
using AIBridge.Internal.Json;

namespace AIBridge.Editor.UnityPipelineAdapter
{
    public static partial class UnityPipelineAdapter
    {
        internal static PipelineCompileSession PrepareCompile(int timeout)
        {
            var body = PipelineCompileProtocol.BuildRequest(timeout);
            PrepareBoundExecution(out var descriptor, out var path);
            try { return new CompileSession(timeout, body, descriptor, path); }
            catch { lock (EvalLock) { s_EvalRequestActive = false; } throw; }
        }

        private sealed class CompileSession : PipelineCompileSession
        {
            private readonly BoundExecution<PipelineCompileResult> _execution;
            internal CompileSession(int timeout, string body, PipelineServerDescriptor descriptor, string path)
            {
                _execution = new BoundExecution<PipelineCompileResult>(descriptor, path, body, timeout, PipelineCompileProtocol.MaxJsonBytes,
                    PipelineCompileProtocol.ParseResponse, result =>
                    { result.data = AIBridgeJson.PreserveObjectNulls(RedactValue(result.data, descriptor.evalToken)); return result; },
                    () => PipelineCompileProtocol.Unknown());
            }
            public override string TargetIdentity => _execution.Identity;
            public override string Generation => JobGeneration;
            public override Task<PipelineCompileResult> SubmitAsync() { return _execution.SubmitAsync(); }
            public override void Release() { _execution.Release(); }
        }
    }
}
