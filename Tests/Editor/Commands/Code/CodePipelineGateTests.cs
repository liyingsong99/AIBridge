using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public sealed class CodePipelineGateTests
    {
        private bool _enabled;
        private bool _accepted;

        [SetUp]
        public void SetUp()
        {
            var settings = AIBridgeProjectSettings.Instance;
            _enabled = settings.EnableCodeExecution;
            _accepted = settings.CodeExecutionRiskAccepted;
            settings.EnableCodeExecution = true;
            settings.CodeExecutionRiskAccepted = true;
        }

        [TearDown]
        public void TearDown()
        {
            var settings = AIBridgeProjectSettings.Instance;
            settings.EnableCodeExecution = _enabled;
            settings.CodeExecutionRiskAccepted = _accepted;
        }

        [Test]
        public void PipelineExecution_WithoutPerRequestConfirmationIsRejected()
        {
            var result = new CodeCommand().Execute(Request(new Dictionary<string, object>
            {
                { "code", "throw new System.Exception();" }, { "dry-run", false }
            }));
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("--confirm true"));
        }

        [TestCase("dry-run", 1)]
        [TestCase("dry-run", "false")]
        [TestCase("confirm", 1)]
        [TestCase("confirm", "true")]
        [TestCase("confirm", null)]
        public void PipelineExecution_RejectsNonBooleanSafetyFlags(string key, object value)
        {
            var parameters = new Dictionary<string, object>
            {
                { "code", "throw new System.Exception();" }, { "dry-run", false }, { "confirm", false }
            };
            parameters[key] = value;
            var result = new CodeCommand().Execute(Request(parameters));
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("must be boolean"));
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(false, false)]
        public void PipelineDryRun_RequiresBothProjectAuthorizations(bool enabled, bool accepted)
        {
            var settings = AIBridgeProjectSettings.Instance;
            settings.EnableCodeExecution = enabled;
            settings.CodeExecutionRiskAccepted = accepted;
            var result = new CodeCommand().Execute(Request(new Dictionary<string, object>
                { { "code", "return 1;" }, { "allowExperimental", true } }));
            Assert.That(result, Is.Not.Null);
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("disabled"));
            Assert.That(PipelineCodeProtocol.IsExecutionAuthorized(), Is.False);
        }

        [Test]
        public void PipelineWithoutDryRun_IsRejectedBeforeAdapterLookup()
        {
            var result = new CodeCommand().Execute(Request(new Dictionary<string, object>
                { { "code", "throw new System.Exception();" }, { "dry-run", false }, { "confirm", false } }));
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("--confirm true"));
        }

        [TestCase("Assets/outside.cs")]
        [TestCase(".aibridge/code/../outside.csx")]
        [TestCase(".aibridge/code/subdir/inside.csx")]
        public void PipelineFiles_UseExistingRestrictedSourceGate(string file)
        {
            var result = new CodeCommand().Execute(Request(new Dictionary<string, object> { { "file", file } }));
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain(".aibridge/code"));
        }

        [Test]
        public void Pipeline_RejectsAmbiguousSourceBeforeAdapterLookup()
        {
            var result = new CodeCommand().Execute(Request(new Dictionary<string, object>
                { { "file", ".aibridge/code/unused.csx" }, { "code", "return 1;" } }));
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("exactly one source"));
        }

        [TestCase("pipeline", true)]
        [TestCase("native", true)]
        [TestCase("pipeline", false)]
        public void RuntimeExecution_RejectsUnsupportedCompileOnlyFlags(string backend, bool dryRun)
        {
            var result = new CodeCommand().Execute(new CommandRequest
            {
                id = "runtime-reject-" + Guid.NewGuid().ToString("N"), type = "code",
                @params = new Dictionary<string, object>
                {
                    { "action", "runtime_execute" }, { "backend", backend }, { "dry-run", dryRun },
                    { "code", "throw new System.Exception();" }
                }
            });
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("does not support dry-run"));
        }

        [Test]
        public void PipelineSource_PreservesOriginalUsingsCommentsAndLines()
        {
            const string code = "// original first line\nusing System;\n\nthrow new Exception(\"dry-run\");";
            var method = typeof(CodeCommand).GetMethod("BuildSourceText", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var targetType = method.GetParameters()[3].ParameterType;
            var source = method.Invoke(null, new object[] { code, "file", ".aibridge/code/check.csx", Enum.Parse(targetType, "Editor") });
            var original = source.GetType().GetField("OriginalCode").GetValue(source);
            Assert.That(original, Is.EqualTo(code));
        }

        [TestCase("code", "return 1;")]
        [TestCase("mode", "hotpatch")]
        [TestCase("defines", "ANY")]
        [TestCase("references", "Other.dll")]
        [TestCase("commandLine", "script run")]
        [TestCase("pdb", true)]
        public void Run_RejectsParametersOutsideFixedWhitelist(string name, object value)
        {
            var result = new CodeCommand().Execute(RunRequest(new Dictionary<string, object> { { name, value } }));
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("Unsupported code run parameter"));
        }

        [Test]
        public void Run_RequiresConfirmationBeforeReadingSource()
        {
            var result = new CodeCommand().Execute(RunRequest(new Dictionary<string, object> { { "dry-run", false } }));
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("--confirm true"));
        }

        [TestCase("bareMethod")]
        [TestCase("Type.Run()")]
        [TestCase("Type.Run;evil")]
        public void Run_RejectsImplicitOrInvalidEntry(string entry)
        {
            var result = new CodeCommand().Execute(RunRequest(new Dictionary<string, object> { { "entry", entry } }));
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("explicit"));
        }

        [TestCase("native")]
        [TestCase("auto")]
        public void Run_DoesNotFallbackToOtherBackend(string backend)
        {
            var result = new CodeCommand().Execute(RunRequest(new Dictionary<string, object> { { "backend", backend } }));
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("only"));
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void Run_RequiresBothProjectAuthorizations(bool enabled, bool accepted)
        {
            AIBridgeProjectSettings.Instance.EnableCodeExecution = enabled;
            AIBridgeProjectSettings.Instance.CodeExecutionRiskAccepted = accepted;
            var result = new CodeCommand().Execute(RunRequest(new Dictionary<string, object>()));
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("disabled"));
        }

        [Test]
        public void DetachedRunRequiresConfirmationEvenForCompileOnly()
        {
            var result = new CodeCommand().Execute(RunRequest(new Dictionary<string, object> { { "job", true } }));
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("no upstream execution timeout"));
        }

        [TestCase(1)]
        [TestCase("true")]
        [TestCase(null)]
        public void DetachedRunRejectsNonBooleanJobFlagBeforeSourceRead(object value)
        {
            var result = new CodeCommand().Execute(RunRequest(new Dictionary<string, object> { { "job", value } }));
            Assert.That(result.success, Is.False); Assert.That(result.error, Does.Contain("boolean"));
        }

        [Test]
        public void EvalDoesNotSilentlyIgnoreDetachedFlag()
        {
            var result = new CodeCommand().Execute(Request(new Dictionary<string, object> { { "job", true }, { "code", "return 1;" } }));
            Assert.That(result.success, Is.False); Assert.That(result.error, Does.Contain("only by code run"));
        }

        [Test]
        public void RemoteCancelCannotControlExternalJobId()
        {
            var result = new CodeCommand().Execute(new CommandRequest
            {
                id = "reject-external-cancel", type = "code",
                @params = new Dictionary<string, object> { { "action", "job_cancel" }, { "jobId", new string('0', 32) }, { "confirm", true } }
            });
            Assert.That(result.success, Is.False); Assert.That(result.error, Does.Contain("only requestId"));
        }

        private static CommandRequest RunRequest(Dictionary<string, object> parameters)
        {
            parameters["action"] = "run";
            if (!parameters.ContainsKey("file")) parameters["file"] = ".aibridge/code/unused.cs";
            if (!parameters.ContainsKey("entry")) parameters["entry"] = "Fixture.Run";
            return new CommandRequest { id = "pipeline-run-gate-" + Guid.NewGuid().ToString("N"), type = "code", @params = parameters };
        }

        private static CommandRequest Request(Dictionary<string, object> parameters)
        {
            parameters["action"] = "execute";
            parameters["backend"] = "pipeline";
            if (!parameters.ContainsKey("dry-run")) parameters["dry-run"] = true;
            return new CommandRequest { id = "pipeline-gate-" + Guid.NewGuid().ToString("N"), type = "code", @params = parameters };
        }
    }
}
