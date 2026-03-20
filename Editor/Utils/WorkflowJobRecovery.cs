using System;
using UnityEditor;
using UnityEditor.Build;

namespace AIBridge.Editor
{
    [InitializeOnLoad]
    public static class WorkflowJobRecovery
    {
        static WorkflowJobRecovery()
        {
            EditorApplication.delayCall += ReconcileStaleJobs;
        }

        private static void ReconcileStaleJobs()
        {
            try
            {
                ReconcileJobType("compile.unity", EditorApplication.isCompiling);
                ReconcileBuildJobType("build.android", BuildPipeline.isBuildingPlayer, BuildTarget.Android);
                ReconcileBuildJobType("build.ios", BuildPipeline.isBuildingPlayer, BuildTarget.iOS);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"WorkflowJobRecovery failed: {ex.Message}");
            }
        }

        private static void ReconcileJobType(string jobType, bool stillRunning)
        {
            var activeState = WorkflowJobCacheManager.LoadActive(jobType);
            if (activeState == null)
            {
                return;
            }

            if (stillRunning)
            {
                return;
            }

            if (!string.Equals(activeState.status, "running", StringComparison.OrdinalIgnoreCase))
            {
                WorkflowJobCacheManager.ClearActive(jobType);
                WorkflowJobCacheManager.ClearGlobalActive(activeState.jobId);
                return;
            }

            activeState.status = "failed";
            activeState.error = "Job was interrupted before completion and has been reconciled as failed.";
            activeState.completedAtUtc = DateTime.UtcNow.ToString("O");
            if (activeState.result == null)
            {
                activeState.result = new System.Collections.Generic.Dictionary<string, object>();
            }

            activeState.result["status"] = "failed";
            activeState.result["error"] = activeState.error;
            activeState.result["reason"] = "abandoned";
            WorkflowJobCacheManager.Save(activeState);
            WorkflowJobCacheManager.ClearActive(jobType);
            WorkflowJobCacheManager.ClearGlobalActive(activeState.jobId);
        }

        private static void ReconcileBuildJobType(string jobType, bool stillRunning, BuildTarget expectedTarget)
        {
            var activeState = WorkflowJobCacheManager.LoadActive(jobType);
            if (activeState == null)
            {
                return;
            }

            if (stillRunning && EditorUserBuildSettings.activeBuildTarget == expectedTarget)
            {
                return;
            }

            if (!string.Equals(activeState.status, "running", StringComparison.OrdinalIgnoreCase))
            {
                WorkflowJobCacheManager.ClearActive(jobType);
                WorkflowJobCacheManager.ClearGlobalActive(activeState.jobId);
                return;
            }

            activeState.status = "failed";
            activeState.error = "Job was interrupted before completion and has been reconciled as failed.";
            activeState.completedAtUtc = DateTime.UtcNow.ToString("O");
            if (activeState.result == null)
            {
                activeState.result = new System.Collections.Generic.Dictionary<string, object>();
            }

            activeState.result["status"] = "failed";
            activeState.result["error"] = activeState.error;
            activeState.result["reason"] = "abandoned";
            WorkflowJobCacheManager.Save(activeState);
            WorkflowJobCacheManager.ClearActive(jobType);
            WorkflowJobCacheManager.ClearGlobalActive(activeState.jobId);
        }
    }
}
