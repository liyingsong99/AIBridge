using System.Collections.Generic;
using AIBridge.Internal.Json;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class PipelineCodeProtocolTests
    {
        private const string Command = "script eval";
        private const string DryRunReceipt = "Compiled successfully (dry run; nothing was loaded or executed).";

        [Test]
        public void BuildDryRunRequest_UsesCompileOnlyPipelineContract()
        {
            var request = AIBridgeJson.DeserializeObject(
                PipelineCodeProtocol.BuildDryRunRequest("return 1;", 2500));
            var parameters = request["parameters"] as Dictionary<string, object>;

            Assert.That(request["command"], Is.EqualTo(Command));
            Assert.That(request["timeout"], Is.EqualTo(2500L));
            Assert.That(request["verbose"], Is.EqualTo(true));
            Assert.That(request["job"], Is.EqualTo(false));
            Assert.That(parameters["code"], Is.EqualTo("return 1;"));
            Assert.That(parameters["dry-run"], Is.EqualTo(true));
            Assert.That(parameters["auto-import"], Is.EqualTo(false));
            Assert.That(parameters["internals"], Is.EqualTo(false));
            Assert.That(parameters["timeout"], Is.EqualTo(2.5d));
        }

        [Test]
        public void BuildDryRunRequest_PreservesTimeoutPrecisionAndEscapesCode()
        {
            const string code = "var text = \"line 1\\nline 2\";\nreturn text;";
            var request = AIBridgeJson.DeserializeObject(
                PipelineCodeProtocol.BuildDryRunRequest(code, 2500));
            var parameters = request["parameters"] as Dictionary<string, object>;

            Assert.That(parameters["code"], Is.EqualTo(code));
            Assert.That(parameters["timeout"], Is.EqualTo(2.5d));
        }

        [Test]
        public void BuildEvalRequest_ExecutionRequiresExplicitDryRunFalseAndNoInternals()
        {
            var request = AIBridgeJson.DeserializeObject(PipelineCodeProtocol.BuildEvalRequest("return 1;", 15000, false));
            var parameters = request["parameters"] as Dictionary<string, object>;
            Assert.That(parameters["dry-run"], Is.EqualTo(false));
            Assert.That(parameters["internals"], Is.EqualTo(false));
            Assert.That(parameters["auto-import"], Is.EqualTo(false));
            Assert.That(request["job"], Is.EqualTo(false));
        }

        [Test]
        public void ParseEvalResponse_ReturnsNestedExecutedValueOnlyAfterBothSuccessLayers()
        {
            const string json = "{\"success\":true,\"command\":\"script eval\",\"result\":{\"success\":true,\"command\":\"script eval\",\"result\":42,\"output\":\"log\",\"message\":null,\"executionTimeMs\":12,\"diagnostics\":[]}}";
            var result = PipelineCodeProtocol.ParseEvalResponse(json, 200, false);
            Assert.That(result.success, Is.True);
            Assert.That(result.status, Is.EqualTo("executed"));
            Assert.That(result.resultValue, Is.EqualTo(42L));
            Assert.That(result.output, Is.EqualTo("log"));
            Assert.That(result.executionTimeMs, Is.EqualTo(12L));
            Assert.That(result.mayStillBeRunning, Is.False);
        }

        [Test]
        public void ParseEvalResponse_RuntimeExceptionIsKnownTerminalFailure()
        {
            const string json = "{\"success\":false,\"command\":\"script eval\",\"errorCode\":\"RUNTIME_EXCEPTION\",\"error\":\"InvalidOperationException\",\"result\":{\"success\":false,\"command\":\"script eval\",\"errorCode\":\"RUNTIME_EXCEPTION\",\"error\":\"InvalidOperationException\",\"errorDetails\":\"fixture stack\",\"diagnostics\":[]}}";
            var result = PipelineCodeProtocol.ParseEvalResponse(json, 400, false);
            Assert.That(result.success, Is.False);
            Assert.That(result.status, Is.EqualTo("execution_failed"));
            Assert.That(result.mayStillBeRunning, Is.False);
            Assert.That(result.errorDetails, Is.EqualTo("fixture stack"));
        }

        [TestCase("{\"success\":false,\"errorCode\":\"UNKNOWN\"}")]
        [TestCase("{\"success\":true,\"command\":\"script eval\",\"result\":{\"success\":true,\"command\":\"script eval\",\"result\":5}}")]
        public void ParseEvalResponse_UnknownOrIncompleteExecutionRemainsUnknown(string json)
        {
            var result = PipelineCodeProtocol.ParseEvalResponse(json, 200, false);
            Assert.That(result.success, Is.False);
            Assert.That(result.status, Is.EqualTo("unknown"));
            Assert.That(result.mayStillBeRunning, Is.True);
        }

        [Test]
        public void ParseEvalResponse_TimeoutRemainsUnknown()
        {
            const string json = "{\"success\":false,\"command\":\"script eval\",\"errorCode\":\"TIMEOUT\",\"result\":{\"success\":false,\"command\":\"script eval\",\"errorCode\":\"TIMEOUT\",\"error\":\"timed out\"}}";
            var result = PipelineCodeProtocol.ParseEvalResponse(json, 504, false);
            Assert.That(result.status, Is.EqualTo("unknown"));
            Assert.That(result.mayStillBeRunning, Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseEvalResponse_MalformedFieldsCannotConfirmCompletion(bool dryRun)
        {
            foreach (var field in new[] { "command", "error", "errorCode", "errorDetails", "message", "output", "executionTimeMs", "success" })
            {
                foreach (var outerField in new[] { false, true })
                {
                    var outer = CreateSuccessfulResponse();
                    var inner = (Dictionary<string, object>)outer["result"];
                    if (!dryRun) { inner["result"] = 42L; inner["message"] = null; }
                    var target = outerField ? outer : inner;
                    target[field] = field == "executionTimeMs" ? (object)(-1L) : new Dictionary<string, object> { { "invalid", true } };
                    AssertInvalidResponse(PipelineCodeProtocol.ParseEvalResponse(SerializeFixture(outer), 200, dryRun));
                }
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseEvalResponse_MalformedDiagnosticsRemainUnknown(bool dryRun)
        {
            var invalidItems = new List<object>
            {
                new Dictionary<string, object>(),
                new Dictionary<string, object> { { "severity", "warning" }, { "message", 7L } },
                new Dictionary<string, object> { { "severity", "other" }, { "message", "text" } },
                new Dictionary<string, object> { { "severity", "warning" }, { "message", "text" }, { "id", 4L } },
                new Dictionary<string, object> { { "severity", "warning" }, { "message", "text" }, { "file", new List<object>() } },
                new Dictionary<string, object> { { "severity", "warning" }, { "message", "text" }, { "line", "0" } },
                new Dictionary<string, object> { { "severity", "warning" }, { "message", "text" }, { "fileLine", 0L } }
            };
            foreach (var item in invalidItems)
            {
                var outer = CreateSuccessfulResponse();
                var inner = (Dictionary<string, object>)outer["result"];
                if (!dryRun) { inner["result"] = 42L; inner["message"] = null; }
                inner["diagnostics"] = new List<object> { item };
                AssertInvalidResponse(PipelineCodeProtocol.ParseEvalResponse(SerializeFixture(outer), 200, dryRun));
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseEvalResponse_MalformedKnownFailureRemainsUnknown(bool dryRun)
        {
            const string json = "{\"success\":false,\"command\":\"script eval\",\"result\":{\"success\":false,\"command\":\"script eval\",\"errorCode\":\"COMPILATION_ERROR\",\"error\":{\"message\":\"bad\"},\"diagnostics\":[]}}";
            AssertInvalidResponse(PipelineCodeProtocol.ParseEvalResponse(json, 400, dryRun));
        }

        [TestCase(201)]
        [TestCase(302)]
        [TestCase(404)]
        [TestCase(502)]
        public void ParseEvalResponse_UnexpectedHttpStatusCannotConfirmKnownFailure(int httpStatus)
        {
            const string json = "{\"success\":false,\"command\":\"script eval\",\"result\":{\"success\":false,\"command\":\"script eval\",\"errorCode\":\"COMPILATION_ERROR\",\"error\":\"bad\",\"diagnostics\":[]}}";
            AssertInvalidResponse(PipelineCodeProtocol.ParseEvalResponse(json, httpStatus, false));
            AssertInvalidResponse(PipelineCodeProtocol.ParseEvalResponse(json, httpStatus, true));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseEvalResponse_ContradictoryOutcomeCannotReleaseUnknown(bool dryRun)
        {
            const string conflict = "{\"success\":false,\"command\":\"script eval\",\"errorCode\":\"TIMEOUT\",\"result\":{\"success\":false,\"command\":\"script eval\",\"errorCode\":\"COMPILATION_ERROR\",\"diagnostics\":[]}}";
            AssertInvalidResponse(PipelineCodeProtocol.ParseEvalResponse(conflict, 200, dryRun));
            const string success = "{\"success\":true,\"command\":\"script eval\",\"result\":{\"success\":true,\"command\":\"script eval\",\"result\":42,\"output\":null,\"diagnostics\":[]}}";
            foreach (var status in new[] { 401, 403, 429, 503, 504 })
                AssertInvalidResponse(PipelineCodeProtocol.ParseEvalResponse(success, status, dryRun));
        }

        [Test]
        public void ParseEvalResponse_CompileOnlyReceiptCannotConfirmExecution()
        {
            AssertInvalidResponse(PipelineCodeProtocol.ParseEvalResponse(SerializeFixture(CreateSuccessfulResponse()), 200, false));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseEvalResponse_DuplicateDecodedKeysRemainUnknown(bool dryRun)
        {
            const string json = "{\"success\":true,\"command\":\"script eval\",\"result\":{\"success\":true,\"command\":\"script eval\",\"result\":{\"key\":1,\"\\u006bey\":2},\"output\":null,\"diagnostics\":[]}}";
            AssertInvalidResponse(PipelineCodeProtocol.ParseEvalResponse(json, 200, dryRun));
        }

        [Test]
        public void StrictJson_RejectsDuplicateKeysAndBoundsNestingWithoutChangingLegacyEntry()
        {
            const string duplicate = "{\"key\":1,\"\\u006bey\":2}";
            Assert.Throws<System.FormatException>(() => AIBridgeJson.DeserializeObjectStrict(duplicate));
            Assert.That(AIBridgeJson.DeserializeObject(duplicate)["key"], Is.EqualTo(2L));
            const string nested = "{\"root\":{\"child\":{\"leaf\":1}}}";
            Assert.Throws<System.FormatException>(() => AIBridgeJson.DeserializeObjectStrict(nested, 2));
            Assert.That(AIBridgeJson.DeserializeObjectStrict(nested, 3), Is.Not.Null);
            var tooDeep = "{\"result\":" + new string('[', 64) + "0" + new string(']', 64) + "}";
            AssertInvalidResponse(PipelineCodeProtocol.ParseEvalResponse(tooDeep, 200, false));
        }

        [Test]
        public void ParseDryRunResponse_AcceptsCompleteTwoLayerCompileOnlyReceipt()
        {
            var outer = CreateSuccessfulResponse();
            var result = PipelineCodeProtocol.ParseDryRunResponse(
                SerializeFixture(outer), 200);

            {
                Assert.That(result.success, Is.True);
                Assert.That(result.status, Is.EqualTo("validated"));
                Assert.That(result.httpStatus, Is.EqualTo(200));
                Assert.That(result.error, Is.Null);
                Assert.That(result.errorCode, Is.Null);
                Assert.That(result.mayStillBeRunning, Is.False);
                Assert.That(result.diagnostics, Is.Empty);
            }
        }

        [Test]
        public void ParseDryRunResponse_OuterSuccessWithInnerFailure_ReturnsFailure()
        {
            var outer = CreateSuccessfulResponse();
            var inner = outer["result"] as Dictionary<string, object>;
            inner["success"] = false;
            inner["errorCode"] = "COMPILATION_ERROR";
            inner["error"] = "Compilation failed.";

            var result = PipelineCodeProtocol.ParseDryRunResponse(
                SerializeFixture(outer), 200);

            {
                Assert.That(result.success, Is.False);
                Assert.That(result.status, Is.EqualTo("compile_failed"));
                Assert.That(result.errorCode, Is.EqualTo("COMPILATION_ERROR"));
                Assert.That(result.error, Is.EqualTo("Compilation failed."));
            }
        }

        [Test]
        public void ParseDryRunResponse_400CompilationErrorMapsZeroBasedCoordinates()
        {
            var outer = CreateSuccessfulResponse();
            var inner = outer["result"] as Dictionary<string, object>;
            inner["success"] = false;
            inner["errorCode"] = "COMPILATION_ERROR";
            inner["error"] = "Compilation failed.";
            inner["diagnostics"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "severity", "error" },
                    { "id", "CS1002" },
                    { "message", "; expected" },
                    { "file", "Assets/Test.cs" },
                    { "line", 0L },
                    { "column", 4L }
                }
            };

            var result = PipelineCodeProtocol.ParseDryRunResponse(
                SerializeFixture(outer), 400);
            var diagnostic = result.diagnostics[0];

            {
                Assert.That(result.success, Is.False);
                Assert.That(result.status, Is.EqualTo("compile_failed"));
                Assert.That(result.httpStatus, Is.EqualTo(400));
                Assert.That(result.diagnostics, Has.Count.EqualTo(1));
                Assert.That(diagnostic.code, Is.EqualTo("CS1002"));
                Assert.That(diagnostic.line, Is.EqualTo(1));
                Assert.That(diagnostic.column, Is.EqualTo(5));
            }
        }

        [TestCase("missing outer command")]
        [TestCase("missing inner command")]
        [TestCase("wrong outer command")]
        [TestCase("wrong inner command")]
        [TestCase("wrong message")]
        [TestCase("missing result")]
        [TestCase("missing output")]
        [TestCase("non-null output")]
        [TestCase("missing diagnostics")]
        public void ParseDryRunResponse_RejectsIncompleteCompileOnlyReceipt(string invalidPart)
        {
            var outer = CreateSuccessfulResponse();
            var inner = outer["result"] as Dictionary<string, object>;
            switch (invalidPart)
            {
                case "missing outer command":
                    outer.Remove("command");
                    break;
                case "missing inner command":
                    inner.Remove("command");
                    break;
                case "wrong outer command":
                    outer["command"] = "script run";
                    break;
                case "wrong inner command":
                    inner["command"] = "script run";
                    break;
                case "wrong message":
                    inner["message"] = "Compiled successfully.";
                    break;
                case "missing result":
                    inner.Remove("result");
                    break;
                case "missing output":
                    inner.Remove("output");
                    break;
                case "non-null output":
                    inner["output"] = "unexpected output";
                    break;
                case "missing diagnostics":
                    inner.Remove("diagnostics");
                    break;
            }

            var result = PipelineCodeProtocol.ParseDryRunResponse(
                SerializeFixture(outer), 200);

            AssertInvalidResponse(result);
        }

        [TestCase(401, "pipelineAuthenticationFailed", "failed", false)]
        [TestCase(403, "pipelineAccessDenied", "failed", false)]
        [TestCase(429, "pipelineResponseInvalid", "unavailable", false)]
        [TestCase(503, "pipelineResponseInvalid", "unavailable", false)]
        [TestCase(504, "pipelineResponseInvalid", "unknown", true)]
        public void ParseDryRunResponse_MapsPipelineHttpFailures(
            int httpStatus, string errorCode, string status, bool mayStillBeRunning)
        {
            var response = new Dictionary<string, object>
            {
                { "success", false },
                { "error", "Pipeline request failed." }
            };

            var result = PipelineCodeProtocol.ParseDryRunResponse(
                AIBridgeJson.Serialize(response), httpStatus);

            {
                Assert.That(result.success, Is.False);
                Assert.That(result.httpStatus, Is.EqualTo(httpStatus));
                Assert.That(result.errorCode, Is.EqualTo(errorCode));
                Assert.That(result.status, Is.EqualTo(status));
                Assert.That(result.mayStillBeRunning, Is.EqualTo(mayStillBeRunning));
            }
        }

        [TestCase("{\"errorCode\":\"UNRECOGNIZED\"}")]
        [TestCase("{\"success\":true,\"errorCode\":\"UNRECOGNIZED\"}")]
        public void ParseDryRunResponse_UnknownFailureShapeRetainsUnknown(string json)
        {
            AssertInvalidResponse(PipelineCodeProtocol.ParseDryRunResponse(json, 200));
        }

        [Test]
        public void ParseDryRunResponse_ExplicitMappedFileLineOverridesEvalWrapperOffset()
        {
            var outer = CreateSuccessfulResponse();
            var inner = (Dictionary<string, object>)outer["result"];
            inner["success"] = false;
            inner["errorCode"] = "COMPILATION_ERROR";
            inner["error"] = "Compilation failed.";
            inner["diagnostics"] = new List<object> { new Dictionary<string, object>
            {
                { "id", "CS0103" }, { "severity", "error" }, { "message", "Missing symbol." },
                { "line", 5 }, { "column", 12 }, { "file", "check.csx" }, { "fileLine", 4 }
            } };
            var result = PipelineCodeProtocol.ParseDryRunResponse(SerializeFixture(outer), 400);
            Assert.That(result.diagnostics[0].line, Is.EqualTo(4));
            Assert.That(result.diagnostics[0].column, Is.EqualTo(13));
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("{\"success\":true")]
        public void ParseDryRunResponse_MalformedOrEmptyJsonIsInvalid(string json)
        {
            var result = PipelineCodeProtocol.ParseDryRunResponse(json, 200);

            AssertInvalidResponse(result);
        }

        [Test]
        public void ParseDryRunResponse_JsonArrayIsInvalid()
        {
            var json = AIBridgeJson.Serialize(new List<object>
            {
                new Dictionary<string, object> { { "success", true } }
            });

            var result = PipelineCodeProtocol.ParseDryRunResponse(json, 200);

            AssertInvalidResponse(result);
        }

        private static string SerializeFixture(Dictionary<string, object> outer)
        {
            // 生产序列化器省略 null；用标记保留上游 verbose 协议中明确存在的 null 字段。
            var copy = AIBridgeJson.DeserializeObject(AIBridgeJson.Serialize(outer));
            if (outer.TryGetValue("result", out var value) && value is Dictionary<string, object> original
                && copy["result"] is Dictionary<string, object> inner)
            {
                foreach (var pair in original)
                    if (pair.Value == null) inner[pair.Key] = "__EXPLICIT_NULL__";
            }
            return AIBridgeJson.Serialize(copy).Replace("\"__EXPLICIT_NULL__\"", "null");
        }

        private static Dictionary<string, object> CreateSuccessfulResponse()
        {
            return new Dictionary<string, object>
            {
                { "success", true },
                { "command", Command },
                { "result", new Dictionary<string, object>
                    {
                        { "success", true },
                        { "command", Command },
                        { "message", DryRunReceipt },
                        { "result", null },
                        { "output", null },
                        { "diagnostics", new List<object>() }
                    } }
            };
        }

        private static void AssertInvalidResponse(PipelineCodeResult result)
        {
            {
                Assert.That(result.success, Is.False);
                Assert.That(result.status, Is.EqualTo("unknown"));
                Assert.That(result.errorCode, Is.EqualTo("pipelineResponseInvalid"));
                Assert.That(result.mayStillBeRunning, Is.True);
            }
        }
    }
}
