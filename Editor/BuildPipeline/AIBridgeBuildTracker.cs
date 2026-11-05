using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AIBridge.Runtime.Internal;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    [InitializeOnLoad]
    internal static class AIBridgeBuildTracker
    {
        private static readonly string Generation = Guid.NewGuid().ToString("N");
        private static AIBridgeBuildRecord _active;
        private static AIBridgeBuildPlan _plan;
        private static bool _invoking;
        internal static bool Busy => _active != null || _invoking || BuildPipeline.isBuildingPlayer;
        internal static Func<BuildPlayerOptions, AIBridgeBuildReport> BuildExecutor =
            options => AIBridgeBuildReportConverter.Capture(BuildPipeline.BuildPlayer(options));
        private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        static AIBridgeBuildTracker() { EditorApplication.delayCall += Recover; }

        public static CommandResult Start(CommandRequest request)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating
                || BuildPipeline.isBuildingPlayer || _active != null)
                return CommandResult.Failure(request.id, "Build requires an idle Edit Mode Editor with no active AIBridge build.");
            var plan = AIBridgeBuildPlan.Parse(request);
            if (plan.DryRun)
                return CommandResult.Success(request.id, new { status = "validated", dryRun = true, target = plan.Target.ToString(),
                    scenes = plan.Scenes, development = plan.Development, noBuildStarted = true });
            var previous = AIBridgeBuildStore.Load(ProjectRoot, request.id, out var invalidPrevious);
            if (previous != null || invalidPrevious)
                return CommandResult.Failure(request.id, "This build requestId is already registered or unreadable; it will not be replayed.");
            if (ProfilerNativeGate.Busy || (PipelineTestTracker.HasBusy || PipelineCompileTracker.HasBusy))
                return CommandResult.Failure(request.id, "A native capture or Pipeline test is active or unknown; resolve its original request before building.");
            if (CodeCommand.HasActiveCodeOperation)
                return CommandResult.Failure(request.id, "An AIBridge code operation is active or unknown; resolve its original request before building.");
            // 缺失latest不能绕过尚在运行/未知的单请求记录。损坏记录必须明确处理而非默认为idle。
            var prior = FindOutstanding(out var unreadable);
            if (unreadable) return CommandResult.Failure(request.id, "Build journal is corrupt or unreadable; no build was queued.");
            if (prior != null) return Failure(request.id, "An earlier build outcome is unknown/busy; query its requestId before explicitly releasing.", prior);
            var id = Guid.NewGuid().ToString("N");
            var record = new AIBridgeBuildRecord
            {
                requestId = request.id, buildId = id, projectPath = ProjectRoot, unityVersion = Application.unityVersion,
                generation = Generation, editorPid = Process.GetCurrentProcess().Id, status = "queued", target = plan.Target.ToString(),
                outputPath = AIBridgeBuildPlan.OutputPath(ProjectRoot, id, plan.Target.ToString()), queuedAt = UtcNow(),
                busy = true, success = null, invocationStarted = false, development = plan.Development,
                scenes = new List<string>(plan.Scenes)
            };
            try { AIBridgeBuildStore.Save(ProjectRoot, record); }
            catch (Exception)
            {
                _active = record; _plan = null;
                SetUnknown(record, "buildIntentWriteFailed", "Build intent could not be persisted; no build was invoked.");
                return Failure(request.id, record.error, record);
            }
            _active = record; _plan = plan;
            EditorApplication.update -= ExecutePending;
            EditorApplication.update += ExecutePending;
            return CommandResult.Success(request.id, new { status = "queued", requestId = record.requestId, buildId = record.buildId,
                target = record.target, outputPath = record.outputPath, executionStarted = false,
                warning = "Build runs synchronously on the Editor main thread; timeout does not stop it. Query build status/report; do not replay." });
        }

        public static CommandResult Query(CommandRequest request, bool reportOnly)
        {
            foreach (var key in request.@params.Keys)
                if (key != "action" && key != "requestId") return CommandResult.Failure(request.id, "Build query accepts only requestId.");
            var id = ReadOptionalId(request);
            var record = _active != null && (id == null || id == _active.requestId) ? _active
                : AIBridgeBuildStore.Load(ProjectRoot, id, out _);
            if (record == null)
            {
                AIBridgeBuildStore.Load(ProjectRoot, id, out var unreadable);
                if (unreadable) return CommandResult.Failure(request.id, "Build record is corrupt or unreadable; do not replay.");
                return reportOnly ? CommandResult.Failure(request.id, "No registered completed BuildReport is available.")
                    : CommandResult.Success(request.id, new { status = "idle", reportAvailable = false });
            }
            if (reportOnly && record.status != "completed") return Failure(request.id, "No confirmed completed BuildReport is available.", record);
            if (reportOnly && record.success != true) return Failure(request.id, record.error ?? "Build ended without confirmed success.", record);
            return CommandResult.Success(request.id, record);
        }

        public static CommandResult Release(CommandRequest request)
        {
            foreach (var key in request.@params.Keys)
                if (key != "action" && key != "requestId" && key != "confirm")
                    return CommandResult.Failure(request.id, "build release accepts only requestId and confirm.");
            if (!AIBridgeBuildPlan.Flag(request, "confirm", false)) return CommandResult.Failure(request.id, "Release requires --confirm true.");
            var id = ReadOptionalId(request);
            if (id == null) return CommandResult.Failure(request.id, "Release requires the exact original requestId.");
            if (_invoking || BuildPipeline.isBuildingPlayer) return CommandResult.Failure(request.id, "A synchronous build cannot be canceled or released while executing.");
            var record = _active != null && _active.requestId == id ? _active : AIBridgeBuildStore.Load(ProjectRoot, id, out _);
            if (record == null)
            {
                var latest = AIBridgeBuildStore.Load(ProjectRoot, null, out _);
                if (latest != null && latest.requestId == id) record = latest;
            }
            if (record != null && (record.status == "queued" || record.status == "building") && record.generation != Generation)
                SetUnknown(record, "buildStateLost", "Old Editor generation ended without a confirmed result; no build was replayed.");
            if (record == null || record.status != "unknown") return CommandResult.Failure(request.id, "Only a registered unknown build can be released locally.");
            var wasReleased = record.released; var wasBusy = record.busy;
            record.released = true; record.busy = false;
            try { AIBridgeBuildStore.Save(ProjectRoot, record); }
            catch (Exception)
            {
                // 只读回已经原子发布的单ID；latest写失败不能撤销已完成的本地释放。
                var published = AIBridgeBuildStore.Load(ProjectRoot, id, out _);
                if (published != null && published.buildId == record.buildId && published.released && !published.busy)
                { if (_active != null && _active.requestId == id) { _active = null; _plan = null; } }
                else { record.released = wasReleased; record.busy = wasBusy; }
                throw;
            }
            if (_active != null && _active.requestId == id) { _active = null; _plan = null; }
            return CommandResult.Success(request.id, new { localReleased = true, remoteCancellationConfirmed = false,
                warning = "Only local unknown build state was released; build artifacts or side effects may exist. Do not replay the original request." });
        }

        private static void ExecutePending()
        {
            EditorApplication.update -= ExecutePending;
            var record = _active; var plan = _plan;
            if (record == null || plan == null || record.status != "queued") return;
            _plan = null;
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating
                    || BuildPipeline.isBuildingPlayer || CodeCommand.HasActiveCodeOperation || ProfilerNativeGate.Busy || (PipelineTestTracker.HasBusy || PipelineCompileTracker.HasBusy)
                    || plan.Target != EditorUserBuildSettings.activeBuildTarget
                    || !AIBridgeRuntimeBuildProcessor.AreRuntimeBootstrapDefinesSynchronized(BuildPipeline.GetBuildTargetGroup(plan.Target))
                    || (plan.Target.ToString() == "Android" && (EditorUserBuildSettings.buildAppBundle || EditorUserBuildSettings.exportAsGoogleAndroidProject)))
                    throw new InvalidOperationException("Editor or target changed before build invocation.");
                var directory = AIBridgeBuildPlan.OutputDirectory(ProjectRoot, record.buildId);
                RejectReparseAncestors(directory);
                if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Build output already exists.");
                record.status = "building"; record.startedAt = UtcNow(); record.invocationStarted = true;
                // 先记录可能开始的调用，再创建产物目录；失败不能自动重新入队。
                AIBridgeBuildStore.Save(ProjectRoot, record);
                Directory.CreateDirectory(directory); RejectReparseAncestors(directory);
                _invoking = true;
                var captured = BuildExecutor(plan.Options(record.outputPath));
                record.report = captured; record.status = "completed"; record.endedAt = UtcNow();
                record.busy = false; record.success = captured.result == "Succeeded";
                if (record.success != true)
                {
                    record.errorCode = "buildFailed"; record.error = "Player build ended with " + captured.result + "; inspect report steps and errors.";
                }
                AIBridgeBuildStore.Save(ProjectRoot, record);
                _active = null;
            }
            catch (Exception)
            {
                // BuildPipeline异常/报告未知/落盘失败均不能据此判定无产物或安全重试。
                SetUnknown(record, "buildOutcomeUnknown", "Build outcome could not be confirmed; query the original request, inspect artifacts and do not replay.");
                try { AIBridgeBuildStore.Save(ProjectRoot, record); } catch (Exception) { }
            }
            finally { _invoking = false; }
        }

        private static void Recover()
        {
            if (_active != null || _invoking) return;
            AIBridgeBuildRecord record;
            try { record = FindOutstanding(out var unreadable); if (unreadable || record == null) return; }
            catch (Exception) { return; }
            // 永不从磁盘重建待执行计划；旧世代仅恢复审计占位。
            _active = record; _plan = null;
            if (record.status != "unknown")
            {
                SetUnknown(record, "buildStateLost", "Build state was lost during reload/restart; no build was replayed.");
                try { AIBridgeBuildStore.Save(ProjectRoot, record); } catch (Exception) { }
            }
        }

        private static AIBridgeBuildRecord FindOutstanding(out bool unreadable)
        {
            // 默认store统一扫描/有界严格读取；latest缺失及多个busy不得各入口解释不同。
            var record = AIBridgeBuildStore.Load(ProjectRoot, null, out unreadable);
            return record != null && record.busy ? record : null;
        }

        internal static void RejectReparseAncestors(string path)
        {
            for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse paths are not supported."); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        private static void SetUnknown(AIBridgeBuildRecord record, string code, string error)
        {
            record.status = "unknown"; record.success = null; record.report = null; record.busy = true; record.released = false;
            record.errorCode = code; record.error = error;
        }
        private static string UtcNow() { return DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture); }
        private static string ReadOptionalId(CommandRequest request)
        {
            if (!request.@params.TryGetValue("requestId", out var value)) return null;
            if (!(value is string id) || string.IsNullOrWhiteSpace(id) || id.Length > 256) throw new ArgumentException("Invalid requestId.");
            return id;
        }
        private static CommandResult Failure(string id, string error, object data)
        { return new CommandResult { id = id, success = false, error = error, data = data }; }
    }
}
