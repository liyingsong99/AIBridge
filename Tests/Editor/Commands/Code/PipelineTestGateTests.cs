using System.Collections.Generic;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class PipelineTestGateTests
    {
        [TestCase("test-name")] [TestCase("testName")] [TestCase("group-name")] [TestCase("groupName")]
        [TestCase("assembly-name")] [TestCase("assemblyName")] [TestCase("category")] [TestCase("filter")]
        [TestCase("runId")] [TestCase("include-explicit")] [TestCase("async-tests")] [TestCase("job")]
        [TestCase("url")] [TestCase("token")]
        public void NativeOrTransportOptionsAreRejectedBeforeJournalOrAdapter(string key)
        {
            var request = Request("run"); request.@params[key] = "fixture";
            var result = PipelineTestTracker.Start(request); Assert.That(result.success, Is.False);
        }

        [TestCase("PlayMode")] [TestCase("All")] [TestCase("editor")] [TestCase("editmode")]
        public void OnlyExactEditModeIsAccepted(string mode)
        {
            var request = Request("run"); request.@params["mode"] = mode;
            Assert.That(PipelineTestTracker.Start(request).success, Is.False);
        }

        [Test]
        public void TextBooleanFloatAndOversizeTimeoutAreRejectedBeforeAnySession()
        {
            foreach (var timeout in new object[] { "120000", 1.5, false, null, 999L, 600001L })
            { var request = Request("run"); request.@params["timeout"] = timeout; Assert.That(PipelineTestTracker.Start(request).success, Is.False); }
            foreach (var key in new[] { "mode", "pipeline-filter", "filter-type" })
            { var request = Request("run"); request.@params[key] = false; Assert.That(PipelineTestTracker.Start(request).success, Is.False); }
        }

        [Test]
        public void UnsupportedFiltersDoNotGetTranslatedIntoNativeFilters()
        {
            var request = Request("run"); request.@params["filter-type"] = "regex";
            Assert.That(PipelineTestTracker.Start(request).success, Is.False);
            request = Request("run"); request.@params["pipeline-filter"] = new string('x', 4097);
            Assert.That(PipelineTestTracker.Start(request).success, Is.False);
        }

        [Test]
        public void QueryRejectsRemoteStatusParametersAndReleaseRequiresLiteralConfirmation()
        {
            var request = Request("status"); request.@params["remote"] = true;
            Assert.That(PipelineTestTracker.Query(request).success, Is.False);
            foreach (var value in new object[] { null, false, 1L, "true" })
            {
                request = Request("release"); request.@params["requestId"] = "fixture_unknown"; request.@params["confirm"] = value;
                Assert.That(PipelineTestTracker.Release(request).success, Is.False);
            }
        }

        private static CommandRequest Request(string action)
        { return new CommandRequest { id = "pipeline_gate_fixture", @params = new Dictionary<string, object> { { "action", action }, { "backend", "pipeline" } } }; }
    }
}
