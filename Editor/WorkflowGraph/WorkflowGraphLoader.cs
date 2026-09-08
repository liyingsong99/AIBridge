using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AIBridge.Editor
{
    internal sealed class WorkflowGraphLoader
    {
        private const string BuiltInRecipeDirectory = "Packages/cn.lys.aibridge/Templates~/Workflows";
        private const string ProjectRecipeDirectory = ".aibridge/workflows/recipes";
        private const string WorkflowRootDirectory = ".aibridge/workflows";
        private const string ActiveRunFileName = "active-run.json";
        private const string WorkflowSkillName = "aibridge-development-workflow";
        private const string BranchDetailManifestRelativePath = "references/implementation-branch.manifest.json";

        private readonly string _projectRoot;

        public WorkflowGraphLoader(string projectRoot)
        {
            _projectRoot = projectRoot;
        }

        public WorkflowGraphDocument LoadRoutingGraph()
        {
            var manifestPath = ResolveRoutingGraphManifestPath();
            if (!string.IsNullOrEmpty(manifestPath) && File.Exists(manifestPath))
            {
                try
                {
                    return LoadRoutingGraphManifest(manifestPath);
                }
                catch
                {
                    // Manifest 是首选来源；读取失败时回退到 ProjectSettings，保证面板仍可打开。
                }
            }

            var workflowUi = AIBridgeProjectSettings.Instance.WorkflowUi;
            var document = new WorkflowGraphDocument
            {
                Title = "Routing Graph",
                Subtitle = "Preflight / Skill Routing",
                Mode = "routing",
                Summary = AIBridgeEditorText.T(
                    "Project workflow branches and the first-level handoff path.",
                    "项目 workflow 分支和一级交接路径。")
            };

            AddNode(document, "root", WorkflowGraphNodeKind.Root, "Root", "Task Entry", WorkflowGraphNodeState.Ready, "AIBridge task entry.", null);
            AddNode(document, "preflight", WorkflowGraphNodeKind.Preflight, "Preflight", "Skill Routing", WorkflowGraphNodeState.Ready, "Reads workflow preferences, selects the branch, and computes active Skills.", null);
            AddNode(document, "requirements", WorkflowGraphNodeKind.Condition, "Requirements Discussion", "When scope or risk is unclear", WorkflowGraphNodeState.Ready, "Used before the main branch when requirements, risks, or acceptance criteria are not locked.", null);
            AddNode(document, "selector", WorkflowGraphNodeKind.Selector, "Branch Selector", "Enabled workflow branches", WorkflowGraphNodeState.Ready, "Routes into a single enabled main branch.", null);

            SetNodeSemantic(document.FindNode("root"), "root", false);
            SetNodeSemantic(document.FindNode("preflight"), "preflight", false);
            SetNodeSemantic(document.FindNode("requirements"), "optional-guard", true);
            SetNodeSemantic(document.FindNode("selector"), "selector", false);

            AddBranchNode(document, "implementation", "Implementation", "Change-oriented", workflowUi.EnableImplementationBranch, "references/branches/implementation.md");
            AddBranchNode(document, "debug", "Debug", "Diagnosis-oriented", workflowUi.EnableDebugBranch, "references/branches/debug.md");
            AddBranchNode(document, "review", "Review", "Read-only risk review", workflowUi.EnableReviewBranch, "references/branches/review.md");
            AddBranchNode(document, "validation", "Validation", "Compile / logs / runtime", workflowUi.EnableValidationBranch, "references/branches/validation.md");
            AddBranchNode(document, "orchestration", "Orchestration", "Multi-step workflow", workflowUi.EnableOrchestrationBranch, "references/branches/orchestration.md");
            AddNode(document, "handoff", WorkflowGraphNodeKind.Handoff, "Mode Exit", "SkillHandoff", WorkflowGraphNodeState.NotStarted, "Passes compact summary, artifact refs, gate status, and open risks.", null);

            AddEdge(document, "root", "preflight", null);
            AddEdge(document, "preflight", "requirements", "on demand", WorkflowGraphEdgeKind.OptionalFlow);
            AddEdge(document, "requirements", "selector", "confirmed", WorkflowGraphEdgeKind.OptionalFlow);
            AddEdge(document, "preflight", "selector", "ready");
            AddEdge(document, "selector", "branch-implementation", "change");
            AddEdge(document, "selector", "branch-debug", "diagnose");
            AddEdge(document, "selector", "branch-review", "review");
            AddEdge(document, "selector", "branch-validation", "validate");
            AddEdge(document, "selector", "branch-orchestration", "recipe");
            AddEdge(document, "branch-implementation", "handoff", null);
            AddEdge(document, "branch-debug", "handoff", null);
            AddEdge(document, "branch-review", "handoff", null);
            AddEdge(document, "branch-validation", "handoff", null);
            AddEdge(document, "branch-orchestration", "handoff", null);

            AddWorkflowOptionDetails(document.FindNode("preflight"), workflowUi);
            return document;
        }

        public WorkflowGraphDocument LoadBranchDetailGraph(string branchId)
        {
            if (string.IsNullOrEmpty(branchId))
            {
                return CreateMessageDocument("Branch Detail Graph", "No branch selected.", null);
            }

            if (!string.Equals(branchId, "implementation", StringComparison.OrdinalIgnoreCase))
            {
                return CreateMessageDocument("Branch Detail Graph", "Branch detail is only available for the implementation branch in this version.", null);
            }

            var manifestPath = ResolveBranchDetailManifestPath();
            if (!string.IsNullOrEmpty(manifestPath) && File.Exists(manifestPath))
            {
                try
                {
                    return LoadBranchDetailGraphManifest(manifestPath);
                }
                catch
                {
                    // Manifest 是首选来源；失败时回退到内置结构，保证面板可用。
                }
            }

            return BuildImplementationBranchDetailDocument(null);
        }

        private string ResolveBranchDetailManifestPath()
        {
            var candidatePaths = new List<string>();
            var targets = AssistantIntegrationRegistry.GetTargets();
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null || !target.SupportsSkillDirectory)
                {
                    continue;
                }

                var manifestRelativePath = target.GetResolvedSiblingSkillFileRelativePath(_projectRoot, WorkflowSkillName);
                if (string.IsNullOrEmpty(manifestRelativePath))
                {
                    continue;
                }

                var manifestDirectory = Path.GetDirectoryName(manifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(manifestDirectory))
                {
                    continue;
                }

                AddCandidatePath(
                    candidatePaths,
                    Path.Combine(
                        _projectRoot,
                        manifestDirectory,
                        BranchDetailManifestRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            }

            AddCandidatePath(
                candidatePaths,
                Path.Combine(
                    _projectRoot,
                    AIBridgeProjectSettings.CanonicalSharedSkillRootDirectory.Replace('/', Path.DirectorySeparatorChar),
                    WorkflowSkillName,
                    BranchDetailManifestRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!string.IsNullOrEmpty(AIBridgeProjectSettings.LegacySharedSkillRootDirectory))
            {
                AddCandidatePath(
                    candidatePaths,
                    Path.Combine(
                        _projectRoot,
                        AIBridgeProjectSettings.LegacySharedSkillRootDirectory.Replace('/', Path.DirectorySeparatorChar),
                        WorkflowSkillName,
                        BranchDetailManifestRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            }

            for (var i = 0; i < candidatePaths.Count; i++)
            {
                var path = candidatePaths[i];
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private string ResolveRoutingGraphManifestPath()
        {
            var candidatePaths = new List<string>();
            var targets = AssistantIntegrationRegistry.GetTargets();
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null || !target.SupportsSkillDirectory)
                {
                    continue;
                }

                var manifestRelativePath = target.GetResolvedSiblingSkillFileRelativePath(_projectRoot, WorkflowSkillName);
                if (string.IsNullOrEmpty(manifestRelativePath))
                {
                    continue;
                }

                var manifestDirectory = Path.GetDirectoryName(manifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(manifestDirectory))
                {
                    continue;
                }

                AddCandidatePath(
                    candidatePaths,
                    Path.Combine(
                        _projectRoot,
                        manifestDirectory,
                        WorkflowPreferenceRenderer.GraphManifestRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            }

            AddCandidatePath(
                candidatePaths,
                Path.Combine(
                    _projectRoot,
                    AIBridgeProjectSettings.CanonicalSharedSkillRootDirectory.Replace('/', Path.DirectorySeparatorChar),
                    WorkflowSkillName,
                    WorkflowPreferenceRenderer.GraphManifestRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!string.IsNullOrEmpty(AIBridgeProjectSettings.LegacySharedSkillRootDirectory))
            {
                AddCandidatePath(
                    candidatePaths,
                    Path.Combine(
                        _projectRoot,
                        AIBridgeProjectSettings.LegacySharedSkillRootDirectory.Replace('/', Path.DirectorySeparatorChar),
                        WorkflowSkillName,
                        WorkflowPreferenceRenderer.GraphManifestRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            }

            for (var i = 0; i < candidatePaths.Count; i++)
            {
                var path = candidatePaths[i];
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private WorkflowGraphDocument LoadRoutingGraphManifest(string manifestPath)
        {
            var manifest = JsonUtility.FromJson<WorkflowGraphManifestData>(File.ReadAllText(manifestPath));
            var document = new WorkflowGraphDocument
            {
                Title = "Routing Graph",
                Subtitle = "workflow-graph.manifest.json",
                SourcePath = manifestPath,
                SourceRootPath = Path.GetDirectoryName(manifestPath),
                Mode = "routing",
                Summary = "Generated routing graph manifest."
            };

            if (manifest != null && manifest.nodes != null)
            {
                for (var i = 0; i < manifest.nodes.Length; i++)
                {
                    var node = manifest.nodes[i];
                    if (node == null || string.IsNullOrEmpty(node.id))
                    {
                        continue;
                    }

                    AddNode(
                        document,
                        node.id,
                        MapNodeKind(node.kind),
                        NormalizeEmpty(node.title, node.id),
                        node.subtitle,
                        node.enabled ? WorkflowGraphNodeState.Ready : WorkflowGraphNodeState.Disabled,
                        node.enabled ? "Generated routing node." : "Disabled in Workflow Options.",
                        node.source);

                    if (string.Equals(node.id, "requirements", StringComparison.OrdinalIgnoreCase))
                    {
                        SetNodeSemantic(document.FindNode(node.id), "optional-guard", true);
                    }
                }
            }

            if (manifest != null && manifest.edges != null)
            {
                for (var i = 0; i < manifest.edges.Length; i++)
                {
                    var edge = manifest.edges[i];
                    if (edge == null || string.IsNullOrEmpty(edge.from) || string.IsNullOrEmpty(edge.to))
                    {
                        continue;
                    }

                    AddEdge(document, edge.from, edge.to, edge.condition);
                }
            }

            var preflight = document.FindNode("preflight");
            if (preflight != null && manifest != null && manifest.generatedFrom != null)
            {
                preflight.Details.Add(new WorkflowGraphDetail("Assistant", manifest.generatedFrom.assistant));
                preflight.Details.Add(new WorkflowGraphDetail("Settings Hash", manifest.generatedFrom.settingsHash));
            }

            MarkRoutingOptionalEdges(document);
            return document;
        }

        private WorkflowGraphDocument LoadBranchDetailGraphManifest(string manifestPath)
        {
            var manifest = JsonUtility.FromJson<WorkflowBranchManifestData>(File.ReadAllText(manifestPath));
            if (manifest == null)
            {
                return BuildImplementationBranchDetailDocument(manifestPath);
            }

            var document = new WorkflowGraphDocument
            {
                Title = NormalizeEmpty(manifest.title, "Implementation Branch"),
                Subtitle = NormalizeEmpty(manifest.subtitle, "Branch Detail"),
                Mode = "branch-detail",
                SourcePath = manifestPath,
                SourceRootPath = Path.GetDirectoryName(manifestPath),
                Summary = NormalizeEmpty(manifest.summary, "Structured branch detail manifest.")
            };

            if (manifest.nodes != null)
            {
                for (var i = 0; i < manifest.nodes.Length; i++)
                {
                    var manifestNode = manifest.nodes[i];
                    if (manifestNode == null || string.IsNullOrEmpty(manifestNode.id))
                    {
                        continue;
                    }

                    AddNode(
                        document,
                        manifestNode.id,
                        MapNodeKind(manifestNode.kind),
                        NormalizeEmpty(manifestNode.title, manifestNode.id),
                        manifestNode.subtitle,
                        MapStatusToState(manifestNode.state),
                        manifestNode.description,
                        manifestNode.source);

                    var node = document.FindNode(manifestNode.id);
                    if (node == null)
                    {
                        continue;
                    }

                    node.LayoutColumn = manifestNode.column;
                    node.LayoutOrder = manifestNode.order;
                    node.LayoutRow = manifestNode.row;
                    node.ParentNodeId = manifestNode.parent;
                    node.SemanticRole = NormalizeEmpty(manifestNode.semanticRole, string.Empty);
                    node.IsOptional = manifestNode.optional;

                    if (manifestNode.details != null)
                    {
                        for (var detailIndex = 0; detailIndex < manifestNode.details.Length; detailIndex++)
                        {
                            var detail = manifestNode.details[detailIndex];
                            if (detail == null || string.IsNullOrEmpty(detail.label))
                            {
                                continue;
                            }

                            node.Details.Add(new WorkflowGraphDetail(detail.label, detail.value));
                        }
                    }
                }
            }

            if (manifest.edges != null)
            {
                for (var i = 0; i < manifest.edges.Length; i++)
                {
                    var edge = manifest.edges[i];
                    if (edge == null || string.IsNullOrEmpty(edge.from) || string.IsNullOrEmpty(edge.to))
                    {
                        continue;
                    }

                    AddEdge(document, edge.from, edge.to, edge.label, MapEdgeKind(edge.kind));
                }
            }

            var root = document.FindNode("implementation-root");
            if (root != null)
            {
                root.Details.Add(new WorkflowGraphDetail("Branch", NormalizeEmpty(manifest.branchId, "implementation")));
                root.Details.Add(new WorkflowGraphDetail("Manifest", Path.GetFileName(manifestPath)));
            }

            return document;
        }

        public List<WorkflowRecipeSummary> ListRecipes()
        {
            var recipes = new List<WorkflowRecipeSummary>();
            AddRecipeSummaries(recipes, Path.Combine(_projectRoot, BuiltInRecipeDirectory), "builtin");
            AddRecipeSummaries(recipes, Path.Combine(_projectRoot, ProjectRecipeDirectory), "project");
            return recipes.OrderBy(item => item.Source).ThenBy(item => item.Name).ToList();
        }

        public WorkflowGraphDocument LoadRecipeGraph(WorkflowRecipeSummary summary)
        {
            if (summary == null || string.IsNullOrEmpty(summary.Path))
            {
                return CreateMessageDocument("Recipe Graph", "No recipe selected.", null);
            }

            try
            {
                var recipe = LoadRecipe(summary.Path);
                return BuildRecipeDocument(recipe, summary);
            }
            catch (Exception ex)
            {
                return CreateMessageDocument("Recipe Graph", "Failed to load recipe: " + ex.Message, summary.Path);
            }
        }

        public WorkflowRunSummaryData LoadActiveRunSummary()
        {
            var activeRunPath = Path.Combine(_projectRoot, WorkflowRootDirectory, ActiveRunFileName);
            if (!File.Exists(activeRunPath))
            {
                return null;
            }

            try
            {
                var pointer = JsonUtility.FromJson<WorkflowActiveRunPointerData>(File.ReadAllText(activeRunPath));
                if (pointer == null || string.IsNullOrEmpty(pointer.runId))
                {
                    return null;
                }

                var manifestPath = Path.Combine(_projectRoot, WorkflowRootDirectory, "runs", pointer.runId, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    return null;
                }

                var manifest = JsonUtility.FromJson<WorkflowRunManifestData>(File.ReadAllText(manifestPath));
                if (manifest == null)
                {
                    return null;
                }

                return new WorkflowRunSummaryData
                {
                    RunId = manifest.runId,
                    RecipeName = string.IsNullOrEmpty(manifest.recipeName) ? pointer.recipeName : manifest.recipeName,
                    Status = manifest.status,
                    ManifestPath = manifestPath,
                    ReportPath = Path.Combine(_projectRoot, WorkflowRootDirectory, "runs", manifest.runId, "report.md"),
                    PhaseCount = manifest.phaseStates == null ? 0 : manifest.phaseStates.Length,
                    StepCount = manifest.stepStates == null ? 0 : manifest.stepStates.Length,
                    ArtifactCount = manifest.artifactRefs == null ? 0 : manifest.artifactRefs.Length,
                    GateCount = manifest.gateResults == null ? 0 : manifest.gateResults.Length,
                    FailedGateCount = CountGateStatus(manifest, "failed"),
                    BlockedGateCount = CountGateStatus(manifest, "blocked"),
                    ExternalSkippedCount = manifest.summary == null ? 0 : manifest.summary.externalSkippedCount,
                    MissingExternalImportCount = manifest.summary == null ? 0 : manifest.summary.missingExternalImportCount
                };
            }
            catch
            {
                return null;
            }
        }

        public WorkflowGraphDocument LoadActiveRunGraph()
        {
            var summary = LoadActiveRunSummary();
            if (summary == null)
            {
                var document = CreateMessageDocument("Run Graph", "No active workflow run found.", null);
                document.Warning = AIBridgeEditorText.T(
                    "Start or attach a workflow run before using this view.",
                    "使用此视图前，请先启动或附加一个 workflow run。");
                return document;
            }

            var manifest = JsonUtility.FromJson<WorkflowRunManifestData>(File.ReadAllText(summary.ManifestPath));
            var runDocument = new WorkflowGraphDocument
            {
                Title = "Run Graph",
                Subtitle = summary.RecipeName,
                Mode = "run",
                SourcePath = summary.ManifestPath,
                SourceRootPath = Path.GetDirectoryName(summary.ManifestPath),
                Summary = "Run " + summary.RunId + " / " + NormalizeEmpty(summary.Status, "unknown")
            };

            AddNode(runDocument, "run", WorkflowGraphNodeKind.Root, summary.RecipeName, "Run " + summary.RunId, MapStatusToState(summary.Status), "Active workflow run manifest.", summary.ManifestPath);
            AddNode(runDocument, "artifacts", WorkflowGraphNodeKind.Artifact, "Artifacts", summary.ArtifactCount.ToString(), summary.ArtifactCount > 0 ? WorkflowGraphNodeState.Passed : WorkflowGraphNodeState.NotStarted, "Collected run artifacts.", summary.ManifestPath);
            AddNode(runDocument, "gates", WorkflowGraphNodeKind.Gate, "Gates", summary.GateCount.ToString(), ResolveGateState(summary), "Required and optional gate results.", summary.ManifestPath);
            AddNode(runDocument, "external", WorkflowGraphNodeKind.Handoff, "External Steps", summary.ExternalSkippedCount.ToString(), summary.ExternalSkippedCount > 0 ? WorkflowGraphNodeState.WaitingExternal : WorkflowGraphNodeState.Passed, "agent/manual steps require external execution and import.", summary.ManifestPath);

            AddEdge(runDocument, "run", "artifacts", null);
            AddEdge(runDocument, "run", "gates", null);
            AddEdge(runDocument, "run", "external", null);

            var runNode = runDocument.FindNode("run");
            if (runNode != null)
            {
                runNode.Details.Add(new WorkflowGraphDetail("Status", summary.Status));
                runNode.Details.Add(new WorkflowGraphDetail("Phases", summary.PhaseCount.ToString()));
                runNode.Details.Add(new WorkflowGraphDetail("Steps", summary.StepCount.ToString()));
            }

            AddPhaseStateNodes(runDocument, manifest, summary.ManifestPath);
            return runDocument;
        }

        private WorkflowGraphDocument BuildRecipeDocument(WorkflowRecipeData recipe, WorkflowRecipeSummary summary)
        {
            var document = new WorkflowGraphDocument
            {
                Title = "Recipe Graph",
                Subtitle = string.IsNullOrEmpty(recipe.title) ? recipe.name : recipe.title,
                Mode = "recipe",
                SourcePath = summary.Path,
                SourceRootPath = Path.GetDirectoryName(summary.Path),
                Summary = NormalizeEmpty(recipe.description, summary.Source + " recipe")
            };

            AddNode(document, "recipe", WorkflowGraphNodeKind.Root, NormalizeEmpty(recipe.title, recipe.name), recipe.name, WorkflowGraphNodeState.Ready, recipe.description, summary.Path);
            AddRecipeDetails(document.FindNode("recipe"), recipe, summary);

            if (recipe.phases != null)
            {
                for (var i = 0; i < recipe.phases.Length; i++)
                {
                    var phase = recipe.phases[i];
                    var phaseId = "phase-" + NormalizeId(phase.id, i);
                    var kind = MapPhaseKind(phase.type);
                    AddNode(document, phaseId, kind, NormalizeEmpty(phase.id, "phase-" + i), NormalizeEmpty(phase.type, "serial"), WorkflowGraphNodeState.NotStarted, phase.description, summary.Path);
                    SetNodeLayout(document.FindNode(phaseId), 1, i, "recipe");
                    AddPhaseDetails(document.FindNode(phaseId), phase);

                    if (phase.dependsOn != null && phase.dependsOn.Length > 0)
                    {
                        for (var depIndex = 0; depIndex < phase.dependsOn.Length; depIndex++)
                        {
                            AddEdge(document, "phase-" + NormalizeId(phase.dependsOn[depIndex], depIndex), phaseId, "depends", WorkflowGraphEdgeKind.Dependency);
                        }
                    }
                    else
                    {
                        AddEdge(document, "recipe", phaseId, null);
                    }

                    if (phase.steps != null)
                    {
                        string previousStepId = null;
                        for (var stepIndex = 0; stepIndex < phase.steps.Length; stepIndex++)
                        {
                            var step = phase.steps[stepIndex];
                            var stepId = phaseId + "-step-" + NormalizeId(step.id, stepIndex);
                            var state = IsExternalStep(step.kind) ? WorkflowGraphNodeState.WaitingExternal : WorkflowGraphNodeState.NotStarted;
                            AddNode(document, stepId, WorkflowGraphNodeKind.Action, NormalizeEmpty(step.id, "step-" + stepIndex), NormalizeEmpty(step.kind, "step"), state, step.description, summary.Path);
                            SetNodeLayout(document.FindNode(stepId), 2, i * 100 + stepIndex, phaseId);
                            AddStepDetails(document.FindNode(stepId), step);

                            if (stepIndex == 0)
                            {
                                AddEdge(document, phaseId, stepId, null);
                            }
                            else if (!string.IsNullOrEmpty(previousStepId))
                            {
                                AddEdge(document, previousStepId, stepId, null);
                            }

                            previousStepId = stepId;
                        }
                    }
                }
            }

            if (recipe.gates != null)
            {
                for (var i = 0; i < recipe.gates.Length; i++)
                {
                    var gate = recipe.gates[i];
                    var gateId = "gate-" + NormalizeId(gate.id, i);
                    var state = gate.required ? WorkflowGraphNodeState.Ready : WorkflowGraphNodeState.NotStarted;
                    AddNode(document, gateId, WorkflowGraphNodeKind.Gate, NormalizeEmpty(gate.id, "gate-" + i), NormalizeEmpty(gate.kind, "gate"), state, gate.required ? "Required gate." : "Optional gate.", summary.Path);
                    SetNodeLayout(document.FindNode(gateId), 3, i, ResolveGateParentNodeId(recipe, gate));
                    AddGateDetails(document.FindNode(gateId), gate);

                    var parentNodeId = ResolveGateParentNodeId(recipe, gate);
                    if (!string.IsNullOrEmpty(parentNodeId))
                    {
                        AddEdge(document, parentNodeId, gateId, gate.required ? "required" : "optional", WorkflowGraphEdgeKind.Gate);
                    }
                    else
                    {
                        AddEdge(document, "recipe", gateId, gate.required ? "required" : "optional", WorkflowGraphEdgeKind.Gate);
                    }
                }
            }

            return document;
        }

        private WorkflowGraphDocument BuildImplementationBranchDetailDocument(string sourcePath)
        {
            var document = new WorkflowGraphDocument
            {
                Title = "Implementation Branch",
                Subtitle = "Branch Detail",
                Mode = "branch-detail",
                SourcePath = sourcePath,
                SourceRootPath = string.IsNullOrEmpty(sourcePath) ? ResolveWorkflowSkillReferencesRoot() : Path.GetDirectoryName(sourcePath),
                Summary = AIBridgeEditorText.T(
                    "Decision-oriented execution steps for change tasks. This graph is editor-only and does not participate in runtime token routing.",
                    "面向改动任务的实施分支执行步骤图。它只用于编辑器展示，不参与运行时 token 路由。")
            };

            AddNode(document, "implementation-root", WorkflowGraphNodeKind.Root, "Implementation Branch", "Change Task Entry", WorkflowGraphNodeState.Ready, "Entry node for implementation tasks.", "references/branches/implementation.md");
            AddNode(document, "implementation-locate", WorkflowGraphNodeKind.Action, "Locate Real Path", "Find actual code or asset entry", WorkflowGraphNodeState.Ready, "Locate the real implementation path before changing anything.", "references/branches/implementation.md");
            AddNode(document, "implementation-risk", WorkflowGraphNodeKind.Condition, "Risk Gate", "Boundary / compatibility / acceptance", WorkflowGraphNodeState.Ready, "Confirm the change is still within the agreed scope.", "references/risk-gates.md");
            AddNode(document, "implementation-modify", WorkflowGraphNodeKind.Action, "Modify Worktree", "Scoped implementation", WorkflowGraphNodeState.Ready, "Change only the real control point and keep the diff narrow.", "references/branches/implementation.md");
            AddNode(document, "implementation-verify", WorkflowGraphNodeKind.Gate, "Verify Result", "Compile and required evidence", WorkflowGraphNodeState.Ready, "Run the default verification path for the project after the change.", "references/checklist.md");
            AddNode(document, "implementation-handoff", WorkflowGraphNodeKind.Handoff, "Handoff", "Result / risk / next step", WorkflowGraphNodeState.NotStarted, "Summarize what changed, what was verified, and what remains.", "references/checklist.md");
            AddNode(document, "implementation-editor", WorkflowGraphNodeKind.Condition, "Editor Generation", "Only for complex one-off editor tasks", WorkflowGraphNodeState.NotStarted, "Optional branch for complex editor-side generation work.", "references/editor-generation.md");
            AddNode(document, "implementation-runtime", WorkflowGraphNodeKind.Condition, "Runtime Evidence", "Only when task explicitly needs runtime proof", WorkflowGraphNodeState.NotStarted, "Optional verification branch for runtime or UI evidence.", "references/branches/validation.md");

            SetNodeLayout(document.FindNode("implementation-root"), 0, 0, null, 0);
            SetNodeLayout(document.FindNode("implementation-locate"), 1, 0, "implementation-root", 0);
            SetNodeLayout(document.FindNode("implementation-risk"), 2, 0, "implementation-root", 0);
            SetNodeLayout(document.FindNode("implementation-modify"), 3, 0, "implementation-root", 0);
            SetNodeLayout(document.FindNode("implementation-verify"), 4, 0, "implementation-root", 0);
            SetNodeLayout(document.FindNode("implementation-handoff"), 5, 0, "implementation-root", 0);
            SetNodeLayout(document.FindNode("implementation-editor"), 3, 1, "implementation-root", 1);
            SetNodeLayout(document.FindNode("implementation-runtime"), 4, 1, "implementation-root", 1);

            SetNodeSemantic(document.FindNode("implementation-root"), "branch-root", false);
            SetNodeSemantic(document.FindNode("implementation-locate"), "mandatory-step", false);
            SetNodeSemantic(document.FindNode("implementation-risk"), "mandatory-gate", false);
            SetNodeSemantic(document.FindNode("implementation-modify"), "mandatory-step", false);
            SetNodeSemantic(document.FindNode("implementation-verify"), "mandatory-gate", false);
            SetNodeSemantic(document.FindNode("implementation-handoff"), "handoff", false);
            SetNodeSemantic(document.FindNode("implementation-editor"), "optional-branch", true);
            SetNodeSemantic(document.FindNode("implementation-runtime"), "optional-branch", true);

            AddEdge(document, "implementation-root", "implementation-locate", null);
            AddEdge(document, "implementation-locate", "implementation-risk", null);
            AddEdge(document, "implementation-risk", "implementation-modify", null);
            AddEdge(document, "implementation-modify", "implementation-verify", null);
            AddEdge(document, "implementation-verify", "implementation-handoff", null);
            AddEdge(document, "implementation-risk", "implementation-editor", "complex editor task", WorkflowGraphEdgeKind.OptionalFlow);
            AddEdge(document, "implementation-editor", "implementation-modify", "generated", WorkflowGraphEdgeKind.OptionalFlow);
            AddEdge(document, "implementation-verify", "implementation-runtime", "runtime needed", WorkflowGraphEdgeKind.OptionalFlow);
            AddEdge(document, "implementation-runtime", "implementation-handoff", "evidence ready", WorkflowGraphEdgeKind.OptionalFlow);

            AddImplementationBranchDetails(document);
            return document;
        }

        private static void AddRecipeSummaries(List<WorkflowRecipeSummary> recipes, string directory, string source)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var files = Directory.GetFiles(directory, "*.aibridge-workflow.json", SearchOption.TopDirectoryOnly);
            for (var i = 0; i < files.Length; i++)
            {
                var path = files[i];
                try
                {
                    var recipe = LoadRecipe(path);
                    recipes.Add(new WorkflowRecipeSummary
                    {
                        Name = NormalizeEmpty(recipe.name, Path.GetFileNameWithoutExtension(path)),
                        Title = recipe.title,
                        Description = recipe.description,
                        Version = recipe.version,
                        Source = source,
                        Path = path,
                        PhaseCount = recipe.phases == null ? 0 : recipe.phases.Length,
                        GateCount = recipe.gates == null ? 0 : recipe.gates.Length
                    });
                }
                catch (Exception ex)
                {
                    recipes.Add(new WorkflowRecipeSummary
                    {
                        Name = Path.GetFileNameWithoutExtension(path),
                        Source = source,
                        Path = path,
                        Description = "Invalid recipe: " + ex.Message
                    });
                }
            }
        }

        private static WorkflowRecipeData LoadRecipe(string path)
        {
            return JsonUtility.FromJson<WorkflowRecipeData>(File.ReadAllText(path));
        }

        private static void AddNode(
            WorkflowGraphDocument document,
            string id,
            WorkflowGraphNodeKind kind,
            string title,
            string subtitle,
            WorkflowGraphNodeState state,
            string description,
            string sourcePath)
        {
            var node = new WorkflowGraphNode
            {
                Id = id,
                Kind = kind,
                Title = title,
                Subtitle = subtitle,
                State = state,
                StatusText = state.ToString(),
                Description = description,
                SourcePath = sourcePath,
                SemanticRole = string.Empty,
                LayoutColumn = -1,
                LayoutOrder = -1
            };
            document.Nodes.Add(node);
        }

        private static void AddEdge(WorkflowGraphDocument document, string from, string to, string label)
        {
            AddEdge(document, from, to, label, WorkflowGraphEdgeKind.Flow);
        }

        private static void AddEdge(WorkflowGraphDocument document, string from, string to, string label, WorkflowGraphEdgeKind kind)
        {
            document.Edges.Add(new WorkflowGraphEdge
            {
                FromNodeId = from,
                ToNodeId = to,
                Label = label,
                Kind = kind
            });
        }

        private static void AddBranchNode(WorkflowGraphDocument document, string id, string title, string subtitle, bool enabled, string sourcePath)
        {
            AddNode(
                document,
                "branch-" + id,
                WorkflowGraphNodeKind.Action,
                title,
                subtitle,
                enabled ? WorkflowGraphNodeState.Ready : WorkflowGraphNodeState.Disabled,
                enabled ? "Enabled workflow branch." : "Disabled in Workflow Options.",
                sourcePath);
        }

        private static void AddWorkflowOptionDetails(WorkflowGraphNode node, AIBridgeProjectSettings.WorkflowUiSettingsData workflowUi)
        {
            if (node == null || workflowUi == null)
            {
                return;
            }

            node.Details.Add(new WorkflowGraphDetail("Validation", AIBridgeProjectSettings.NormalizeWorkflowValidationLevel(workflowUi.DefaultValidationLevel)));
            node.Details.Add(new WorkflowGraphDetail("Runtime Evidence", workflowUi.PreferRuntimeEvidence ? "preferred" : "on demand"));
            node.Details.Add(new WorkflowGraphDetail("Code Index", workflowUi.PreferCodeIndexGuidance ? "preferred" : "not preferred"));
        }

        private static void AddRecipeDetails(WorkflowGraphNode node, WorkflowRecipeData recipe, WorkflowRecipeSummary summary)
        {
            if (node == null || recipe == null)
            {
                return;
            }

            node.Details.Add(new WorkflowGraphDetail("Name", recipe.name));
            node.Details.Add(new WorkflowGraphDetail("Version", recipe.version));
            node.Details.Add(new WorkflowGraphDetail("Source", summary == null ? null : summary.Source));
            node.Details.Add(new WorkflowGraphDetail("Phases", recipe.phases == null ? "0" : recipe.phases.Length.ToString()));
            node.Details.Add(new WorkflowGraphDetail("Gates", recipe.gates == null ? "0" : recipe.gates.Length.ToString()));
        }

        private static void AddPhaseDetails(WorkflowGraphNode node, WorkflowPhaseData phase)
        {
            if (node == null || phase == null)
            {
                return;
            }

            node.Details.Add(new WorkflowGraphDetail("Type", phase.type));
            node.Details.Add(new WorkflowGraphDetail("Depends On", Join(phase.dependsOn)));
            node.Details.Add(new WorkflowGraphDetail("Required Skills", Join(phase.requiredSkills)));
            node.Details.Add(new WorkflowGraphDetail("Release Skills", Join(phase.releaseSkillsAfter)));
            node.Details.Add(new WorkflowGraphDetail("Steps", phase.steps == null ? "0" : phase.steps.Length.ToString()));
        }

        private static void AddStepDetails(WorkflowGraphNode node, WorkflowStepData step)
        {
            if (node == null || step == null)
            {
                return;
            }

            node.Details.Add(new WorkflowGraphDetail("Kind", step.kind));
            node.Details.Add(new WorkflowGraphDetail("Role", step.role));
            node.Details.Add(new WorkflowGraphDetail("Command", step.command));
            node.Details.Add(new WorkflowGraphDetail("Outputs", Join(step.outputs)));
            node.Details.Add(new WorkflowGraphDetail("Required Skills", Join(step.requiredSkills)));
        }

        private static void AddImplementationBranchDetails(WorkflowGraphDocument document)
        {
            AddDetails(
                document.FindNode("implementation-root"),
                new WorkflowGraphDetail("Goal", "Change current worktree and verify"),
                new WorkflowGraphDetail("Source", "implementation.md"));

            AddDetails(
                document.FindNode("implementation-locate"),
                new WorkflowGraphDetail("Rule", "Locate real code path before editing"),
                new WorkflowGraphDetail("Why", "Avoid changing guessed or mirrored paths"));

            AddDetails(
                document.FindNode("implementation-risk"),
                new WorkflowGraphDetail("Check", "scope / compatibility / acceptance"),
                new WorkflowGraphDetail("Fallback", "Return to requirements or debug when preconditions are not met"));

            AddDetails(
                document.FindNode("implementation-modify"),
                new WorkflowGraphDetail("Rule", "Keep diff narrow"),
                new WorkflowGraphDetail("Tooling", "aibridge / code-index / prefab-patch / yaml-editing on demand"));

            AddDetails(
                document.FindNode("implementation-verify"),
                new WorkflowGraphDetail("Default", "compileAndLogs"),
                new WorkflowGraphDetail("Command", "$CLI compile unity -> $CLI get_logs --logType Error"));

            AddDetails(
                document.FindNode("implementation-handoff"),
                new WorkflowGraphDetail("Output", "changed files / verification / residual risk"),
                new WorkflowGraphDetail("Next", "validation branch when extra proof is needed"));

            AddDetails(
                document.FindNode("implementation-editor"),
                new WorkflowGraphDetail("Condition", "complex one-off editor C# task"),
                new WorkflowGraphDetail("Optional", "yes"));

            AddDetails(
                document.FindNode("implementation-runtime"),
                new WorkflowGraphDetail("Condition", "runtime or UI evidence explicitly required"),
                new WorkflowGraphDetail("Optional", "yes"));
        }

        private static void AddGateDetails(WorkflowGraphNode node, WorkflowGateData gate)
        {
            if (node == null || gate == null)
            {
                return;
            }

            node.Details.Add(new WorkflowGraphDetail("Kind", gate.kind));
            node.Details.Add(new WorkflowGraphDetail("Required", gate.required.ToString()));
            node.Details.Add(new WorkflowGraphDetail("Step", gate.stepId));
            node.Details.Add(new WorkflowGraphDetail("Artifact Kind", gate.artifactKind));
            node.Details.Add(new WorkflowGraphDetail("Schema", gate.schema));
            node.Details.Add(new WorkflowGraphDetail("Min", gate.min.ToString()));
            node.Details.Add(new WorkflowGraphDetail("Allow", Join(gate.allow)));
        }

        private static void AddDetails(WorkflowGraphNode node, params WorkflowGraphDetail[] details)
        {
            if (node == null || details == null)
            {
                return;
            }

            for (var i = 0; i < details.Length; i++)
            {
                var detail = details[i];
                if (detail == null)
                {
                    continue;
                }

                node.Details.Add(detail);
            }
        }

        private static void AddPhaseStateNodes(WorkflowGraphDocument document, WorkflowRunManifestData manifest, string sourcePath)
        {
            if (manifest == null || manifest.phaseStates == null)
            {
                return;
            }

            for (var i = 0; i < manifest.phaseStates.Length; i++)
            {
                var phase = manifest.phaseStates[i];
                var id = "run-phase-" + NormalizeId(phase.phaseId, i);
                AddNode(
                    document,
                    id,
                    WorkflowGraphNodeKind.Sequence,
                    NormalizeEmpty(phase.phaseId, "phase-" + i),
                    NormalizeEmpty(phase.status, "unknown"),
                    MapStatusToState(phase.status),
                    phase.error,
                    sourcePath);
                SetNodeLayout(document.FindNode(id), 2, i, "run");
                AddEdge(document, "run", id, null);
            }
        }

        private static WorkflowGraphDocument CreateMessageDocument(string title, string message, string sourcePath)
        {
            var document = new WorkflowGraphDocument
            {
                Title = title,
                Subtitle = message,
                SourcePath = sourcePath,
                SourceRootPath = string.IsNullOrEmpty(sourcePath) ? null : Path.GetDirectoryName(sourcePath),
                Mode = "message",
                Summary = message
            };
            AddNode(document, "message", WorkflowGraphNodeKind.Root, title, message, WorkflowGraphNodeState.NotStarted, message, sourcePath);
            return document;
        }

        private static WorkflowGraphNodeKind MapPhaseKind(string type)
        {
            if (string.Equals(type, "parallel", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeKind.Parallel;
            }

            if (string.Equals(type, "pipeline", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeKind.Pipeline;
            }

            if (string.Equals(type, "report", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeKind.Handoff;
            }

            return WorkflowGraphNodeKind.Sequence;
        }

        private static WorkflowGraphNodeKind MapNodeKind(string kind)
        {
            if (string.Equals(kind, "Preflight", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeKind.Preflight;
            }

            if (string.Equals(kind, "Selector", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeKind.Selector;
            }

            if (string.Equals(kind, "Condition", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeKind.Condition;
            }

            if (string.Equals(kind, "Gate", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeKind.Gate;
            }

            if (string.Equals(kind, "Handoff", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeKind.Handoff;
            }

            if (string.Equals(kind, "Artifact", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeKind.Artifact;
            }

            if (string.Equals(kind, "Root", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeKind.Root;
            }

            return WorkflowGraphNodeKind.Action;
        }

        private static WorkflowGraphEdgeKind MapEdgeKind(string kind)
        {
            if (string.Equals(kind, "Dependency", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphEdgeKind.Dependency;
            }

            if (string.Equals(kind, "Gate", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphEdgeKind.Gate;
            }

            if (string.Equals(kind, "OptionalFlow", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphEdgeKind.OptionalFlow;
            }

            return WorkflowGraphEdgeKind.Flow;
        }

        private static WorkflowGraphNodeState MapStatusToState(string status)
        {
            if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeState.Running;
            }

            if (string.Equals(status, "passed", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeState.Passed;
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeState.Failed;
            }

            if (string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeState.Blocked;
            }

            if (string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase)
                || (status != null && status.StartsWith("skipped", StringComparison.OrdinalIgnoreCase)))
            {
                return WorkflowGraphNodeState.WaitingExternal;
            }

            if (string.Equals(status, "stale", StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowGraphNodeState.Stale;
            }

            return WorkflowGraphNodeState.NotStarted;
        }

        private static WorkflowGraphNodeState ResolveGateState(WorkflowRunSummaryData summary)
        {
            if (summary == null)
            {
                return WorkflowGraphNodeState.NotStarted;
            }

            if (summary.FailedGateCount > 0)
            {
                return WorkflowGraphNodeState.Failed;
            }

            if (summary.BlockedGateCount > 0)
            {
                return WorkflowGraphNodeState.Blocked;
            }

            return summary.GateCount > 0 ? WorkflowGraphNodeState.Passed : WorkflowGraphNodeState.NotStarted;
        }

        private static int CountGateStatus(WorkflowRunManifestData manifest, string status)
        {
            if (manifest == null || manifest.gateResults == null)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < manifest.gateResults.Length; i++)
            {
                var gate = manifest.gateResults[i];
                if (gate != null && string.Equals(gate.status, status, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsExternalStep(string kind)
        {
            return string.Equals(kind, "agent", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(kind, "manual", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeId(string value, int fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback.ToString() : value.Replace(" ", "-").ToLowerInvariant();
        }

        private static string NormalizeEmpty(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static string Join(string[] values)
        {
            return values == null || values.Length == 0 ? string.Empty : string.Join(", ", values);
        }

        private string ResolveWorkflowSkillReferencesRoot()
        {
            var manifestPath = ResolveBranchDetailManifestPath();
            if (!string.IsNullOrEmpty(manifestPath))
            {
                return Path.GetDirectoryName(manifestPath);
            }

            var routingManifestPath = ResolveRoutingGraphManifestPath();
            if (!string.IsNullOrEmpty(routingManifestPath))
            {
                return Path.GetDirectoryName(routingManifestPath);
            }

            return null;
        }

        private static void SetNodeLayout(WorkflowGraphNode node, int column, int order, string parentNodeId)
        {
            SetNodeLayout(node, column, order, parentNodeId, order);
        }

        private static void SetNodeLayout(WorkflowGraphNode node, int column, int order, string parentNodeId, int row)
        {
            if (node == null)
            {
                return;
            }

            node.LayoutColumn = column;
            node.LayoutOrder = order;
            node.LayoutRow = row;
            node.ParentNodeId = parentNodeId;
        }

        private static void SetNodeSemantic(WorkflowGraphNode node, string semanticRole, bool isOptional)
        {
            if (node == null)
            {
                return;
            }

            node.SemanticRole = semanticRole ?? string.Empty;
            node.IsOptional = isOptional;
        }

        private static void MarkRoutingOptionalEdges(WorkflowGraphDocument document)
        {
            if (document == null)
            {
                return;
            }

            for (var i = 0; i < document.Edges.Count; i++)
            {
                var edge = document.Edges[i];
                if (edge == null)
                {
                    continue;
                }

                if ((string.Equals(edge.FromNodeId, "preflight", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(edge.ToNodeId, "requirements", StringComparison.OrdinalIgnoreCase))
                    || (string.Equals(edge.FromNodeId, "requirements", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(edge.ToNodeId, "selector", StringComparison.OrdinalIgnoreCase)))
                {
                    edge.Kind = WorkflowGraphEdgeKind.OptionalFlow;
                }
            }
        }

        private static string ResolveGateParentNodeId(WorkflowRecipeData recipe, WorkflowGateData gate)
        {
            if (gate == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(gate.stepId))
            {
                return FindStepNodeId(recipe, gate.stepId);
            }

            if (!string.IsNullOrEmpty(gate.kind))
            {
                var normalizedKind = gate.kind.Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
                if (normalizedKind.Contains("compile"))
                {
                    return FindStepNodeId(recipe, "compile-unity");
                }

                if (normalizedKind.Contains("console"))
                {
                    return FindStepNodeId(recipe, "check-console-errors-after-fix")
                           ?? FindStepNodeId(recipe, "check-console-errors");
                }

                if (normalizedKind.Contains("verdict"))
                {
                    return FindStepNodeId(recipe, "verify-candidates");
                }

                if (normalizedKind.Contains("test"))
                {
                    return FindStepNodeId(recipe, "collect-extra-evidence");
                }
            }

            return FindLastStepNodeId(recipe);
        }

        private static string FindStepNodeId(WorkflowRecipeData recipe, string stepId)
        {
            if (recipe == null || recipe.phases == null || string.IsNullOrEmpty(stepId))
            {
                return null;
            }

            for (var phaseIndex = 0; phaseIndex < recipe.phases.Length; phaseIndex++)
            {
                var phase = recipe.phases[phaseIndex];
                if (phase == null || phase.steps == null)
                {
                    continue;
                }

                for (var stepIndex = 0; stepIndex < phase.steps.Length; stepIndex++)
                {
                    var step = phase.steps[stepIndex];
                    if (step != null && string.Equals(step.id, stepId, StringComparison.OrdinalIgnoreCase))
                    {
                        return "phase-" + NormalizeId(phase.id, phaseIndex) + "-step-" + NormalizeId(step.id, stepIndex);
                    }
                }
            }

            return null;
        }

        private static string FindLastStepNodeId(WorkflowRecipeData recipe)
        {
            if (recipe == null || recipe.phases == null)
            {
                return null;
            }

            for (var phaseIndex = recipe.phases.Length - 1; phaseIndex >= 0; phaseIndex--)
            {
                var phase = recipe.phases[phaseIndex];
                if (phase == null || phase.steps == null || phase.steps.Length == 0)
                {
                    continue;
                }

                var lastStepIndex = phase.steps.Length - 1;
                var lastStep = phase.steps[lastStepIndex];
                if (lastStep != null)
                {
                    return "phase-" + NormalizeId(phase.id, phaseIndex) + "-step-" + NormalizeId(lastStep.id, lastStepIndex);
                }
            }

            return null;
        }

        private static void AddCandidatePath(List<string> candidatePaths, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            for (var i = 0; i < candidatePaths.Count; i++)
            {
                if (string.Equals(candidatePaths[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            candidatePaths.Add(path);
        }

        [Serializable]
        private sealed class WorkflowRecipeData
        {
            public int schemaVersion;
            public string name;
            public string title;
            public string description;
            public string version;
            public string[] requiredSkills;
            public WorkflowPhaseData[] phases;
            public WorkflowGateData[] gates;
            public WorkflowArtifactDeclarationData[] artifacts;
        }

        [Serializable]
        private sealed class WorkflowPhaseData
        {
            public string id;
            public string type;
            public string description;
            public string[] dependsOn;
            public string itemSource;
            public string[] requiredSkills;
            public string[] releaseSkillsAfter;
            public WorkflowStepData[] steps;
        }

        [Serializable]
        private sealed class WorkflowStepData
        {
            public string id;
            public string kind;
            public string description;
            public string role;
            public string command;
            public string[] requiredSkills;
            public string[] releaseSkillsAfter;
            public string[] outputs;
        }

        [Serializable]
        private sealed class WorkflowGateData
        {
            public string id;
            public string kind;
            public bool required = true;
            public string artifactKind;
            public string stepId;
            public string schema;
            public int min;
            public string[] allow;
        }

        [Serializable]
        private sealed class WorkflowArtifactDeclarationData
        {
            public string kind;
            public string description;
            public bool required;
        }

        [Serializable]
        private sealed class WorkflowGraphManifestData
        {
            public int schemaVersion;
            public string kind;
            public WorkflowGraphManifestGeneratedFromData generatedFrom;
            public WorkflowGraphManifestNodeData[] nodes;
            public WorkflowGraphManifestEdgeData[] edges;
        }

        [Serializable]
        private sealed class WorkflowGraphManifestGeneratedFromData
        {
            public string assistant;
            public string settingsHash;
        }

        [Serializable]
        private sealed class WorkflowGraphManifestNodeData
        {
            public string id;
            public string kind;
            public string title;
            public string subtitle;
            public string source;
            public string editable;
            public bool enabled;
        }

        [Serializable]
        private sealed class WorkflowGraphManifestEdgeData
        {
            public string from;
            public string to;
            public string condition;
        }

        [Serializable]
        private sealed class WorkflowBranchManifestData
        {
            public int schemaVersion;
            public string kind;
            public string branchId;
            public string title;
            public string subtitle;
            public string summary;
            public WorkflowBranchManifestNodeData[] nodes;
            public WorkflowBranchManifestEdgeData[] edges;
        }

        [Serializable]
        private sealed class WorkflowBranchManifestNodeData
        {
            public string id;
            public string kind;
            public string title;
            public string subtitle;
            public string state;
            public string description;
            public string source;
            public string parent;
            public string semanticRole;
            public bool optional;
            public int column;
            public int order;
            public int row;
            public WorkflowBranchManifestDetailData[] details;
        }

        [Serializable]
        private sealed class WorkflowBranchManifestEdgeData
        {
            public string from;
            public string to;
            public string label;
            public string kind;
        }

        [Serializable]
        private sealed class WorkflowBranchManifestDetailData
        {
            public string label;
            public string value;
        }

        [Serializable]
        private sealed class WorkflowActiveRunPointerData
        {
            public string runId;
            public string recipeName;
            public string runDirectory;
            public string attachedAtUtc;
            public string updatedAtUtc;
        }

        [Serializable]
        private sealed class WorkflowRunManifestData
        {
            public string runId;
            public string recipeName;
            public string recipePath;
            public string status;
            public WorkflowPhaseStateData[] phaseStates;
            public WorkflowStepStateData[] stepStates;
            public WorkflowArtifactRefData[] artifactRefs;
            public WorkflowGateResultData[] gateResults;
            public WorkflowRunSummaryJsonData summary;
        }

        [Serializable]
        private sealed class WorkflowPhaseStateData
        {
            public string phaseId;
            public string status;
            public string error;
        }

        [Serializable]
        private sealed class WorkflowStepStateData
        {
            public string stepId;
            public string phaseId;
            public string kind;
            public string status;
            public string error;
        }

        [Serializable]
        private sealed class WorkflowArtifactRefData
        {
            public string artifactId;
            public string kind;
        }

        [Serializable]
        private sealed class WorkflowGateResultData
        {
            public string gateId;
            public string kind;
            public string status;
            public bool required;
        }

        [Serializable]
        private sealed class WorkflowRunSummaryJsonData
        {
            public int externalSkippedCount;
            public int missingExternalImportCount;
        }
    }

    internal sealed class WorkflowRecipeSummary
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string Source { get; set; }
        public string Path { get; set; }
        public int PhaseCount { get; set; }
        public int GateCount { get; set; }

        public string DisplayName
        {
            get
            {
                return string.IsNullOrEmpty(Title) ? Name : Title;
            }
        }
    }

    internal sealed class WorkflowRunSummaryData
    {
        public string RunId { get; set; }
        public string RecipeName { get; set; }
        public string Status { get; set; }
        public string ManifestPath { get; set; }
        public string ReportPath { get; set; }
        public int PhaseCount { get; set; }
        public int StepCount { get; set; }
        public int ArtifactCount { get; set; }
        public int GateCount { get; set; }
        public int FailedGateCount { get; set; }
        public int BlockedGateCount { get; set; }
        public int ExternalSkippedCount { get; set; }
        public int MissingExternalImportCount { get; set; }
    }
}
