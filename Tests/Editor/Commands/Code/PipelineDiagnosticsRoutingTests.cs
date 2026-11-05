using System.Collections.Generic;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public sealed class PipelineDiagnosticsRoutingTests
    {
        [TestCase("compile")]
        [TestCase("test")]
        public void ExplicitNonStringBackendCannotFallBackToNative(string type)
        {
            foreach (var value in new object[] { null, false, 1L, new Dictionary<string, object>(), new object[0] })
            {
                var request = new CommandRequest { id = "routing", type = type, @params = new Dictionary<string, object>
                    { { "action", type == "compile" ? "start" : "run" }, { "backend", value } } };
                var result = type == "compile" ? new CompileCommand().Execute(request) : new TestCommand().Execute(request);
                Assert.That(result.success, Is.False);
                Assert.That(result.error, Does.Contain("literal strings"));
            }
        }

        [TestCase("compile")]
        [TestCase("test")]
        public void InvalidActionCannotUseDefaultNativeAction(string type)
        {
            var request = new CommandRequest { id = "routing", type = type, @params = new Dictionary<string, object>
                { { "action", new object[0] } } };
            var result = type == "compile" ? new CompileCommand().Execute(request) : new TestCommand().Execute(request);
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("literal strings"));
        }

        [TestCase("../escape")]
        [TestCase("../../escape")]
        [TestCase("C:\\escape")]
        [TestCase("a/b")]
        [TestCase("a.b")]
        [TestCase("")]
        public void UnsafeIdsAreRejectedBeforeTheAdapterCanBeInvoked(string id)
        {
            Assert.That(PipelineDiagnosticsCommand.ValidId(id), Is.False);
            var result = PipelineDiagnosticsCommand.Execute(new CommandRequest { id = id, @params = new Dictionary<string, object>() }, "compile_status");
            Assert.That(result, Is.Not.Null); Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("parameters"));
        }

        [TestCase(401)] [TestCase(403)] [TestCase(404)] [TestCase(405)] [TestCase(429)] [TestCase(501)] [TestCase(503)]
        public void AdapterResultMappingPreservesUnavailableHttpEvidenceWithoutRemoteBody(int status)
        {
            var type = System.Type.GetType("AIBridge.Editor.UnityPipelineAdapter.UnityPipelineAdapter, cn.lys.aibridge.Editor.UnityPipelineAdapter");
            if (type == null) Assert.Ignore("Optional Pipeline adapter is not installed.");
            var method = type.GetMethod("DiagnosticResult", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var parsed = PipelineDiagnosticsProtocol.ParseResponse("compile_status", "sensitive-remote-body", status);
            var result = (CommandResult)method.Invoke(null, new object[] { "fixture", parsed.valid, parsed.error, parsed.data });
            Assert.That(result.success, Is.False);
            var wire = global::AIBridge.Internal.Json.AIBridgeJson.DeserializeObjectStrict(global::AIBridge.Internal.Json.AIBridgeJson.Serialize(result.data));
            Assert.That(wire["status"], Is.EqualTo("unavailable"));
            Assert.That(wire["httpStatus"], Is.EqualTo((long)status));
            Assert.That(wire["errorCode"], Is.EqualTo("pipelineDiagnosticsUnavailable"));
            Assert.That(result.error, Does.Not.Contain("sensitive-remote-body"));
        }

        [Test]
        public void CliGeneratedIdIsAcceptedButOverlongIdIsNot()
        {
            Assert.That(PipelineDiagnosticsCommand.ValidId("cmd_1793857401778_91ad9a55034548"), Is.True);
            Assert.That(PipelineDiagnosticsCommand.ValidId(new string('a', 129)), Is.False);
        }
    }
}
