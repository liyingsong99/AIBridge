using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Internal.Json;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class TestCommandTests
    {
        [Test]
        public void Execute_WhenEditorIsInPlayMode_ReturnsExplicitPlayModeFailure()
        {
            var result = new TestCommand(() => true).Execute(new CommandRequest
            {
                id = "test-playmode-state-failure",
                type = "test",
                @params = new Dictionary<string, object>
                {
                    { "action", "run" },
                    { "mode", "EditMode" }
                }
            });

            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("Play Mode"));
            Assert.That(result.error, Does.Contain("Edit Mode"));
        }

        [Test]
        public void ReloadPersistedState_MarksLostRunUnknownAndKeepsPendingQueue()
        {
            var directory = Path.Combine(Path.GetTempPath(), "AIBridge.TestRunTracker." + Guid.NewGuid().ToString("N"));
            var statePath = Path.Combine(directory, "state.json");
            var now = DateTime.Now.ToString("o");

            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(statePath, AIBridgeJson.Serialize(new Dictionary<string, object>
                {
                    { "schemaVersion", 1 },
                    { "currentRunId", "run-active" },
                    { "pendingRunIds", new List<string> { "run-pending" } },
                    {
                        "runs",
                        new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                { "runId", "run-active" },
                                { "status", "running" },
                                { "mode", "EditMode" },
                                { "queuedTime", now },
                                { "startTime", now },
                                { "timeoutMs", 120000 },
                                { "startedByInvocation", true },
                                { "isRunning", true },
                                { "nativeRunGuid", "missing-native-run" },
                                { "requestedFilter", new Dictionary<string, object> { { "testName", "Fixture.Active" } } },
                                { "executedFilter", new Dictionary<string, object> { { "testName", "Fixture.Active" } } }
                            },
                            new Dictionary<string, object>
                            {
                                { "runId", "run-pending" },
                                { "status", "queued" },
                                { "mode", "EditMode" },
                                { "queuedTime", now },
                                { "timeoutMs", 120000 },
                                { "requestedFilter", new Dictionary<string, object> { { "testName", "Fixture.Pending" } } }
                            }
                        }
                    }
                }, pretty: true));

                TestRunTracker.ReloadPersistedStateForTests(statePath);

                var active = TestRunTracker.GetSnapshot("run-active");
                var pending = TestRunTracker.GetSnapshot("run-pending");

                Assert.That(active.status, Is.EqualTo("unknown"));
                Assert.That(active.queuePosition, Is.EqualTo(-1));
                Assert.That(active.nativeRunGuid, Is.EqualTo("missing-native-run"));
                Assert.That(active.error, Does.Contain("lost"));
                Assert.That(active.requestedFilter.testName, Is.EqualTo("Fixture.Active"));
                Assert.That(pending.status, Is.EqualTo("queued"));
                Assert.That(pending.queuePosition, Is.EqualTo(1));
                Assert.That(pending.requestedFilter.testName, Is.EqualTo("Fixture.Pending"));
            }
            finally
            {
                TestRunTracker.ResetStateForTests();
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }
    }
}
