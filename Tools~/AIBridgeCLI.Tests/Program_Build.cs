using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Runtime.Internal;
using AIBridgeCLI.Commands;
using AIBridgeCLI.Core;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Tests
{
    internal static partial class Program
    {
        private static void BuildCommands_ValidateParametersAndReadJournalWithoutEditor()
        {
            foreach (var action in new[] { "save_profile", "load_profile" })
            {
                var rejected = false;
                try { new ProfilerCommandBuilder().Build(action, new Dictionary<string, string> { { "path", ".aibridge/profiler/native/test.data" }, { "confirm", "1" } }); }
                catch (ArgumentException) { rejected = true; }
                AssertTrue(rejected, "Native profiler confirm must be actual true.");
            }
            var memory = new MemoryCommandBuilder().Build("capture_snapshot", new Dictionary<string, string> { { "confirm", "true" }, { "timeout", "5000" } });
            AssertEqual(5000L, memory.@params["timeout"], "Memory timeout must survive global option exclusion.");
            AssertEqual("editor", memory.@params["target"], "Memory defaults to local Editor without switching.");
            foreach (var extra in new[] { "target", "overwrite", "collectGarbage" })
            {
                var rejected = false;
                try { new MemoryCommandBuilder().Build("capture_snapshot", new Dictionary<string, string> { { "confirm", "true" }, { extra, "true" } }); }
                catch (ArgumentException) { rejected = true; }
                AssertTrue(rejected, "Unsupported memory flags cannot be forwarded.");
            }
            var compileDiagnostic = new CompileCommandBuilder().Build("status", new Dictionary<string, string> { { "backend", "pipeline" } });
            AssertEqual(10000L, compileDiagnostic.@params["timeout"], "Pipeline diagnostic has a bounded timeout.");
            var compileTrigger = new CompileCommandBuilder().Build("start", new Dictionary<string, string> { { "backend", "pipeline" }, { "confirm", "true" } });
            AssertEqual(true, compileTrigger.@params["confirm"], "Compile trigger requires explicit literal confirmation.");
            AssertEqual(10000L, compileTrigger.@params["timeout"], "Compile trigger has bounded acknowledgement wait.");
            var compileLocal = new CompileCommandBuilder().Build("status", new Dictionary<string, string> { { "backend", "pipeline" }, { "requestId", "123" } });
            AssertEqual("123", compileLocal.@params["requestId"], "Local compile requestId stays literal text.");
            AssertTrue(!compileLocal.@params.ContainsKey("timeout"), "Local journal query does not trigger remote query.");
            foreach (var key in new[] { "focus", "job", "no-wait", "json", "force", "poll-interval" })
            {
                var rejected = false;
                try { new CompileCommandBuilder().Build("start", new Dictionary<string, string> { { "backend", "pipeline" }, { "confirm", "true" }, { key, "true" } }); }
                catch (ArgumentException) { rejected = true; }
                AssertTrue(rejected, "Compile trigger rejects unsupported flag: " + key);
            }
            foreach (var invalidOption in new[] { new[] { "start", "requestId", "original" }, new[] { "release", "timeout", "1000" } })
            {
                var options = new Dictionary<string, string> { { "backend", "pipeline" }, { "confirm", "true" } };
                if (invalidOption[0] == "release") options["requestId"] = "original";
                options[invalidOption[1]] = invalidOption[2];
                var rejected = false;
                try { new CompileCommandBuilder().Build(invalidOption[0], options); }
                catch (ArgumentException) { rejected = true; }
                AssertTrue(rejected, "Action-inapplicable compile option must fail before submission: " + invalidOption[1]);
            }
            var testListing = new TestCommandBuilder().Build("list", new Dictionary<string, string> { { "backend", "pipeline" } });
            AssertEqual("EditMode", testListing.@params["mode"], "Pipeline list is explicitly EditMode.");
            foreach (var type in new[] { "compile", "test" })
            {
                foreach (var action in new[] { "run", "unity", "start", "cancel" })
                {
                    if (type == "test" && action == "run") continue;
                    var rejected = false;
                    try
                    {
                        if (type == "compile") new CompileCommandBuilder().Build(action, new Dictionary<string, string> { { "backend", "pipeline" } });
                        else new TestCommandBuilder().Build(action, new Dictionary<string, string> { { "backend", "pipeline" } });
                    }
                    catch (ArgumentException) { rejected = true; }
                    AssertTrue(rejected, "Unsupported Pipeline actions cannot use native fallback.");
                }
            }
            var pipelineRun = new TestCommandBuilder().Build("run", new Dictionary<string, string> { { "backend", "pipeline" }, { "pipeline-filter", "123" } });
            AssertEqual(120000L, pipelineRun.@params["timeout"], "Pipeline run wait budget is explicit.");
            AssertEqual("123", pipelineRun.@params["pipeline-filter"], "A numeric-looking substring stays literal text.");
            foreach (var key in new[] { "test-name", "group-name", "assembly-name", "job", "async-tests", "no-wait", "json", "run-id" })
            {
                var rejected = false;
                try { new TestCommandBuilder().Build("run", new Dictionary<string, string> { { "backend", "pipeline" }, { key, "true" } }); }
                catch (ArgumentException) { rejected = true; }
                AssertTrue(rejected, "Pipeline run must reject incompatible options: " + key);
            }
            var runMethod = typeof(AIBridgeCLI.Program).GetMethod("Run", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            foreach (var backend in new[] { "pipeline", "garbage" })
            {
                using (var capture = new StringWriter())
                {
                    var savedOutput = Console.Out; var savedError = Console.Error;
                    try
                    {
                        Console.SetOut(capture); Console.SetError(capture);
                        var exit = (int)runMethod.Invoke(null, new object[] { new[] { "compile", "dotnet", "--backend", backend } });
                        AssertEqual(1, exit, "Explicit non-native compile dotnet must fail before the local build.");
                        AssertTrue(capture.ToString().Contains("unsupported"), "Program routing reports no fallback.");
                    }
                    finally { Console.SetOut(savedOutput); Console.SetError(savedError); }
                }
            }
            var builder = new BuildCommandBuilder();
            var request = builder.Build("run", new Dictionary<string, string>());
            AssertEqual(true, request.@params["dry-run"], "Build defaults to validation.");
            AssertEqual(false, request.@params["confirm"], "Build default is unconfirmed.");
            foreach (var options in new[]
            {
                new Dictionary<string, string> { { "dry-run", "false" } },
                new Dictionary<string, string> { { "dry-run", "0" } },
                new Dictionary<string, string> { { "confirm", "1" } },
                new Dictionary<string, string> { { "development", "1" } },
                new Dictionary<string, string> { { "outputPath", "outside.exe" } },
                new Dictionary<string, string> { { "json", "{}" } }
            })
            {
                var rejected = false; try { builder.Build("run", options); } catch (ArgumentException) { rejected = true; }
                AssertTrue(rejected, "Invalid build options must be rejected before queuing.");
            }
            request = builder.Build("run", new Dictionary<string, string> { { "dry-run", "false" }, { "confirm", "true" } });
            AssertEqual(false, request.@params["dry-run"], "Explicit build flag survives validation.");
            var root = Path.Combine(Path.GetTempPath(), "aibridge-build-cli-" + Guid.NewGuid().ToString("N"));
            var oldRoot = Environment.GetEnvironmentVariable("UNITY_PROJECT_ROOT");
            var priorOutput = Console.Out;
            try
            {
                Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("UNITY_PROJECT_ROOT", root);
                var id = "controlled-build";
                var record = new AIBridgeBuildRecord { requestId = id, buildId = new string('a', 32), projectPath = root,
                    unityVersion = "6000.0.51f1", generation = new string('b', 32), status = "building", target = "StandaloneWindows64",
                    outputPath = Path.Combine(root, ".aibridge", "builds", new string('a', 32), "Player.exe"),
                    queuedAt = "2026-11-05T01:00:00Z", startedAt = "2026-11-05T01:00:01Z", editorPid = 123,
                    success = null, busy = true, invocationStarted = true, scenes = new List<string> { "Assets/Fixture.unity" } };
                AIBridgeBuildStore.Save(root, record);
                using (var capture = new StringWriter())
                {
                    Console.SetOut(capture);
                    var exit = BuildStatusCommand.Execute("status", new Dictionary<string, string> { { "requestId", id } }, OutputMode.Raw);
                    AssertEqual(0, exit, "State query succeeds without an Editor connection.");
                    var output = JObject.Parse(capture.ToString());
                    AssertEqual("building", (string)output["data"]["record"]["status"], "Query reads exact prior journal state.");
                    AssertEqual(JTokenType.Null, output["data"]["record"]["success"].Type, "Unknown success must stay explicit null.");
                    AssertEqual(true, (bool)output["data"]["localJournal"], "State does not claim realtime liveness.");
                    capture.GetStringBuilder().Clear();
                    exit = BuildStatusCommand.Execute("report", new Dictionary<string, string> { { "requestId", id } }, OutputMode.Raw);
                    AssertEqual(1, exit, "Unfinished report is not available.");
                    record.status = "completed"; record.endedAt = "2026-11-05T01:00:02Z"; record.busy = false;
                    record.success = false; record.errorCode = "controlledFailure"; record.error = "controlled failure";
                    record.report = new AIBridgeBuildReport { result = "Failed", target = record.target, outputPath = record.outputPath,
                        totalErrors = 1, files = new List<AIBridgeBuildFile>(), steps = new List<AIBridgeBuildStep>() };
                    AIBridgeBuildStore.Save(root, record); capture.GetStringBuilder().Clear();
                    exit = BuildStatusCommand.Execute("report", new Dictionary<string, string> { { "requestId", id } }, OutputMode.Raw);
                    AssertEqual(1, exit, "Failed build report returns failure while retaining the DTO.");
                    var failed = JObject.Parse(capture.ToString());
                    AssertEqual("Failed", (string)failed["data"]["record"]["report"]["result"], "Failure report stays available.");
                    capture.GetStringBuilder().Clear();
                    exit = BuildStatusCommand.Execute("REPORT", new Dictionary<string, string> { { "requestId", id } }, OutputMode.Raw);
                    AssertEqual(1, exit, "Uppercase report cannot bypass failure semantics.");
                    capture.GetStringBuilder().Clear();
                    exit = BuildStatusCommand.Execute("REPORT", new Dictionary<string, string> { { "requestId", "missing" } }, OutputMode.Raw);
                    AssertEqual(1, exit, "Missing report cannot return successful idle.");
                }
                AssertTrue(!Directory.Exists(Path.Combine(root, ".aibridge", "commands")), "Readonly status must not queue any command.");
            }
            finally
            {
                Console.SetOut(priorOutput); Environment.SetEnvironmentVariable("UNITY_PROJECT_ROOT", oldRoot);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
