using System;
using System.Reflection;
using UnityEditor.TestTools.TestRunner.Api;

namespace AIBridge.Editor
{
    /// <summary>Pipeline 提交前的主线程探针；本地互斥不能约束外部 UI 或其它插件。</summary>
    [UnityEditor.InitializeOnLoad]
    internal static class PipelineTestExecutionGate
    {
        static PipelineTestExecutionGate() { PipelineTestTracker.GateProbe = Refusal; }

        internal static string Refusal()
        {
            if (AIBridgeBuildTracker.Busy) return "An AIBridge build is active or unknown.";
            if (ProfilerNativeGate.Busy) return "Native Profiler/Memory state is active or unknown.";
            if (CodeCommand.HasActiveCodeOperation) return "An AIBridge code operation is active or unknown.";
            if (TestRunTracker.HasTrackedOrQueuedRun) return "A native AIBridge test run is active or queued.";
            return ProbeNative(typeof(TestRunnerApi));
        }

        internal static string ProbeNative(Type apiType)
        {
            try
            {
                var method = apiType == null ? null : apiType.GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (method == null || method.ReturnType != typeof(bool) || method.ContainsGenericParameters)
                    return "Exact Unity Test Framework activity probe is unavailable.";
                return (bool)method.Invoke(null, null) ? "Unity Test Framework is running tests." : null;
            }
            catch (Exception) { return "Unity Test Framework activity could not be confirmed."; }
        }
    }
}
