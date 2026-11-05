using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public sealed class PipelineReadinessCacheTests
    {
        private Type _type;
        private readonly Dictionary<string, object> _snapshot = new Dictionary<string, object>();

        [SetUp]
        public void SetUp()
        {
            _type = Type.GetType("AIBridge.Editor.UnityPipelineAdapter.UnityPipelineAdapter, cn.lys.aibridge.Editor.UnityPipelineAdapter");
            if (_type == null) Assert.Ignore("Optional Pipeline adapter is not installed.");
            foreach (var field in _type.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                if (field.Name.StartsWith("s_Probe", StringComparison.Ordinal)) _snapshot[field.Name] = field.GetValue(null);
        }

        [TearDown]
        public void TearDown()
        {
            if (_type == null) return;
            foreach (var pair in _snapshot) Field(pair.Key).SetValue(null, pair.Value);
            _snapshot.Clear();
        }

        [Test]
        public void PublishedRefresh_AlwaysInvalidatesPreviousResponseValidation()
        {
            Set("s_ProbeKey", "fixture"); Set("s_ProbeGeneration", 12);
            Set("s_ProbeResponseValidated", true);
            Publish(12, "{\"status\":\"not-ready\"}");
            Assert.That(Get("s_ProbeResponseValidated"), Is.EqualTo(false));
            Assert.That(Get("s_ProbeCompleted"), Is.EqualTo(true));
        }

        [Test]
        public void LateResponse_FromPreviousTargetGeneration_IsIgnored()
        {
            Set("s_ProbeKey", "fixture"); Set("s_ProbeGeneration", 12);
            Set("s_ProbeResponseBody", "current");
            Publish(11, "stale");
            Assert.That(Get("s_ProbeResponseBody"), Is.EqualTo("current"));
        }

        [TestCase("evalToken")]
        [TestCase("startedAt")]
        [TestCase("unityVersion")]
        public void CredentialsAndServerIdentity_ArePartOfCacheIdentity(string property)
        {
            var descriptorType = _type.GetNestedType("PipelineServerDescriptor", BindingFlags.NonPublic);
            var descriptor = Activator.CreateInstance(descriptorType, true);
            descriptorType.GetField("port").SetValue(descriptor, 7800);
            var method = _type.GetMethod("GetProbeKey", BindingFlags.NonPublic | BindingFlags.Static);
            var before = (string)method.Invoke(null, new[] { descriptor });
            descriptorType.GetField(property).SetValue(descriptor, "fixture-rotated");
            var after = (string)method.Invoke(null, new[] { descriptor });
            Assert.That(after, Is.Not.EqualTo(before));
        }

        [Test]
        public void RegisteredPackageVersion_IsUsedInsteadOfStalePackageCacheDirectories()
        {
            var method = _type.GetMethod("FindPipelinePackage", BindingFlags.NonPublic | BindingFlags.Static);
            var package = method.Invoke(null, null);
            var packageType = package.GetType();
            var version = (string)packageType.GetField("Version", BindingFlags.Public | BindingFlags.Instance).GetValue(package);
            var path = (string)packageType.GetField("ResolvedPath", BindingFlags.Public | BindingFlags.Instance).GetValue(package);
            Assert.That(version, Is.EqualTo("0.10.0-exp.1"));
            Assert.That(path, Does.Contain("com.unity.pipeline"));
        }

        [Test]
        public void DecodedCredentialInExecutionResultAndOutput_IsRedactedBeforePersistence()
        {
            const string token = "fixture-secret";
            const string json = "{\"success\":true,\"command\":\"script eval\",\"result\":{\"success\":true,\"command\":\"script eval\",\"result\":\"fixture-secret\",\"output\":\"\\u0066ixture-secret\",\"message\":null,\"executionTimeMs\":1,\"diagnostics\":[]}}";
            var result = PipelineCodeProtocol.ParseEvalResponse(json, 200, false);
            _type.GetMethod("RedactResult", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { result, token });
            var serialized = global::AIBridge.Internal.Json.AIBridgeJson.Serialize(result);
            Assert.That(serialized, Does.Not.Contain(token));
            Assert.That(result.resultValue, Is.EqualTo("[redacted]"));
            Assert.That(result.output, Is.EqualTo("[redacted]"));
        }

        [Test]
        public void DecodedCredentialInUpstreamError_IsRedactedBeforePersistence()
        {
            const string token = "fixture-secret";
            const string json = "{\"success\":false,\"command\":\"script eval\",\"result\":{\"success\":false,\"command\":\"script eval\",\"errorCode\":\"COMPILATION_ERROR\",\"error\":\"\\u0066ixture-secret\",\"errorDetails\":\"fixture-secret\",\"diagnostics\":[{\"severity\":\"error\",\"message\":\"fixture-secret\"}]}}";
            var result = PipelineCodeProtocol.ParseDryRunResponse(json, 400);
            Assert.That(result.error, Is.EqualTo(token));
            _type.GetMethod("RedactResult", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { result, token });
            var serialized = global::AIBridge.Internal.Json.AIBridgeJson.Serialize(result);
            Assert.That(serialized, Does.Not.Contain(token));
            Assert.That(result.error, Is.EqualTo("[redacted]"));
            Assert.That(result.diagnostics[0].message, Is.EqualTo("[redacted]"));
        }

        [Test]
        public void AdapterExecution_RequiresConfirmationEvenWithProjectAuthorization()
        {
            var settings = AIBridgeProjectSettings.Instance;
            var enabled = settings.EnableCodeExecution;
            var accepted = settings.CodeExecutionRiskAccepted;
            try
            {
                settings.EnableCodeExecution = true;
                settings.CodeExecutionRiskAccepted = true;
                var method = _type.GetMethod("EvaluateAsync", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(method.GetParameters().Length, Is.EqualTo(4));
                var task = (System.Threading.Tasks.Task<PipelineCodeResult>)method.Invoke(null,
                    new object[] { "throw new System.Exception();", 5000, false, false });
                Assert.That(task.IsCompleted, Is.True, "Rejection must precede all descriptor/network work.");
                Assert.That(task.Result.status, Is.EqualTo("not_submitted"));
                Assert.That(task.Result.errorCode, Is.EqualTo("pipelineConfirmationRequired"));
            }
            finally
            {
                settings.EnableCodeExecution = enabled;
                settings.CodeExecutionRiskAccepted = accepted;
            }
        }

        [Test]
        public void RunAdapter_RejectsUnconfirmedExecutionBeforeAllIo()
        {
            var settings = AIBridgeProjectSettings.Instance;
            var enabled = settings.EnableCodeExecution;
            var accepted = settings.CodeExecutionRiskAccepted;
            try
            {
                settings.EnableCodeExecution = true;
                settings.CodeExecutionRiskAccepted = true;
                var method = _type.GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static);
                var task = (System.Threading.Tasks.Task<PipelineCodeResult>)method.Invoke(null,
                    new object[] { "invalid class source", "invalid-source.cs", "Fixture.Run", "[]", 5000, false, false });
                Assert.That(task.IsCompleted, Is.True);
                Assert.That(task.Result.status, Is.EqualTo("not_submitted"));
                Assert.That(task.Result.errorCode, Is.EqualTo("pipelineConfirmationRequired"));
            }
            finally
            {
                settings.EnableCodeExecution = enabled;
                settings.CodeExecutionRiskAccepted = accepted;
            }
        }

        [Test]
        public void DecodedCredentialInNestedObjectKeysAndValues_IsRedacted()
        {
            const string token = "fixture-secret";
            var result = new PipelineCodeResult
            {
                resultValue = global::AIBridge.Internal.Json.AIBridgeJson.Deserialize(
                    "{\"\\u0066ixture-secret\":[{\"nested-fixture-secret\":\"fixture-secret\"},null,7]}")
            };
            _type.GetMethod("RedactResult", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { result, token });
            var json = global::AIBridge.Internal.Json.AIBridgeJson.Serialize(result.resultValue);
            Assert.That(json, Does.Not.Contain(token));
            Assert.That(json, Does.Contain("nested-[redacted]"));
            var root = (Dictionary<string, object>)result.resultValue;
            Assert.That(((System.Collections.IList)root["[redacted]"]).Count, Is.EqualTo(3));
        }

        [Test]
        public void RedactedKeyCollision_IsRejectedInsteadOfSilentlyOverwritingResult()
        {
            var result = new PipelineCodeResult
            {
                resultValue = new Dictionary<string, object> { { "fixture-secret", 1L }, { "[redacted]", 2L } }
            };
            var method = _type.GetMethod("RedactResult", BindingFlags.NonPublic | BindingFlags.Static);
            var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null,
                new object[] { result, "fixture-secret" }));
            Assert.That(exception.InnerException, Is.TypeOf<System.IO.InvalidDataException>());
        }

        [TestCase("../api/exec", "script eval")]
        [TestCase("00000000000000000000000000000000", "asset delete")]
        public void JobObservation_InvalidInputIsRejectedBeforeDescriptorOrNetwork(string jobId, string command)
        {
            var settings = AIBridgeProjectSettings.Instance;
            var enabled = settings.EnableCodeExecution;
            var accepted = settings.CodeExecutionRiskAccepted;
            try
            {
                settings.EnableCodeExecution = true;
                settings.CodeExecutionRiskAccepted = true;
                var method = _type.GetMethod("ObserveJobAsync", BindingFlags.NonPublic | BindingFlags.Static);
                var task = (System.Threading.Tasks.Task<PipelineJobObservation>)method.Invoke(null,
                    new object[] { jobId, command, 5000 });
                Assert.That(task.IsCompleted, Is.True);
                Assert.That(task.Result.valid, Is.False);
                Assert.That(task.Result.status, Is.EqualTo("unknown"));
                Assert.That(task.Result.errorCode, Is.EqualTo("pipelineJobInputInvalid"));
            }
            finally
            {
                settings.EnableCodeExecution = enabled;
                settings.CodeExecutionRiskAccepted = accepted;
            }
        }

        [Test]
        public void JobObservation_DecodedResultProgressAndWarningsAreRedacted()
        {
            var observation = new PipelineJobObservation
            {
                resultValue = new Dictionary<string, object> { { "fixture-secret", null } },
                progress = new Dictionary<string, object> { { "info", "fixture-secret" } },
                warnings = new List<string> { "fixture-secret" }, errorDetails = "fixture-secret"
            };
            _type.GetMethod("RedactJobObservation", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { observation, "fixture-secret" });
            var json = global::AIBridge.Internal.Json.AIBridgeJson.Serialize(observation);
            Assert.That(json, Does.Not.Contain("fixture-secret"));
            Assert.That(json, Does.Contain("\"[redacted]\":null"));
        }

        [Test]
        public void JobObservation_RedactedKeyCollisionIsRejected()
        {
            var observation = new PipelineJobObservation
            {
                resultValue = new Dictionary<string, object> { { "fixture-secret", 1L }, { "[redacted]", 2L } }
            };
            var exception = Assert.Throws<TargetInvocationException>(() => _type.GetMethod("RedactJobObservation",
                BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { observation, "fixture-secret" }));
            Assert.That(exception.InnerException, Is.TypeOf<System.IO.InvalidDataException>());
        }

        private void Publish(int generation, string body)
        {
            _type.GetMethod("PublishProbeResult", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { "fixture", generation, true, null, body });
        }
        private FieldInfo Field(string name) { return _type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static); }
        private object Get(string name) { return Field(name).GetValue(null); }
        private void Set(string name, object value) { Field(name).SetValue(null, value); }
    }
}
