using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public class WorkflowRunStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        };

        public WorkflowRunStore(string runId = null)
        {
            RunId = string.IsNullOrWhiteSpace(runId) ? WorkflowPathHelper.GenerateRunId() : runId;
            RunDirectory = Path.Combine(WorkflowPathHelper.GetRunsDirectory(), RunId);
        }

        public string RunId { get; }
        public string RunDirectory { get; }

        public string ManifestPath => Path.Combine(RunDirectory, "manifest.json");
        public string InputsPath => Path.Combine(RunDirectory, "inputs.json");
        public string PhasesDirectory => Path.Combine(RunDirectory, "phases");
        public string StepsDirectory => Path.Combine(RunDirectory, "steps");
        public string ArtifactsDirectory => Path.Combine(RunDirectory, "artifacts");
        public string CommandResultsDirectory => Path.Combine(RunDirectory, "command-results");
        public string GatesDirectory => Path.Combine(RunDirectory, "gates");
        public string ReportPath => Path.Combine(RunDirectory, "report.md");

        public static WorkflowRunStore Open(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId))
            {
                throw new ArgumentException("Missing run id.");
            }

            return new WorkflowRunStore(runId);
        }

        public void EnsureDirectories()
        {
            Directory.CreateDirectory(RunDirectory);
            Directory.CreateDirectory(PhasesDirectory);
            Directory.CreateDirectory(StepsDirectory);
            Directory.CreateDirectory(ArtifactsDirectory);
            Directory.CreateDirectory(CommandResultsDirectory);
            Directory.CreateDirectory(GatesDirectory);
        }

        public void SaveInputs(JObject inputs)
        {
            EnsureDirectories();
            WriteJson(InputsPath, inputs ?? new JObject());
        }

        public void SaveManifest(WorkflowRunManifest manifest)
        {
            EnsureDirectories();
            WriteJson(ManifestPath, manifest);
        }

        public WorkflowRunManifest LoadManifest()
        {
            if (!File.Exists(ManifestPath))
            {
                throw new FileNotFoundException("Workflow run manifest was not found: " + ManifestPath);
            }

            return JsonConvert.DeserializeObject<WorkflowRunManifest>(File.ReadAllText(ManifestPath, Encoding.UTF8));
        }

        public string SaveCommandResult(string commandId, JObject result)
        {
            EnsureDirectories();
            var path = Path.Combine(CommandResultsDirectory, commandId + ".json");
            WriteJson(path, result);
            return path;
        }

        public void SavePhaseState(WorkflowPhaseState state)
        {
            EnsureDirectories();
            WriteJson(Path.Combine(PhasesDirectory, state.PhaseId + ".json"), state);
        }

        public void SaveStepState(WorkflowStepState state)
        {
            EnsureDirectories();
            WriteJson(Path.Combine(StepsDirectory, state.StepId + ".json"), state);
        }

        public void SaveGateResult(WorkflowGateResult result)
        {
            EnsureDirectories();
            WriteJson(Path.Combine(GatesDirectory, result.GateId + ".json"), result);
        }

        public string GetArtifactDirectory(string artifactId)
        {
            var directory = Path.Combine(ArtifactsDirectory, artifactId);
            Directory.CreateDirectory(directory);
            return directory;
        }

        public void SaveArtifact(WorkflowArtifactRef artifact)
        {
            var directory = GetArtifactDirectory(artifact.ArtifactId);
            WriteJson(Path.Combine(directory, "artifact.json"), artifact);
        }

        public void SaveReport(string markdown)
        {
            EnsureDirectories();
            File.WriteAllText(ReportPath, markdown ?? string.Empty, new UTF8Encoding(false));
        }

        private static void WriteJson(string path, object value)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonConvert.SerializeObject(value, JsonSettings), new UTF8Encoding(false));
        }
    }
}
