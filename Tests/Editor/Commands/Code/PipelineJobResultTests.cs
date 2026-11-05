using System;
using System.Collections.Generic;
using System.Reflection;
using AIBridge.Internal.Json;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class PipelineJobResultTests
    {
        private const string Command = "script run";
        private const string JobId = "0123456789abcdef0123456789abcdef";
        private const string Enqueued = "2026-11-05T01:00:00Z";
        private const string Started = "2026-11-05T01:00:01Z";
        private const string Ended = "2026-11-05T01:00:02Z";
        private const string Receipt = "Compiled successfully (dry run; nothing was loaded or executed).";
        private const string CanceledText =
            "Pipeline job was canceled; side effects are unknown and are not rolled back. Do not replay automatically.";

        [Test]
        public void PublicBoundary_ExposesOnlyObservationAndDryRunInputs()
        {
            var method = typeof(PipelineCodeProtocol).GetMethod("ParseRunJobResult", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(PipelineJobObservation), typeof(bool) }, null);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.ReturnType, Is.EqualTo(typeof(PipelineCodeResult)));
            AssertUnknown(PipelineCodeProtocol.ParseRunJobResult(null, false), 0);
            AssertUnknown(Parse(new PipelineJobObservation()), 0);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Completed_ActualRunPayloadUsesBusinessOutcomeAndNeverInventsDuration(bool dryRun)
        {
            var result = Parse(Observe("completed", Success(dryRun)), dryRun);
            Assert.That(result.success, Is.True);
            Assert.That(result.status, Is.EqualTo(dryRun ? "validated" : "executed"));
            Assert.That(result.mayStillBeRunning, Is.False);
            Assert.That(result.httpStatus, Is.EqualTo(200));
            Assert.That(result.executionTimeMs, Is.Null);
            Assert.That(result.error, Is.Null);
            Assert.That(result.errorCode, Is.Null);
            Assert.That(result.output, Is.Null);
            if (dryRun) Assert.That(result.resultValue, Is.Null);
            else Assert.That(result.resultValue, Is.EqualTo(42L));
            Assert.That(result.message, Is.EqualTo(dryRun ? Receipt : null));
        }

        [Test]
        public void Completed_CancellationRequestDoesNotChangeConfirmedBusinessSuccess()
        {
            var observation = Observe("completed", Success(false));
            observation.cancellationRequested = true;
            Assert.That(Parse(observation).success, Is.True);
            Assert.That(Parse(observation).executionTimeMs, Is.Null);
        }

        [Test]
        public void Completed_ResultTreeKeepsExplicitNullsAndNullableBusinessValue()
        {
            var payload = Success(false);
            payload["result"] = new Dictionary<string, object>
            {
                { "nullable", null }, { "items", new List<object> { null, false,
                    new Dictionary<string, object> { { "nested", null } } } }
            };
            var result = Parse(Observe("completed", payload));
            Assert.That(result.success, Is.True);
            var json = AIBridgeJson.Serialize(result.resultValue);
            Assert.That(json, Does.Contain("\"nullable\":null"));
            Assert.That(json, Does.Contain("\"nested\":null"));
            Assert.That(json, Does.Contain("[null,false,"));
            payload["result"] = null;
            result = Parse(Observe("completed", payload));
            Assert.That(result.success, Is.True);
            Assert.That(result.resultValue, Is.Null);
        }

        [TestCase("Compilation Failed", "pipelineRunCompilationFailed")]
        [TestCase("Runtime Error", "pipelineRunRuntimeError")]
        [TestCase("Entry Point Not Found", "pipelineRunEntryPointNotFound")]
        [TestCase("Bad Request", "pipelineRunBadRequest")]
        [TestCase("File Not Found", "pipelineRunFileNotFound")]
        public void Completed_ActualRunFailIsBusinessFailedEvenThoughJobCompleted(string error, string code)
        {
            var result = Parse(Observe("completed", Failure(error)));
            AssertFailed(result, error, code);
            Assert.That(result.errorDetails, Is.EqualTo("Fixture failure details."));
            Assert.That(result.diagnostics.Count, Is.EqualTo(error == "Compilation Failed" ? 1 : 0));
        }

        [TestCase("Compilation Failed")]
        [TestCase("Runtime Error")]
        [TestCase("Entry Point Not Found")]
        [TestCase("Bad Request")]
        [TestCase("File Not Found")]
        public void Failed_ConsistentInnerFailureUsesSameStrictBusinessParser(string error)
        {
            var payload = Failure(error);
            var result = Parse(Observe("failed", payload));
            var reference = PipelineCodeProtocol.ParseRunResponse(Json(Envelope(payload)), 200, false);
            AssertFailed(result, error, reference.errorCode);
            Assert.That(result.errorDetails, Is.EqualTo(reference.errorDetails));
            Assert.That(result.diagnostics.Count, Is.EqualTo(reference.diagnostics.Count));
        }

        [TestCase("completed")]
        [TestCase("failed")]
        public void Terminal_TimeoutOrUnrecognizedBusinessFailureRemainsUnknown(string state)
        {
            foreach (var error in new[] { "Timeout", "Execution Failed", "Unexpected Failure" })
            {
                var result = Parse(Observe(state, Failure(error)));
                AssertUnknown(result);
                Assert.That(result.error, Is.EqualTo(error));
                Assert.That(result.errorCode, Is.EqualTo(error == "Timeout" ? "pipelineRunTimeout" : "pipelineResponseInvalid"));
            }
        }

        [TestCase("completed")]
        [TestCase("failed")]
        public void Terminal_MissingMalformedOrEnvelopeInsteadOfInnerIsNeverSuccess(string state)
        {
            foreach (var value in new object[] { null, new Dictionary<string, object>(), false, 42L, "payload",
                new List<object>(), Envelope(Success(false)) })
            {
                var observation = Observe(state, Failure("Compilation Failed"));
                observation.resultValue = value;
                AssertUnknown(Parse(observation));
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Canceled_FixedWarningHasNoBusinessValuesOrDuration(bool started)
        {
            foreach (var requested in new[] { true, false })
            {
                var data = Status("canceled", null);
                data["startedAt"] = started ? Started : null;
                data["cancellationRequested"] = requested;
                var observation = Observe(data);
                var result = Parse(observation);
                Assert.That(result.success, Is.False);
                Assert.That(result.status, Is.EqualTo("canceled"));
                Assert.That(result.mayStillBeRunning, Is.False);
                Assert.That(result.errorCode, Is.EqualTo("pipelineJobCanceled"));
                Assert.That(result.error, Is.EqualTo(CanceledText));
                Assert.That(result.errorDetails, Is.Null);
                Assert.That(result.resultValue, Is.Null);
                Assert.That(result.executionTimeMs, Is.Null);
                Assert.That(result.message, Is.Null);
                Assert.That(result.output, Is.Null);
                Assert.That(result.diagnostics, Is.Empty);
                Assert.That(Parse(observation, true).error, Is.EqualTo(CanceledText));
            }
        }

        [TestCase("queued")]
        [TestCase("running")]
        [TestCase("unknown")]
        [TestCase("COMPLETED")]
        [TestCase("cancelled")]
        [TestCase(null)]
        public void UnknownOrNonterminalStateNeverReleasesSlot(string state)
        {
            var observation = Observe("completed", Success(false));
            observation.state = state;
            AssertUnknown(Parse(observation));
        }

        [Test]
        public void Observation_RequiresValidObservedExactRunHttp200AndDerivedFlags()
        {
            var changes = new Action<PipelineJobObservation>[]
            {
                value => value.valid = false, value => value.status = "unknown", value => value.status = null,
                value => value.status = "Observed", value => value.executionEnded = false,
                value => value.notStarted = true, value => value.sideEffectsUnknown = false
            };
            foreach (var change in changes)
            {
                var observation = Observe("completed", Success(false));
                change(observation);
                AssertUnknown(Parse(observation));
            }
            foreach (var command in new[] { null, "", "script eval", "Script Run", "script run ", "editor quit" })
            {
                var observation = Observe("completed", Success(false));
                observation.command = command;
                AssertUnknown(Parse(observation));
            }
            foreach (var http in new[] { 0, 201, 202, 204, 302, 400, 401, 403, 404, 429, 500, 503, 504 })
            {
                var observation = Observe("completed", Success(false));
                observation.httpStatus = http;
                AssertUnknown(Parse(observation), http);
            }
        }

        [Test]
        public void Observation_RevalidatesJobIdentityTimestampsAndStageMetadata()
        {
            var changes = new Action<PipelineJobObservation>[]
            {
                value => value.jobId = null, value => value.jobId = "invalid",
                value => value.enqueuedAt = null, value => value.enqueuedAt = "not UTC",
                value => value.startedAt = null, value => value.completedAt = null,
                value => value.completedAt = Enqueued,
                value => value.startedAt = "2026-11-05T01:00:01+08:00",
                value => value.error = "unexpected", value => value.errorDetails = "unexpected",
                value => value.errorCode = "TIMEOUT", value => value.progress = new Dictionary<string, object>(),
                value => value.warnings = new List<string> { null }
            };
            foreach (var change in changes)
            {
                var observation = Observe("completed", Success(false));
                change(observation);
                AssertUnknown(Parse(observation));
            }
        }

        [Test]
        public void Canceled_ContradictoryPayloadErrorsOrFlagsRemainUnknown()
        {
            var changes = new Action<PipelineJobObservation>[]
            {
                value => value.resultValue = Success(false), value => value.error = "unexpected",
                value => value.errorDetails = "unexpected", value => value.errorCode = "TIMEOUT",
                value => value.completedAt = null, value => value.progress = new Dictionary<string, object>(),
                value => value.executionEnded = false, value => value.notStarted = true
            };
            foreach (var change in changes)
            {
                var observation = Observe("canceled", null);
                change(observation);
                AssertUnknown(Parse(observation));
            }
        }

        [Test]
        public void Failed_ContradictorySuccessAndErrorMetadataRemainUnknown()
        {
            AssertUnknown(Parse(Observe("failed", Success(false))));
            foreach (var field in new[] { "error", "errorDetails", "errorCode" })
            {
                var data = Status("failed", Failure("Runtime Error"));
                data[field] = "conflicting job failure";
                AssertUnknown(Parse(Observe(data)));
            }
            var emptyError = Observe("failed", Failure("Runtime Error"));
            emptyError.error = null;
            AssertUnknown(Parse(emptyError));
            var beforeStart = Status("failed", Failure("Compilation Failed"));
            beforeStart["startedAt"] = null;
            AssertUnknown(Parse(Observe(beforeStart)));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Terminal_RequiresEveryStrictRunFieldAndExactInnerCommand(bool dryRun)
        {
            foreach (var field in new[] { "success", "command", "result", "message", "error", "errorDetails",
                "diagnostics", "compileMs", "executeMs", "assemblyName" })
            {
                var payload = Success(dryRun);
                payload.Remove(field);
                AssertUnknown(Parse(Observe("completed", payload), dryRun));
            }
            foreach (var command in new[] { null, "", "script eval", "Script Run", "script run ", "editor quit" })
            {
                var payload = Success(dryRun);
                payload["command"] = command;
                AssertUnknown(Parse(Observe("completed", payload), dryRun));
            }
        }

        [Test]
        public void Terminal_MalformedTypesEvalFieldsAndBusyMarkersCannotBypassStrictParser()
        {
            foreach (var field in new[] { "success", "command", "message", "error", "errorDetails", "errorCode",
                "diagnostics", "assemblyName", "compileMs", "executeMs", "executionTimeMs" })
            {
                var payload = Success(false);
                payload[field] = new Dictionary<string, object>();
                AssertUnknown(Parse(Observe("completed", payload)));
            }
            foreach (var field in new[] { "output", "error", "errorCode", "errorDetails", "status", "retryable", "busyReason" })
            {
                var payload = Success(false);
                payload[field] = field == "retryable" ? (object)true : "unexpected";
                AssertUnknown(Parse(Observe("completed", payload)));
            }
        }

        [TestCase("completed")]
        [TestCase("failed")]
        public void Terminal_MalformedBusinessFailuresCannotReleaseSlot(string state)
        {
            foreach (var field in new[] { "result", "message", "errorDetails", "executeMs", "diagnostics" })
            {
                var payload = Failure("Compilation Failed");
                payload[field] = field == "diagnostics" ? null : (object)42L;
                AssertUnknown(Parse(Observe(state, payload)));
            }
            foreach (var error in new[] { "Compilation Failed", "Entry Point Not Found", "File Not Found", "Bad Request" })
            {
                var payload = Failure(error);
                payload["executeMs"] = 1L;
                AssertUnknown(Parse(Observe(state, payload)));
            }
        }

        [Test]
        public void DryRun_RequiresExactReceiptAndRejectsExecutionPhaseBusinessFailures()
        {
            foreach (var field in new[] { "result", "assemblyName", "executeMs", "message" })
            {
                var payload = Success(true);
                payload[field] = field == "result" || field == "executeMs" ? (object)1L : "unexpected";
                AssertUnknown(Parse(Observe("completed", payload), true));
            }
            AssertUnknown(Parse(Observe("completed", Success(true)), false));
            AssertUnknown(Parse(Observe("completed", Success(false)), true));
            foreach (var error in new[] { "Runtime Error", "Entry Point Not Found" })
                AssertUnknown(Parse(Observe("completed", Failure(error)), true));
            foreach (var error in new[] { "Compilation Failed", "Bad Request", "File Not Found" })
                Assert.That(Parse(Observe("completed", Failure(error)), true).status, Is.EqualTo("failed"));
        }

        [Test]
        public void Diagnostics_ReuseCoordinatesAndRejectErrorOnSuccessOrMalformedList()
        {
            var payload = Success(false);
            payload["diagnostics"] = new List<object> { Diagnostic("warning") };
            var result = Parse(Observe("completed", payload));
            Assert.That(result.success, Is.True);
            Assert.That(result.diagnostics[0].code, Is.EqualTo("CS1234"));
            Assert.That(result.diagnostics[0].line, Is.EqualTo(1));
            Assert.That(result.diagnostics[0].column, Is.EqualTo(5));
            var diagnostic = (Dictionary<string, object>)((List<object>)payload["diagnostics"])[0];
            diagnostic["fileLine"] = 7L;
            Assert.That(Parse(Observe("completed", payload)).diagnostics[0].line, Is.EqualTo(7));
            diagnostic["severity"] = "error";
            AssertUnknown(Parse(Observe("completed", payload)));
            foreach (var invalid in new object[] { null, "[]", new List<object> { null },
                new List<object> { Diagnostic("other") } })
            {
                payload["diagnostics"] = invalid;
                AssertUnknown(Parse(Observe("completed", payload)));
            }
            var excessive = new List<object>();
            for (var i = 0; i < 513; i++) excessive.Add(Diagnostic("warning"));
            payload["diagnostics"] = excessive;
            AssertUnknown(Parse(Observe("completed", payload)));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Duration_ValidInnerDurationIsValidatedButNeverReportedAsJobDuration(bool dryRun)
        {
            foreach (var duration in new object[] { null, 0L, 12L, long.MaxValue })
            {
                var payload = Success(dryRun);
                payload["executionTimeMs"] = duration;
                var result = Parse(Observe("completed", payload), dryRun);
                Assert.That(result.success, Is.True);
                Assert.That(result.executionTimeMs, Is.Null);
            }
            var noDuration = Success(dryRun);
            noDuration.Remove("executionTimeMs");
            Assert.That(Parse(Observe("completed", noDuration), dryRun).success, Is.True);
            Assert.That(Parse(Observe("completed", noDuration), dryRun).executionTimeMs, Is.Null);
            foreach (var invalid in new object[] { -1L, 1.5d, "12", true })
            {
                var payload = Success(dryRun);
                payload["executionTimeMs"] = invalid;
                AssertUnknown(Parse(Observe("completed", payload), dryRun));
            }
        }

        [Test]
        public void MutableObservation_RawNumericTypesAreNotNormalizedIntoValidRunFields()
        {
            foreach (var field in new[] { "compileMs", "executeMs", "executionTimeMs" })
            {
                foreach (var invalid in new object[] { 1d, 1, 1m, -1L, true, "1" })
                {
                    var observation = Observe("completed", Success(false));
                    ((Dictionary<string, object>)observation.resultValue)[field] = invalid;
                    AssertUnknown(Parse(observation));
                }
            }
            var diagnosticObservation = Observe("completed", Success(false));
            var diagnostic = Diagnostic("warning");
            diagnostic["line"] = 0d;
            ((Dictionary<string, object>)diagnosticObservation.resultValue)["diagnostics"] =
                new List<object> { diagnostic };
            AssertUnknown(Parse(diagnosticObservation));
        }

        [Test]
        public void ActualNonterminalObservationsNeverBecomeBusinessResults()
        {
            foreach (var state in new[] { "queued", "running" })
            {
                var data = Status(state, null);
                data["completedAt"] = null;
                if (state == "queued") data["startedAt"] = null;
                var observation = Observe(data);
                Assert.That(observation.valid, Is.True);
                Assert.That(observation.executionEnded, Is.False);
                AssertUnknown(Parse(observation));
                observation.executionEnded = true;
                AssertUnknown(Parse(observation));
            }
        }

        [Test]
        public void Parse_DoesNotMutateObservationOrItsPayload()
        {
            var observation = Observe("completed", Success(false));
            var payload = observation.resultValue;
            var before = AIBridgeJson.Serialize(observation);
            Assert.That(Parse(observation).success, Is.True);
            Assert.That(observation.resultValue, Is.SameAs(payload));
            Assert.That(AIBridgeJson.Serialize(observation), Is.EqualTo(before));
            Assert.That(((Dictionary<string, object>)payload)["executionTimeMs"], Is.EqualTo(12L));
        }

        private static Dictionary<string, object> Success(bool dryRun)
        {
            // 来源：上游 RunScriptResponse.Ok；verbose 保留全部必需 null 字段，job 直接保存此 payload。
            return new Dictionary<string, object>
            {
                { "success", true }, { "command", Command }, { "result", dryRun ? null : (object)42L },
                { "message", dryRun ? Receipt : null }, { "error", null }, { "errorDetails", null },
                { "diagnostics", new List<object>() }, { "compileMs", 8L }, { "executeMs", dryRun ? 0L : 2L },
                { "assemblyName", dryRun ? null : "PipelineRunScript_Test_fixture" },
                { "executionTimeMs", 12L }, { "executedAt", Ended }
            };
        }

        private static Dictionary<string, object> Failure(string error)
        {
            var payload = Success(false);
            payload["success"] = false;
            payload["result"] = null;
            payload["error"] = error;
            payload["errorDetails"] = "Fixture failure details.";
            payload["executeMs"] = error == "Runtime Error" || error == "Timeout" || error == "Execution Failed" ? 2L : 0L;
            if (error == "Bad Request" || error == "File Not Found")
            {
                payload["assemblyName"] = null;
                payload["compileMs"] = 0L;
            }
            if (error == "Compilation Failed") payload["diagnostics"] = new List<object> { Diagnostic("error") };
            return payload;
        }

        private static Dictionary<string, object> Status(string state, object payload)
        {
            // 来源：上游 BasePipelineServer.BuildJobResponse；run 的返回失败通常仍为 completed。
            var inner = payload as Dictionary<string, object>;
            return new Dictionary<string, object>
            {
                { "jobId", JobId }, { "command", Command }, { "state", state }, { "cancellationRequested", false },
                { "enqueuedAt", Enqueued }, { "startedAt", Started }, { "completedAt", Ended }, { "result", payload },
                { "error", state == "failed" ? Value(inner, "error") ?? "Command Execution Failed" : null },
                { "errorDetails", state == "failed" ? Value(inner, "errorDetails") : null },
                { "errorCode", state == "failed" ? Value(inner, "errorCode") : null },
                { "progress", null }, { "warnings", null }
            };
        }

        private static Dictionary<string, object> Envelope(Dictionary<string, object> payload)
        {
            return new Dictionary<string, object>
            {
                { "success", true }, { "command", Command }, { "result", payload },
                { "message", null }, { "error", null }, { "errorDetails", null }, { "executionTimeMs", null }
            };
        }

        private static Dictionary<string, object> Diagnostic(string severity)
        {
            return new Dictionary<string, object>
            {
                { "severity", severity }, { "id", "CS1234" }, { "message", "Fixture diagnostic." },
                { "file", "Test.cs" }, { "line", 0L }, { "column", 4L }
            };
        }

        private static PipelineJobObservation Observe(string state, object payload)
        {
            return Observe(Status(state, payload));
        }

        private static PipelineJobObservation Observe(Dictionary<string, object> data)
        {
            return PipelineJobProtocol.ParseStatus(Json(data), 200, JobId, Command);
        }

        private static string Json(object data)
        {
            return AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(data));
        }

        private static object Value(Dictionary<string, object> data, string key)
        {
            return data != null && data.TryGetValue(key, out var value) ? value : null;
        }

        private static PipelineCodeResult Parse(PipelineJobObservation observation, bool dryRun = false)
        {
            return PipelineCodeProtocol.ParseRunJobResult(observation, dryRun);
        }

        private static void AssertFailed(PipelineCodeResult result, string error, string code)
        {
            Assert.That(result.success, Is.False);
            Assert.That(result.status, Is.EqualTo("failed"));
            Assert.That(result.mayStillBeRunning, Is.False);
            Assert.That(result.error, Is.EqualTo(error));
            Assert.That(result.errorCode, Is.EqualTo(code));
            Assert.That(result.resultValue, Is.Null);
            Assert.That(result.executionTimeMs, Is.Null);
        }

        private static void AssertUnknown(PipelineCodeResult result, int httpStatus = 200)
        {
            Assert.That(result.success, Is.False);
            Assert.That(result.status, Is.EqualTo("unknown"));
            Assert.That(result.mayStillBeRunning, Is.True);
            Assert.That(result.httpStatus, Is.EqualTo(httpStatus));
            Assert.That(result.resultValue, Is.Null);
            Assert.That(result.executionTimeMs, Is.Null);
        }
    }
}
