using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace AIBridge.Editor.UnityPipelineAdapter
{
    public static partial class UnityPipelineAdapter
    {
        private static readonly List<PipelineRunSource.Snapshot> UnknownRunSnapshots = new List<PipelineRunSource.Snapshot>();
        internal static Task<PipelineCodeResult> RunAsync(string source, string sourcePath, string entry, string argumentsJson,
            int timeoutMs, bool dryRun, bool confirmed)
        {
            // 与 eval 共用授权及并发门；反射直调也不能省略每请求确认。
            if (!PipelineCodeProtocol.IsExecutionAuthorized())
                return RejectedSubmission("pipelineAuthorizationDenied", "Code execution is disabled.");
            if (!dryRun && !confirmed)
                return RejectedSubmission("pipelineConfirmationRequired", "Pipeline execution requires per-request confirmation.");
            if (timeoutMs < 1000 || timeoutMs > 60000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            var descriptorPath = GetPipelineDescriptorPath();
            if (!TryReadPortDescriptor(descriptorPath, out var descriptor))
                return RejectedSubmission("pipelineDescriptorMismatch", "Pipeline descriptor does not match the current Editor.");
            var package = FindPipelinePackage();
            if (package == null || package.Version != PipelineCodeProtocol.SupportedVersion)
                return RejectedSubmission("pipelineVersionUnsupported", "Pipeline run protocol version is unsupported.");
            lock (EvalLock)
            {
                if (s_EvalRequestActive) return RejectedSubmission("pipelineRequestBusy", "A Pipeline code request is already active.");
                s_EvalRequestActive = true;
            }
            PipelineRunSource.Snapshot snapshot = null;
            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                snapshot = PipelineRunSource.Capture(projectRoot, source, sourcePath);
                var body = PipelineCodeProtocol.BuildRunRequest(snapshot.Path, entry, argumentsJson, timeoutMs, dryRun);
                var captured = snapshot;
                // 持有 FileShare.Read 句柄至响应：禁止本机写改/替换快照，worker只访问已捕获目标。
                return Task.Run(() => ExecuteRunSnapshot(captured, descriptor, descriptorPath, body, timeoutMs, dryRun));
            }
            catch
            {
                snapshot?.DeleteAfterConfirmedOutcome(); // 尚未安排 POST，可清理本次独有源码。
                lock (EvalLock) { s_EvalRequestActive = false; }
                throw;
            }
        }

        private static PipelineCodeResult ExecuteRunSnapshot(PipelineRunSource.Snapshot snapshot, PipelineServerDescriptor descriptor,
            string descriptorPath, string body, int timeoutMs, bool dryRun)
        {
            PipelineCodeResult result = null;
            try
            {
                result = ExecuteEvalDryRunRequest(descriptor, descriptorPath, body, timeoutMs, dryRun, true);
                return result;
            }
            finally
            {
                // 未知结果可能尚在排队读取；不删源码、不重放。正常/明确拒绝后只清理本次快照。
                if (result != null && !result.mayStillBeRunning) snapshot.DeleteAfterConfirmedOutcome();
                else
                {
                    // 本进程继续持有只读锁；显式 cancel 也不能允许尚未读取的源码被修改。
                    // reload/进程退出会失去 OS 句柄，持久状态仍标 unknown，不声称远端已停止。
                    lock (EvalLock) { UnknownRunSnapshots.Add(snapshot); }
                }
            }
        }
    }
}
