using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>测试执行集成点；不修改原生队列，父路由必须在原生启动和出队处检查 HasBusy。</summary>
    [InitializeOnLoad]
    internal static class PipelineTestTracker
    {
        private const string AdapterName = "AIBridge.Editor.UnityPipelineAdapter.UnityPipelineAdapter, cn.lys.aibridge.Editor.UnityPipelineAdapter";
        private static readonly string Generation = Guid.NewGuid().ToString("N");
        private static PipelineTestEngine _engine;
        // 父层接入真实 UTF IsRunActive + native tracked/queued + Build/Memory/code 门；未接入不得提交。
        internal static Func<string> GateProbe;

        static PipelineTestTracker() { EditorApplication.update += Update; }

        internal static bool HasBusy
        {
            get { try { return Engine.HasBusy; } catch (Exception) { return true; } }
        }

        private static PipelineTestEngine Engine
        {
            get
            {
                if (_engine != null) return _engine;
#if UNITY_2020_2_OR_NEWER
                if (AssetDatabase.IsAssetImportWorkerProcess()) throw new InvalidOperationException("Import worker cannot own Pipeline test state.");
#endif
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                _engine = new PipelineTestEngine(new PipelineTestContext
                {
                    Root = root, EditorPid = Process.GetCurrentProcess().Id, UnityVersion = Application.unityVersion,
                    Generation = Generation, Clock = () => DateTimeOffset.UtcNow, Journal = new PipelineTestStore(root),
                    NativeActivity = CheckGate, SessionFactory = Prepare
                });
                return _engine;
            }
        }

        internal static CommandResult Start(CommandRequest request)
        {
            try
            {
                RequireParameters(request, "mode", "pipeline-filter", "filter-type", "timeout");
                if (ReadText(request, "mode", "EditMode") != "EditMode") throw new ArgumentException("Pipeline test run supports only EditMode.");
                var options = new PipelineTestOptions(ReadText(request, "pipeline-filter", ""),
                    ReadText(request, "filter-type", "testName"), ReadTimeout(request));
                var record = Engine.Start(request.id, options);
                if (record.status == "not_submitted" || record.status == "unknown")
                {
                    var failure = CommandResult.Failure(request.id, record.error); failure.data = PipelineTestStore.ToData(record); return failure;
                }
                return CommandResult.Success(request.id, PipelineTestStore.ToData(record));
            }
            catch (Exception) { return Failure(request.id, "Pipeline test was not submitted or its journal is unavailable; no native fallback or automatic replay."); }
        }

        internal static CommandResult Query(CommandRequest request)
        {
            try
            {
                RequireParameters(request, "requestId");
                var id = ReadText(request, "requestId", null);
                var record = Engine.Query(id);
                if (record != null) return CommandResult.Success(request.id, PipelineTestStore.ToData(record));
                if (id != null) return Failure(request.id, "No Pipeline test is registered for this requestId.");
                return CommandResult.Success(request.id, new { backend = "pipeline", status = "idle", busy = false, operationCorrelation = true });
            }
            catch (Exception) { return Failure(request.id, "Pipeline test journal is unavailable; do not replay."); }
        }

        internal static CommandResult Release(CommandRequest request)
        {
            try
            {
                RequireParameters(request, "requestId", "confirm");
                if (!request.@params.TryGetValue("confirm", out var value) || !(value is bool confirm) || !confirm)
                    throw new ArgumentException("Local release requires confirm=true.");
                var record = Engine.Release(ReadText(request, "requestId", null), true);
                return CommandResult.Success(request.id, new { backend = "pipeline", status = record.status, requestId = record.requestId,
                    localReleased = true, remoteCancellationConfirmed = false, busy = record.busy,
                    warning = "Only local unknown test state was released; the remote tests may still run. Do not replay the original request." });
            }
            catch (Exception) { return Failure(request.id, "Only an exact registered unknown test request with confirm=true can be released locally."); }
        }

        private static string CheckGate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return "Editor requires idle Edit Mode.";
            if (PipelineCompileTracker.HasBusy) return "A Pipeline compilation request is active or unknown.";
            if (GateProbe == null) return "Native test activity probe has not been integrated.";
            return GateProbe();
        }

        private static PipelineTestSession Prepare(PipelineTestOptions options)
        {
            var type = Type.GetType(AdapterName, false);
            var method = type == null ? null : type.GetMethod("PrepareTestRun", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(PipelineTestOptions) }, null);
            if (method == null || method.ReturnType != typeof(PipelineTestSession))
                throw new NotSupportedException("Pipeline test adapter is unavailable.");
            return method.Invoke(null, new object[] { options }) as PipelineTestSession;
        }

        private static void Update()
        {
            // 不在 import worker 或默认路径额外初始化；已有会话才执行纯本地进度观察。
            if (_engine == null) return;
            try { _engine.Tick(); } catch (Exception) { /* HasBusy 对无法确认的状态继续失败关闭。 */ }
        }

        private static void RequireParameters(CommandRequest request, params string[] extra)
        {
            if (request == null || request.@params == null) throw new ArgumentException("Missing request.");
            foreach (var key in request.@params.Keys)
                if (key != "action" && key != "backend" && Array.IndexOf(extra, key) < 0)
                    throw new ArgumentException("Unsupported Pipeline test parameter: " + key);
            if (!request.@params.TryGetValue("backend", out var backend) || !Equals(backend, "pipeline"))
                throw new ArgumentException("Explicit Pipeline backend is required.");
        }
        private static string ReadText(CommandRequest request, string key, string fallback)
        {
            if (!request.@params.TryGetValue(key, out var value)) return fallback;
            if (!(value is string text)) throw new ArgumentException("Invalid text parameter.");
            return text;
        }
        private static int ReadTimeout(CommandRequest request)
        {
            if (!request.@params.TryGetValue("timeout", out var value)) return 120000;
            if (value is int integer) return integer;
            if (value is long number && number >= int.MinValue && number <= int.MaxValue) return checked((int)number);
            throw new ArgumentException("Timeout must be integer milliseconds.");
        }
        private static CommandResult Failure(string id, string message)
        {
            var result = CommandResult.Failure(id, message);
            result.data = new { backend = "pipeline", status = "unavailable", automaticReplay = false, remoteCancellationConfirmed = false };
            return result;
        }
    }
}
