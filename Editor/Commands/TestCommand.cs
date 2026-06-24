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
```

`test run` must start while the Editor is in Edit Mode. If Unity is already in Play Mode, the command fails immediately and tells the agent to stop Play Mode before retrying.";

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "status");

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
