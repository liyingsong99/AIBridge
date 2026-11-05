using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AIBridge.Internal.Json;
using AIBridge.Runtime.Internal;
using UnityEditor;

namespace AIBridge.Editor
{
    internal static class PipelineDiagnosticsCommand
    {
        private const string AdapterType = "AIBridge.Editor.UnityPipelineAdapter.UnityPipelineAdapter, cn.lys.aibridge.Editor.UnityPipelineAdapter";
        private static readonly Dictionary<string, Task<CommandResult>> Pending = new Dictionary<string, Task<CommandResult>>(StringComparer.Ordinal);

        internal static CommandResult Execute(CommandRequest request, string action)
        {
            try
            {
                if (action != "compile_status" && action != "test_list")
                    return CommandResult.Failure(request.id, "This Pipeline backend action is unsupported; no native fallback was submitted.");
                if (request.@params == null || !ValidId(request.id)) throw new ArgumentException();
                foreach (var key in request.@params.Keys)
                    if (key != "action" && key != "backend" && key != "timeout" && !(action == "test_list" && key == "mode"))
                        throw new ArgumentException("Unsupported Pipeline diagnostic parameter.");
                if (action == "test_list" && request.HasParam("mode") && request.GetParam<string>("mode", null) != "EditMode")
                    throw new ArgumentException("Pipeline test list supports only EditMode.");
                var timeout = Timeout(request);
                if (Pending.Count != 0) return CommandResult.Failure(request.id, "A Pipeline diagnostic query is already active.");
                var adapter = System.Type.GetType(AdapterType);
                var method = adapter == null ? null : adapter.GetMethod("QueryDiagnosticsAsync", BindingFlags.Static | BindingFlags.NonPublic,
                    null, new[] { typeof(string), typeof(string), typeof(int) }, null);
                if (method == null) return CommandResult.Failure(request.id, "Pipeline diagnostics adapter is unavailable.");
                var task = method.Invoke(null, new object[] { request.id, action, timeout }) as Task<CommandResult>;
                if (task == null) return CommandResult.Failure(request.id, "Pipeline diagnostic adapter returned an invalid task.");
                if (task.IsCompleted) return Complete(request.id, task);
                Pending.Add(request.id, task);
                EditorApplication.update -= Tick; EditorApplication.update += Tick;
                return null;
            }
            catch (Exception) { return CommandResult.Failure(request.id, "Pipeline diagnostic parameters or adapter are unavailable; no fallback was submitted."); }
        }

        internal static bool ValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128) return false;
            foreach (var c in id)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
            return true;
        }

        private static int Timeout(CommandRequest request)
        {
            if (!request.@params.TryGetValue("timeout", out var value)) return 10000;
            if (!(value is long) && !(value is int)) throw new ArgumentException();
            var timeout = Convert.ToInt64(value);
            if (timeout < 1000 || timeout > 60000) throw new ArgumentException();
            return (int)timeout;
        }

        private static CommandResult Complete(string id, Task<CommandResult> task)
        {
            // 只读请求丢失/重载不认证任何操作结果；不重放、不伪造成功。
            if (task.IsCanceled || task.IsFaulted) return CommandResult.Failure(id, "Pipeline diagnostic result is unknown.");
            return task.Result ?? CommandResult.Failure(id, "Pipeline diagnostic result is unavailable.");
        }

        private static void Tick()
        {
            foreach (var item in new List<KeyValuePair<string, Task<CommandResult>>>(Pending))
            {
                if (!item.Value.IsCompleted) continue;
                var result = Complete(item.Key, item.Value);
                try
                {
                    var directory = Path.GetFullPath(Path.Combine(AIBridge.BridgeDirectory, "results"));
                    var path = Path.GetFullPath(Path.Combine(directory, item.Key + ".json"));
                    if (!ValidId(item.Key) || Path.GetDirectoryName(path) != directory) throw new InvalidDataException();
                    AIBridgeAtomicFile.WriteTextAtomic(path, AIBridgeJson.Serialize(result, true), Encoding.UTF8);
                }
                catch (Exception) { AIBridgeLogger.LogWarning("Pipeline diagnostic result could not be written; no retry was submitted."); }
                Pending.Remove(item.Key);
            }
            if (Pending.Count == 0) EditorApplication.update -= Tick;
        }
    }
}
