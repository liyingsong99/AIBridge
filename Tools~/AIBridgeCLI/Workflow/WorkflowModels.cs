using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public class WorkflowRecipe
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("inputs")]
        public JObject Inputs { get; set; } = new JObject();

        [JsonProperty("requiredSkills")]
        public List<string> RequiredSkills { get; set; } = new List<string>();

        [JsonProperty("phases")]
        public List<WorkflowPhase> Phases { get; set; } = new List<WorkflowPhase>();

        [JsonProperty("gates")]
        public List<WorkflowGate> Gates { get; set; } = new List<WorkflowGate>();

        [JsonProperty("artifacts")]
        public List<WorkflowArtifactDeclaration> Artifacts { get; set; } = new List<WorkflowArtifactDeclaration>();
    }

    public class WorkflowPhase
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("dependsOn")]
        public List<string> DependsOn { get; set; } = new List<string>();

        [JsonProperty("itemSource")]
        public string ItemSource { get; set; }

        [JsonProperty("requiredSkills")]
        public List<string> RequiredSkills { get; set; } = new List<string>();

        [JsonProperty("releaseSkillsAfter")]
        public List<string> ReleaseSkillsAfter { get; set; } = new List<string>();

        [JsonProperty("steps")]
        public List<WorkflowStep> Steps { get; set; } = new List<WorkflowStep>();
    }

    public class WorkflowStep
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("command")]
        public string Command { get; set; }

        [JsonProperty("requiredSkills")]
        public List<string> RequiredSkills { get; set; } = new List<string>();

        [JsonProperty("releaseSkillsAfter")]
        public List<string> ReleaseSkillsAfter { get; set; } = new List<string>();

        [JsonProperty("outputs")]
        public List<string> Outputs { get; set; } = new List<string>();
    }

    public class WorkflowGate
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("required")]
        public bool Required { get; set; } = true;

        [JsonProperty("threshold")]
        public JObject Threshold { get; set; }

        [JsonProperty("artifactKind")]
        public string ArtifactKind { get; set; }

        [JsonProperty("stepId")]
        public string StepId { get; set; }

        [JsonProperty("schema")]
        public string Schema { get; set; }

        [JsonProperty("min")]
        public int? Min { get; set; }

        [JsonProperty("allow")]
        public List<string> Allow { get; set; } = new List<string>();

        [JsonProperty("evidenceRefs")]
        public List<string> EvidenceRefs { get; set; } = new List<string>();
    }

    public class WorkflowArtifactDeclaration
    {
        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("required")]
        public bool Required { get; set; }
    }

    public class WorkflowRunManifest
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonProperty("runId")]
        public string RunId { get; set; }

        [JsonProperty("recipeName")]
        public string RecipeName { get; set; }

        [JsonProperty("recipePath")]
        public string RecipePath { get; set; }

        [JsonProperty("projectRoot")]
        public string ProjectRoot { get; set; }

        [JsonProperty("startedAtUtc")]
        public string StartedAtUtc { get; set; }

        [JsonProperty("endedAtUtc")]
        public string EndedAtUtc { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("phaseStates")]
        public List<WorkflowPhaseState> PhaseStates { get; set; } = new List<WorkflowPhaseState>();

        [JsonProperty("stepStates")]
        public List<WorkflowStepState> StepStates { get; set; } = new List<WorkflowStepState>();

        [JsonProperty("artifactRefs")]
        public List<WorkflowArtifactRef> ArtifactRefs { get; set; } = new List<WorkflowArtifactRef>();

        [JsonProperty("commandResults")]
        public List<WorkflowCommandResultRef> CommandResults { get; set; } = new List<WorkflowCommandResultRef>();

        [JsonProperty("gateResults")]
        public List<WorkflowGateResult> GateResults { get; set; } = new List<WorkflowGateResult>();

        [JsonProperty("summary")]
        public WorkflowRunSummary Summary { get; set; } = new WorkflowRunSummary();
    }

    public class WorkflowRunSummary
    {
        [JsonProperty("cliCommandCount")]
        public int CliCommandCount { get; set; }

        [JsonProperty("agentStepCount")]
        public int AgentStepCount { get; set; }

        [JsonProperty("artifactCount")]
        public int ArtifactCount { get; set; }

        [JsonProperty("failedGateCount")]
        public int FailedGateCount { get; set; }

        [JsonProperty("autoCleanCandidateCount")]
        public int AutoCleanCandidateCount { get; set; }

        [JsonProperty("autoCleanDeletedCount")]
        public int AutoCleanDeletedCount { get; set; }

        [JsonProperty("autoCleanError")]
        public string AutoCleanError { get; set; }
    }

    public class WorkflowPhaseState
    {
        [JsonProperty("phaseId")]
        public string PhaseId { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("startedAtUtc")]
        public string StartedAtUtc { get; set; }

        [JsonProperty("endedAtUtc")]
        public string EndedAtUtc { get; set; }

        [JsonProperty("stepIds")]
        public List<string> StepIds { get; set; } = new List<string>();

        [JsonProperty("artifactIds")]
        public List<string> ArtifactIds { get; set; } = new List<string>();

        [JsonProperty("requiredSkills")]
        public List<string> RequiredSkills { get; set; } = new List<string>();

        [JsonProperty("releaseSkillsAfter")]
        public List<string> ReleaseSkillsAfter { get; set; } = new List<string>();

        [JsonProperty("error")]
        public string Error { get; set; }
    }

    public class WorkflowStepState
    {
        [JsonProperty("stepId")]
        public string StepId { get; set; }

        [JsonProperty("phaseId")]
        public string PhaseId { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("command")]
        public string Command { get; set; }

        [JsonProperty("commandResultRef")]
        public string CommandResultRef { get; set; }

        [JsonProperty("artifactIds")]
        public List<string> ArtifactIds { get; set; } = new List<string>();

        [JsonProperty("requiredSkills")]
        public List<string> RequiredSkills { get; set; } = new List<string>();

        [JsonProperty("releaseSkillsAfter")]
        public List<string> ReleaseSkillsAfter { get; set; } = new List<string>();

        [JsonProperty("startedAtUtc")]
        public string StartedAtUtc { get; set; }

        [JsonProperty("endedAtUtc")]
        public string EndedAtUtc { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }
    }

    public class WorkflowArtifactRef
    {
        [JsonProperty("artifactId")]
        public string ArtifactId { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("semanticKind")]
        public string SemanticKind { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("sourcePath")]
        public string SourcePath { get; set; }

        [JsonProperty("sourceCommand")]
        public string SourceCommand { get; set; }

        [JsonProperty("sourceCommandId")]
        public string SourceCommandId { get; set; }

        [JsonProperty("stepId")]
        public string StepId { get; set; }

        [JsonProperty("schema")]
        public string Schema { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        [JsonProperty("contentType")]
        public string ContentType { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("copied")]
        public bool Copied { get; set; }

        [JsonProperty("createdAtUtc")]
        public string CreatedAtUtc { get; set; }
    }

    public class WorkflowCommandResultRef
    {
        [JsonProperty("commandId")]
        public string CommandId { get; set; }

        [JsonProperty("command")]
        public string Command { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("exitCode")]
        public int ExitCode { get; set; }

        [JsonProperty("resultPath")]
        public string ResultPath { get; set; }

        [JsonProperty("artifactIds")]
        public List<string> ArtifactIds { get; set; } = new List<string>();

        [JsonProperty("startedAtUtc")]
        public string StartedAtUtc { get; set; }

        [JsonProperty("endedAtUtc")]
        public string EndedAtUtc { get; set; }
    }

    public class WorkflowGateResult
    {
        [JsonProperty("gateId")]
        public string GateId { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("required")]
        public bool Required { get; set; }

        [JsonProperty("threshold")]
        public JObject Threshold { get; set; }

        [JsonProperty("evidenceRefs")]
        public List<string> EvidenceRefs { get; set; } = new List<string>();

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    public class WorkflowValidationResult
    {
        [JsonProperty("success")]
        public bool Success => Errors.Count == 0;

        [JsonProperty("recipeName")]
        public string RecipeName { get; set; }

        [JsonProperty("file")]
        public string File { get; set; }

        [JsonProperty("errors")]
        public List<string> Errors { get; } = new List<string>();

        [JsonProperty("warnings")]
        public List<string> Warnings { get; } = new List<string>();
    }

    public class WorkflowExternalResult
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("schema")]
        public string Schema { get; set; }

        [JsonProperty("stepId")]
        public string StepId { get; set; }

        [JsonProperty("artifactId")]
        public string ArtifactId { get; set; }
    }

    public class WorkflowEvidenceRef
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("artifactPath")]
        public string ArtifactPath { get; set; }

        [JsonProperty("sourceCommand")]
        public string SourceCommand { get; set; }

        [JsonProperty("targetId")]
        public string TargetId { get; set; }

        [JsonProperty("createdAtUtc")]
        public string CreatedAtUtc { get; set; }
    }

    public class WorkflowCommandEvidence
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("command")]
        public string Command { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("exitCode")]
        public int? ExitCode { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("artifactIds")]
        public List<string> ArtifactIds { get; set; } = new List<string>();

        [JsonProperty("startedAtUtc")]
        public string StartedAtUtc { get; set; }

        [JsonProperty("endedAtUtc")]
        public string EndedAtUtc { get; set; }
    }

    public class WorkflowFinding
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("severity")]
        public string Severity { get; set; }

        [JsonProperty("file")]
        public string File { get; set; }

        [JsonProperty("line")]
        public int? Line { get; set; }

        [JsonProperty("claim")]
        public string Claim { get; set; }

        [JsonProperty("evidence")]
        public List<string> Evidence { get; set; } = new List<string>();

        [JsonProperty("repro")]
        public string Repro { get; set; }

        [JsonProperty("confidence")]
        public string Confidence { get; set; }
    }

    public class WorkflowVerdict
    {
        [JsonProperty("claimId")]
        public string ClaimId { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("evidenceRefs")]
        public List<string> EvidenceRefs { get; set; } = new List<string>();

        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("remainingRisk")]
        public string RemainingRisk { get; set; }
    }

    public class WorkflowPatchProposal
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("files")]
        public List<string> Files { get; set; } = new List<string>();

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("risk")]
        public string Risk { get; set; }

        [JsonProperty("validation")]
        public List<string> Validation { get; set; } = new List<string>();
    }

    public class WorkflowValidationResultArtifact
    {
        [JsonProperty("gate")]
        public string Gate { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("command")]
        public string Command { get; set; }

        [JsonProperty("evidence")]
        public List<string> Evidence { get; set; } = new List<string>();

        [JsonProperty("artifacts")]
        public List<string> Artifacts { get; set; } = new List<string>();
    }

    public class WorkflowRecipeListItem
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("source")]
        public string Source { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }
    }
}
