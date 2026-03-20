using System;
using UnityEditor;
using UnityEditor.Compilation;

namespace AIBridge.Editor
{
    [InitializeOnLoad]
    public static class WorkflowJobCompileTracker
    {
        private const string CompileUnityJobType = "compile.unity";

        static WorkflowJobCompileTracker()
        {
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        private static void OnCompilationFinished(object context)
        {
            try
            {
                var state = WorkflowJobCacheManager.LoadActive(CompileUnityJobType);
                if (state == null)
                {
                    return;
                }

                var compileResult = CompilationTracker.GetResult();
                state.phase = "compile";
                state.completedAtUtc = DateTime.UtcNow.ToString("O");
                state.status = compileResult.status == CompilationTracker.CompilationStatus.Failed ? "failed" : "success";
                state.error = state.status == "failed" ? "Unity compilation failed." : null;
                state.result = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["status"] = state.status,
                    ["errorCount"] = compileResult.errorCount,
                    ["warningCount"] = compileResult.warningCount,
                    ["duration"] = compileResult.durationSeconds
                };

                WorkflowJobCacheManager.Save(state);
                WorkflowJobCacheManager.ClearActive(CompileUnityJobType);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"WorkflowJobCompileTracker failed to persist compile result: {ex.Message}");
            }
        }
    }
}
