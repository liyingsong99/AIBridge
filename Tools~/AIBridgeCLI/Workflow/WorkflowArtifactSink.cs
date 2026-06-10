using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIBridgeCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public sealed class WorkflowAttachResult
    {
        public bool Attached { get; set; }
        public string RunId { get; set; }
        public string Source { get; set; }
        public string Error { get; set; }
    }

    public static class WorkflowArtifactSink
    {
        public static WorkflowAttachResult TryAttachCommandResult(
            Dictionary<string, string> options,
            string command,
            CommandResult result,
            int exitCode)
        {
            var context = WorkflowRunContext.Resolve(options);
            if (context == null)
            {
                return new WorkflowAttachResult { Attached = false };
            }

            try
            {
                AttachCommandResult(context.RunId, command, result, exitCode);
                return new WorkflowAttachResult
                {
                    Attached = true,
                    RunId = context.RunId,
                    Source = context.Source
                };
            }
            catch (Exception ex)
            {
                return new WorkflowAttachResult
                {
                    Attached = false,
                    RunId = context.RunId,
                    Source = context.Source,
                    Error = ex.Message
                };
            }
        }

        public static WorkflowArtifactRef AttachFile(
            string runId,
            string kind,
            string path,
            string sourceCommand,
            string summary,
            string schema = null,
            string stepId = null)
        {
            var store = WorkflowRunStore.Open(runId);
            var manifest = store.LoadManifest();
            var artifact = new WorkflowArtifactRef
            {
                ArtifactId = CreateArtifactId(kind),
                Kind = string.IsNullOrWhiteSpace(kind) ? "artifact" : kind,
                Path = WorkflowPathHelper.ToDisplayPath(path),
                SourcePath = WorkflowPathHelper.ToDisplayPath(path),
                SourceCommand = sourceCommand,
                StepId = stepId,
                Schema = schema,
                Summary = summary,
                ContentType = GuessContentType(path),
                Copied = false,
                CreatedAtUtc = DateTime.UtcNow.ToString("o")
            };

            AddArtifact(manifest, artifact);
            UpdateSummary(manifest);
            store.SaveArtifact(artifact);
            store.SaveManifest(manifest);
            return artifact;
        }

        public static void RefreshReportArtifact(WorkflowRunStore store, WorkflowRunManifest manifest, string sourceCommand)
        {
            var report = WorkflowReportWriter.WriteMarkdown(manifest);
            store.SaveReport(report);
            var existing = manifest.ArtifactRefs.FirstOrDefault(artifact =>
                string.Equals(artifact.Kind, "workflow-report", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ResolveDisplayPath(artifact.Path), ResolveDisplayPath(WorkflowPathHelper.ToDisplayPath(store.ReportPath)), StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = new WorkflowArtifactRef
                {
                    ArtifactId = "art_workflow_report_" + manifest.RunId,
                    Kind = "workflow-report",
                    Path = WorkflowPathHelper.ToDisplayPath(store.ReportPath),
                    SourceCommand = sourceCommand,
                    Summary = "Workflow markdown report.",
                    ContentType = "text/markdown",
                    Copied = true,
                    CreatedAtUtc = DateTime.UtcNow.ToString("o")
                };
                AddArtifact(manifest, existing);
                store.SaveArtifact(existing);
            }
            else
            {
                existing.SourceCommand = sourceCommand;
                existing.CreatedAtUtc = DateTime.UtcNow.ToString("o");
                store.SaveArtifact(existing);
            }

            UpdateSummary(manifest);
            store.SaveManifest(manifest);
        }

        internal static void UpdateSummary(WorkflowRunManifest manifest)
        {
            WorkflowRunInsight.UpdateSummary(manifest);
        }

        private static void AttachCommandResult(string runId, string command, CommandResult result, int exitCode)
        {
            if (result == null)
            {
                throw new ArgumentException("Missing command result.");
            }

            var store = WorkflowRunStore.Open(runId);
            var manifest = store.LoadManifest();
            var execution = CreateExecution(command, result, exitCode);
            var commandResultPath = store.SaveCommandResult(execution.CommandId, execution.Result);
            var collector = new WorkflowArtifactCollector(store);
            var artifacts = collector.CollectForCommand(command, execution, commandResultPath);

            var commandResult = new WorkflowCommandResultRef
            {
                CommandId = execution.CommandId,
                Command = command,
                Success = execution.Success,
                ExitCode = execution.ExitCode,
                ResultPath = WorkflowPathHelper.ToDisplayPath(commandResultPath),
                StartedAtUtc = execution.StartedAtUtc,
                EndedAtUtc = execution.EndedAtUtc
            };

            foreach (var artifact in artifacts)
            {
                commandResult.ArtifactIds.Add(artifact.ArtifactId);
                AddArtifact(manifest, artifact);
            }

            RemoveCommandResult(manifest, commandResult.CommandId);
            manifest.CommandResults.Add(commandResult);
            UpdateSummary(manifest);
            store.SaveManifest(manifest);
        }

        private static WorkflowCommandExecution CreateExecution(string command, CommandResult result, int exitCode)
        {
            var now = DateTime.UtcNow.ToString("o");
            var commandId = string.IsNullOrWhiteSpace(result.id) ? AIBridgeCLI.Core.PathHelper.GenerateCommandId() : result.id;
            var payload = JObject.FromObject(new
            {
                id = commandId,
                success = result.success,
                exitCode = exitCode,
                command = command,
                data = result.data,
                error = result.error,
                executionTime = result.executionTime,
                startedAtUtc = now,
                endedAtUtc = now
            }, JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));

            return new WorkflowCommandExecution
            {
                CommandId = commandId,
                Command = command,
                Success = result.success,
                ExitCode = exitCode,
                StartedAtUtc = now,
                EndedAtUtc = now,
                Error = result.error,
                Result = payload
            };
        }

        private static void AddArtifact(WorkflowRunManifest manifest, WorkflowArtifactRef artifact)
        {
            if (manifest.ArtifactRefs == null)
            {
                manifest.ArtifactRefs = new List<WorkflowArtifactRef>();
            }

            var existing = manifest.ArtifactRefs.FirstOrDefault(item =>
                string.Equals(item.ArtifactId, artifact.ArtifactId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                manifest.ArtifactRefs.Remove(existing);
            }

            manifest.ArtifactRefs.Add(artifact);
        }

        private static void RemoveCommandResult(WorkflowRunManifest manifest, string commandId)
        {
            if (manifest.CommandResults == null)
            {
                manifest.CommandResults = new List<WorkflowCommandResultRef>();
                return;
            }

            var existing = manifest.CommandResults.FirstOrDefault(item =>
                string.Equals(item.CommandId, commandId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                manifest.CommandResults.Remove(existing);
            }
        }

        private static string CreateArtifactId(string kind)
        {
            var normalized = (kind ?? "artifact").Replace("-", "_");
            return "art_" + normalized + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static string GuessContentType(string path)
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                return "text/markdown";
            }

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                return "application/json";
            }

            if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
            {
                return "text/javascript";
            }

            if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sh", StringComparison.OrdinalIgnoreCase))
            {
                return "text/plain";
            }

            return "application/octet-stream";
        }

        private static string ResolveDisplayPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }
    }
}
