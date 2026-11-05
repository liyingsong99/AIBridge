using System;
using System.Collections.Generic;
using AIBridge.Internal.Json;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class PipelineDiagnosticsProtocolTests
    {
        private const string Compile = "compile_status";
        private const string List = "test_list";
        private const string CompileCommand = "script recompile_status";
        private const string ListCommand = "test list";
        private const string Utc = "2026-11-05T05:30:11.1234567Z";
        private const int Limit = 1024 * 1024;

        [TestCase(Compile, CompileCommand, 1000)]
        [TestCase(Compile, CompileCommand, 60000)]
        [TestCase(List, ListCommand, 1000)]
        [TestCase(List, ListCommand, 60000)]
        public void Request_FixedStructuredReadOnlyFlags(string action, string command, int timeout)
        {
            var data = AIBridgeJson.DeserializeObjectStrict(PipelineDiagnosticsProtocol.BuildRequest(action, timeout));
            Assert.That(data.Count, Is.EqualTo(6));
            Assert.That(data["command"], Is.EqualTo(command));
            Assert.That(data["timeout"], Is.EqualTo((long)timeout));
            Assert.That(data["verbose"], Is.EqualTo(true));
            Assert.That(data["omitNulls"], Is.EqualTo(false));
            Assert.That(data["job"], Is.EqualTo(false));
            var parameters = (Dictionary<string, object>)data["parameters"];
            Assert.That(parameters.Count, Is.EqualTo(action == Compile ? 0 : 1));
            if (action == List) Assert.That(parameters["mode"], Is.EqualTo("editor"));
            Assert.That(PipelineDiagnosticsProtocol.SupportedVersion, Is.EqualTo(PipelineCodeProtocol.SupportedVersion));
        }

        [TestCase(0)]
        [TestCase(999)]
        [TestCase(60001)]
        [TestCase(int.MaxValue)]
        [TestCase(int.MinValue)]
        public void Request_TimeoutIsNotClamped(int timeout)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PipelineDiagnosticsProtocol.BuildRequest(Compile, timeout));
            Assert.Throws<ArgumentOutOfRangeException>(() => PipelineDiagnosticsProtocol.BuildRequest(List, timeout));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Compile_status")]
        [TestCase("test_List")]
        [TestCase("compile_status ")]
        [TestCase("script recompile")]
        [TestCase("test_run")]
        [TestCase("test status")]
        public void UnknownActionsCannotBuildOrEchoArbitraryInput(string action)
        {
            Assert.Throws<ArgumentException>(() => PipelineDiagnosticsProtocol.BuildRequest(action, 5000));
            var result = PipelineDiagnosticsProtocol.ParseResponse(action, "raw-fixture-content", 200);
            AssertFailure(result, "unavailable", "pipelineDiagnosticsActionUnsupported");
            Assert.That(Data(result)["action"], Is.Null);
            Assert.That(Data(result)["command"], Is.Null);
            Assert.That(AIBridgeJson.Serialize(result), Does.Not.Contain("raw-fixture-content"));
        }

        [TestCase("idle")]
        [TestCase("triggered")]
        [TestCase("compiling")]
        [TestCase("completed")]
        [TestCase("up_to_date")]
        public void Compile_AllKnownStatesAreOnlyGlobalObservations(string state)
        {
            var result = ParseCompile(CompilePayload(state));
            AssertObserved(result, CompileCommand);
            var data = Data(result);
            Assert.That(data["compileStatus"], Is.EqualTo(state));
            Assert.That(data["failed"], Is.EqualTo(false));
            Assert.That(data["observedDiagnosticCount"], Is.EqualTo(0));
            foreach (var key in new[] { "errorCount", "warningCount", "duration" })
                Assert.That(data.ContainsKey(key) && data[key] == null, Is.True, key);
            foreach (var key in new[] { "success", "compileSucceeded", "statusConfirmed", "startedByInvocation" })
                Assert.That(data.ContainsKey(key), Is.False, key);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Compile_CompletedFlagsRemainIndependentWithoutInventedSuccess(bool failed, bool nativeFailed)
        {
            var result = ParseCompile(CompilePayload("completed", failed, nativeFailed));
            AssertObserved(result, CompileCommand);
            Assert.That(Data(result)["failed"], Is.EqualTo(failed));
            Assert.That(Data(result)["compilationFailed"], Is.EqualTo(nativeFailed));
        }

        [Test]
        public void Compile_PartialErrorsAreDiagnosticCountNotCompleteErrorCount()
        {
            var payload = CompilePayload("completed", true, true);
            payload["errors"] = new List<object> { "first line\nsecond line", "第二个诊断" };
            var result = ParseCompile(payload);
            AssertObserved(result, CompileCommand);
            Assert.That(Data(result)["observedDiagnosticCount"], Is.EqualTo(2));
            Assert.That(Data(result)["errorCount"], Is.Null);
            Assert.That((List<object>)Data(result)["errors"], Has.Count.EqualTo(2));
        }

        [Test]
        public void Compile_InvalidStageCombinationsRemainUnknown()
        {
            foreach (var state in new[] { "idle", "triggered", "compiling", "up_to_date" })
            {
                AssertUnknown(ParseCompile(CompilePayload(state, true)));
                var payload = CompilePayload(state);
                payload["errors"] = new List<object> { "unexpected" };
                AssertUnknown(ParseCompile(payload));
            }
            foreach (var state in new[] { "idle", "up_to_date" })
                AssertUnknown(ParseCompile(CompilePayload(state, false, true)));
            foreach (var state in new[] { "triggered", "compiling" })
                AssertObserved(ParseCompile(CompilePayload(state, false, true)), CompileCommand);
            var completed = CompilePayload("completed");
            completed["errors"] = new List<object> { "contradiction" };
            AssertUnknown(ParseCompile(completed));
        }

        [Test]
        public void Compile_EveryRequiredFieldMissingNullOrWrongTypeIsRejected()
        {
            foreach (var field in new[] { "status", "failed", "errors", "compilationFailed" })
            {
                var payload = CompilePayload();
                payload.Remove(field);
                AssertUnknown(ParseCompile(payload));
                payload = CompilePayload();
                payload[field] = null;
                AssertUnknown(ParseCompile(payload));
                payload[field] = new Dictionary<string, object>();
                AssertUnknown(ParseCompile(payload));
            }
            foreach (var field in new[] { "failed", "compilationFailed" })
                foreach (var wrong in new object[] { "false", 0L, 0.0, new List<object>() })
                {
                    var payload = CompilePayload();
                    payload[field] = wrong;
                    AssertUnknown(ParseCompile(payload));
                }
        }

        [Test]
        public void Compile_RejectsUnknownStatusCaseFieldsAndNonStringDiagnostics()
        {
            foreach (var status in new object[] { "failed", "Completed", "IDLE", "", "completed ", 1L })
            {
                var payload = CompilePayload();
                payload["status"] = status;
                AssertUnknown(ParseCompile(payload));
            }
            foreach (var wrong in new object[] { "single string", true, 1L, new List<object> { null },
                new List<object> { 1L }, new List<object> { new Dictionary<string, object>() } })
            {
                var payload = CompilePayload("completed", true);
                payload["errors"] = wrong;
                AssertUnknown(ParseCompile(payload));
            }
            foreach (var field in new[] { "Status", "errorCount", "warningCount", "duration", "success", "operationId" })
            {
                var payload = CompilePayload();
                payload[field] = null;
                AssertUnknown(ParseCompile(payload));
            }
        }

        [TestCase(Compile)]
        [TestCase(List)]
        public void Outer_RequiresEveryVerboseFieldAndRejectsUnknownKeys(string action)
        {
            foreach (var field in new List<string>(Response(action).Keys))
            {
                var outer = Response(action);
                outer.Remove(field);
                AssertUnknown(Parse(action, outer));
            }
            foreach (var key in new[] { "Success", "status", "retryable", "parameters", "argProblems", "output", "diagnostics" })
            {
                var outer = Response(action);
                outer[key] = null;
                AssertUnknown(Parse(action, outer));
            }
            AssertUnknown(PipelineDiagnosticsProtocol.ParseResponse(action, "{\"success\":true,\"result\":{}}", 200));
        }

        [TestCase(Compile)]
        [TestCase(List)]
        public void Outer_StrictSuccessTypesAndKnownCommandAreRequired(string action)
        {
            foreach (var field in new[] { "success", "command", "result", "executedAt" })
                foreach (var wrong in new object[] { null, false, 1L, new List<object>() })
                {
                    var outer = Response(action);
                    outer[field] = wrong;
                    AssertUnknown(Parse(action, outer));
                }
            foreach (var command in new[] { "script eval", "script recompile", "test run", "test List", "test list " })
            {
                var outer = Response(action);
                outer["command"] = command;
                AssertUnknown(Parse(action, outer));
            }
            var value = Response(action);
            value["success"] = "true";
            AssertUnknown(Parse(action, value));
        }

        [TestCase(Compile)]
        [TestCase(List)]
        public void Outer_SuccessCannotCarryFailureOrInventedDuration(string action)
        {
            foreach (var field in new[] { "executionTimeMs", "message", "error", "errorDetails", "errorCode" })
                foreach (var wrong in new object[] { "", "remote-fixture-error", false, 0L, 1.5, new List<object>() })
                {
                    var outer = Response(action);
                    outer[field] = wrong;
                    AssertUnknown(Parse(action, outer));
                }
            var response = Response(action);
            response["errorCode"] = null;
            response["warnings"] = null;
            AssertObserved(Parse(action, response), action == Compile ? CompileCommand : ListCommand);
        }

        [Test]
        public void Warnings_OptionalBoundedArraysArePreservedForAdapterRedaction()
        {
            var response = Response(List);
            response["warnings"] = new List<object> { "outer fixture" };
            var inner = (Dictionary<string, object>)response["result"];
            inner["warnings"] = new List<object> { "inner fixture" };
            inner["errorCode"] = null;
            var result = Parse(List, response);
            AssertObserved(result, ListCommand);
            Assert.That((List<object>)Data(result)["warnings"], Is.EquivalentTo(new[] { "outer fixture" }));
            Assert.That((List<object>)Data(result)["innerWarnings"], Is.EquivalentTo(new[] { "inner fixture" }));
        }

        [Test]
        public void Warnings_InvalidTypeCountAndLengthAtBothEnvelopeLevelsAreRejected()
        {
            var tooMany = Strings(33, "warning");
            foreach (var wrong in new object[] { "warning", false, 1L, new List<object> { null },
                new List<object> { false }, tooMany, new List<object> { new string('w', 4097) } })
                foreach (var inner in new[] { false, true })
                {
                    var response = Response(List);
                    var target = inner ? (Dictionary<string, object>)response["result"] : response;
                    target["warnings"] = wrong;
                    AssertUnknown(Parse(List, response));
                }
            var valid = Response(List);
            valid["warnings"] = Strings(32, new string('w', 4096));
            AssertObserved(Parse(List, valid), ListCommand);
        }

        [TestCase("2026-11-05T05:30:11Z")]
        [TestCase("2026-11-05T05:30:11.1Z")]
        [TestCase("2026-11-05T05:30:11.1234567+00:00")]
        public void Envelope_UtcIsAcceptedAtBothLevels(string utc)
        {
            var response = Response(List);
            response["executedAt"] = utc;
            ((Dictionary<string, object>)response["result"])["executedAt"] = utc;
            AssertObserved(Parse(List, response), ListCommand);
        }

        [TestCase("2026-11-05T05:30:11")]
        [TestCase("2026-11-05T05:30:11+08:00")]
        [TestCase("2026-11-05T05:30:11-00:00")]
        [TestCase("2026-11-05T05:30:11z")]
        [TestCase("2026-11-05 05:30:11Z")]
        [TestCase("2026-11-05T05:30:11.Z")]
        [TestCase("2026-11-05T05:30:11.12345678Z")]
        [TestCase("2026-13-05T05:30:11Z")]
        [TestCase("2026-11-05T05:30:61Z")]
        [TestCase(" 2026-11-05T05:30:11Z")]
        public void Envelope_InvalidUtcAtEitherLevelIsUnknown(string utc)
        {
            foreach (var inner in new[] { false, true })
            {
                var response = Response(List);
                (inner ? (Dictionary<string, object>)response["result"] : response)["executedAt"] = utc;
                AssertUnknown(Parse(List, response));
            }
        }

        [Test]
        public void List_ActualPascalCaseShapeRetainsNullableAssemblyAndExplicitTests()
        {
            var result = Parse(List, Response(List));
            AssertObserved(result, ListCommand);
            var data = Data(result);
            Assert.That(data["mode"], Is.EqualTo("EditMode"));
            Assert.That(data["count"], Is.EqualTo(1L));
            Assert.That(data["listingIsExecution"], Is.EqualTo(false));
            var item = (Dictionary<string, object>)((List<object>)data["tests"])[0];
            Assert.That(item["Assembly"], Is.Null);
            Assert.That(item["Explicit"], Is.EqualTo(true));
            Assert.That(item.ContainsKey("success"), Is.False);
        }

        [Test]
        public void List_NonExplicitTestWithAssemblyAndEmptyCategoriesIsValid()
        {
            var item = Item();
            item["Assembly"] = "Fixture.Editor";
            item["Categories"] = new List<object>();
            item["Explicit"] = false;
            AssertObserved(ParseListItem(item), ListCommand);
        }

        [Test]
        public void List_ZeroTestsIsValidListingAndDoesNotClaimRunSuccess()
        {
            var payload = ListPayload();
            payload["Tests"] = new List<object>();
            payload["Count"] = 0L;
            payload["message"] = "Found 0 test(s).";
            var result = Parse(List, Envelope(ListCommand, payload));
            AssertObserved(result, ListCommand);
            Assert.That(Data(result)["count"], Is.EqualTo(0L));
            Assert.That(Data(result).ContainsKey("success"), Is.False);
        }

        [Test]
        public void List_EveryInnerBaseAndListFieldIsRequiredWithExactCase()
        {
            foreach (var key in new List<string>(ListPayload().Keys))
            {
                var payload = ListPayload();
                payload.Remove(key);
                AssertUnknown(Parse(List, Envelope(ListCommand, payload)));
                payload = ListPayload();
                payload[key] = null;
                if (key != "result" && key != "executionTimeMs" && key != "error" && key != "errorDetails")
                    AssertUnknown(Parse(List, Envelope(ListCommand, payload)));
            }
            foreach (var field in new[] { "mode", "count", "tests", "status", "retryable", "output", "diagnostics" })
            {
                var payload = ListPayload();
                payload[field] = null;
                AssertUnknown(Parse(List, Envelope(ListCommand, payload)));
            }
        }

        [Test]
        public void List_InnerSuccessMetadataAndCommandCannotConflict()
        {
            foreach (var field in new[] { "success", "command", "result", "executionTimeMs", "error", "errorDetails", "errorCode" })
                foreach (var wrong in new object[] { false, 0L, new List<object>(), "remote-fixture-error" })
                {
                    var payload = ListPayload();
                    payload[field] = wrong;
                    AssertUnknown(Parse(List, Envelope(ListCommand, payload)));
                }
            foreach (var wrong in new[] { "test List", CompileCommand, "test run", "" })
            {
                var payload = ListPayload();
                payload["command"] = wrong;
                AssertUnknown(Parse(List, Envelope(ListCommand, payload)));
            }
        }

        [Test]
        public void List_CountModeAndMessageMustMatchActualItems()
        {
            foreach (var bad in new object[] { null, -1L, 0L, 2L, 65537L, 1.5, "1", false })
            {
                var payload = ListPayload();
                payload["Count"] = bad;
                AssertUnknown(Parse(List, Envelope(ListCommand, payload)));
            }
            foreach (var bad in new object[] { null, "editor", "editmode", "PlayMode", "All", 1L })
            {
                var payload = ListPayload();
                payload["Mode"] = bad;
                AssertUnknown(Parse(List, Envelope(ListCommand, payload)));
            }
            foreach (var bad in new object[] { null, "", "Found 2 test(s).", "Found 1 tests.", false, 1L })
            {
                var payload = ListPayload();
                payload["message"] = bad;
                AssertUnknown(Parse(List, Envelope(ListCommand, payload)));
            }
        }

        [Test]
        public void List_ItemEveryFieldRequiredAndUnknownCaseRejected()
        {
            foreach (var field in new List<string>(Item().Keys))
            {
                var item = Item();
                item.Remove(field);
                AssertUnknown(ParseListItem(item));
                item = Item();
                item[field] = null;
                if (field == "Assembly") AssertObserved(ParseListItem(item), ListCommand);
                else AssertUnknown(ParseListItem(item));
            }
            foreach (var field in new[] { "fullName", "mode", "assembly", "categories", "explicit", "Outcome", "success" })
            {
                var item = Item();
                item[field] = null;
                AssertUnknown(ParseListItem(item));
            }
        }

        [Test]
        public void List_ItemFieldsAreTypedAndOnlyEditModeIsAllowed()
        {
            foreach (var field in new[] { "FullName", "Mode", "Assembly", "Categories", "Explicit" })
            {
                var item = Item();
                item[field] = new Dictionary<string, object>();
                AssertUnknown(ParseListItem(item));
            }
            foreach (var field in new[] { "FullName", "Assembly" })
                foreach (var bad in new[] { "", " ", new string('n', 4097) })
                {
                    var item = Item();
                    item[field] = bad;
                    AssertUnknown(ParseListItem(item));
                }
            foreach (var bad in new object[] { "editor", "editmode", "PlayMode", "All", "EditMode ", 1L })
            {
                var item = Item();
                item["Mode"] = bad;
                AssertUnknown(ParseListItem(item));
            }
        }

        [Test]
        public void List_InvalidTestArrayCategoriesAndExplicitTypesAreRejected()
        {
            foreach (var bad in new object[] { null, "tests", false, new List<object> { null }, new List<object> { "test" } })
            {
                var payload = ListPayload();
                payload["Tests"] = bad;
                AssertUnknown(Parse(List, Envelope(ListCommand, payload)));
            }
            foreach (var bad in new object[] { null, "category", new List<object> { null }, new List<object> { 1L },
                Strings(129, "c"), new List<object> { new string('c', 1025) } })
            {
                var item = Item();
                item["Categories"] = bad;
                AssertUnknown(ParseListItem(item));
            }
            foreach (var bad in new object[] { "true", 0L, 1L, new List<object>() })
            {
                var item = Item();
                item["Explicit"] = bad;
                AssertUnknown(ParseListItem(item));
            }
        }

        [Test]
        public void Result_OnlineSerializationKeepsAllUnknownCountersAndAssemblyNull()
        {
            var compile = PipelineDiagnosticsProtocol.ParseResponse(Compile, Json(Response(Compile)), 200);
            var json = AIBridgeJson.Serialize(compile);
            foreach (var key in new[] { "errorCount", "warningCount", "duration", "warnings" })
                Assert.That(json, Does.Contain("\"" + key + "\":null"));
            var roundtrip = AIBridgeJson.DeserializeObjectStrict(json);
            Assert.That(((Dictionary<string, object>)roundtrip["data"])["operationCorrelation"], Is.EqualTo(false));
            var list = PipelineDiagnosticsProtocol.ParseResponse(List, Json(Response(List)), 200);
            Assert.That(AIBridgeJson.Serialize(list), Does.Contain("\"Assembly\":null"));
        }

        [TestCase(401)]
        [TestCase(403)]
        [TestCase(404)]
        [TestCase(405)]
        [TestCase(429)]
        [TestCase(501)]
        [TestCase(503)]
        public void Http_UnavailableDoesNotParseOrEchoBody(int status)
        {
            foreach (var action in new[] { Compile, List })
            {
                var result = PipelineDiagnosticsProtocol.ParseResponse(action, Json(Response(action)), status);
                AssertFailure(result, "unavailable", "pipelineDiagnosticsUnavailable");
                Assert.That(Data(result)["httpStatus"], Is.EqualTo(status));
                Assert.That(Data(result).ContainsKey("compileStatus"), Is.False);
                Assert.That(Data(result).ContainsKey("tests"), Is.False);
            }
        }

        [TestCase(0)]
        [TestCase(201)]
        [TestCase(302)]
        [TestCase(400)]
        [TestCase(408)]
        [TestCase(413)]
        [TestCase(500)]
        [TestCase(504)]
        public void Http_OtherStatusesStayUnknownEvenWithValidBody(int status)
        {
            foreach (var action in new[] { Compile, List })
                AssertUnknown(PipelineDiagnosticsProtocol.ParseResponse(action, Json(Response(action)), status));
        }

        [Test]
        public void Json_DecodedDuplicateKeysAtEverySchemaLevelAreRejected()
        {
            var json = Json(Response(Compile));
            AssertUnknown(Raw(Compile, json.Replace("\"success\":true", "\"success\":true,\"su\\u0063cess\":true")));
            AssertUnknown(Raw(Compile, json.Replace("\"failed\":false", "\"failed\":false,\"fa\\u0069led\":false")));
            json = Json(Response(List));
            AssertUnknown(Raw(List, json.Replace("\"Count\":1", "\"Count\":1,\"Co\\u0075nt\":1")));
            AssertUnknown(Raw(List, json.Replace("\"Assembly\":null", "\"Assembly\":null,\"Asse\\u006dbly\":null")));
            AssertUnknown(Raw(List, json.Replace("\"Count\":1", "\"Count\":1,\"Count\":2")));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("null")]
        [TestCase("[]")]
        [TestCase("true")]
        [TestCase("{} trailing")]
        [TestCase("{\"success\":NaN}")]
        [TestCase("{\"success\":Infinity}")]
        [TestCase("{\"success\":1e9999}")]
        [TestCase("{\"success\":01}")]
        [TestCase("{\"success\":\"\\u 001\"}")]
        public void Json_InvalidOrScalarBodiesStayUnknown(string json)
        {
            AssertUnknown(Raw(Compile, json));
            AssertUnknown(Raw(List, json));
        }

        [Test]
        public void Json_FiniteFractionalOrNonFiniteCounterCannotPass()
        {
            var json = Json(Response(List));
            foreach (var value in new[] { "1.0", "1.5", "NaN", "Infinity", "-Infinity", "1e9999", "9223372036854775808" })
                AssertUnknown(Raw(List, json.Replace("\"Count\":1", "\"Count\":" + value)));
        }

        [Test]
        public void Limits_DiagnosticCountAndTextAreBounded()
        {
            var payload = CompilePayload("completed", true);
            payload["errors"] = Strings(4096, "");
            AssertObserved(ParseCompile(payload), CompileCommand);
            payload["errors"] = Strings(4097, "");
            AssertUnknown(ParseCompile(payload));
            payload["errors"] = new List<object> { new string('d', 32768) };
            AssertObserved(ParseCompile(payload), CompileCommand);
            payload["errors"] = new List<object> { new string('d', 32769) };
            AssertUnknown(ParseCompile(payload));
        }

        [Test]
        public void Limits_OneMiBUtf8IsAcceptedButAnyAdditionalByteIsRejected()
        {
            var json = Json(Response(Compile));
            AssertObserved(Raw(Compile, json + new string(' ', Limit - json.Length)), CompileCommand);
            AssertUnknown(Raw(Compile, json + new string(' ', Limit - json.Length + 1)));
            var payload = CompilePayload("completed", true);
            payload["errors"] = Strings(32, new string('界', 12000));
            var utf8Heavy = Json(Envelope(CompileCommand, payload));
            Assert.That(utf8Heavy.Length, Is.LessThan(Limit));
            AssertUnknown(Raw(Compile, utf8Heavy));
        }

        [Test]
        public void Limits_DepthSixteenParserBudgetRejectsDeeperUntrustedTrees()
        {
            foreach (var depth in new[] { 16, 17, 64 })
            {
                var nested = new string('[', depth) + "null" + new string(']', depth);
                AssertUnknown(Raw(Compile, "{\"untrusted\":" + nested + "}"));
                AssertUnknown(Raw(List, Json(Response(List)).Replace("\"Assembly\":null", "\"Assembly\":" + nested)));
            }
        }

        private static Dictionary<string, object> CompilePayload(string state = "idle", bool failed = false, bool nativeFailed = false)
        {
            return new Dictionary<string, object>
            {
                { "status", state }, { "failed", failed }, { "errors", new List<object>() }, { "compilationFailed", nativeFailed }
            };
        }

        private static Dictionary<string, object> Item()
        {
            return new Dictionary<string, object>
            {
                { "FullName", "Fixture.ExplicitTest(42)" }, { "Mode", "EditMode" }, { "Assembly", null },
                { "Categories", new List<object> { "fixture", "中文分类" } }, { "Explicit", true }
            };
        }

        private static Dictionary<string, object> ListPayload()
        {
            var data = Envelope(ListCommand, null);
            data["Mode"] = "EditMode";
            data["Count"] = 1L;
            data["Tests"] = new List<object> { Item() };
            data["message"] = "Found 1 test(s).";
            return data;
        }

        private static Dictionary<string, object> Envelope(string command, object payload)
        {
            return new Dictionary<string, object>
            {
                { "success", true }, { "command", command }, { "result", payload }, { "executionTimeMs", null },
                { "message", null }, { "error", null }, { "errorDetails", null }, { "executedAt", Utc }
            };
        }

        private static Dictionary<string, object> Response(string action)
        {
            return action == Compile ? Envelope(CompileCommand, CompilePayload()) : Envelope(ListCommand, ListPayload());
        }

        private static PipelineDiagnosticsResult ParseCompile(Dictionary<string, object> payload)
        {
            return Parse(Compile, Envelope(CompileCommand, payload));
        }

        private static PipelineDiagnosticsResult ParseListItem(Dictionary<string, object> item)
        {
            var payload = ListPayload();
            payload["Tests"] = new List<object> { item };
            return Parse(List, Envelope(ListCommand, payload));
        }

        private static PipelineDiagnosticsResult Parse(string action, Dictionary<string, object> value)
        {
            return Raw(action, Json(value));
        }

        private static PipelineDiagnosticsResult Raw(string action, string json)
        {
            return PipelineDiagnosticsProtocol.ParseResponse(action, json, 200);
        }

        private static string Json(Dictionary<string, object> value)
        {
            return AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(value));
        }

        private static List<object> Strings(int count, string value)
        {
            var result = new List<object>();
            for (var i = 0; i < count; i++) result.Add(value);
            return result;
        }

        private static Dictionary<string, object> Data(PipelineDiagnosticsResult result)
        {
            return (Dictionary<string, object>)result.data;
        }

        private static void AssertObserved(PipelineDiagnosticsResult result, string command)
        {
            Assert.That(result.valid, Is.True);
            Assert.That(result.errorCode, Is.Null);
            Assert.That(result.error, Is.Null);
            Assert.That(Data(result)["status"], Is.EqualTo("observed"));
            Assert.That(Data(result)["backend"], Is.EqualTo("pipeline"));
            Assert.That(Data(result)["command"], Is.EqualTo(command));
            Assert.That(Data(result)["httpStatus"], Is.EqualTo(200));
            Assert.That(Data(result)["operationCorrelation"], Is.EqualTo(false));
        }

        private static void AssertUnknown(PipelineDiagnosticsResult result)
        {
            AssertFailure(result, "unknown", "pipelineDiagnosticsResponseInvalid");
            Assert.That(AIBridgeJson.Serialize(result), Does.Not.Contain("remote-fixture-error"));
            foreach (var key in new[] { "compileStatus", "errors", "count", "tests", "executedAt", "warnings" })
                Assert.That(Data(result).ContainsKey(key), Is.False, key);
        }

        private static void AssertFailure(PipelineDiagnosticsResult result, string status, string code)
        {
            Assert.That(result.valid, Is.False);
            Assert.That(result.errorCode, Is.EqualTo(code));
            Assert.That(result.error, Is.Not.Null.And.Not.Empty);
            Assert.That(Data(result)["status"], Is.EqualTo(status));
            Assert.That(Data(result)["operationCorrelation"], Is.EqualTo(false));
        }
    }
}
