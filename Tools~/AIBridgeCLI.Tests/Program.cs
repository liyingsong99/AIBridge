using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AIBridgeCLI.Commands;
using AIBridgeCLI.Core;
using AIBridgeCLI.Workflow;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                WorkflowReport_IncludesRuntimePerformanceEvidence();
                WorkflowReport_IncludesFailedRuntimePerformanceEvidence();
                ArtifactRequiredGate_MatchesSemanticKind();
                LostTestRunStatus_IsRecognizedAfterAck();
                DialogButtonInfo_ExposesStrictLogicalChoices();
                DialogButtonInfo_DoesNotExposeChoicesForDisabledButtons();
                SelectButton_FindsUniqueMatchAcrossDialogs();
                SelectButton_IgnoresDisabledButtons();
                SelectButton_RejectsAmbiguousChoiceAcrossDialogs();
                SelectButton_RespectsExplicitDialogId();
                BatchDialogAutoClickPlan_PreservesTargetKind();
                CodeIndex_Help_OnlyListsLightweightActions();
                CodeIndex_UnsupportedAction_ReturnsUnsupportedAction();
                CodeIndex_DefinitionSourceLocationArguments_RequireQuery();
                CodeIndex_InternalActions_AreNotShownInHelp();
                Console.WriteLine("AIBridgeCLI tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static void WorkflowReport_IncludesRuntimePerformanceEvidence()
        {
            var previousRoot = Environment.GetEnvironmentVariable("UNITY_PROJECT_ROOT");
            var previousDirectory = Directory.GetCurrentDirectory();
            var projectRoot = Path.Combine(Path.GetTempPath(), "AIBridgeCLI.Tests." + Guid.NewGuid().ToString("N"));
            var artifactPath = Path.Combine(projectRoot, "perf-command-result.json");
            try
            {
                Directory.CreateDirectory(projectRoot);
                Environment.SetEnvironmentVariable("UNITY_PROJECT_ROOT", projectRoot);
                ResetPathHelperCache();

                File.WriteAllText(artifactPath, CreateRuntimePerfCommandResult().ToString());

                var manifest = new WorkflowRunManifest
                {
                    RunId = "wf_perf_report_test",
                    RecipeName = "performance-hotspot-investigation",
                    StartedAtUtc = DateTime.UtcNow.ToString("o"),
                    Status = "passed"
                };
                manifest.ArtifactRefs.Add(new WorkflowArtifactRef
                {
                    ArtifactId = "art_runtime_perf_cmd",
                    Kind = "command-result",
                    SemanticKind = "runtime-perf",
                    Path = artifactPath,
                    SourceCommand = "runtime perf --target latest --duration 15s --interval 100ms --hitchThresholdMs 50",
                    CreatedAtUtc = DateTime.UtcNow.ToString("o")
                });

                var markdown = WorkflowReportWriter.WriteMarkdown(manifest);

                AssertContains(markdown, "## Performance Evidence", "Report should include the performance section.");
                AssertContains(markdown, "### Runtime Perf", "Report should include runtime perf subsection.");
                AssertContains(markdown, "avg 58.4", "Report should include average FPS.");
                AssertContains(markdown, "p95 24.2 ms", "Report should include p95 frame time.");
                AssertContains(markdown, "2 >= 50 ms", "Report should include hitch count and threshold.");
                AssertContains(markdown, "profilerRecorder", "Report should include recorder mode.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("UNITY_PROJECT_ROOT", previousRoot);
                Directory.SetCurrentDirectory(previousDirectory);
                ResetPathHelperCache();
                if (Directory.Exists(projectRoot))
                {
                    Directory.Delete(projectRoot, true);
                }
            }
        }

        private static void WorkflowReport_IncludesFailedRuntimePerformanceEvidence()
        {
            var previousRoot = Environment.GetEnvironmentVariable("UNITY_PROJECT_ROOT");
            var previousDirectory = Directory.GetCurrentDirectory();
            var projectRoot = Path.Combine(Path.GetTempPath(), "AIBridgeCLI.Tests." + Guid.NewGuid().ToString("N"));
            var artifactPath = Path.Combine(projectRoot, "perf-command-result-failed.json");
            try
            {
                Directory.CreateDirectory(projectRoot);
                Environment.SetEnvironmentVariable("UNITY_PROJECT_ROOT", projectRoot);
                ResetPathHelperCache();

                File.WriteAllText(artifactPath, CreateFailedRuntimePerfCommandResult().ToString());

                var manifest = new WorkflowRunManifest
                {
                    RunId = "wf_perf_report_failed_test",
                    RecipeName = "performance-hotspot-investigation",
                    StartedAtUtc = DateTime.UtcNow.ToString("o"),
                    Status = "failed"
                };
                manifest.ArtifactRefs.Add(new WorkflowArtifactRef
                {
                    ArtifactId = "art_runtime_perf_cmd_failed",
                    Kind = "command-result",
                    SemanticKind = "runtime-perf",
                    Path = artifactPath,
                    SourceCommand = "runtime perf --target latest --duration 15s --interval 100ms --hitchThresholdMs 50",
                    CreatedAtUtc = DateTime.UtcNow.ToString("o")
                });

                var markdown = WorkflowReportWriter.WriteMarkdown(manifest);

                AssertContains(markdown, "## Performance Evidence", "Failed runtime perf report should include the performance section.");
                AssertContains(markdown, "### Runtime Perf", "Failed runtime perf report should include runtime perf subsection.");
                AssertContains(markdown, "`failed`", "Failed runtime perf report should show failed status.");
                AssertContains(markdown, "Runtime target was not found", "Failed runtime perf report should surface the command error.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("UNITY_PROJECT_ROOT", previousRoot);
                Directory.SetCurrentDirectory(previousDirectory);
                ResetPathHelperCache();
                if (Directory.Exists(projectRoot))
                {
                    Directory.Delete(projectRoot, true);
                }
            }
        }

        private static void ArtifactRequiredGate_MatchesSemanticKind()
        {
            var artifactPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(artifactPath, "{}");
                var recipe = new WorkflowRecipe();
                recipe.Gates.Add(new WorkflowGate
                {
                    Id = "runtime-perf-required",
                    Kind = "artifactRequired",
                    Required = true,
                    ArtifactKind = "runtime-perf",
                    Min = 1
                });

                var manifest = new WorkflowRunManifest();
                manifest.ArtifactRefs.Add(new WorkflowArtifactRef
                {
                    ArtifactId = "art_runtime_perf_cmd",
                    Kind = "command-result",
                    SemanticKind = "runtime-perf",
                    Path = artifactPath,
                    CreatedAtUtc = DateTime.UtcNow.ToString("o")
                });

                var results = WorkflowGateEvaluator.Evaluate(recipe, manifest);

                AssertEqual(1, results.Count, "Gate evaluator should return one result.");
                AssertEqual("passed", results[0].Status, "artifactRequired should match semanticKind.");
            }
            finally
            {
                if (File.Exists(artifactPath))
                {
                    File.Delete(artifactPath);
                }
            }
        }

        private static void LostTestRunStatus_IsRecognizedAfterAck()
        {
            AssertTrue(AIBridgeCLI.Program.IsLostTestRunStatus("cmd_123", "unknown", true), "Confirmed unknown status should be treated as a lost test run.");
            AssertTrue(!AIBridgeCLI.Program.IsLostTestRunStatus("cmd_123", "unknown", false), "Unconfirmed unknown status should not fail fast.");
            AssertTrue(!AIBridgeCLI.Program.IsLostTestRunStatus(null, "unknown", true), "Missing runId should not be treated as a lost run.");
            AssertTrue(!AIBridgeCLI.Program.IsLostTestRunStatus("cmd_123", "running", true), "Running status should not be treated as lost.");
        }

        private static JObject CreateRuntimePerfCommandResult()
        {
            return new JObject
            {
                ["id"] = "cmd_perf",
                ["success"] = true,
                ["exitCode"] = 0,
                ["command"] = "runtime perf --target latest --duration 15s --interval 100ms --hitchThresholdMs 50",
                ["data"] = new JObject
                {
                    ["success"] = true,
                    ["data"] = new JObject
                    {
                        ["targetId"] = "player-1",
                        ["durationMs"] = 15000,
                        ["intervalMs"] = 100,
                        ["sampleCount"] = 150,
                        ["fps"] = new JObject
                        {
                            ["avg"] = 58.4,
                            ["min"] = 41.2,
                            ["max"] = 60.1
                        },
                        ["frameTimeMs"] = new JObject
                        {
                            ["avg"] = 17.1,
                            ["p95"] = 24.2,
                            ["p99"] = 49.7,
                            ["max"] = 72.5,
                            ["hitchCount"] = 2,
                            ["hitchThresholdMs"] = 50
                        },
                        ["memory"] = new JObject
                        {
                            ["monoUsedBytes"] = 10485760,
                            ["gcUsedBytes"] = 20971520,
                            ["totalReservedBytes"] = 104857600,
                            ["systemUsedBytes"] = 209715200,
                            ["graphicsDriverBytes"] = 31457280
                        },
                        ["gc"] = new JObject
                        {
                            ["collectionCount0Delta"] = 1,
                            ["allocatedBytesDelta"] = 5242880
                        },
                        ["rendering"] = new JObject
                        {
                            ["vSyncCount"] = 1,
                            ["targetFrameRate"] = 60,
                            ["graphicsDeviceType"] = "Direct3D11",
                            ["screenWidth"] = 1920,
                            ["screenHeight"] = 1080,
                            ["renderPipeline"] = "Built-in"
                        },
                        ["recorderMode"] = "profilerRecorder",
                        ["warnings"] = new JArray("ProfilerRecorder counter is unavailable: Total Reserved Memory"),
                        ["unsupported"] = new JArray(new JObject
                        {
                            ["feature"] = "scriptFunctionTimings",
                            ["reason"] = "Function-level script timings are not available through the stable Runtime bridge."
                        })
                    }
                }
            };
        }

        private static JObject CreateFailedRuntimePerfCommandResult()
        {
            return new JObject
            {
                ["id"] = "cmd_perf_failed",
                ["success"] = false,
                ["exitCode"] = 1,
                ["command"] = "runtime perf --target latest --duration 15s --interval 100ms --hitchThresholdMs 50",
                ["error"] = "Runtime target was not found. Start a Player with AIBridgeRuntime, run runtime discover for LAN targets, or pass --url/--target.",
                ["data"] = new JObject
                {
                    ["transport"] = "http",
                    ["runtimeDirectory"] = ".aibridge/runtime",
                    ["target"] = "latest",
                    ["action"] = "runtime.perf"
                }
            };
        }

        private static void DialogButtonInfo_ExposesStrictLogicalChoices()
        {
            var close = DialogService.CreateButtonInfo("button:close", "Close", true);
            AssertEqual("cancel", close.choice, "Close should map to cancel.");
            AssertContains(close.choices, "cancel", "Close choices should include cancel.");

            var discard = DialogService.CreateButtonInfo("button:discard", "Don't Save", true);
            AssertEqual("discard", discard.choice, "Don't Save should map to discard.");
            AssertContains(discard.choices, "discard", "Don't Save choices should include discard.");

            var unknown = DialogService.CreateButtonInfo("button:custom", "Maybe Later", true);
            AssertEqual(null, unknown.choice, "Unknown text must not become a fake logical choice.");
            AssertEqual(null, unknown.choices, "Unknown text should require --button exact text.");
        }

        private static void DialogButtonInfo_DoesNotExposeChoicesForDisabledButtons()
        {
            var disabledCancel = DialogService.CreateButtonInfo("button:disabledCancel", "Cancel", false);

            AssertTrue(!disabledCancel.enabled, "Disabled button should keep enabled=false.");
            AssertEqual(null, disabledCancel.choice, "Disabled button must not expose a clickable logical choice.");
            AssertEqual(null, disabledCancel.choices, "Disabled button must not expose clickable choices.");
        }

        private static void SelectButton_FindsUniqueMatchAcrossDialogs()
        {
            var dialogs = new List<DialogInfo>
            {
                CreateDialog("dialog:first", DialogService.CreateButtonInfo("button:ok", "OK", true)),
                CreateDialog("dialog:second", DialogService.CreateButtonInfo("button:cancel", "Cancel", true))
            };

            var selection = DialogService.SelectButton(dialogs, "cancel", null, null);

            AssertTrue(selection.Success, "Unique cancel should be selected across dialogs.");
            AssertEqual("dialog:second", selection.Dialog.id, "The matching dialog should be selected.");
            AssertEqual("button:cancel", selection.Button.id, "The matching button should be selected.");
        }

        private static void SelectButton_IgnoresDisabledButtons()
        {
            var dialogs = new List<DialogInfo>
            {
                CreateDialog("dialog:first", DialogService.CreateButtonInfo("button:disabledCancel", "Cancel", false))
            };

            var choiceSelection = DialogService.SelectButton(dialogs, "cancel", null, null);
            AssertTrue(!choiceSelection.Success, "Disabled cancel must not match --choice cancel.");
            AssertEqual("dialog_button_not_found", choiceSelection.ErrorCode, "Disabled choice should be reported as not found.");

            var buttonSelection = DialogService.SelectButton(dialogs, null, "Cancel", null);
            AssertTrue(!buttonSelection.Success, "Disabled cancel must not match --button Cancel.");
            AssertEqual("dialog_button_not_found", buttonSelection.ErrorCode, "Disabled button text should be reported as not found.");
        }

        private static void SelectButton_RejectsAmbiguousChoiceAcrossDialogs()
        {
            var dialogs = new List<DialogInfo>
            {
                CreateDialog("dialog:first", DialogService.CreateButtonInfo("button:firstCancel", "Cancel", true)),
                CreateDialog("dialog:second", DialogService.CreateButtonInfo("button:secondCancel", "Close", true))
            };

            var selection = DialogService.SelectButton(dialogs, "cancel", null, null);

            AssertTrue(!selection.Success, "Ambiguous cancel must fail.");
            AssertEqual("dialog_button_ambiguous", selection.ErrorCode, "Ambiguous cancel should be reported explicitly.");
        }

        private static void SelectButton_RespectsExplicitDialogId()
        {
            var dialogs = new List<DialogInfo>
            {
                CreateDialog("dialog:first", DialogService.CreateButtonInfo("button:firstCancel", "Cancel", true)),
                CreateDialog("dialog:second", DialogService.CreateButtonInfo("button:secondCancel", "Close", true))
            };

            var selection = DialogService.SelectButton(dialogs, "cancel", null, "dialog:second");

            AssertTrue(selection.Success, "Explicit dialog id should disambiguate cancel.");
            AssertEqual("dialog:second", selection.Dialog.id, "Explicit dialog id should be respected.");
            AssertEqual("button:secondCancel", selection.Button.id, "Explicit dialog button should be selected.");
        }

        private static void BatchDialogAutoClickPlan_PreservesTargetKind()
        {
            var plan = BatchDialogAutoClickPlan.Parse(
                "dialog click --choice cancel\n" +
                "dialog click --button \"Don't Save\"\n" +
                "dialog click ok | yes | \"Don't Save\"\n");

            AssertEqual(3, plan.Rules.Count, "All dialog click rules should parse.");

            var choiceTarget = plan.Rules[0].Targets[0];
            AssertEqual("cancel", choiceTarget.Value, "--choice value should parse.");
            AssertEqual("choice", choiceTarget.Kind, "--choice target kind should be preserved.");
            AssertTrue(choiceTarget.AllowsChoiceMatch(), "--choice should allow choice matching.");
            AssertTrue(!choiceTarget.AllowsButtonMatch(), "--choice should not allow button-text matching.");

            var buttonTarget = plan.Rules[1].Targets[0];
            AssertEqual("Don't Save", buttonTarget.Value, "--button value should parse.");
            AssertEqual("button", buttonTarget.Kind, "--button target kind should be preserved.");
            AssertTrue(!buttonTarget.AllowsChoiceMatch(), "--button should not allow choice matching.");
            AssertTrue(buttonTarget.AllowsButtonMatch(), "--button should allow button-text matching.");

            foreach (var target in plan.Rules[2].Targets)
            {
                AssertEqual("any", target.Kind, "Unqualified alternatives should keep compatibility with both matching modes.");
                AssertTrue(target.AllowsChoiceMatch(), "Unqualified target should allow choice matching.");
                AssertTrue(target.AllowsButtonMatch(), "Unqualified target should allow button-text matching.");
            }
        }

        private static void CodeIndex_UnsupportedAction_ReturnsUnsupportedAction()
        {
            var result = ExecuteCodeIndex("references", new Dictionary<string, string>(), 1000);

            AssertEqual(false, result.Value<bool>("success"), "Unsupported action should fail.");
            AssertEqual("unsupported_action", result.Value<string>("errorCode"), "Unsupported action should return unsupported_action.");
        }

        private static void CodeIndex_Help_OnlyListsLightweightActions()
        {
            var help = new CodeIndexCommandBuilder().GetHelp();

            AssertContains(help, "  symbol", "Code Index help should include symbol.");
            AssertContains(help, "  definition", "Code Index help should include definition.");
            AssertTrue(help.IndexOf("  status", StringComparison.OrdinalIgnoreCase) < 0, "Code Index help should not include status.");
            AssertTrue(help.IndexOf("  doctor", StringComparison.OrdinalIgnoreCase) < 0, "Code Index help should not include doctor.");
            AssertTrue(help.IndexOf("  warmup", StringComparison.OrdinalIgnoreCase) < 0, "Code Index help should not include warmup.");
            AssertTrue(help.IndexOf("  reset", StringComparison.OrdinalIgnoreCase) < 0, "Code Index help should not include reset.");
            AssertTrue(help.IndexOf("  build_snapshot", StringComparison.OrdinalIgnoreCase) < 0, "Code Index help should not include build_snapshot.");
            AssertTrue(help.IndexOf("warmup-mode", StringComparison.OrdinalIgnoreCase) < 0, "Code Index help should not expose warmup-mode.");
            AssertTrue(help.IndexOf("  references", StringComparison.OrdinalIgnoreCase) < 0, "Code Index help should not include references.");
            AssertTrue(help.IndexOf("  implementations", StringComparison.OrdinalIgnoreCase) < 0, "Code Index help should not include implementations.");
            AssertTrue(help.IndexOf("  derived", StringComparison.OrdinalIgnoreCase) < 0, "Code Index help should not include derived.");
            AssertTrue(help.IndexOf("  callers", StringComparison.OrdinalIgnoreCase) < 0, "Code Index help should not include callers.");
            AssertTrue(help.IndexOf("  diagnostics", StringComparison.OrdinalIgnoreCase) < 0, "Code Index help should not include diagnostics.");
            AssertTrue(help.IndexOf("  batch", StringComparison.OrdinalIgnoreCase) < 0, "Code Index help should not include batch.");
        }

        private static void CodeIndex_DefinitionSourceLocationArguments_RequireQuery()
        {
            var result = ExecuteCodeIndex(
                "definition",
                new Dictionary<string, string>
                {
                    ["file"] = "Assets/Scripts/Foo.cs",
                    ["line"] = "42",
                    ["column"] = "17"
                },
                1000);

            AssertEqual(false, result.Value<bool>("success"), "Definition with source location arguments should fail.");
            AssertEqual("invalid_arguments", result.Value<string>("errorCode"), "Definition source location arguments should be rejected as invalid arguments.");
            AssertEqual("definition now requires --query", result.Value<string>("error"), "Definition should require --query.");
        }

        private static void CodeIndex_InternalActions_AreNotShownInHelp()
        {
            var builder = new CodeIndexCommandBuilder();

            AssertEqual(builder.GetHelp(), builder.GetHelp("status"), "Internal status help should fall back to public help.");
            AssertEqual(builder.GetHelp(), builder.GetHelp("doctor"), "Internal doctor help should fall back to public help.");
            AssertEqual(builder.GetHelp(), builder.GetHelp("warmup"), "Internal warmup help should fall back to public help.");
            AssertEqual(builder.GetHelp(), builder.GetHelp("reset"), "Internal reset help should fall back to public help.");
            AssertEqual(builder.GetHelp(), builder.GetHelp("build_snapshot"), "Internal build_snapshot help should fall back to public help.");
        }

        private static JObject ExecuteCodeIndex(string action, Dictionary<string, string> options, int timeout)
        {
            var previousRoot = Environment.GetEnvironmentVariable("UNITY_PROJECT_ROOT");
            var previousDirectory = Directory.GetCurrentDirectory();
            var projectRoot = Path.Combine(Path.GetTempPath(), "AIBridgeCLI.CodeIndex.Tests." + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(projectRoot);
                Environment.SetEnvironmentVariable("UNITY_PROJECT_ROOT", projectRoot);
                Directory.SetCurrentDirectory(projectRoot);
                ResetPathHelperCache();

                var writer = new StringWriter();
                var originalOut = Console.Out;
                try
                {
                    Console.SetOut(writer);
                    CodeIndexCommand.Execute(action, options, timeout, false, OutputMode.Raw);
                }
                finally
                {
                    Console.SetOut(originalOut);
                }

                var output = writer.ToString().Trim();
                if (string.IsNullOrWhiteSpace(output))
                {
                    throw new InvalidOperationException("CodeIndexCommand produced no JSON output.");
                }

                return JObject.Parse(output);
            }
            finally
            {
                Environment.SetEnvironmentVariable("UNITY_PROJECT_ROOT", previousRoot);
                Directory.SetCurrentDirectory(previousDirectory);
                ResetPathHelperCache();
                if (Directory.Exists(projectRoot))
                {
                    Directory.Delete(projectRoot, true);
                }
            }
        }

        private static DialogInfo CreateDialog(string id, params DialogButtonInfo[] buttons)
        {
            return new DialogInfo
            {
                id = id,
                title = id,
                buttons = new List<DialogButtonInfo>(buttons)
            };
        }

        private static void AssertContains(List<string> values, string expected, string message)
        {
            if (values == null)
            {
                throw new InvalidOperationException(message);
            }

            foreach (var value in values)
            {
                if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            throw new InvalidOperationException(message);
        }

        private static void AssertContains(string value, string expected, string message)
        {
            if (value == null || value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(message + " Expected text: " + expected);
            }
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(message + " Expected: " + expected + ", actual: " + actual);
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void ResetPathHelperCache()
        {
            var field = typeof(AIBridgeCLI.Core.PathHelper).GetField("_exchangeDir", BindingFlags.Static | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(null, null);
            }
        }
    }
}
