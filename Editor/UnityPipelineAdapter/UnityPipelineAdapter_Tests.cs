using System;
using System.Threading.Tasks;
using AIBridge.Internal.Json;
using UnityEditor;

namespace AIBridge.Editor.UnityPipelineAdapter
{
    public static partial class UnityPipelineAdapter
    {
        // Prepare在主线程捕获身份，worker只访问纯数据和HTTP；两种触发共用传输规则。
        private static void PrepareBoundExecution(out PipelineServerDescriptor descriptor, out string path)
        {
            if (!IsEditorSupported() || EditorApplication.isPlayingOrWillChangePlaymode
                || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new NotSupportedException("Pipeline execution requires an idle Edit Mode Unity 6 Editor.");
#if UNITY_2020_2_OR_NEWER
            if (AssetDatabase.IsAssetImportWorkerProcess()) throw new NotSupportedException("Import workers cannot execute Pipeline commands.");
#endif
            var package = FindPipelinePackage();
            if (package == null || package.Version != PipelineCodeProtocol.SupportedVersion)
                throw new NotSupportedException("Pipeline execution protocol version is unsupported.");
            path = GetPipelineDescriptorPath();
            if (!TryReadPortDescriptor(path, out descriptor)) throw new InvalidOperationException("Pipeline target is unavailable.");
            lock (EvalLock)
            {
                if (s_EvalRequestActive) throw new InvalidOperationException("Pipeline execution is busy.");
                s_EvalRequestActive = true;
            }
        }

        internal static PipelineTestSession PrepareTestRun(PipelineTestOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            PrepareBoundExecution(out var descriptor, out var path);
            try { return new TestRunSession(options, descriptor, path); }
            catch { lock (EvalLock) { s_EvalRequestActive = false; } throw; }
        }

        private sealed class TestRunSession : PipelineTestSession
        {
            private readonly BoundExecution<PipelineTestResult> _execution;
            internal TestRunSession(PipelineTestOptions options, PipelineServerDescriptor descriptor, string path)
            {
                _execution = new BoundExecution<PipelineTestResult>(descriptor, path, PipelineTestProtocol.BuildRequest(options),
                    options.TimeoutMs, PipelineTestProtocol.MaxJsonBytes,
                    (json, status) => PipelineTestProtocol.ParseResponse(json, status, options), result =>
                    { result.data = AIBridgeJson.PreserveObjectNulls(RedactValue(result.data, descriptor.evalToken)); return result; },
                    () => PipelineTestProtocol.Unknown());
            }
            public override string TargetIdentity => _execution.Identity;
            public override string Generation => JobGeneration;
            public override Task<PipelineTestResult> SubmitAsync() { return _execution.SubmitAsync(); }
            public override void Release(bool executionEnded) { _execution.Release(); }
        }
    }
}
