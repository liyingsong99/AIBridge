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
            "runtime-ui-validation",
            "prefab-asset-sweep",
            "bug-hunter-loop"
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
            "runtimeReachable", "runtimeErrors", "artifactRequired", "externalVerdict"
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

            StringAssert.Contains("$CLI workflow validate", schema);
            StringAssert.Contains("skipped_requires_external_executor", schema);
            StringAssert.Contains("runtime-target-sweep", recipes);
            StringAssert.Contains("prefab-asset-sweep", recipes);
            StringAssert.Contains("Never parallel-write", schema);
        }

        private static void AssertRecipeShape(string file, string[] allowedNames)
        {
            var json = File.ReadAllText(file);
            var recipe = JsonUtility.FromJson<Recipe>(json);

            Assert.AreEqual(1, recipe.schemaVersion, file);
            Assert.IsTrue(allowedNames.Contains(recipe.name), file);
            Assert.IsFalse(string.IsNullOrWhiteSpace(recipe.description), file);
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
            public Phase[] phases;
            public Gate[] gates;
        }

        [Serializable]
        private class Phase
        {
            public string id;
            public string type;
            public string[] dependsOn;
            public Step[] steps;
        }

        [Serializable]
        private class Step
        {
            public string id;
            public string kind;
            public string command;
        }

        [Serializable]
        private class Gate
        {
            public string id;
            public string kind;
        }
    }
}
