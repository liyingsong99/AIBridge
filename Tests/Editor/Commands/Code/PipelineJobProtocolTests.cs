using System;
using System.Collections.Generic;
using AIBridge.Internal.Json;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class PipelineJobProtocolTests
    {
        private const string JobId = "0123456789abcdef0123456789abcdef";
        private const string OtherId = "fedcba9876543210fedcba9876543210";
        private const string Eval = "script eval";
        private const string Run = "script run";
        private const string Enqueued = "2026-11-05T01:00:00Z";
        private const string Started = "2026-11-05T01:00:01.1234567Z";
        private const string Ended = "2026-11-05T01:00:02Z";

        [Test]
        public void PublicBoundary_DefaultObservationIsUnknownAndVersionIsShared()
        {
            var value = new PipelineJobObservation();
            Assert.That(value.valid, Is.False);
            Assert.That(value.status, Is.EqualTo("unknown"));
            Assert.That(value.executionEnded, Is.False);
            Assert.That(value.notStarted, Is.False);
            Assert.That(value.sideEffectsUnknown, Is.True);
            Assert.That(PipelineJobProtocol.SupportedVersion, Is.EqualTo(PipelineCodeProtocol.SupportedVersion));
        }

        [TestCase(Eval)]
        [TestCase(Run)]
        public void Submission_ActualVerboseCmdSuccessIsOnlyAnObservation(string command)
        {
            var value = PipelineJobProtocol.ParseSubmission(Json(Submission(command)), 200, command);
            AssertObserved(value, "queued", false);
            Assert.That(value.jobId, Is.EqualTo(JobId));
            Assert.That(value.command, Is.EqualTo(command));
            Assert.That(value.enqueuedAt, Is.Null);
            Assert.That(value.notStarted, Is.False, "提交 queued 固定回执不证明查询时仍未启动");
            Assert.That(value.resultValue, Is.Null);
        }

        [Test]
        public void Submission_ActualLiteralShapeAndNullableOptionalMetadata()
        {
            const string json = "{\"success\":true,\"command\":\"script eval\",\"result\":{\"jobId\":\"0123456789abcdef0123456789abcdef\",\"state\":\"queued\"},\"executionTimeMs\":null,\"message\":null,\"error\":null,\"errorDetails\":null,\"executedAt\":\"2026-11-05T01:00:00.0000001Z\"}";
            AssertObserved(PipelineJobProtocol.ParseSubmission(json, 200, Eval), "queued", false);
            var data = Submission();
            data["errorCode"] = null;
            data["warnings"] = new List<object> { "fixture guidance" };
            var value = PipelineJobProtocol.ParseSubmission(Json(data), 200, Eval);
            AssertObserved(value, "queued", false);
            Assert.That(value.warnings, Is.EqualTo(new[] { "fixture guidance" }));
        }

        [Test]
        public void Submission_LeanAndEveryMissingVerboseFieldRemainUnknown()
        {
            AssertUnknown(PipelineJobProtocol.ParseSubmission("{\"success\":true,\"result\":{\"jobId\":\"" + JobId
                + "\",\"state\":\"queued\"}}", 200, Eval));
            foreach (var key in new List<string>(Submission().Keys))
            {
                var data = Submission();
                data.Remove(key);
                AssertUnknown(PipelineJobProtocol.ParseSubmission(Json(data), 200, Eval));
            }
            foreach (var key in new[] { "jobId", "state" })
            {
                var data = Submission();
                ((Dictionary<string, object>)data["result"]).Remove(key);
                AssertUnknown(PipelineJobProtocol.ParseSubmission(Json(data), 200, Eval));
            }
        }

        [Test]
        public void Submission_ContradictorySuccessMetadataAndTypesAreRejected()
        {
            var mutations = new Dictionary<string, object[]>
            {
                { "success", new object[] { false, null, "true", 1L } },
                { "command", new object[] { Run, null, 1L } },
                { "result", new object[] { null, new List<object>(), "receipt" } },
                { "executionTimeMs", new object[] { 0L, -1L, 0.5d, "0", true } },
                { "message", new object[] { "done", 1L, false } },
                { "error", new object[] { "failed", "", 1L } },
                { "errorDetails", new object[] { "stack", "", new List<object>() } },
                { "errorCode", new object[] { "TIMEOUT", "", true } },
                { "executedAt", new object[] { null, 1L, "not a date" } }
            };
            foreach (var pair in mutations)
                foreach (var bad in pair.Value)
                {
                    var data = Submission();
                    data[pair.Key] = bad;
                    AssertUnknown(PipelineJobProtocol.ParseSubmission(Json(data), 200, Eval));
                }
        }

        [Test]
        public void Submission_HandleMustBeExactQueuedPair()
        {
            foreach (var state in new object[] { null, "running", "completed", "failed", "canceled", "QUEUED", 1L })
            {
                var data = Submission();
                ((Dictionary<string, object>)data["result"])["state"] = state;
                AssertUnknown(PipelineJobProtocol.ParseSubmission(Json(data), 200, Eval));
            }
            foreach (var id in InvalidIds())
            {
                var data = Submission();
                ((Dictionary<string, object>)data["result"])["jobId"] = id;
                AssertUnknown(PipelineJobProtocol.ParseSubmission(Json(data), 200, Eval));
            }
            var extra = Submission();
            ((Dictionary<string, object>)extra["result"])["success"] = true;
            AssertUnknown(PipelineJobProtocol.ParseSubmission(Json(extra), 200, Eval));
        }

        [TestCase("queued", false)]
        [TestCase("running", false)]
        [TestCase("completed", true)]
        [TestCase("failed", true)]
        [TestCase("canceled", true)]
        public void Status_ActualBareDtoStagesAreObserved(string state, bool ended)
        {
            foreach (var command in new[] { Eval, Run })
            {
                var value = Parse(Status(state, command), command);
                AssertObserved(value, state, ended);
                Assert.That(value.jobId, Is.EqualTo(JobId));
                Assert.That(value.command, Is.EqualTo(command));
                Assert.That(value.enqueuedAt, Is.EqualTo(Enqueued));
            }
        }

        [Test]
        public void Status_CompletedRunInnerFalseIsPreservedWithoutBusinessSuccess()
        {
            var data = Status("completed", Run);
            data["result"] = new Dictionary<string, object>
            {
                { "success", false }, { "command", Run }, { "result", null },
                { "error", "Invocation failed" }, { "errorCode", "RUNTIME_EXCEPTION" },
                { "executionTimeMs", 3L }, { "executedAt", Ended },
                { "nested", new Dictionary<string, object> { { "missingValue", null } } },
                { "array", new List<object> { null, false, 7L, new Dictionary<string, object> { { "x", null } } } }
            };
            var value = Parse(data, Run);
            AssertObserved(value, "completed", true);
            Assert.That(value.error, Is.Null);
            var payload = (Dictionary<string, object>)value.resultValue;
            Assert.That(payload["success"], Is.EqualTo(false));
            Assert.That(payload.ContainsKey("result"), Is.True);
            Assert.That(payload["result"], Is.Null);
            var roundtrip = AIBridgeJson.DeserializeObjectStrict(AIBridgeJson.Serialize(value));
            var result = (Dictionary<string, object>)roundtrip["resultValue"];
            Assert.That(result.ContainsKey("result"), Is.True);
            Assert.That(((Dictionary<string, object>)result["nested"]).ContainsKey("missingValue"), Is.True);
            Assert.That(((List<object>)result["array"])[0], Is.Null);
            Assert.That(((Dictionary<string, object>)((List<object>)result["array"])[3]).ContainsKey("x"), Is.True);
        }

        [Test]
        public void Status_FailedMayKeepEvalPayloadOrNullAndMayNotHaveStarted()
        {
            var data = Status("failed");
            data["result"] = new Dictionary<string, object>
            {
                { "success", false }, { "command", Eval }, { "error", "exception" },
                { "errorCode", "RUNTIME_EXCEPTION" }, { "result", null }, { "diagnostics", new List<object>() }
            };
            data["errorCode"] = "RUNTIME_EXCEPTION";
            data["errorDetails"] = "fixture stack";
            var value = Parse(data);
            AssertObserved(value, "failed", true);
            Assert.That(((Dictionary<string, object>)value.resultValue)["success"], Is.EqualTo(false));
            Assert.That(value.errorCode, Is.EqualTo("RUNTIME_EXCEPTION"));
            Assert.That(value.errorDetails, Is.EqualTo("fixture stack"));
            data["result"] = null;
            data["startedAt"] = null;
            AssertObserved(Parse(data), "failed", true);
            Assert.That(Parse(data).notStarted, Is.False);
        }

        [Test]
        public void Status_CanceledDoesNotPromiseNoSideEffectsAndFlagDoesNotProveEnded()
        {
            var data = Status("canceled");
            data["cancellationRequested"] = true;
            var afterStart = Parse(data);
            AssertObserved(afterStart, "canceled", true);
            Assert.That(afterStart.notStarted, Is.False);
            Assert.That(afterStart.resultValue, Is.Null);
            data["startedAt"] = null;
            var beforeStart = Parse(data);
            Assert.That(beforeStart.notStarted, Is.True);
            Assert.That(beforeStart.sideEffectsUnknown, Is.True);
            foreach (var state in new[] { "queued", "running" })
            {
                data = Status(state);
                data["cancellationRequested"] = true;
                AssertObserved(Parse(data), state, false);
                Assert.That(Parse(data).notStarted, Is.False);
            }
        }

        [Test]
        public void Status_EveryTopLevelFieldMustExistEvenWhenNullable()
        {
            foreach (var key in new List<string>(Status().Keys))
            {
                var data = Status();
                data.Remove(key);
                AssertUnknown(Parse(data));
            }
        }

        [Test]
        public void Status_EnvelopeScalarFieldsAreTyped()
        {
            foreach (var key in new[] { "jobId", "command", "state", "cancellationRequested", "enqueuedAt",
                "startedAt", "completedAt", "error", "errorDetails", "errorCode", "progress", "result" })
            {
                var data = Status("completed");
                data[key] = new List<object> { "wrong type" };
                AssertUnknown(Parse(data));
            }
            foreach (var bad in new object[] { null, "false", 0L })
            {
                var data = Status();
                data["cancellationRequested"] = bad;
                AssertUnknown(Parse(data));
            }
            foreach (var key in new[] { "error", "errorDetails", "errorCode" })
            {
                var data = Status("failed");
                data[key] = true;
                AssertUnknown(Parse(data));
            }
        }

        [TestCase("unknown")]
        [TestCase("success")]
        [TestCase("cancelled")]
        [TestCase("RUNNING")]
        [TestCase(null)]
        public void Status_UnknownStateCannotProveCompletion(string state)
        {
            var data = Status();
            data["state"] = state;
            AssertUnknown(Parse(data));
        }

        [Test]
        public void Status_ResultMustBeDictionaryOrNullWithoutInspectingBusinessTree()
        {
            foreach (var result in new object[] { false, 3L, "text", new List<object> { null } })
            {
                var data = Status("completed");
                data["result"] = result;
                AssertUnknown(Parse(data));
            }
            var arbitrary = Status("completed");
            arbitrary["result"] = new Dictionary<string, object>
            {
                { "anyBusinessField", new List<object> { null, "value", new Dictionary<string, object> { { "x", null } } } }
            };
            AssertObserved(Parse(arbitrary), "completed", true);
            arbitrary["result"] = null;
            AssertObserved(Parse(arbitrary), "completed", true);
        }

        [Test]
        public void Status_StageContradictionsAreRejected()
        {
            foreach (var state in new[] { "queued", "running", "completed", "canceled" })
                foreach (var field in new[] { "error", "errorDetails", "errorCode" })
                {
                    var data = Status(state);
                    data[field] = "conflict";
                    AssertUnknown(Parse(data));
                }
            foreach (var state in new[] { "queued", "running", "canceled" })
            {
                var data = Status(state);
                data["result"] = new Dictionary<string, object>();
                AssertUnknown(Parse(data));
            }
            foreach (var error in new object[] { null, "", "   " })
            {
                var data = Status("failed");
                data["error"] = error;
                AssertUnknown(Parse(data));
            }
            AssertBadChange("queued", "startedAt", Started);
            AssertBadChange("queued", "completedAt", Ended);
            AssertBadChange("running", "startedAt", null);
            AssertBadChange("running", "completedAt", Ended);
            AssertBadChange("completed", "startedAt", null);
            foreach (var state in new[] { "completed", "failed", "canceled" }) AssertBadChange(state, "completedAt", null);
            foreach (var state in new[] { "queued", "completed", "failed", "canceled" }) AssertBadChange(state, "progress", Progress());
        }

        [Test]
        public void Status_UtcTimestampsAndOrderingAreStrict()
        {
            foreach (var key in new[] { "enqueuedAt", "startedAt", "completedAt" })
                foreach (var time in new object[] { 3L, "", "2026-11-05", "2026-11-05T01:00:00",
                    "2026-11-05T01:00:00+08:00", "2026-02-30T01:00:00Z", "2026-11-05T01:00:00.Z",
                    "2026-11-05T01:00:00.12345678Z", " 2026-11-05T01:00:00Z", "2026-11-05T01:00:00z" })
                {
                    var data = Status("completed");
                    data[key] = time;
                    AssertUnknown(Parse(data));
                }
            AssertBadChange("completed", "startedAt", "2026-11-05T00:59:59Z");
            AssertBadChange("completed", "completedAt", Enqueued);
            AssertBadChange("canceled", "completedAt", "2026-11-05T00:59:59Z");
            var equal = Status("completed");
            equal["startedAt"] = Enqueued;
            equal["completedAt"] = "2026-11-05T01:00:00+00:00";
            AssertObserved(Parse(equal), "completed", true);
            var canceled = Status("canceled");
            canceled["startedAt"] = null;
            canceled["completedAt"] = "2026-11-05T00:59:59Z";
            AssertUnknown(Parse(canceled));
        }

        [Test]
        public void Status_RequiredIdentityAndEnqueueValuesCannotBeNull()
        {
            foreach (var field in new[] { "jobId", "command", "enqueuedAt" })
            {
                var data = Status();
                data[field] = null;
                AssertUnknown(Parse(data));
            }
            foreach (var id in InvalidIds())
            {
                var data = Status();
                data["jobId"] = id;
                AssertUnknown(Parse(data));
            }
            var uppercase = Status();
            uppercase["jobId"] = JobId.ToUpperInvariant();
            AssertObserved(PipelineJobProtocol.ParseStatus(Json(uppercase), 200, JobId.ToUpperInvariant(), Eval), "queued", false);
        }

        [Test]
        public void Submission_ExecutedAtMustBeUtcAndRealDate()
        {
            foreach (var bad in new[] { "2026-11-05", "2026-11-05T01:00:00", "2026-11-05T01:00:00+08:00", "2026-02-30T00:00:00Z" })
            {
                var data = Submission();
                data["executedAt"] = bad;
                AssertUnknown(PipelineJobProtocol.ParseSubmission(Json(data), 200, Eval));
            }
        }

        [Test]
        public void BothParsers_OnlyFixedCommandsAndExactExpectedIdAreAccepted()
        {
            foreach (var command in new[] { null, "", " ", "script", "script eval ", "SCRIPT EVAL", "editor status", "script run --file x" })
            {
                AssertUnknown(PipelineJobProtocol.ParseSubmission(Json(Submission()), 200, command));
                AssertUnknown(PipelineJobProtocol.ParseStatus(Json(Status()), 200, JobId, command));
            }
            AssertUnknown(PipelineJobProtocol.ParseStatus(Json(Status()), 200, OtherId, Eval));
            AssertUnknown(PipelineJobProtocol.ParseStatus(Json(Status()), 200, JobId.ToUpperInvariant(), Eval));
            AssertUnknown(PipelineJobProtocol.ParseStatus(Json(Status()), 200, JobId, Run));
            foreach (var id in InvalidIds()) AssertUnknown(PipelineJobProtocol.ParseStatus(Json(Status()), 200, id, Eval));
            var mismatch = Status();
            mismatch["jobId"] = OtherId;
            AssertUnknown(Parse(mismatch));
        }

        [TestCase(0)]
        [TestCase(202)]
        [TestCase(302)]
        [TestCase(400)]
        [TestCase(401)]
        [TestCase(403)]
        [TestCase(404)]
        [TestCase(405)]
        [TestCase(413)]
        [TestCase(429)]
        [TestCase(500)]
        [TestCase(503)]
        [TestCase(504)]
        public void BothParsers_Non200IncludingReload404NeverProvesExecutionEnded(int httpStatus)
        {
            AssertUnknown(PipelineJobProtocol.ParseSubmission(Json(Submission()), httpStatus, Eval), httpStatus);
            AssertUnknown(PipelineJobProtocol.ParseStatus(Json(Status("completed")), httpStatus, JobId, Eval), httpStatus);
            const string rejection = "{\"message\":null,\"error\":\"Job Not Found\",\"errorDetails\":\"fixture reload/retention secret\",\"executedAt\":\"2026-11-05T01:00:00Z\"}";
            AssertUnknown(PipelineJobProtocol.ParseStatus(rejection, httpStatus, JobId, Eval), httpStatus);
        }

        [Test]
        public void BothParsers_FieldInjectionsNeverBecomeMetadataOrRouting()
        {
            foreach (var field in new[] { "success", "status", "executionTimeMs", "message", "token", "evalToken",
                "descriptor", "url", "path", "argv", "parameters", "retryable", "errorDetailsInjected", "State", "$type" })
            {
                var data = Status();
                data[field] = "fixture secret";
                AssertUnknown(Parse(data));
            }
            foreach (var field in new[] { "status", "retryable", "parameters", "argProblems", "commandSchema", "listedGroup", "token", "$type" })
            {
                var data = Submission();
                data[field] = null;
                AssertUnknown(PipelineJobProtocol.ParseSubmission(Json(data), 200, Eval));
            }
        }

        [Test]
        public void Status_ProgressNullableFieldsAndNumericBoundariesAreRetained()
        {
            var data = Status("running");
            var progress = Progress();
            data["progress"] = progress;
            AssertObserved(Parse(data), "running", false);
            foreach (var field in new List<string>(progress.Keys)) progress[field] = null;
            var value = Parse(data);
            AssertObserved(value, "running", false);
            var tree = (Dictionary<string, object>)value.progress;
            Assert.That(tree.Count, Is.EqualTo(5));
            Assert.That(tree.ContainsKey("pct"), Is.True);
            Assert.That(tree["pct"], Is.Null);
            progress["current"] = 1.5d;
            progress["total"] = 0L;
            foreach (var pct in new object[] { 0L, 1L, 0.5d })
            {
                progress["pct"] = pct;
                AssertObserved(Parse(data), "running", false);
            }
        }

        [Test]
        public void Status_ProgressTypesMissingFieldsAndNonFiniteNumbersAreRejected()
        {
            foreach (var field in new[] { "title", "info", "current", "total", "pct" })
            {
                var data = Status("running");
                var progress = Progress();
                data["progress"] = progress;
                progress.Remove(field);
                AssertUnknown(Parse(data));
                progress = Progress();
                data["progress"] = progress;
                progress[field] = new List<object>();
                AssertUnknown(Parse(data));
            }
            foreach (var field in new[] { "current", "total", "pct" })
                foreach (var bad in new object[] { -1L, -0.1d, "1", true })
                {
                    var data = Status("running");
                    var progress = Progress();
                    data["progress"] = progress;
                    progress[field] = bad;
                    AssertUnknown(Parse(data));
                }
            var extra = Status("running");
            var injected = Progress();
            injected["success"] = true;
            extra["progress"] = injected;
            AssertUnknown(Parse(extra));
            foreach (var number in new[] { "1.01", "1e999", "NaN", "Infinity" })
            {
                var json = Json(Status("running")).Replace("\"progress\":null", "\"progress\":{\"title\":null,\"info\":null,\"current\":null,\"total\":null,\"pct\":" + number + "}");
                AssertUnknown(PipelineJobProtocol.ParseStatus(json, 200, JobId, Eval));
            }
        }

        [Test]
        public void BothParsers_WarningsAreBoundedNullableStringArrays()
        {
            foreach (var warnings in new object[] { null, new List<object>(), new List<object> { "guide", "" } })
            {
                var data = Status();
                data["warnings"] = warnings;
                AssertObserved(Parse(data), "queued", false);
                var submit = Submission();
                submit["warnings"] = warnings;
                AssertObserved(PipelineJobProtocol.ParseSubmission(Json(submit), 200, Eval), "queued", false);
            }
            var tooMany = new List<object>();
            for (var i = 0; i < 33; i++) tooMany.Add("warning");
            foreach (var bad in new object[] { "warning", new List<object> { null }, new List<object> { 1L },
                new List<object> { new string('x', 4097) }, tooMany })
            {
                var data = Status();
                data["warnings"] = bad;
                AssertUnknown(Parse(data));
                var submit = Submission();
                submit["warnings"] = bad;
                AssertUnknown(PipelineJobProtocol.ParseSubmission(Json(submit), 200, Eval));
            }
        }

        [Test]
        public void BothParsers_StrictJsonRejectsDecodedDuplicatesMalformedAndTrailingData()
        {
            var status = Json(Status("completed"));
            var submission = Json(Submission());
            foreach (var json in new[] { null, "", "null", "[]", "{}", status + "{}", "{", "{\"secret\":\"fixture\"}" })
            {
                AssertUnknown(PipelineJobProtocol.ParseStatus(json, 200, JobId, Eval));
                AssertUnknown(PipelineJobProtocol.ParseSubmission(json, 200, Eval));
            }
            AssertUnknown(PipelineJobProtocol.ParseStatus(status.Replace("\"state\":\"completed\"", "\"state\":\"completed\",\"st\\u0061te\":\"completed\""), 200, JobId, Eval));
            AssertUnknown(PipelineJobProtocol.ParseSubmission(submission.Replace("\"success\":true", "\"success\":true,\"s\\u0075ccess\":true"), 200, Eval));
            var nested = Status("completed");
            nested["result"] = new Dictionary<string, object> { { "key", 1L } };
            var duplicate = Json(nested).Replace("\"key\":1", "\"key\":1,\"\\u006bey\":2");
            AssertUnknown(PipelineJobProtocol.ParseStatus(duplicate, 200, JobId, Eval));
        }

        [Test]
        public void BothParsers_SizeIsUtf8BytesAndDepthIs64()
        {
            var data = Status("completed");
            data["result"] = new Dictionary<string, object> { { "value", 0L } };
            var basis = Json(data);
            var atLimit = basis.Replace("\"value\":0", "\"value\":" + new string('[', 62) + "0" + new string(']', 62));
            AssertObserved(PipelineJobProtocol.ParseStatus(atLimit, 200, JobId, Eval), "completed", true);
            var tooDeep = basis.Replace("\"value\":0", "\"value\":" + new string('[', 63) + "0" + new string(']', 63));
            AssertUnknown(PipelineJobProtocol.ParseStatus(tooDeep, 200, JobId, Eval));
            AssertUnknown(PipelineJobProtocol.ParseSubmission(tooDeep, 200, Eval));
            var padding = new string(' ', 1024 * 1024 - basis.Length);
            AssertObserved(PipelineJobProtocol.ParseStatus(basis + padding, 200, JobId, Eval), "completed", true);
            AssertUnknown(PipelineJobProtocol.ParseStatus(basis + padding + " ", 200, JobId, Eval));
            var submit = Json(Submission());
            var submitPadding = new string(' ', 1024 * 1024 - submit.Length);
            AssertObserved(PipelineJobProtocol.ParseSubmission(submit + submitPadding, 200, Eval), "queued", false);
            AssertUnknown(PipelineJobProtocol.ParseSubmission(submit + submitPadding + " ", 200, Eval));
            data["result"] = new Dictionary<string, object> { { "value", new string('界', 400000) } };
            AssertUnknown(Parse(data));
        }

        [Test]
        public void CancelRequest_RequiresStrict32HexAndContainsOnlyId()
        {
            foreach (var id in new[] { JobId, JobId.ToUpperInvariant(), new string('0', 32) })
            {
                Assert.That(PipelineJobProtocol.IsJobId(id), Is.True);
                var data = AIBridgeJson.DeserializeObjectStrict(PipelineJobProtocol.BuildCancelRequest(id));
                Assert.That(data.Count, Is.EqualTo(1));
                Assert.That(data["id"], Is.EqualTo(id));
            }
            foreach (var id in InvalidIds())
            {
                Assert.That(PipelineJobProtocol.IsJobId(id), Is.False);
                Assert.Throws<ArgumentException>(() => PipelineJobProtocol.BuildCancelRequest(id));
            }
        }

        private static Dictionary<string, object> Submission(string command = Eval)
        {
            return new Dictionary<string, object>
            {
                { "success", true }, { "command", command },
                { "result", new Dictionary<string, object> { { "jobId", JobId }, { "state", "queued" } } },
                { "executionTimeMs", null }, { "message", null }, { "error", null }, { "errorDetails", null }, { "executedAt", Enqueued }
            };
        }

        private static Dictionary<string, object> Status(string state = "queued", string command = Eval)
        {
            var terminal = state == "completed" || state == "failed" || state == "canceled";
            return new Dictionary<string, object>
            {
                { "jobId", JobId }, { "command", command }, { "state", state }, { "cancellationRequested", false },
                { "enqueuedAt", Enqueued }, { "startedAt", state == "queued" ? null : Started },
                { "completedAt", terminal ? Ended : null }, { "result", null },
                { "error", state == "failed" ? "Command Execution Failed" : null }, { "errorDetails", null },
                { "errorCode", null }, { "progress", null }, { "warnings", null }
            };
        }

        private static Dictionary<string, object> Progress()
        {
            return new Dictionary<string, object> { { "title", "work" }, { "info", "phase" }, { "current", 1L }, { "total", 2L }, { "pct", 0.5d } };
        }

        private static string[] InvalidIds()
        {
            return new[] { null, "", " ", new string('0', 31), new string('0', 33),
                "01234567-89ab-cdef-0123-456789abcdef", "{" + JobId + "}", JobId + " ",
                "g123456789abcdef0123456789abcdef", "０123456789abcdef0123456789abcdef", JobId.Substring(0, 31) + "\n" };
        }

        private static string Json(Dictionary<string, object> value)
        {
            return AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(value));
        }

        private static PipelineJobObservation Parse(Dictionary<string, object> value, string command = Eval)
        {
            return PipelineJobProtocol.ParseStatus(Json(value), 200, JobId, command);
        }

        private static void AssertBadChange(string state, string field, object value)
        {
            var data = Status(state);
            data[field] = value;
            AssertUnknown(Parse(data));
        }

        private static void AssertObserved(PipelineJobObservation value, string state, bool ended)
        {
            Assert.That(value.valid, Is.True);
            Assert.That(value.status, Is.EqualTo("observed"));
            Assert.That(value.httpStatus, Is.EqualTo(200));
            Assert.That(value.state, Is.EqualTo(state));
            Assert.That(value.executionEnded, Is.EqualTo(ended));
            Assert.That(value.sideEffectsUnknown, Is.True);
        }

        private static void AssertUnknown(PipelineJobObservation value, int httpStatus = 200)
        {
            Assert.That(value.valid, Is.False);
            Assert.That(value.status, Is.EqualTo("unknown"));
            Assert.That(value.httpStatus, Is.EqualTo(httpStatus));
            Assert.That(value.executionEnded, Is.False);
            Assert.That(value.notStarted, Is.False);
            Assert.That(value.sideEffectsUnknown, Is.True);
            Assert.That(value.errorCode, Is.EqualTo("pipelineJobResponseInvalid"));
            Assert.That(value.error, Is.EqualTo("Pipeline job state could not be confirmed; do not replay automatically."));
            Assert.That(value.errorDetails, Is.Null);
            Assert.That(value.jobId, Is.Null);
            Assert.That(value.command, Is.Null);
            Assert.That(value.state, Is.Null);
            Assert.That(value.resultValue, Is.Null);
            Assert.That(value.progress, Is.Null);
            Assert.That(value.warnings, Is.Null);
        }
    }
}
