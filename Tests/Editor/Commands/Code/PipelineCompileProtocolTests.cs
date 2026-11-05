using System;
using System.Collections.Generic;
using AIBridge.Internal.Json;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class PipelineCompileProtocolTests
    {
        internal const string Utc = "2026-11-05T06:00:00.1234567Z";

        [TestCase(1000)] [TestCase(60000)] [TestCase(2500)]
        public void RequestIsExactSingleSynchronousRefreshWithoutFocus(int timeout)
        {
            var data = AIBridgeJson.DeserializeObjectStrict(PipelineCompileProtocol.BuildRequest(timeout));
            Assert.That(data.Count, Is.EqualTo(6));
            Assert.That(data["command"], Is.EqualTo("script recompile"));
            Assert.That(data["timeout"], Is.EqualTo((long)timeout));
            Assert.That(data["verbose"], Is.True); Assert.That(data["omitNulls"], Is.False); Assert.That(data["job"], Is.False);
            var parameters = (Dictionary<string, object>)data["parameters"];
            Assert.That(parameters.Count, Is.EqualTo(1)); Assert.That(parameters["focus"], Is.False);
            Assert.That(PipelineCompileProtocol.SupportedVersion, Is.EqualTo("0.10.0-exp.1"));
        }

        [TestCase(-1)] [TestCase(0)] [TestCase(999)] [TestCase(60001)] [TestCase(int.MaxValue)]
        public void RequestRejectsTimeoutOutsideBoundedWindow(int timeout)
        { Assert.Throws<ArgumentOutOfRangeException>(() => PipelineCompileProtocol.BuildRequest(timeout)); }

        [TestCase("up_to_date")] [TestCase("compiling")] [TestCase("failed")]
        public void ThreeAcknowledgementsNeverProveCompilationSuccess(string acknowledgement)
        {
            var result = Parse(Outer(acknowledgement));
            Assert.That(result.valid, Is.True); Assert.That(result.httpStatus, Is.EqualTo(200));
            Assert.That(result.acknowledgement, Is.EqualTo(acknowledgement));
            var data = AIBridgeJson.DeserializeObjectStrict(AIBridgeJson.Serialize(result.data));
            Assert.That(data.Count, Is.EqualTo(8)); Assert.That(data["acknowledgement"], Is.EqualTo(acknowledgement));
            foreach (var field in new[] { "compilationSuccess", "errorCount", "warningCount", "duration", "warnings" })
            { Assert.That(data.ContainsKey(field), Is.True, field); Assert.That(data[field], Is.Null, field); }
            Assert.That(data["operationCorrelation"], Is.False); Assert.That(data["executedAt"], Is.EqualTo(Utc));
            Assert.That(PipelineCompileProtocol.ValidAcknowledgedData(data, acknowledgement), Is.True);
            Assert.That(result.errorCode, Is.EqualTo(acknowledgement == "failed" ? "pipelineCompileFailed" : null));
        }

        [Test]
        public void RequiredOuterEightKeysAndExactDedicatedResultAreEnforced()
        {
            foreach (var key in new List<string>(Outer().Keys))
            { var data = Outer(); data.Remove(key); Unknown(Parse(data)); }
            foreach (var key in new[] { "status", "message" })
            { var data = Outer(); Result(data).Remove(key); Unknown(Parse(data)); }
            foreach (var key in new[] { "success", "jobId", "status", "Mode", "parameters" })
            {
                var data = Outer(); data[key] = null; Unknown(Parse(data));
                data = Outer(); Result(data)[key] = null; Unknown(Parse(data));
            }
        }

        [Test]
        public void WrongCommandFalseSuccessAndNonNullEnvelopeMetadataRemainUnknown()
        {
            foreach (var command in new[] { "script recompile_status", "recompile", "Script recompile", "script eval" })
            { var data = Outer(); data["command"] = command; Unknown(Parse(data)); }
            foreach (var bad in new object[] { null, false, "true", 1L })
            { var data = Outer(); data["success"] = bad; Unknown(Parse(data)); }
            foreach (var field in new[] { "executionTimeMs", "message", "error", "errorDetails", "errorCode" })
            {
                foreach (var bad in new object[] { "private raw error", 0L, false, new List<object>() })
                { var data = Outer(); data[field] = bad; Unknown(Parse(data)); }
            }
        }

        [TestCase(null)] [TestCase("completed")] [TestCase("idle")] [TestCase("triggered")]
        [TestCase("UP_TO_DATE")] [TestCase("compiling ")]
        public void UnrecognizedAcknowledgementCannotPromoteGlobalState(string acknowledgement)
        { Unknown(Parse(Outer(acknowledgement))); }

        [Test]
        public void ResultMessageIsRequiredBoundedTextButNotFixedEnglish()
        {
            var data = Outer(); Result(data)["message"] = "已确认 Refresh 请求"; Assert.That(Parse(data).valid, Is.True);
            Result(data)["message"] = new string('x', 4096); Assert.That(Parse(data).valid, Is.True);
            foreach (var bad in new object[] { null, "", " ", 1L, true, new string('x', 4097), "\ud800" })
            { data = Outer(); Result(data)["message"] = bad; Unknown(Parse(data)); }
            data = Outer(); data["result"] = new List<object>(); Unknown(Parse(data));
        }

        [TestCase("2026-11-05T06:00:00Z")]
        [TestCase("2026-11-05T06:00:00+00:00")]
        [TestCase("2026-11-05T06:00:00.1234567+00:00")]
        public void ExplicitBoundedUtcIsAccepted(string time)
        { var data = Outer(); data["executedAt"] = time; Assert.That(Parse(data).valid, Is.True); }

        [TestCase("2026-11-05T06:00:00")]
        [TestCase("2026-11-05T06:00:00+08:00")]
        [TestCase("2026-11-05T06:00:00.12345678Z")]
        [TestCase("2026-11-05T06:00:00.Z")]
        [TestCase("2026-11-05")]
        public void NonUtcOrAmbiguousTimeIsUnknown(string time)
        { var data = Outer(); data["executedAt"] = time; Unknown(Parse(data)); }

        [Test]
        public void OptionalWarningsAreBoundedAndCannotCarryArbitraryObjects()
        {
            var data = Outer(); data["warnings"] = new List<object> { "warning", new string('x', 4096) };
            data["errorCode"] = null;
            var result = Parse(data); Assert.That(result.valid, Is.True);
            Assert.That(((List<object>)((Dictionary<string, object>)result.data)["warnings"]).Count, Is.EqualTo(2));
            foreach (var bad in new object[] { true, "warning", new List<object> { null }, new List<object> { 1L },
                new List<object> { new string('x', 4097) }, new List<object> { "\ud800" } })
            { data = Outer(); data["warnings"] = bad; Unknown(Parse(data)); }
            var warnings = new List<object>(); for (var i = 0; i < 33; i++) warnings.Add("x");
            data = Outer(); data["warnings"] = warnings; Unknown(Parse(data));
        }

        [TestCase(0)] [TestCase(201)] [TestCase(400)] [TestCase(401)] [TestCase(403)]
        [TestCase(429)] [TestCase(500)] [TestCase(503)] [TestCase(504)]
        public void Non200NeverExposesRawErrorOrAcknowledgement(int httpStatus)
        { Unknown(PipelineCompileProtocol.ParseResponse(Json(Outer()), httpStatus), httpStatus); }

        [Test]
        public void StrictJsonRejectsDuplicateEscapedKeysTrailingInputDepthAndInvalidUnicode()
        {
            var original = Json(Outer());
            foreach (var json in new[] { null, "", "null", "[]", "{}", "{", original + "x",
                original.Replace("\"success\":true", "\"success\":true,\"suc\\u0063ess\":true"),
                original.Replace("\"status\":\"compiling\"", "\"status\":\"compiling\",\"status\":\"failed\""),
                "{\"x\":" + new string('[', 17) + "0" + new string(']', 17) + "}", original + "\ud800" })
                Unknown(PipelineCompileProtocol.ParseResponse(json, 200));
            Unknown(PipelineCompileProtocol.ParseResponse(new string('界', PipelineCompileProtocol.MaxJsonBytes / 3 + 1), 200));
        }

        [Test]
        public void RecordedDataCannotInventMetricsCorrelationOrExtraEvidence()
        {
            foreach (var field in new[] { "compilationSuccess", "errorCount", "warningCount", "duration", "operationCorrelation" })
            {
                var data = AIBridgeJson.DeserializeObjectStrict(AIBridgeJson.Serialize(Parse(Outer()).data)); data[field] = true;
                Assert.That(PipelineCompileProtocol.ValidAcknowledgedData(data, "compiling"), Is.False);
            }
            var extra = AIBridgeJson.DeserializeObjectStrict(AIBridgeJson.Serialize(Parse(Outer()).data)); extra["success"] = true;
            Assert.That(PipelineCompileProtocol.ValidAcknowledgedData(extra, "compiling"), Is.False);
        }

        internal static Dictionary<string, object> Outer(string acknowledgement = "compiling")
        {
            return new Dictionary<string, object>
            {
                { "success", true }, { "command", "script recompile" },
                { "result", new Dictionary<string, object> { { "status", acknowledgement }, { "message", "Fixture Refresh acknowledgement." } } },
                { "executionTimeMs", null }, { "message", null }, { "error", null }, { "errorDetails", null }, { "executedAt", Utc }
            };
        }
        internal static string Json(object value) { return AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(value)); }
        internal static PipelineCompileResult Parse(Dictionary<string, object> outer)
        { return PipelineCompileProtocol.ParseResponse(Json(outer), 200); }
        private static Dictionary<string, object> Result(Dictionary<string, object> outer)
        { return (Dictionary<string, object>)outer["result"]; }
        private static void Unknown(PipelineCompileResult result, int httpStatus = 200)
        {
            Assert.That(result.valid, Is.False); Assert.That(result.acknowledgement, Is.Null);
            Assert.That(result.data, Is.Null); Assert.That(result.httpStatus, Is.EqualTo(httpStatus));
            Assert.That(result.errorCode, Is.EqualTo("pipelineCompileOutcomeUnknown"));
            Assert.That(result.error, Does.Not.Contain("private raw error"));
        }
    }
}
