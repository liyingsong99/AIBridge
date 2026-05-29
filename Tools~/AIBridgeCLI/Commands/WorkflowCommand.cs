using System;
using System.Collections.Generic;
using System.IO;
using AIBridgeCLI.Core;
using AIBridgeCLI.Workflow;
using Newtonsoft.Json;

namespace AIBridgeCLI.Commands
{
    public static class WorkflowCommand
    {
        public static int Execute(string action, Dictionary<string, string> options, int timeoutMs, OutputMode outputMode)
        {
            var normalizedAction = string.IsNullOrWhiteSpace(action) ? "list" : action.Trim();
            switch (normalizedAction.ToLowerInvariant())
            {
                case "list":
                    return ExecuteList(outputMode);
                case "validate":
                    return ExecuteValidate(options, outputMode);
                case "plan":
                    return ExecutePlan(options, outputMode);
                case "init":
                    return ExecuteInit(options, outputMode);
                case "run-cli":
                    return ExecuteRunCli(options, timeoutMs, outputMode);
                case "status":
                    return ExecuteStatus(options, outputMode);
                case "report":
                    return ExecuteReport(options, outputMode);
                case "clean":
                    return ExecuteClean(options, outputMode);
                default:
                    return PrintResult(new CommandResult
                    {
                        success = false,
                        error = "Unknown workflow action: " + normalizedAction
                    }, outputMode);
            }
        }

        public static string GetHelp()
        {
            return new WorkflowCommandBuilder().GetHelp();
        }

        private static int ExecuteList(OutputMode outputMode)
        {
            var recipes = WorkflowRecipeLoader.ListRecipes();
            return PrintResult(new CommandResult
            {
                success = true,
                data = new
                {
                    count = recipes.Count,
                    builtinDirectory = WorkflowPathHelper.ToDisplayPath(WorkflowPathHelper.GetBuiltInRecipesDirectory()),
                    projectDirectory = WorkflowPathHelper.ToDisplayPath(WorkflowPathHelper.GetProjectRecipesDirectory()),
                    recipes = recipes
                }
            }, outputMode);
        }

        private static int ExecuteValidate(Dictionary<string, string> options, OutputMode outputMode)
        {
            var recipePath = GetFileOrRecipe(options);
            var result = WorkflowValidator.ValidateFile(recipePath);
            return PrintResult(new CommandResult
            {
                success = result.Success,
                error = result.Success ? null : string.Join("; ", result.Errors),
                data = result
            }, outputMode);
        }

        private static int ExecutePlan(Dictionary<string, string> options, OutputMode outputMode)
        {
            var recipePath = GetFileOrRecipe(options);
            recipePath = WorkflowPathHelper.ResolveRecipePath(recipePath);
            var doc = WorkflowRecipeLoader.Load(recipePath);
            var validation = WorkflowValidator.ValidateDocument(doc);
            if (!validation.Success)
            {
                return PrintResult(new CommandResult
                {
                    success = false,
                    error = string.Join("; ", validation.Errors),
                    data = validation
                }, outputMode);
            }

            var format = GetOption(options, "format", "json");
            if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(WorkflowPlanWriter.WriteMarkdown(doc.Recipe, doc.Path));
                return 0;
            }

            return PrintResult(new CommandResult
            {
                success = true,
                data = JsonConvert.DeserializeObject(WorkflowPlanWriter.WriteJson(doc.Recipe))
            }, outputMode);
        }

        private static int ExecuteInit(Dictionary<string, string> options, OutputMode outputMode)
        {
            var recipe = GetOption(options, "recipe", null) ?? GetFileOrRecipe(options);
            var output = GetOption(options, "output", null);
            var targetPath = WorkflowRecipeLoader.SaveRecipe(recipe, output);
            return PrintResult(new CommandResult
            {
                success = true,
                data = new
                {
                    recipe = recipe,
                    output = WorkflowPathHelper.ToDisplayPath(targetPath)
                }
            }, outputMode);
        }

        private static int ExecuteRunCli(Dictionary<string, string> options, int timeoutMs, OutputMode outputMode)
        {
            var recipePath = GetFileOrRecipe(options);
            recipePath = WorkflowPathHelper.ResolveRecipePath(recipePath);
            var inputs = GetOption(options, "inputs", null);
            var resume = GetOption(options, "resume", null);
            var rerun = GetOption(options, "rerun", null);
            var runner = new WorkflowCliRunner(timeoutMs);
            var manifest = runner.Run(recipePath, inputs, resume, rerun);
            return PrintResult(new CommandResult
            {
                success = manifest.Status == "passed" || manifest.Status == "partial",
                error = manifest.Status == "failed" || manifest.Status == "blocked" ? "Workflow run ended with status: " + manifest.Status : null,
                data = new
                {
                    runId = manifest.RunId,
                    status = manifest.Status,
                    runDirectory = WorkflowPathHelper.ToDisplayPath(Path.Combine(WorkflowPathHelper.GetRunsDirectory(), manifest.RunId)),
                    manifest = manifest
                }
            }, outputMode);
        }

        private static int ExecuteStatus(Dictionary<string, string> options, OutputMode outputMode)
        {
            var runId = GetRequiredOption(options, "run");
            var store = WorkflowRunStore.Open(runId);
            var manifest = store.LoadManifest();
            return PrintResult(new CommandResult
            {
                success = true,
                data = manifest
            }, outputMode);
        }

        private static int ExecuteReport(Dictionary<string, string> options, OutputMode outputMode)
        {
            var runId = GetRequiredOption(options, "run");
            var store = WorkflowRunStore.Open(runId);
            var manifest = store.LoadManifest();
            var markdown = WorkflowReportWriter.WriteMarkdown(manifest);
            store.SaveReport(markdown);

            var format = GetOption(options, "format", "json");
            if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(markdown);
                return 0;
            }

            return PrintResult(new CommandResult
            {
                success = true,
                data = new
                {
                    runId = runId,
                    reportPath = WorkflowPathHelper.ToDisplayPath(store.ReportPath),
                    manifest = manifest
                }
            }, outputMode);
        }

        private static int ExecuteClean(Dictionary<string, string> options, OutputMode outputMode)
        {
            var settings = WorkflowSettings.Load();
            var olderThan = GetOption(options, "older-than", settings.AutoCleanOlderThan ?? "30d");
            var dryRun = GetBool(options, "dry-run", true);
            var keepFailed = GetBool(options, "keep-failed", settings.KeepFailed);
            var keepLatest = GetInt(options, "keep-latest", settings.KeepLatest);
            var maxDelete = GetInt(options, "max-delete", settings.MaxDeletePerRun);
            var result = WorkflowCleaner.Clean(new WorkflowCleanOptions
            {
                OlderThan = olderThan,
                DryRun = dryRun,
                KeepFailed = keepFailed,
                KeepLatest = keepLatest,
                MaxDeletePerRun = maxDelete
            });

            if (GetBool(options, "save-settings", false))
            {
                settings.AutoCleanEnabled = GetBool(options, "auto-clean", settings.AutoCleanEnabled);
                settings.AutoCleanOlderThan = olderThan;
                settings.KeepFailed = keepFailed;
                settings.KeepLatest = keepLatest;
                settings.MaxDeletePerRun = maxDelete;
                settings.Save();
            }

            return PrintResult(new CommandResult
            {
                success = true,
                data = new
                {
                    clean = result,
                    settings = settings,
                    settingsPath = WorkflowPathHelper.ToDisplayPath(WorkflowSettings.GetSettingsPath())
                }
            }, outputMode);
        }

        private static int PrintResult(CommandResult result, OutputMode outputMode)
        {
            OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false);
            return result.success ? 0 : 1;
        }

        private static string GetFileOrRecipe(Dictionary<string, string> options)
        {
            if (options.TryGetValue("file", out var file) && !string.IsNullOrWhiteSpace(file))
            {
                return file;
            }

            if (options.TryGetValue("recipe", out var recipe) && !string.IsNullOrWhiteSpace(recipe))
            {
                return recipe;
            }

            throw new ArgumentException("Missing required option: --file or --recipe.");
        }

        private static string GetRequiredOption(Dictionary<string, string> options, string key)
        {
            if (options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            throw new ArgumentException("Missing required option: --" + key + ".");
        }

        private static string GetOption(Dictionary<string, string> options, string key, string defaultValue)
        {
            return options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;
        }

        private static bool GetBool(Dictionary<string, string> options, string key, bool defaultValue)
        {
            if (!options.TryGetValue(key, out var value))
            {
                return defaultValue;
            }

            return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        private static int GetInt(Dictionary<string, string> options, string key, int defaultValue)
        {
            if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return int.TryParse(value, out var intValue) ? intValue : defaultValue;
        }

    }
}
