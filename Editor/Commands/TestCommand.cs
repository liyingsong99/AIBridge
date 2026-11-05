using System;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace AIBridge.Editor
{
    /// <summary>
    /// Native test command backed by Unity TestRunnerApi.
    /// </summary>
    public class TestCommand : ICommand
    {
        private const string PlayModeRunFailureMessage =
            "AIBridge test run failed because Unity Editor is currently in Play Mode. Stop Play Mode and wait for Edit Mode before retrying.";

        private readonly Func<bool> _isEditorInPlayMode;

        public TestCommand()
            : this(() => EditorApplication.isPlaying)
        {
        }

        internal TestCommand(Func<bool> isEditorInPlayMode)
        {
            _isEditorInPlayMode = isEditorInPlayMode ?? (() => EditorApplication.isPlaying);
        }

        public string Type => "test";
        public bool RequiresRefresh => false;

        public string SkillDescription => @"### `test` - Native Unity Test Runner

```bash
$CLI test run --mode EditMode
$CLI test run --test-name ""MyNamespace.MyFixture.MyTest""
$CLI test status
$CLI test list --backend pipeline --mode EditMode --timeout 10000
$CLI test run --backend pipeline --pipeline-filter ""MyFixture"" --filter-type testName --timeout 120000
$CLI test status --backend pipeline --requestId <original-request-id>
$CLI test release --backend pipeline --requestId <unknown-request-id> --confirm true
```

Pipeline `list` is enumeration, not execution. Pipeline run is synchronous EditMode only, persisted once, with strict counts: zero tests, all skipped, failed or inconclusive are not passing. `pipeline-filter` uses case-insensitive substrings for testName/assembly or exact category; native exact/regex/group flags are rejected. Pipeline status reads only the local original request journal; timeout/reload stays unknown and cannot replay. release frees local unknown state only; remote tests may still run. No async/job/remote cancel/native fallback.

`test run` must start while the Editor is in Edit Mode. If Unity is already in Play Mode, the command fails immediately and tells the agent to stop Play Mode before retrying.";

        public CommandResult Execute(CommandRequest request)
        {
            if (request == null || request.@params == null) return CommandResult.Failure(request == null ? null : request.id, "Test parameters are required.");
            request.@params.TryGetValue("backend", out var rawBackend);
            request.@params.TryGetValue("action", out var rawAction);
            if ((request.HasParam("backend") && !(rawBackend is string)) || (request.HasParam("action") && !(rawAction is string)))
                return CommandResult.Failure(request.id, "Test backend/action must be literal strings.");
            var action = rawAction as string ?? "status";
            var backend = rawBackend as string ?? "native";
            if (backend == "pipeline")
            {
                switch (action)
                {
                    case "list": return PipelineDiagnosticsCommand.Execute(request, "test_list");
                    case "run": return PipelineTestTracker.Start(request);
                    case "status": return PipelineTestTracker.Query(request);
                    case "release": return PipelineTestTracker.Release(request);
                    default: return CommandResult.Failure(request.id, "Unsupported Pipeline test action; no native fallback or remote cancel.");
                }
            }
            if (backend != "native") return CommandResult.Failure(request.id, "Unsupported test backend.");

            try
            {
                switch (action.ToLower())
                {
                    case "run":
                        return RunTests(request);
                    case "status":
                        return QueryStatus(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: run, status");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult RunTests(CommandRequest request)
        {
            var modeText = request.GetParam("mode", "EditMode");
            var timeoutMs = request.GetParam("timeout", 120000);
            var runId = request.GetParam<string>("runId", null);
            var testName = request.GetParam<string>("testName", null);
            var groupName = request.GetParam<string>("groupName", null);
            var assemblyName = request.GetParam<string>("assemblyName", null);

            if (!TryParseMode(modeText, out var mode))
            {
                return CommandResult.Failure(request.id, $"Unsupported test mode: {modeText}. Supported: EditMode, PlayMode");
            }

            // First version only guarantees EditMode. Keep the PlayMode API surface but return not supported for now.
            if (mode == TestMode.PlayMode)
            {
                return CommandResult.Failure(request.id, "PlayMode tests are not supported yet. Please use EditMode for now.");
            }

            if (_isEditorInPlayMode())
            {
                return CommandResult.Failure(request.id, PlayModeRunFailureMessage);
            }

            var startResult = TestRunTracker.StartRun(runId, mode, testName, groupName, assemblyName, timeoutMs);
            var snapshot = startResult.snapshot;

            return CommandResult.Success(request.id, new
            {
                action = "run",
                runId = snapshot.runId,
                status = snapshot.status,
                mode = snapshot.mode,
                queuedAt = snapshot.queuedAt,
                startedAt = snapshot.startedAt,
                duration = snapshot.duration,
                total = snapshot.total,
                passed = snapshot.passed,
                failed = snapshot.failed,
                skipped = snapshot.skipped,
                inconclusive = snapshot.inconclusive,
                failedTests = snapshot.failedTests,
                startedByInvocation = startResult.startedByInvocation,
                attachedToExistingRun = startResult.attachedToExistingRun,
                queuedByInvocation = startResult.queuedByInvocation,
                queuePosition = snapshot.queuePosition,
                requestedFilter = snapshot.requestedFilter,
                executedFilter = snapshot.executedFilter,
                error = snapshot.error
            });
        }

        private CommandResult QueryStatus(CommandRequest request)
        {
            var runId = request.GetParam<string>("runId", null);
            var snapshot = TestRunTracker.GetSnapshot(runId);

            return CommandResult.Success(request.id, new
            {
                action = "status",
                runId = snapshot.runId,
                status = snapshot.status,
                mode = snapshot.mode,
                queuedAt = snapshot.queuedAt,
                startedAt = snapshot.startedAt,
                duration = snapshot.duration,
                total = snapshot.total,
                passed = snapshot.passed,
                failed = snapshot.failed,
                skipped = snapshot.skipped,
                inconclusive = snapshot.inconclusive,
                failedTests = snapshot.failedTests,
                startedByInvocation = snapshot.startedByInvocation,
                attachedToExistingRun = snapshot.attachedToExistingRun,
                queuedByInvocation = snapshot.status == "queued",
                queuePosition = snapshot.queuePosition,
                requestedFilter = snapshot.requestedFilter,
                executedFilter = snapshot.executedFilter,
                error = snapshot.error
            });
        }

        private bool TryParseMode(string modeText, out TestMode mode)
        {
            if (string.Equals(modeText, "PlayMode", StringComparison.OrdinalIgnoreCase))
            {
                mode = TestMode.PlayMode;
                return true;
            }

            if (string.Equals(modeText, "EditMode", StringComparison.OrdinalIgnoreCase))
            {
                mode = TestMode.EditMode;
                return true;
            }

            mode = TestMode.EditMode;
            return false;
        }

    }
}
