using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

namespace AIBridge.Editor.Tests
{
    public class WorkflowRecipeTemplateTests
    {
        private static readonly string[] ExpectedRecipes =
        {
            "unity-change-implementation",
            "unity-sharded-review",
            "runtime-target-sweep",
            "runtime-debug-investigation",
            "runtime-ui-validation",
            "prefab-asset-sweep",
            "bug-hunter-loop",
            "harness-readiness-check"
        };

        private static readonly HashSet<string> PhaseTypes = new HashSet<string>
        {
            "serial", "parallel", "pipeline", "barrier", "report"
        };

        private static readonly HashSet<string> StepKinds = new HashSet<string>
        {
            "cli", "agent", "manual", "barrier", "report"
        };

        private static readonly HashSet<string> GateKinds = new HashSet<string>
        {
            "unityCompile", "dotnetBuild", "consoleErrors", "testRun", "screenshotExists",
            "runtimeReachable", "runtimeErrors", "artifactRequired", "externalVerdict", "patchProposalRequired"
        };

        [Test]
        public void BuiltInWorkflowTemplatesHaveMetaAndValidShape()
        {
            var workflowDirectory = Path.Combine(GetPackageRoot(), "Templates~", "Workflows");

            Assert.IsTrue(Directory.Exists(workflowDirectory), workflowDirectory);
            Assert.IsTrue(File.Exists(workflowDirectory + ".meta"), workflowDirectory + ".meta");

            var files = Directory.GetFiles(workflowDirectory, "*.aibridge-workflow.json")
                .OrderBy(Path.GetFileNameWithoutExtension)
                .ToArray();
            var names = files.Select(path => Path.GetFileName(path).Replace(".aibridge-workflow.json", "")).ToArray();
            CollectionAssert.AreEquivalent(ExpectedRecipes, names);

            foreach (var file in files)
            {
                Assert.IsTrue(File.Exists(file + ".meta"), file + ".meta");
                AssertRecipeShape(file, ExpectedRecipes);
            }
        }

        [Test]
        public void CliSmokeWorkflowFixtureHasValidShape()
        {
            var packageRoot = GetPackageRoot();
            var file = Path.Combine(packageRoot, "Tests", "Editor", "AssistantIntegration", "cli-smoke.aibridge-workflow.json");

            Assert.IsTrue(File.Exists(file), file);
            Assert.IsTrue(File.Exists(file + ".meta"), file + ".meta");
            AssertRecipeShape(file, new[] { "cli-smoke" });
        }

        [Test]
        public void WorkflowSkillReferencesDescribeCliBoundary()
        {
            var packageRoot = GetPackageRoot();
            var schema = File.ReadAllText(Path.Combine(packageRoot, "Skill~", "aibridge-workflow-orchestration", "references", "recipe-schema.md"));
            var recipes = File.ReadAllText(Path.Combine(packageRoot, "Skill~", "aibridge-workflow-orchestration", "references", "builtin-recipes.md"));
            var evidenceSchema = File.ReadAllText(Path.Combine(packageRoot, "Skill~", "aibridge-workflow-orchestration", "references", "evidence-schema.md"));

            StringAssert.Contains("$CLI workflow validate", schema);
            StringAssert.Contains("skipped_requires_external_executor", schema);
            StringAssert.Contains("runtime-target-sweep", recipes);
            StringAssert.Contains("runtime-debug-investigation", recipes);
            StringAssert.Contains("prefab-asset-sweep", recipes);
            StringAssert.Contains("harness-readiness-check", recipes);
            StringAssert.Contains("harness status", recipes);
            StringAssert.Contains("EvidenceRef", schema);
            StringAssert.Contains("CommandEvidence", schema);
            StringAssert.Contains("SkillHandoff", schema);
            StringAssert.Contains("EvidenceRef", evidenceSchema);
            StringAssert.Contains("CommandEvidence", evidenceSchema);
            StringAssert.Contains("SkillHandoff", evidenceSchema);
            StringAssert.Contains("workflow status --run", schema);
            StringAssert.Contains("--detail full", schema);
            StringAssert.Contains("Never parallel-write", schema);
            StringAssert.Contains("requiredSkills", schema);
            StringAssert.Contains("releaseSkillsAfter", schema);
        }

        [Test]
        public void WorkflowExporterAndImporterSupportHarnessEvidenceSchemas()
        {
            var packageRoot = GetPackageRoot();
            var exporter = File.ReadAllText(Path.Combine(packageRoot, "Tools~", "AIBridgeCLI", "Workflow", "WorkflowExporter.cs"));
            var importer = File.ReadAllText(Path.Combine(packageRoot, "Tools~", "AIBridgeCLI", "Workflow", "WorkflowExternalResultImporter.cs"));
            var command = File.ReadAllText(Path.Combine(packageRoot, "Tools~", "AIBridgeCLI", "Commands", "WorkflowCommand.cs"));
            var report = File.ReadAllText(Path.Combine(packageRoot, "Tools~", "AIBridgeCLI", "Workflow", "WorkflowReportWriter.cs"));
            var registry = File.ReadAllText(Path.Combine(packageRoot, "Tools~", "AIBridgeCLI", "Commands", "CommandRegistry.cs"));
            var harnessCommand = File.ReadAllText(Path.Combine(packageRoot, "Tools~", "AIBridgeCLI", "Commands", "HarnessCommand.cs"));

            StringAssert.Contains("EvidenceRef", exporter);
            StringAssert.Contains("CommandEvidence", exporter);
            StringAssert.Contains("SkillHandoff", exporter);
            StringAssert.Contains("skipped_requires_external_executor", exporter);
            StringAssert.Contains("workflow status --run <runId>", exporter);
            StringAssert.Contains("Skill Routing And Scope", exporter);
            StringAssert.Contains("\"evidence\"", importer);
            StringAssert.Contains("\"command-evidence\"", importer);
            StringAssert.Contains("\"skill-handoff\"", importer);
            StringAssert.Contains("SkillHandoff.completedMode", importer);
            StringAssert.Contains("skillScopes", command);
            StringAssert.Contains("## Skill Scope", report);
            StringAssert.Contains("HarnessCommandBuilder", registry);
            StringAssert.Contains("capabilities.json", harnessCommand);
        }

        private static void AssertRecipeShape(string file, string[] allowedNames)
        {
            var json = File.ReadAllText(file);
            var recipe = JsonUtility.FromJson<Recipe>(json);

            Assert.AreEqual(1, recipe.schemaVersion, file);
            Assert.IsTrue(allowedNames.Contains(recipe.name), file);
            Assert.IsFalse(string.IsNullOrWhiteSpace(recipe.description), file);
            Assert.IsNotNull(recipe.requiredSkills, file);
            Assert.IsTrue(recipe.requiredSkills.Contains("aibridge-development-workflow"), file);
            Assert.IsNotNull(recipe.phases, file);
            Assert.Greater(recipe.phases.Length, 0, file);
            Assert.IsNotNull(recipe.gates, file);

            var seenPhases = new HashSet<string>();
            var seenSteps = new HashSet<string>();
            foreach (var phase in recipe.phases)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(phase.id), file);
                Assert.IsTrue(seenPhases.Add(phase.id), "Duplicate phase id in " + file + ": " + phase.id);
                Assert.IsTrue(PhaseTypes.Contains(phase.type), "Unsupported phase type in " + file + ": " + phase.type);

                if (phase.dependsOn != null)
                {
                    foreach (var dependency in phase.dependsOn)
                    {
                        Assert.IsTrue(seenPhases.Contains(dependency), "Phase dependency must reference an earlier phase in " + file + ": " + dependency);
                    }
                }

                if (phase.steps == null)
                {
                    continue;
                }

                foreach (var step in phase.steps)
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(step.id), file);
                    Assert.IsTrue(seenSteps.Add(step.id), "Duplicate step id in " + file + ": " + step.id);
                    Assert.IsTrue(StepKinds.Contains(step.kind), "Unsupported step kind in " + file + ": " + step.kind);
                    if (step.kind == "agent" || step.kind == "manual")
                    {
                        Assert.IsNotNull(step.requiredSkills, "External step missing requiredSkills in " + file + ": " + step.id);
                        Assert.Greater(step.requiredSkills.Length, 0, "External step has empty requiredSkills in " + file + ": " + step.id);
                    }

                    if (step.kind == "cli")
                    {
                        Assert.IsFalse(string.IsNullOrWhiteSpace(step.command), "CLI step missing command in " + file + ": " + step.id);
                    }
                }
            }

            foreach (var gate in recipe.gates)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(gate.id), file);
                Assert.IsTrue(GateKinds.Contains(gate.kind), "Unsupported gate kind in " + file + ": " + gate.kind);
            }
        }

        private static string GetPackageRoot()
        {
            var packageInfo = PackageInfo.FindForAssembly(typeof(AIBridgeProjectSettings).Assembly);
            if (packageInfo != null && !string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
            {
                return packageInfo.resolvedPath;
            }

            return Directory.GetCurrentDirectory();
        }

        [Serializable]
        private class Recipe
        {
            public int schemaVersion;
            public string name;
            public string description;
            public string[] requiredSkills;
            public Phase[] phases;
            public Gate[] gates;
        }

        [Serializable]
        private class Phase
        {
            public string id;
            public string type;
            public string[] dependsOn;
            public string[] requiredSkills;
            public string[] releaseSkillsAfter;
            public Step[] steps;
        }

        [Serializable]
        private class Step
        {
            public string id;
            public string kind;
            public string command;
            public string[] requiredSkills;
            public string[] releaseSkillsAfter;
        }

        [Serializable]
        private class Gate
        {
            public string id;
            public string kind;
        }
    }
}
