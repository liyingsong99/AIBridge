using System;
using System.Collections.Generic;
using AIBridge.Internal.Json;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class PipelineTestProtocolTests
    {
        internal const string Utc = "2026-11-05T05:30:11.1234567Z";
        internal static readonly PipelineTestOptions Options = new PipelineTestOptions("Fixture.Unique", "testName", 120000);

        [Test]
        public void Request_IsOneRestrictedSynchronousEditModeCommand()
        {
            var d = AIBridgeJson.DeserializeObjectStrict(PipelineTestProtocol.BuildRequest(Options));
            Assert.That(d.Count, Is.EqualTo(6));
            Assert.That(d["command"], Is.EqualTo("test run"));
            Assert.That(d["job"], Is.EqualTo(false));
            Assert.That(d["verbose"], Is.EqualTo(true));
            Assert.That(d["omitNulls"], Is.EqualTo(false));
            Assert.That(d["timeout"], Is.EqualTo(120000L));
            var p = (Dictionary<string, object>)d["parameters"];
            Assert.That(p.Count, Is.EqualTo(6));
            Assert.That(p["mode"], Is.EqualTo("editor"));
            Assert.That(p["async-tests"], Is.EqualTo(false));
            Assert.That(p["include-explicit"], Is.EqualTo(false));
            Assert.That(p["filter"], Is.EqualTo(Options.Filter));
            Assert.That(p["filter-type"], Is.EqualTo("testName"));
            Assert.That(p["timeout"], Is.EqualTo(120L));
        }

        [TestCase(999)] [TestCase(600001)] [TestCase(0)]
        public void Options_TimeoutOutOfRangeIsRejected(int timeout)
        { Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineTestOptions(null, null, timeout)); }

        [TestCase("regex")] [TestCase("testname")] [TestCase("groupName")] [TestCase("category " )]
        public void Options_UnsupportedFilterTypeIsRejected(string type)
        { Assert.Throws<ArgumentException>(() => new PipelineTestOptions("x", type, 1000)); }

        [TestCase("testName", "testname: Fixture.Unique")]
        [TestCase("assembly", "assembly: Fixture.Unique")]
        [TestCase("category", "category: Fixture.Unique")]
        public void Filter_EchoUsesActualUpstreamNormalization(string type, string applied)
        {
            var options = new PipelineTestOptions("Fixture.Unique", type, 5000);
            var inner = Inner(); inner["FilterApplied"] = applied;
            Assert.That(Parse(inner, options).valid, Is.True);
        }

        [TestCase(1, 0, 0, 0, "passed", true)]
        [TestCase(1, 1, 0, 0, "failed", false)]
        [TestCase(0, 0, 0, 0, "no_tests", false)]
        [TestCase(0, 0, 2, 0, "skipped", false)]
        [TestCase(0, 0, 0, 2, "inconclusive", false)]
        [TestCase(1, 0, 0, 1, "inconclusive", false)]
        [TestCase(1, 0, 1, 0, "passed", true)]
        public void BusinessOutcome_IsIndependentFromBothSuccessEnvelopes(int pass, int fail, int skip, int inconclusive, string status, bool success)
        {
            var result = Parse(Inner(pass, fail, skip, inconclusive));
            Assert.That(result.valid && result.executionEnded, Is.True);
            Assert.That(result.status, Is.EqualTo(status));
            Assert.That(result.success, Is.EqualTo(success));
            Assert.That(PipelineTestProtocol.ValidCompletedData(result.data, Options, status), Is.True);
        }

        [Test]
        public void NullMembersSurviveSerializationAndNoFilterStaysNull()
        {
            var options = new PipelineTestOptions(null, null, 1000);
            var inner = Inner(); inner["FilterApplied"] = null;
            var result = Parse(inner, options);
            Assert.That(result.valid, Is.True);
            var roundTrip = AIBridgeJson.DeserializeObjectStrict(AIBridgeJson.Serialize(result.data));
            Assert.That(roundTrip.ContainsKey("filterApplied") && roundTrip["filterApplied"] == null, Is.True);
            var item = (Dictionary<string, object>)((List<object>)roundTrip["results"])[0];
            Assert.That(item.ContainsKey("Message") && item["Message"] == null, Is.True);
            Assert.That(item.ContainsKey("StackTrace") && item["StackTrace"] == null, Is.True);
        }

        [Test]
        public void EveryRequiredFieldAndNoUnknownFieldsAreEnforcedAtBothLevels()
        {
            foreach (var key in new List<string>(Inner().Keys))
            { var inner = Inner(); inner.Remove(key); Unknown(Parse(inner)); }
            var outerFields = Outer(Inner());
            foreach (var key in new List<string>(outerFields.Keys))
            { var outer = Outer(Inner()); outer.Remove(key); Unknown(ParseRaw(Json(outer))); }
            foreach (var injected in new[] { "retryable", "status", "parameters", "jobId", "Mode" })
            {
                var outer = Outer(Inner()); outer[injected] = null; Unknown(ParseRaw(Json(outer)));
            }
            var bad = Inner(); bad["mode"] = "EditMode"; Unknown(Parse(bad));
        }

        [Test]
        public void InnerAndOuterBaseTypesAndNoAsyncMarkerAreEnforced()
        {
            foreach (var key in new[] { "success", "command", "result", "executionTimeMs", "message", "error", "errorDetails", "executedAt" })
            {
                var inner = Inner(); inner[key] = new List<object>(); Unknown(Parse(inner));
                var outer = Outer(Inner()); outer[key] = new List<object>(); Unknown(ParseRaw(Json(outer)));
            }
            foreach (var key in new[] { "StatusPath", "Mode", "FilterApplied" })
            { var inner = Inner(); inner[key] = "wrong"; Unknown(Parse(inner)); }
            var failed = Inner(); failed["success"] = false; failed["error"] = "timeout"; Unknown(Parse(failed));
            var outerFailed = Outer(Inner()); outerFailed["success"] = false; Unknown(ParseRaw(Json(outerFailed)));
        }

        [Test]
        public void SummaryMustHaveExactlyFiveNonnegativeIntegralBoundedConsistentCounts()
        {
            foreach (var key in new[] { "Total", "Passed", "Failed", "Skipped", "Inconclusive" })
            {
                foreach (var bad in new object[] { null, -1L, 1.5, "1", true, long.MaxValue })
                { var inner = Inner(); Summary(inner)[key] = bad; Unknown(Parse(inner)); }
                var missing = Inner(); Summary(missing).Remove(key); Unknown(Parse(missing));
            }
            var contradiction = Inner(); Summary(contradiction)["Total"] = 2L; Unknown(Parse(contradiction));
            var extra = Inner(); Summary(extra)["Other"] = 0L; Unknown(Parse(extra));
        }

        [Test]
        public void ResultsMustMatchSummaryAndHaveKnownOutcomes()
        {
            foreach (var bad in new object[] { null, "Passed", new Dictionary<string, object>() })
            { var inner = Inner(); inner["Results"] = bad; Unknown(Parse(inner)); }
            var empty = Inner(); empty["Results"] = new List<object>(); Unknown(Parse(empty));
            foreach (var key in new[] { "FullName", "Status", "Duration", "Message", "StackTrace" })
            { var inner = Inner(); First(inner).Remove(key); Unknown(Parse(inner)); }
            foreach (var status in new object[] { null, "passed", "NotRun", 1L, "Total" })
            { var inner = Inner(); First(inner)["Status"] = status; Unknown(Parse(inner)); }
            var wrongCount = Inner(); First(wrongCount)["Status"] = "Failed"; Unknown(Parse(wrongCount));
            var extra = Inner(); First(extra)["Other"] = null; Unknown(Parse(extra));
        }

        [Test]
        public void DurationsAreFiniteNonnegativeAndRunTimingIsCoherent()
        {
            foreach (var bad in new object[] { null, -1L, -0.1, "1", true })
            {
                var inner = Inner(); inner["Duration"] = bad; Unknown(Parse(inner));
                inner = Inner(); First(inner)["Duration"] = bad; Unknown(Parse(inner));
            }
            var mismatch = Inner(); mismatch["executionTimeMs"] = 9000L; Unknown(Parse(mismatch));
            foreach (var number in new[] { "1e9999", "NaN", "Infinity" })
                Unknown(ParseRaw(Json(Outer(Inner())).Replace("\"Duration\":1", "\"Duration\":" + number)));
        }

        [TestCase("2026-11-05T05:30:11Z")]
        [TestCase("2026-11-05T05:30:11+00:00")]
        [TestCase("2026-11-05T05:30:11.1234567+00:00")]
        public void UtcAcceptsOnlyBoundedExplicitZeroOffset(string time)
        { var inner = Inner(); inner["executedAt"] = time; var outer = Outer(inner); outer["executedAt"] = time; Assert.That(ParseRaw(Json(outer)).valid, Is.True); }

        [TestCase("2026-11-05T05:30:11")]
        [TestCase("2026-11-05T05:30:11+01:00")]
        [TestCase("2026-11-05T05:30:11.12345678Z")]
        [TestCase("2026-11-05T05:30:11.Z")]
        [TestCase("2026-11-05")]
        public void BadUtcAndContradictoryOrderRemainUnknown(string time)
        { var inner = Inner(); inner["executedAt"] = time; Unknown(Parse(inner)); }

        [Test]
        public void InnerCannotBeExecutedAfterOuter()
        { var inner = Inner(); inner["executedAt"] = "2026-11-06T00:00:00Z"; Unknown(Parse(inner)); }

        [Test]
        public void WarningsAndTextAreBoundedAndTyped()
        {
            foreach (var bad in new object[] { true, new List<object> { null }, new List<object> { new string('x', 4097) } })
            { var inner = Inner(); inner["warnings"] = bad; Unknown(Parse(inner)); }
            var many = new List<object>(); for (var i = 0; i < 33; i++) many.Add("x");
            var outer = Outer(Inner()); outer["warnings"] = many; Unknown(ParseRaw(Json(outer)));
            var invalid = Inner(); First(invalid)["FullName"] = " "; Unknown(Parse(invalid));
            invalid = Inner(); First(invalid)["Message"] = new string('x', 16385); Unknown(Parse(invalid));
            var valid = Inner(); valid["warnings"] = new List<object> { "warning" };
            Assert.That(Parse(valid).valid, Is.True);
        }

        [TestCase(0)] [TestCase(201)] [TestCase(400)] [TestCase(401)] [TestCase(404)] [TestCase(429)] [TestCase(500)] [TestCase(504)]
        public void EveryNon200ResponseIsUnknownWithoutPayload(int status)
        { Unknown(PipelineTestProtocol.ParseResponse(Json(Outer(Inner())), status, Options), status); }

        [Test]
        public void StrictJsonRejectsDuplicatesDepthAndUtf8Oversize()
        {
            foreach (var json in new[] { "", "null", "[]", "{}", "{", "{\"success\":true,\"success\":true}",
                Json(Outer(Inner())) + "x", Json(Outer(Inner())).Replace("\"success\":true", "\"success\":true,\"suc\\u0063ess\":true") }) Unknown(ParseRaw(json));
            var nested = new string('[', 17) + "0" + new string(']', 17);
            Unknown(ParseRaw("{\"x\":" + nested + "}"));
            Unknown(ParseRaw(new string('界', PipelineTestProtocol.MaxJsonBytes / 3 + 1)));
            Assert.That(PipelineTestProtocol.ParseResponse(Json(Outer(Inner())), 200, null).valid, Is.False);
        }

        internal static Dictionary<string, object> Inner(int pass = 1, int fail = 0, int skip = 0, int inconclusive = 0)
        {
            var d = Base();
            d["executionTimeMs"] = 1000L;
            d.Add("Summary", new Dictionary<string, object> { { "Total", (long)(pass + fail + skip + inconclusive) },
                { "Passed", (long)pass }, { "Failed", (long)fail }, { "Skipped", (long)skip }, { "Inconclusive", (long)inconclusive } });
            var results = new List<object>();
            var counts = new[] { pass, fail, skip, inconclusive }; var outcomes = new[] { "Passed", "Failed", "Skipped", "Inconclusive" };
            for (var index = 0; index < counts.Length; index++)
                for (var i = 0; i < counts[index]; i++) results.Add(new Dictionary<string, object> { { "FullName", "Fixture.Test" + index + "_" + i },
                    { "Status", outcomes[index] }, { "Duration", 1.0 }, { "Message", null }, { "StackTrace", null } });
            d.Add("Results", results); d.Add("Duration", 1.0); d.Add("StatusPath", null); d.Add("Mode", "EditMode");
            d.Add("FilterApplied", Options.ExpectedFilterApplied);
            return d;
        }
        internal static Dictionary<string, object> Outer(Dictionary<string, object> inner) { var d = Base(); d["result"] = inner; return d; }
        private static Dictionary<string, object> Base()
        { return new Dictionary<string, object> { { "success", true }, { "command", "test run" }, { "result", null },
            { "executionTimeMs", null }, { "message", null }, { "error", null }, { "errorDetails", null }, { "executedAt", Utc } }; }
        internal static string Json(object value) { return AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(value)); }
        internal static PipelineTestResult Parse(Dictionary<string, object> inner, PipelineTestOptions options = null)
        { return PipelineTestProtocol.ParseResponse(Json(Outer(inner)), 200, options ?? Options); }
        private static PipelineTestResult ParseRaw(string json) { return PipelineTestProtocol.ParseResponse(json, 200, Options); }
        private static Dictionary<string, object> Summary(Dictionary<string, object> inner) { return (Dictionary<string, object>)inner["Summary"]; }
        private static Dictionary<string, object> First(Dictionary<string, object> inner) { return (Dictionary<string, object>)((List<object>)inner["Results"])[0]; }
        private static void Unknown(PipelineTestResult result, int http = 200)
        {
            Assert.That(result.valid || result.success || result.executionEnded, Is.False);
            Assert.That(result.status, Is.EqualTo("unknown")); Assert.That(result.httpStatus, Is.EqualTo(http));
            Assert.That(result.data, Is.Null);
        }
    }
}
