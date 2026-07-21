using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIBridge.Runtime.Internal;
using AIBridgeCLI.Commands;
using AIBridgeCLI.Core;
using AIBridgeCLI.Workflow;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args != null
                    && args.Length == 2
                    && string.Equals(args[0], "--bounded-output-chars", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(args[1], out var characterCount))
                {
                    AssertBoundedStream(characterCount, 1000);
                    Console.WriteLine("Bounded output probe passed.");
                    return 0;
                }

                WorkflowReport_IncludesRuntimePerformanceEvidence();
                WorkflowReport_IncludesFailedRuntimePerformanceEvidence();
                ArtifactRequiredGate_MatchesSemanticKind();
                AtomicFile_WriteTextAtomic_ReplacesExistingFileAndRemovesTemp();
                CommandSender_TryGetResult_RetriesIncompleteJson();
                AssetSearch_PositionalKeywordShortcut_MapsToKeyword();
                AssetSearch_PositionalKeywordShortcut_RejectsDuplicateKeyword();
                AssetSearch_Help_ListsPositionalKeywordUsage();
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
                CommandRegistry_DoesNotExposeExec();
                BoundedOutputReader_CapsLargeStreamsWithoutPreallocatingOutput();
                BoundedOutputReader_CapsLongLinesWithoutWaitingForNewline();
                WorkflowCommandLine_AcceptsCompactJsonAndRejectsTruncatedOutput();
                DotnetDiagnostics_BoundsRetainedItemsAndPreservesCounts();
                DotnetDiagnostics_AppliesFiltersBeforeRetention();
                EditorInstancePaths_UsesEnvOverrideAndStableProjectKey();
                EditorInstancePaths_PrefersExternalThenLegacyCandidates();
                Console.WriteLine("AIBridgeCLI tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static void CommandRegistry_DoesNotExposeExec()
        {
            CommandRegistry.Initialize();
            foreach (var type in CommandRegistry.GetTypes())
            {
                AssertTrue(!string.Equals(type, "exec", StringComparison.OrdinalIgnoreCase), "Removed exec command must not remain registered.");
            }

            AssertTrue(
                CommandRegistry.GetGlobalHelp().IndexOf(Environment.NewLine + "  exec", StringComparison.OrdinalIgnoreCase) < 0,
                "Global help must not list the removed exec command.");
        }

        private static void BoundedOutputReader_CapsLargeStreamsWithoutPreallocatingOutput()
        {
            AssertBoundedStream(64L * 1024L * 1024L, 1000);
            AssertBoundedStream(512L * 1024L * 1024L, 1000);
        }

        private static void AssertBoundedStream(long characterCount, int captureLimit)
        {
            using (var reader = new RepeatingTextReader(characterCount, 'x'))
            {
                var result = BoundedProcessOutputReader.ReadAsync(reader, captureLimit).GetAwaiter().GetResult();
                AssertEqual(characterCount, result.CharsRead, "Bounded reader should count the complete external stream.");
                AssertEqual(captureLimit, result.Text.Length, "Bounded reader should retain only the configured prefix.");
                AssertTrue(result.Truncated, "Large external output should be marked truncated.");
            }
        }

        private static void BoundedOutputReader_CapsLongLinesWithoutWaitingForNewline()
        {
            var observedLineLength = -1;
            var observedLineTruncated = false;
            using (var reader = new RepeatingTextReader(128L * 1024L, 'y'))
            {
                var result = BoundedProcessOutputReader.ReadAsync(
                    reader,
                    1000,
                    32768,
                    (line, truncated) =>
                    {
                        observedLineLength = line.Length;
                        observedLineTruncated = truncated;
                    }).GetAwaiter().GetResult();

                AssertEqual(32768, observedLineLength, "Long line parsing should retain only the configured line prefix.");
                AssertTrue(observedLineTruncated, "Long line parsing should report truncation.");
                AssertEqual(1L, result.TruncatedLineCount, "Long line parsing should count the truncated line.");
            }
        }

        private static void DotnetDiagnostics_BoundsRetainedItemsAndPreservesCounts()
        {
            var collector = new DotnetBuildCommand.DotnetBuildDiagnosticCollector(
                "C:\\project",
                false,
                false,
                Array.Empty<string>(),
                Array.Empty<string>());

            for (var i = 0; i < 1005; i++)
            {
                collector.AddLine($"C:\\project\\Assets\\Test{i}.cs(1,1): error CS1000: failure", false);
            }

            var result = new DotnetBuildResult();
            collector.ApplyTo(result);
            AssertEqual(1005, result.TotalErrorCount, "All parsed errors should contribute to the total count.");
            AssertEqual(1000, result.Errors.Count, "Retained errors should respect the fixed item limit.");
            AssertEqual(5, result.OmittedErrorCount, "Overflow errors should be reported as omitted.");
            AssertEqual(1005, collector.UnfilteredErrorCount, "Build success checks should use the complete unfiltered error count.");
        }

        private static void WorkflowCommandLine_AcceptsCompactJsonAndRejectsTruncatedOutput()
        {
            var compact = WorkflowCommandLine.Execute("workflow list", 10000, 1000000);
            AssertTrue(compact.Success, "Compact nested CLI JSON should remain parseable.");
            AssertTrue(!compact.StdoutTruncated && !compact.StderrTruncated, "Compact nested CLI output should not be truncated.");
            AssertTrue(compact.Result["data"] != null, "Compact nested CLI output should retain parsed JSON data.");

            var truncated = WorkflowCommandLine.Execute("--help", 10000, 64);
            AssertTrue(!truncated.Success, "Truncated nested CLI output must fail the workflow step.");
            AssertTrue(truncated.StdoutTruncated || truncated.StderrTruncated, "Truncated nested CLI output should expose stream metadata.");
            AssertContains(truncated.Error, "capture limit", "Truncated workflow output should report the bounded capture limit.");
        }

        private static void DotnetDiagnostics_AppliesFiltersBeforeRetention()
        {
            var collector = new DotnetBuildCommand.DotnetBuildDiagnosticCollector(
                "C:\\project",
                true,
                true,
                new[] { "ThirdParty" },
                Array.Empty<string>());

            collector.AddLine("C:\\project\\ThirdParty\\Ignored.cs(1,1): error CS1000: ignored", false);
            collector.AddLine("C:\\project\\Assets\\Kept.cs(1,1): error CS1001: kept", false);
            collector.AddLine("C:\\project\\Assets\\Hidden.cs(1,1): warning CS1002: hidden", false);

            var result = new DotnetBuildResult();
            collector.ApplyTo(result);
            AssertEqual(2, result.TotalErrorCount, "Filtered diagnostics should remain visible in total counts.");
            AssertEqual(1, result.FilteredErrorCount, "Excluded error should increment the filtered count.");
            AssertEqual(1, result.Errors.Count, "Only the non-filtered error should be retained.");
            AssertEqual(1, result.FilteredWarningCount, "Hidden warning should increment the filtered count.");
            AssertEqual(0, result.Warnings.Count, "Hidden warnings should not be retained.");
            AssertEqual(1, collector.UnfilteredErrorCount, "Only non-filtered errors should affect the visible failure count.");
        }

        private sealed class RepeatingTextReader : TextReader
        {
            private long _remaining;
            private readonly char _character;

            public RepeatingTextReader(long characterCount, char character)
            {
                _remaining = characterCount;
                _character = character;
            }

            public override Task<int> ReadAsync(char[] buffer, int index, int count)
            {
                var read = (int)Math.Min(_remaining, count);
                for (var i = 0; i < read; i++)
                {
                    buffer[index + i] = _character;
                }

                _remaining -= read;
                return Task.FromResult(read);
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

        private static void AtomicFile_WriteTextAtomic_ReplacesExistingFileAndRemovesTemp()
        {
            var directory = Path.Combine(Path.GetTempPath(), "AIBridgeCLI.Atomic.Tests." + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "result.json");
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, "old", new UTF8Encoding(false));

                AIBridgeAtomicFile.WriteTextAtomic(path, "new", new UTF8Encoding(false));

                AssertEqual("new", File.ReadAllText(path, Encoding.UTF8), "Atomic write should replace the final file.");
                AssertEqual(0, Directory.GetFiles(directory, "*.tmp.*").Length, "Atomic write should remove temporary files.");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        private static void CommandSender_TryGetResult_RetriesIncompleteJson()
        {
            var previousRoot = Environment.GetEnvironmentVariable("UNITY_PROJECT_ROOT");
            var previousDirectory = Directory.GetCurrentDirectory();
            var projectRoot = Path.Combine(Path.GetTempPath(), "AIBridgeCLI.ResultRead.Tests." + Guid.NewGuid().ToString("N"));
            var commandId = "cmd_result_retry";
            Thread writer = null;
            try
            {
                Directory.CreateDirectory(projectRoot);
                Environment.SetEnvironmentVariable("UNITY_PROJECT_ROOT", projectRoot);
                Directory.SetCurrentDirectory(projectRoot);
                ResetPathHelperCache();
                PathHelper.EnsureDirectoriesExist();

                var resultPath = Path.Combine(projectRoot, ".aibridge", "results", commandId + ".json");
                File.WriteAllText(resultPath, "{\"id\":\"" + commandId, new UTF8Encoding(false));

                writer = new Thread(() =>
                {
                    Thread.Sleep(40);
                    AIBridgeAtomicFile.WriteTextAtomic(
                        resultPath,
                        "{\"id\":\"" + commandId + "\",\"success\":true}",
                        new UTF8Encoding(false));
                });
                writer.Start();

                var result = new CommandSender().TryGetResult(commandId);

                AssertTrue(result != null, "TryGetResult should retry while JSON is incomplete.");
                AssertTrue(result.success, "TryGetResult should return the completed result.");
                AssertTrue(!File.Exists(resultPath), "TryGetResult should delete the consumed result file.");
                AssertEqual(0, Directory.GetFiles(Path.GetDirectoryName(resultPath), "*.tmp.*").Length, "Result retry should not leave temp files.");
            }
            finally
            {
                if (writer != null)
                {
                    writer.Join();
                }

                Environment.SetEnvironmentVariable("UNITY_PROJECT_ROOT", previousRoot);
                Directory.SetCurrentDirectory(previousDirectory);
                ResetPathHelperCache();
                if (Directory.Exists(projectRoot))
                {
                    Directory.Delete(projectRoot, true);
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

        private static void AssetSearch_PositionalKeywordShortcut_MapsToKeyword()
        {
            var parsed = ParseArgs("asset", "search", "BattleSettlementCityChallengeFailPanel", "--mode", "prefab", "--format", "paths");
            var request = new AssetCommandBuilder().Build(GetParsedAction(parsed), GetParsedOptions(parsed));

            AssertEqual("asset", GetParsedCommandType(parsed), "Command type should parse.");
            AssertEqual("search", GetParsedAction(parsed), "Action should parse.");
            AssertEqual("BattleSettlementCityChallengeFailPanel", request.@params["keyword"], "Positional asset search value should map to keyword.");
            AssertEqual("prefab", request.@params["mode"], "Mode option should be preserved.");
            AssertEqual("paths", request.@params["format"], "Format option should be preserved.");
            AssertEqual(0, GetParsedExtraArgs(parsed).Count, "Positional shortcut should not leave extra args.");
        }

        private static void AssetSearch_PositionalKeywordShortcut_RejectsDuplicateKeyword()
        {
            try
            {
                ParseArgs("asset", "search", "Player", "--keyword", "Enemy");
                throw new InvalidOperationException("Duplicate positional and explicit keyword should fail.");
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException as ArgumentException;
                if (inner == null || inner.Message.IndexOf("Use either `asset search <keyword>`", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw;
                }
            }
        }

        private static void AssetSearch_Help_ListsPositionalKeywordUsage()
        {
            var help = new AssetCommandBuilder().GetHelp("search");

            AssertContains(help, "AIBridgeCLI asset search [keyword] [options]", "Asset search help should show the positional keyword shortcut.");
            AssertContains(help, "AIBridgeCLI asset search --keyword <keyword> [options]", "Asset search help should still show explicit keyword usage.");
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

        private static object ParseArgs(params string[] args)
        {
            var method = typeof(AIBridgeCLI.Program).GetMethod("ParseArguments", BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new InvalidOperationException("ParseArguments method not found.");
            }

            return method.Invoke(null, new object[] { args });
        }

        private static string GetParsedCommandType(object parsed)
        {
            return (string)GetParsedProperty(parsed, "CommandType");
        }

        private static string GetParsedAction(object parsed)
        {
            return (string)GetParsedProperty(parsed, "Action");
        }

        private static Dictionary<string, string> GetParsedOptions(object parsed)
        {
            return (Dictionary<string, string>)GetParsedProperty(parsed, "Options");
        }

        private static List<string> GetParsedExtraArgs(object parsed)
        {
            return (List<string>)GetParsedProperty(parsed, "ExtraArgs");
        }

        private static object GetParsedProperty(object parsed, string name)
        {
            var property = parsed.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
            {
                throw new InvalidOperationException("ParsedArgs property not found: " + name);
            }

            return property.GetValue(parsed);
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

        private static void EditorInstancePaths_UsesEnvOverrideAndStableProjectKey()
        {
            var previous = Environment.GetEnvironmentVariable(AIBridgeEditorInstancePaths.StateDirEnvironment);
            var tempRoot = Path.Combine(Path.GetTempPath(), "aibridge-editor-instance-paths-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable(AIBridgeEditorInstancePaths.StateDirEnvironment, tempRoot);
                var projectRoot = Path.Combine(tempRoot, "Projects", "DemoGame");
                Directory.CreateDirectory(projectRoot);

                AssertEqual(
                    Path.GetFullPath(tempRoot),
                    Path.GetFullPath(AIBridgeEditorInstancePaths.GetStateRoot()),
                    "AIBRIDGE_STATE_DIR should override the state root.");

                var key = AIBridgeEditorInstancePaths.BuildProjectKey(projectRoot);
                AssertTrue(key.StartsWith("DemoGame_", StringComparison.Ordinal), "Project key should start with sanitized project folder name.");
                AssertEqual(key, AIBridgeEditorInstancePaths.BuildProjectKey(projectRoot), "Project key must be stable for the same project root.");

                var metadataPath = AIBridgeEditorInstancePaths.GetMetadataPath(projectRoot);
                AssertTrue(
                    metadataPath.StartsWith(Path.Combine(Path.GetFullPath(tempRoot), AIBridgeEditorInstancePaths.InstancesDirectoryName), StringComparison.OrdinalIgnoreCase),
                    "Metadata path should live under the overridden state root.");
                AssertTrue(
                    metadataPath.EndsWith(AIBridgeEditorInstancePaths.MetadataFileName, StringComparison.OrdinalIgnoreCase),
                    "Metadata path should end with editor-instance.json.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AIBridgeEditorInstancePaths.StateDirEnvironment, previous);
                TryDeleteDirectory(tempRoot);
            }
        }

        private static void EditorInstancePaths_PrefersExternalThenLegacyCandidates()
        {
            var previous = Environment.GetEnvironmentVariable(AIBridgeEditorInstancePaths.StateDirEnvironment);
            var tempRoot = Path.Combine(Path.GetTempPath(), "aibridge-editor-instance-candidates-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable(AIBridgeEditorInstancePaths.StateDirEnvironment, tempRoot);
                var projectRoot = Path.Combine(tempRoot, "Projects", "CandidateGame");
                Directory.CreateDirectory(projectRoot);

                var candidates = AIBridgeEditorInstancePaths.GetMetadataCandidatePaths(projectRoot);
                AssertEqual(2, candidates.Count, "Candidate list should include external and legacy paths.");
                AssertEqual(
                    AIBridgeEditorInstancePaths.GetMetadataPath(projectRoot),
                    candidates[0],
                    "External metadata path should be preferred.");
                AssertEqual(
                    AIBridgeEditorInstancePaths.GetLegacyMetadataPath(projectRoot),
                    candidates[1],
                    "Legacy .aibridge metadata path should be the fallback candidate.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AIBridgeEditorInstancePaths.StateDirEnvironment, previous);
                TryDeleteDirectory(tempRoot);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
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
