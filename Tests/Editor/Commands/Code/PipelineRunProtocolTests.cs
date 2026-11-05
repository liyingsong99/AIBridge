using System;
using System.Collections.Generic;
using System.Reflection;
using AIBridge.Internal.Json;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class PipelineRunProtocolTests
    {
        private const string Command = "script run";
        private const string Receipt = "Compiled successfully (dry run; nothing was loaded or executed).";

        [TestCase(true)]
        [TestCase(false)]
        public void BuildRunRequest_OnlyEmitsRestrictedNamedFileContract(bool dryRun)
        {
            var request = AIBridgeJson.DeserializeObjectStrict(PipelineCodeProtocol.BuildRunRequest(
                ".aibridge/code/Test.cs", "Example.Builder.Run", "[1,null,{\"nullable\":null}]", 2500, dryRun));
            var parameters = (Dictionary<string, object>)request["parameters"];
            CollectionAssert.AreEquivalent(new[] { "command", "parameters", "timeout", "verbose", "job" }, request.Keys);
            CollectionAssert.AreEquivalent(new[] { "file", "entry", "args", "mode", "pdb", "timeout", "dry-run" }, parameters.Keys);
            Assert.That(request["command"], Is.EqualTo(Command));
            Assert.That(request["timeout"], Is.EqualTo(2500L));
            Assert.That(request["verbose"], Is.True);
            Assert.That(request["job"], Is.False);
            Assert.That(parameters["file"], Is.EqualTo(".aibridge/code/Test.cs"));
            Assert.That(parameters["entry"], Is.EqualTo("Example.Builder.Run"));
            Assert.That(parameters["mode"], Is.EqualTo("ephemeral"));
            Assert.That(parameters["pdb"], Is.True);
            Assert.That(parameters["dry-run"], Is.EqualTo(dryRun));
            Assert.That(parameters["timeout"], Is.EqualTo(2.5d));
            var arguments = (List<object>)parameters["args"];
            Assert.That(arguments[1], Is.Null);
            Assert.That(((Dictionary<string, object>)arguments[2]).ContainsKey("nullable"), Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("Test.csx")]
        [TestCase("Test.cs ")]
        [TestCase("Test\n.cs")]
        [TestCase("Test\0.cs")]
        public void BuildRunRequest_RejectsInvalidSourcePath(string path)
        {
            Assert.Throws<ArgumentException>(() => PipelineCodeProtocol.BuildRunRequest(path, "Builder.Run", "[]", 1000, true));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Run")]
        [TestCase("Builder.Run()")]
        [TestCase("Builder.Run;script eval")]
        [TestCase(" Builder.Run")]
        [TestCase("Builder..Run")]
        [TestCase("Builder.Run\n")]
        public void BuildRunRequest_RejectsImplicitOrMalformedEntry(string entry)
        {
            Assert.Throws<ArgumentException>(() => PipelineCodeProtocol.BuildRunRequest("Test.cs", entry, "[]", 1000, true));
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(999)]
        [TestCase(60001)]
        [TestCase(int.MaxValue)]
        public void BuildRunRequest_RejectsTimeoutOutsideCoreRange(int timeout)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PipelineCodeProtocol.BuildRunRequest("Test.cs", "Builder.Run", "[]", timeout, false));
        }

        [TestCase(1000)]
        [TestCase(60000)]
        public void BuildRunRequest_AcceptsTimeoutBoundaryAndNestedType(int timeout)
        {
            Assert.DoesNotThrow(() => PipelineCodeProtocol.BuildRunRequest("Test.CS", "Example.Outer+Inner.Run", "[]", timeout, true));
            Assert.Throws<ArgumentException>(() => PipelineCodeProtocol.BuildRunRequest("Test.cs", new string('T', 253) + ".Run", "[]", timeout, true));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("null")]
        [TestCase("{}")]
        [TestCase("1")]
        [TestCase("true")]
        [TestCase("\"[]\"")]
        [TestCase("[1,]")]
        [TestCase("[],\"command\":\"script eval\"")]
        public void BuildRunRequest_RejectsNonArrayMalformedOrInjectedArgs(string arguments)
        {
            Assert.Catch<Exception>(() => PipelineCodeProtocol.BuildRunRequest("Test.cs", "Builder.Run", arguments, 1000, true));
        }

        [Test]
        public void BuildRunRequest_BoundsLengthDepthAndDecodedDuplicateKeys()
        {
            var maximum = "[" + new string(' ', 32766) + "]";
            Assert.DoesNotThrow(() => PipelineCodeProtocol.BuildRunRequest("Test.cs", "Builder.Run", maximum, 1000, true));
            Assert.Throws<ArgumentException>(() => PipelineCodeProtocol.BuildRunRequest("Test.cs", "Builder.Run", maximum + " ", 1000, true));
            var allowed = new string('[', 15) + "null" + new string(']', 15);
            Assert.DoesNotThrow(() => PipelineCodeProtocol.BuildRunRequest("Test.cs", "Builder.Run", allowed, 1000, true));
            var tooDeep = "[" + allowed + "]";
            Assert.Throws<FormatException>(() => PipelineCodeProtocol.BuildRunRequest("Test.cs", "Builder.Run", tooDeep, 1000, true));
            const string duplicate = "[{\"key\":null,\"\\u006bey\":2}]";
            Assert.Throws<FormatException>(() => PipelineCodeProtocol.BuildRunRequest("Test.cs", "Builder.Run", duplicate, 1000, true));
        }

        [Test]
        public void BuildRunRequest_HasNoCommandOverrideAndCannotBeRedirectedThroughValues()
        {
            var signature = new[] { typeof(string), typeof(string), typeof(string), typeof(int), typeof(bool) };
            var method = typeof(PipelineCodeProtocol).GetMethod("BuildRunRequest", BindingFlags.Public | BindingFlags.Static, null, signature, null);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.ReturnType, Is.EqualTo(typeof(string)));
            const string path = "Test\",\"command\":\"script eval.cs";
            var request = AIBridgeJson.DeserializeObjectStrict(PipelineCodeProtocol.BuildRunRequest(
                path, "Builder.Run", "[\"script eval\",{\"mode\":\"hotpatch\",\"command\":\"editor quit\"}]", 1000, false));
            Assert.That(request["command"], Is.EqualTo(Command));
            var parameters = (Dictionary<string, object>)request["parameters"];
            Assert.That(parameters["file"], Is.EqualTo(path));
            Assert.That(parameters["mode"], Is.EqualTo("ephemeral"));
            Assert.That(parameters.ContainsKey("code"), Is.False);
            Assert.That(parameters.ContainsKey("internals"), Is.False);
            Assert.That(parameters.ContainsKey("auto-import"), Is.False);
            Assert.That(parameters.ContainsKey("references"), Is.False);
            Assert.That(parameters.ContainsKey("defines"), Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseRunResponse_AcceptsActualVerboseTwoLayerSuccessWithoutEvalFields(bool dryRun)
        {
            var result = Parse(CreateSuccess(dryRun), 200, dryRun);
            Assert.That(result.success, Is.True);
            Assert.That(result.status, Is.EqualTo(dryRun ? "validated" : "executed"));
            Assert.That(result.httpStatus, Is.EqualTo(200));
            Assert.That(result.mayStillBeRunning, Is.False);
            Assert.That(result.errorCode, Is.Null);
            Assert.That(result.error, Is.Null);
            Assert.That(result.output, Is.Null);
            Assert.That(result.executionTimeMs, Is.EqualTo(12L));
            if (dryRun) Assert.That(result.resultValue, Is.Null);
            else Assert.That(result.resultValue, Is.EqualTo(42L));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseRunResponse_AcceptsNullableOrMissingExecutionDuration(bool dryRun)
        {
            var outer = CreateSuccess(dryRun);
            Inner(outer)["executionTimeMs"] = null;
            outer["executionTimeMs"] = null;
            var result = Parse(outer, 200, dryRun);
            Assert.That(result.success, Is.True);
            Assert.That(result.executionTimeMs, Is.Null);
            Inner(outer).Remove("executionTimeMs");
            outer.Remove("executionTimeMs");
            Assert.That(Parse(outer, 200, dryRun).success, Is.True);
        }

        [Test]
        public void ParseRunResponse_ExecutionPreservesResultTreeNulls()
        {
            var outer = CreateSuccess(false);
            Inner(outer)["result"] = new Dictionary<string, object>
            {
                { "nullable", null }, { "items", new List<object> { null, new Dictionary<string, object> { { "inner", null } } } }
            };
            var result = Parse(outer, 200, false);
            var serialized = AIBridgeJson.Serialize(result.resultValue);
            var tree = AIBridgeJson.DeserializeObjectStrict(serialized);
            Assert.That(tree.ContainsKey("nullable"), Is.True);
            Assert.That(tree["nullable"], Is.Null);
            Assert.That(serialized, Does.Contain("\"inner\":null"));
            Inner(outer)["result"] = null;
            Assert.That(Parse(outer, 200, false).success, Is.True);
            Assert.That(Parse(outer, 200, false).resultValue, Is.Null);
        }

        [TestCase("Compilation Failed", "compile_failed", "pipelineRunCompilationFailed")]
        [TestCase("Runtime Error", "execution_failed", "pipelineRunRuntimeError")]
        [TestCase("Entry Point Not Found", "rejected", "pipelineRunEntryPointNotFound")]
        [TestCase("Bad Request", "rejected", "pipelineRunBadRequest")]
        [TestCase("File Not Found", "rejected", "pipelineRunFileNotFound")]
        public void ParseRunResponse_OuterTrueInnerFalseMapsKnownTerminalFailures(string error, string status, string code)
        {
            var outer = CreateFailure(error);
            Assert.That(outer["success"], Is.True);
            var result = Parse(outer, 200, false);
            Assert.That(result.success, Is.False);
            Assert.That(result.status, Is.EqualTo(status));
            Assert.That(result.errorCode, Is.EqualTo(code));
            Assert.That(result.error, Is.EqualTo(error));
            Assert.That(result.errorDetails, Is.EqualTo("Fixture failure details."));
            Assert.That(result.mayStillBeRunning, Is.False);
        }

        [TestCase("Compilation Failed")]
        [TestCase("Bad Request")]
        [TestCase("File Not Found")]
        public void ParseRunResponse_DryRunOnlyAllowsKnownPreExecutionFailures(string error)
        {
            var result = Parse(CreateFailure(error), 200, true);
            Assert.That(result.success, Is.False);
            Assert.That(result.status, Is.Not.EqualTo("unknown"));
            Assert.That(result.mayStillBeRunning, Is.False);
        }

        [TestCase("Runtime Error")]
        [TestCase("Entry Point Not Found")]
        public void ParseRunResponse_DryRunCannotConfirmExecutionPhaseFailure(string error)
        {
            AssertUnknown(Parse(CreateFailure(error), 200, true));
        }

        [Test]
        public void ParseRunResponse_ArgumentCoercionFailureWithLoadedAssemblyIsPreInvocation()
        {
            var outer = CreateFailure("Bad Request");
            Inner(outer)["assemblyName"] = "PipelineRunScript_Test_fixture";
            Inner(outer)["compileMs"] = 8L;
            Assert.That(Parse(outer, 200, false).status, Is.EqualTo("rejected"));
            AssertUnknown(Parse(outer, 200, true));
        }

        [TestCase("Timeout")]
        [TestCase("Execution Failed")]
        [TestCase("Unexpected Failure")]
        public void ParseRunResponse_UncertainFailuresNeverConfirmRemoteCompletion(string error)
        {
            var result = Parse(CreateFailure(error), 200, false);
            AssertUnknown(result);
            Assert.That(result.error, Is.EqualTo(error));
        }

        [TestCase(200)]
        [TestCase(400)]
        [TestCase(504)]
        public void ParseRunResponse_OuterTimeoutCannotBeHiddenByInnerFailure(int httpStatus)
        {
            var outer = CreateFailure("Compilation Failed");
            outer["success"] = false;
            outer["errorCode"] = "TIMEOUT";
            outer["error"] = "Request Timed Out";
            AssertUnknown(Parse(outer, httpStatus, false));
            var timeout = CreateOuterFailure("Request Timed Out");
            timeout["errorCode"] = "TIMEOUT";
            AssertUnknown(Parse(timeout, httpStatus, true));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseRunResponse_UnexpectedHttpAndOuterFalseCannotConfirmRunPayload(bool dryRun)
        {
            foreach (var http in new[] { 201, 204, 302, 400, 401, 403, 404, 429, 500, 502, 503, 504 })
            {
                AssertUnknown(Parse(CreateSuccess(dryRun), http, dryRun));
                AssertUnknown(Parse(CreateFailure("Compilation Failed"), http, dryRun));
            }
            var outer = CreateFailure("Compilation Failed");
            outer["success"] = false;
            AssertUnknown(Parse(outer, 200, dryRun));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseRunResponse_EnforcesCommandsAtBothLayers(bool dryRun)
        {
            foreach (var command in new[] { "script eval", "script methodreload reload", "editor quit", "Script Run", "" })
            {
                var outer = CreateSuccess(dryRun);
                outer["command"] = command;
                AssertUnknown(Parse(outer, 200, dryRun));
                outer = CreateSuccess(dryRun);
                Inner(outer)["command"] = command;
                AssertUnknown(Parse(outer, 200, dryRun));
            }
        }

        [TestCase("result", 1)]
        [TestCase("assemblyName", "unexpected assembly")]
        [TestCase("executeMs", 1)]
        [TestCase("message", "Compiled successfully.")]
        public void ParseRunResponse_DryRunRequiresExactCompileOnlyReceipt(string field, object value)
        {
            var outer = CreateSuccess(true);
            Inner(outer)[field] = value;
            AssertUnknown(Parse(outer, 200, true));
        }

        [Test]
        public void ParseRunResponse_ExecutionRejectsDryRunReceiptAndEmptyAssembly()
        {
            AssertUnknown(Parse(CreateSuccess(true), 200, false));
            foreach (var assembly in new[] { null, "", " " })
            {
                var outer = CreateSuccess(false);
                Inner(outer)["assemblyName"] = assembly;
                AssertUnknown(Parse(outer, 200, false));
            }
            var executed = CreateSuccess(false);
            Inner(executed)["message"] = Receipt;
            AssertUnknown(Parse(executed, 200, false));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseRunResponse_RejectsMissingRequiredRunFields(bool dryRun)
        {
            foreach (var field in new[] { "success", "command", "result", "message", "error", "errorDetails", "diagnostics", "compileMs", "executeMs", "assemblyName" })
            {
                var outer = CreateSuccess(dryRun);
                Inner(outer).Remove(field);
                AssertUnknown(Parse(outer, 200, dryRun));
            }
            foreach (var field in new[] { "success", "command", "result" })
            {
                var outer = CreateSuccess(dryRun);
                outer.Remove(field);
                AssertUnknown(Parse(outer, 200, dryRun));
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseRunResponse_RejectsMalformedEnvelopeTypesAtEitherLayer(bool dryRun)
        {
            foreach (var field in new[] { "success", "command", "message", "error", "errorCode", "errorDetails", "executionTimeMs", "output", "status", "retryable" })
            {
                foreach (var innerField in new[] { true, false })
                {
                    var outer = CreateSuccess(dryRun);
                    var target = innerField ? Inner(outer) : outer;
                    target[field] = new Dictionary<string, object> { { "invalid", true } };
                    AssertUnknown(Parse(outer, 200, dryRun));
                }
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseRunResponse_RejectsMalformedPhaseDurationsAndAssembly(bool dryRun)
        {
            foreach (var field in new[] { "compileMs", "executeMs", "executionTimeMs" })
            {
                foreach (var invalid in new object[] { -1L, 1.5d, "1", true })
                {
                    var outer = CreateSuccess(dryRun);
                    Inner(outer)[field] = invalid;
                    AssertUnknown(Parse(outer, 200, dryRun));
                }
            }
            foreach (var field in new[] { "compileMs", "executeMs" })
            {
                var outer = CreateSuccess(dryRun);
                Inner(outer)[field] = null;
                AssertUnknown(Parse(outer, 200, dryRun));
            }
            var invalidAssembly = CreateSuccess(dryRun);
            Inner(invalidAssembly)["assemblyName"] = 1L;
            AssertUnknown(Parse(invalidAssembly, 200, dryRun));
        }

        [Test]
        public void ParseRunResponse_RejectsEvalFieldsBusyMarkersAndSuccessfulErrors()
        {
            foreach (var field in new[] { "output", "errorCode", "error", "errorDetails", "status", "retryable", "busyReason" })
            {
                foreach (var innerField in new[] { true, false })
                {
                    var outer = CreateSuccess(false);
                    var target = innerField ? Inner(outer) : outer;
                    target[field] = field == "retryable" ? (object)true : "unexpected";
                    AssertUnknown(Parse(outer, 200, false));
                }
            }
            var misplaced = CreateSuccess(false);
            misplaced["compileMs"] = 1L;
            AssertUnknown(Parse(misplaced, 200, false));
        }

        [TestCase("Runtime Error")]
        [TestCase("Entry Point Not Found")]
        [TestCase("File Not Found")]
        [TestCase("Bad Request")]
        [TestCase("Compilation Failed")]
        public void ParseRunResponse_MalformedKnownFailureCannotReleaseSlot(string error)
        {
            foreach (var field in new[] { "command", "error", "errorDetails", "compileMs", "executeMs", "diagnostics" })
            {
                var outer = CreateFailure(error);
                Inner(outer)[field] = new Dictionary<string, object>();
                AssertUnknown(Parse(outer, 200, false));
            }
            var failure = CreateFailure(error);
            Inner(failure)["result"] = 42L;
            AssertUnknown(Parse(failure, 200, false));
            failure = CreateFailure(error);
            Inner(failure)["message"] = Receipt;
            AssertUnknown(Parse(failure, 200, false));
        }

        [TestCase("Compilation Failed")]
        [TestCase("Entry Point Not Found")]
        [TestCase("File Not Found")]
        [TestCase("Bad Request")]
        public void ParseRunResponse_PreInvocationFailureRequiresZeroExecuteTime(string error)
        {
            var outer = CreateFailure(error);
            Inner(outer)["executeMs"] = 1L;
            AssertUnknown(Parse(outer, 200, false));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseRunResponse_ValidDiagnosticsReuseCoordinateMapping(bool dryRun)
        {
            var outer = CreateSuccess(dryRun);
            Inner(outer)["diagnostics"] = new List<object> { Diagnostic("warning", "Careful", 0L) };
            var result = Parse(outer, 200, dryRun);
            Assert.That(result.success, Is.True);
            Assert.That(result.diagnostics[0].code, Is.EqualTo("CS1234"));
            Assert.That(result.diagnostics[0].line, Is.EqualTo(1));
            Assert.That(result.diagnostics[0].column, Is.EqualTo(5));
            var diagnostic = (Dictionary<string, object>)((List<object>)Inner(outer)["diagnostics"])[0];
            diagnostic["fileLine"] = 7L;
            Assert.That(Parse(outer, 200, dryRun).diagnostics[0].line, Is.EqualTo(7));
            diagnostic["severity"] = "error";
            AssertUnknown(Parse(outer, 200, dryRun));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParseRunResponse_MalformedOrOversizedDiagnosticsRemainUnknown(bool dryRun)
        {
            foreach (var diagnostics in InvalidDiagnostics())
            {
                var outer = CreateSuccess(dryRun);
                Inner(outer)["diagnostics"] = diagnostics;
                AssertUnknown(Parse(outer, 200, dryRun));
            }
        }

        [TestCase(401, "Unauthorized", "failed", "pipelineAuthenticationFailed")]
        [TestCase(403, "Forbidden", "failed", "pipelineAccessDenied")]
        [TestCase(429, "Too Many Queued Commands", "unavailable", "pipelineUnavailable")]
        [TestCase(503, "Server Busy", "unavailable", "pipelineUnavailable")]
        [TestCase(400, "Parameter Validation Failed", "rejected", "pipelineRunRejected")]
        [TestCase(400, "Command Not Found", "rejected", "pipelineRunRejected")]
        [TestCase(413, "Payload Too Large", "rejected", "pipelineRunRejected")]
        public void ParseRunResponse_AcceptsOnlySelfConsistentKnownHttpRefusals(int http, string error, string status, string code)
        {
            var outer = CreateOuterFailure(error);
            if (http == 401 || http == 403 || http == 413) { outer.Remove("success"); outer.Remove("command"); }
            if (http == 503) { outer["status"] = "busy"; outer["retryable"] = true; }
            var result = Parse(outer, http, false);
            Assert.That(result.status, Is.EqualTo(status));
            Assert.That(result.errorCode, Is.EqualTo(code));
            Assert.That(result.mayStillBeRunning, Is.False);
            outer["success"] = true;
            AssertUnknown(Parse(outer, http, false));
        }

        [Test]
        public void ParseRunResponse_ArgumentBindingAndBaseRequestFailuresArePreExecution()
        {
            var outer = CreateOuterFailure("Invalid Command Arguments");
            outer["errorCode"] = "INVALID_COMMAND_ARGS";
            Assert.That(Parse(outer, 400, false).status, Is.EqualTo("rejected"));
            foreach (var error in new[] { "Bad Request", "Invalid JSON", "Invalid Request" })
            {
                var baseFailure = new Dictionary<string, object> { { "error", error }, { "errorDetails", "Before dispatch." } };
                Assert.That(Parse(baseFailure, 400, false).status, Is.EqualTo("rejected"));
                AssertUnknown(Parse(baseFailure, 200, false));
            }
        }

        [TestCase(401)]
        [TestCase(403)]
        [TestCase(429)]
        [TestCase(503)]
        public void ParseRunResponse_ContradictoryOrUnrecognizedHttpRefusalRemainsUnknown(int http)
        {
            var outer = CreateOuterFailure("Runtime Error");
            AssertUnknown(Parse(outer, http, false));
            outer["errorCode"] = "TIMEOUT";
            AssertUnknown(Parse(outer, http, false));
            outer["result"] = Inner(CreateFailure("Compilation Failed"));
            AssertUnknown(Parse(outer, http, false));
            outer = CreateOuterFailure("Server Busy");
            outer["status"] = "busy";
            outer["retryable"] = "true";
            AssertUnknown(Parse(outer, http, false));
        }

        [Test]
        public void ParseRunResponse_RefusalCannotCarryExecutionTimeOrConflictingBusyMarkers()
        {
            var outer = CreateOuterFailure("Unauthorized");
            outer["executionTimeMs"] = 1L;
            AssertUnknown(Parse(outer, 401, false));
            outer["executionTimeMs"] = null;
            outer["status"] = "busy";
            outer["retryable"] = true;
            AssertUnknown(Parse(outer, 401, false));
            outer = CreateOuterFailure("Server Busy");
            outer["status"] = "busy";
            outer["retryable"] = true;
            outer["busyReason"] = "execution_in_progress";
            AssertUnknown(Parse(outer, 503, false));
        }

        [Test]
        public void ParseRunResponse_AcceptsDiagnosticCountBoundaryWithoutDiscardingWarnings()
        {
            var outer = CreateSuccess(false);
            var diagnostics = new List<object>();
            for (var i = 0; i < 512; i++) diagnostics.Add(Diagnostic("warning", "Careful", 0L));
            Inner(outer)["diagnostics"] = diagnostics;
            var result = Parse(outer, 200, false);
            Assert.That(result.success, Is.True);
            Assert.That(result.diagnostics.Count, Is.EqualTo(512));
        }

        [TestCase(200)]
        [TestCase(504)]
        public void ParseRunResponse_DuplicateDecodedKeysDepthAndMalformedJsonRemainUnknown(int http)
        {
            var valid = Serialize(CreateSuccess(false));
            var duplicate = valid.Replace("\"compileMs\":8", "\"compileMs\":8,\"\\u0063ompileMs\":0");
            Assert.That(duplicate, Is.Not.EqualTo(valid));
            AssertUnknown(PipelineCodeProtocol.ParseRunResponse(duplicate, http, false));
            foreach (var json in new[] { "", "null", "[]", "{", "not json", valid + "true" })
                AssertUnknown(PipelineCodeProtocol.ParseRunResponse(json, http, false));
            var tooDeep = "{\"result\":" + new string('[', 64) + "0" + new string(']', 64) + "}";
            AssertUnknown(PipelineCodeProtocol.ParseRunResponse(tooDeep, http, false));
        }

        private static Dictionary<string, object> CreateSuccess(bool dryRun)
        {
            var inner = new Dictionary<string, object>
            {
                { "success", true }, { "command", Command }, { "result", dryRun ? null : (object)42L },
                { "message", dryRun ? Receipt : null }, { "error", null }, { "errorDetails", null },
                { "diagnostics", new List<object>() }, { "compileMs", 8L }, { "executeMs", dryRun ? 0L : 2L },
                { "assemblyName", dryRun ? null : "PipelineRunScript_Test_fixture" }, { "executionTimeMs", 12L }
            };
            return new Dictionary<string, object>
            {
                { "success", true }, { "command", Command }, { "result", inner }, { "message", null },
                { "error", null }, { "errorDetails", null }, { "executionTimeMs", null }
            };
        }

        private static Dictionary<string, object> CreateFailure(string error)
        {
            var outer = CreateSuccess(false);
            var inner = Inner(outer);
            inner["success"] = false;
            inner["result"] = null;
            inner["error"] = error;
            inner["errorDetails"] = "Fixture failure details.";
            inner["executeMs"] = error == "Runtime Error" || error == "Timeout" || error == "Execution Failed" ? 2L : 0L;
            if (error == "Bad Request" || error == "File Not Found")
            {
                inner["assemblyName"] = null;
                inner["compileMs"] = 0L;
            }
            if (error == "Compilation Failed")
                inner["diagnostics"] = new List<object> { Diagnostic("error", "Missing semicolon", 0L) };
            return outer;
        }

        private static Dictionary<string, object> CreateOuterFailure(string error)
        {
            return new Dictionary<string, object>
            {
                { "success", false }, { "command", Command }, { "result", null },
                { "error", error }, { "errorDetails", "Fixture refusal details." }, { "executionTimeMs", null }
            };
        }

        private static Dictionary<string, object> Diagnostic(string severity, string message, object line)
        {
            return new Dictionary<string, object>
            {
                { "severity", severity }, { "id", "CS1234" }, { "message", message },
                { "file", "Test.cs" }, { "line", line }, { "column", 4L }
            };
        }

        private static List<object> InvalidDiagnostics()
        {
            var cases = new List<object> { null, "[]", new Dictionary<string, object>(), new List<object> { null } };
            foreach (var field in new[] { "severity", "id", "message", "file", "line", "column", "fileLine" })
            {
                var diagnostic = Diagnostic("warning", "Careful", 0L);
                diagnostic[field] = new Dictionary<string, object>();
                cases.Add(new List<object> { diagnostic });
            }
            foreach (var line in new object[] { -1L, int.MaxValue, "0", 1.5d })
                cases.Add(new List<object> { Diagnostic("warning", "Careful", line) });
            cases.Add(new List<object> { Diagnostic("other", "Careful", 0L) });
            var zeroFileLine = Diagnostic("warning", "Careful", 0L);
            zeroFileLine["fileLine"] = 0L;
            cases.Add(new List<object> { zeroFileLine });
            var tooMany = new List<object>();
            for (var i = 0; i < 513; i++) tooMany.Add(Diagnostic("warning", "Careful", 0L));
            cases.Add(tooMany);
            return cases;
        }

        private static Dictionary<string, object> Inner(Dictionary<string, object> outer)
        {
            return (Dictionary<string, object>)outer["result"];
        }

        private static string Serialize(Dictionary<string, object> outer)
        {
            // 保留上游 verbose 的显式 null，不用测试序列化器默认省略行为掩盖缺字段。
            return AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(outer));
        }

        private static PipelineCodeResult Parse(Dictionary<string, object> outer, int http, bool dryRun)
        {
            return PipelineCodeProtocol.ParseRunResponse(Serialize(outer), http, dryRun);
        }

        private static void AssertUnknown(PipelineCodeResult result)
        {
            Assert.That(result.success, Is.False);
            Assert.That(result.status, Is.EqualTo("unknown"));
            Assert.That(result.mayStillBeRunning, Is.True);
        }
    }
}
