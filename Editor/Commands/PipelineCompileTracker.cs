using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;

namespace AIBridge.Editor
{
    [InitializeOnLoad]
    internal static class PipelineCompileTracker
    {
        private const string AdapterType = "AIBridge.Editor.UnityPipelineAdapter.UnityPipelineAdapter, cn.lys.aibridge.Editor.UnityPipelineAdapter";
        private static readonly string Generation = Guid.NewGuid().ToString("N");
        private static PipelineCompileEngine _engine;
        static PipelineCompileTracker() { EditorApplication.update += Tick; }

        private static PipelineCompileEngine Engine
        {
            get
            {
                if (_engine != null) return _engine;
#if UNITY_2020_2_OR_NEWER
                if (AssetDatabase.IsAssetImportWorkerProcess()) throw new InvalidOperationException("Import worker cannot own Pipeline compile state.");
#endif
                var root = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
                _engine = new PipelineCompileEngine(new PipelineCompileContext { Root = root, Store = new PipelineCompileStore(root),
                    EditorPid = System.Diagnostics.Process.GetCurrentProcess().Id, UnityVersion = UnityEngine.Application.unityVersion,
                    Generation = Generation, Clock = () => DateTimeOffset.UtcNow, NativeActivity = Refusal, Prepare = Prepare });
                return _engine;
            }
        }
        internal static bool HasBusy { get { try { return Engine.HasBusy; } catch (Exception) { return true; } } }

        internal static CommandResult Execute(CommandRequest request, string action)
        {
            try
            {
                Require(request, action);
                if (action == "release")
                {
                    var released = Engine.Release(ReadId(request), Confirm(request));
                    return CommandResult.Success(request.id, global::AIBridge.Internal.Json.AIBridgeJson.PreserveObjectNulls(new Dictionary<string, object>
                    {
                        { "backend", "pipeline" }, { "requestId", released.requestId }, { "status", "unknown" }, { "busy", false },
                        { "localReleased", true }, { "remoteCancellationConfirmed", false }, { "compilationSuccess", null },
                        { "warning", "Only the local request gate was released; compilation may still run. Do not replay." }
                    }));
                }
                var record = action == "start" ? Engine.Start(request.id, Timeout(request)) : Engine.Query(ReadId(request));
                if (record == null) throw new InvalidOperationException();
                if (action == "start" && (record.status == "unknown" || record.status == "not_submitted"))
                { var failure = CommandResult.Failure(request.id, record.error); failure.data = Projection(record); return failure; }
                return CommandResult.Success(request.id, Projection(record));
            }
            catch (Exception)
            {
                var failure = CommandResult.Failure(request == null ? null : request.id,
                    "Pipeline compile request unavailable or invalid; no native fallback or automatic replay.");
                failure.data = new { backend = "pipeline", status = "unavailable", automaticReplay = false }; return failure;
            }
        }

        internal static object Projection(PipelineCompileRecord record)
        {
            var data = (Dictionary<string, object>)PipelineCompileStore.ToData(record);
            data.Add("operationCorrelation", false); data.Add("automaticReplay", false);
            data.Add("compilationSuccess", null); data.Add("errorCount", null);
            data.Add("warningCount", null); data.Add("duration", null);
            data.Add("acknowledgementConfirmed", record.status == "acknowledged");
            return global::AIBridge.Internal.Json.AIBridgeJson.PreserveObjectNulls(data);
        }

        private static void Require(CommandRequest request, string action)
        {
            if (request == null || request.@params == null || !PipelineTestStore.IsRequestId(request.id)
                || !Equals(Value(request, "backend"), "pipeline") || !Equals(Value(request, "action"), action)) throw new ArgumentException();
            if (action != "start" && action != "status" && action != "release") throw new ArgumentException();
            foreach (var key in request.@params.Keys)
                if (key != "backend" && key != "action" && !(action == "start" && (key == "confirm" || key == "timeout"))
                    && !(action != "start" && key == "requestId") && !(action == "release" && key == "confirm")) throw new ArgumentException();
            if (action == "start" && !Confirm(request)) throw new ArgumentException();
        }
        private static object Value(CommandRequest request, string key) { return request.@params.TryGetValue(key, out var value) ? value : null; }
        private static bool Confirm(CommandRequest request) { return Value(request, "confirm") is bool confirmed && confirmed; }
        private static string ReadId(CommandRequest request)
        {
            var id = Value(request, "requestId") as string;
            if (!PipelineTestStore.IsRequestId(id)) throw new ArgumentException(); return id;
        }
        private static int Timeout(CommandRequest request)
        {
            var value = Value(request, "timeout"); if (value == null && !request.HasParam("timeout")) return 10000;
            if (!(value is int) && !(value is long)) throw new ArgumentException();
            var number = Convert.ToInt64(value); if (number < 1000 || number > 60000) throw new ArgumentException(); return (int)number;
        }
        private static string Refusal()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return "Editor is not idle.";
            if (PipelineTestTracker.HasBusy) return "A Pipeline test is active or unknown.";
            return PipelineTestExecutionGate.Refusal();
        }
        private static PipelineCompileSession Prepare(int timeout)
        {
            var type = Type.GetType(AdapterType);
            var method = type == null ? null : type.GetMethod("PrepareCompile", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
            if (method == null) throw new NotSupportedException();
            return method.Invoke(null, new object[] { timeout }) as PipelineCompileSession;
        }
        private static void Tick()
        {
            if (_engine == null) return;
            try { _engine.Tick(); } catch (Exception) { /* 无法确认时保留日志/门，不重放提交。 */ }
        }
    }
}
