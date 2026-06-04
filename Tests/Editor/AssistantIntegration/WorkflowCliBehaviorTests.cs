using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

namespace AIBridge.Editor.Tests
{
    public class WorkflowCliBehaviorTests
    {
        [Test]
        public void FinishPassedDowngradesMissingRequiredGateEvidence()
        {
            var packageRoot = GetPackageRoot();
            var cliPath = ResolveCliPath(packageRoot);
            if (string.IsNullOrEmpty(cliPath) || !File.Exists(cliPath))
            {
                Assert.Ignore("AIBridgeCLI executable was not found for this platform.");
            }

            var projectRoot = CreateTemporaryUnityProject();
            try
            {
                var begin = RunCli(cliPath, projectRoot, "workflow begin --recipe unity-change-implementation --raw");
                Assert.AreEqual(0, begin.ExitCode, begin.Stderr + begin.Stdout);

                var finish = RunCli(cliPath, projectRoot, "workflow finish --status passed --raw");
                var result = JsonUtility.FromJson<WorkflowCommandResultView>(finish.Stdout);

                Assert.AreNotEqual(0, finish.ExitCode, finish.Stdout);
                Assert.IsNotNull(result, finish.Stdout);
                Assert.IsFalse(result.success, finish.Stdout);
                Assert.IsNotNull(result.data, finish.Stdout);
                Assert.AreEqual("blocked", result.data.status, finish.Stdout);
                Assert.IsFalse(string.IsNullOrWhiteSpace(result.data.manifestPath), finish.Stdout);
                AssertRequiredGateStatus(result.data.gateResults, "unity-compile", "skipped");
                AssertRequiredGateStatus(result.data.gateResults, "console-errors", "skipped");
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                {
                    Directory.Delete(projectRoot, true);
                }
            }
        }

        [Test]
        public void LogGateBlocksFailedLogCommand()
        {
            var packageRoot = GetPackageRoot();
            var cliPath = ResolveCliPath(packageRoot);
            if (string.IsNullOrEmpty(cliPath) || !File.Exists(cliPath))
            {
                Assert.Ignore("AIBridgeCLI executable was not found for this platform.");
            }

            var projectRoot = CreateTemporaryUnityProject();
            try
            {
                var recipePath = Path.Combine(projectRoot, "log-gate.aibridge-workflow.json");
                File.WriteAllText(recipePath, BuildLogGateRecipeJson());

                var run = RunCli(cliPath, projectRoot, "workflow run-cli --file " + QuoteCliValue(recipePath) + " --timeout 100 --raw");
                var result = JsonUtility.FromJson<WorkflowCommandResultView>(run.Stdout);

                Assert.AreNotEqual(0, run.ExitCode, run.Stdout);
                Assert.IsNotNull(result, run.Stdout);
                Assert.IsFalse(result.success, run.Stdout);
                Assert.IsNotNull(result.data, run.Stdout);
                Assert.AreEqual("blocked", result.data.status, run.Stdout);
                AssertRequiredGateStatus(result.data.gateResults, "console-errors", "blocked");
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                {
                    Directory.Delete(projectRoot, true);
                }
            }
        }

        [Test]
        public void ImportSkillHandoffRequiresCompletedMode()
        {
            var packageRoot = GetPackageRoot();
            var cliPath = ResolveCliPath(packageRoot);
            if (string.IsNullOrEmpty(cliPath) || !File.Exists(cliPath))
            {
                Assert.Ignore("AIBridgeCLI executable was not found for this platform.");
            }

            var projectRoot = CreateTemporaryUnityProject();
            try
            {
                var begin = RunCli(cliPath, projectRoot, "workflow begin --recipe unity-change-implementation --raw");
                var beginResult = JsonUtility.FromJson<WorkflowCommandResultView>(begin.Stdout);

                Assert.AreEqual(0, begin.ExitCode, begin.Stderr + begin.Stdout);
                Assert.IsNotNull(beginResult, begin.Stdout);
                Assert.IsNotNull(beginResult.data, begin.Stdout);
                Assert.IsFalse(string.IsNullOrWhiteSpace(beginResult.data.runId), begin.Stdout);

                var payloadPath = Path.Combine(projectRoot, "skill-handoff-missing-completed-mode.json");
                File.WriteAllText(payloadPath, "{\n"
                    + "  \"summary\": \"missing completedMode\",\n"
                    + "  \"releasedSkills\": [],\n"
                    + "  \"nextRecommendedSkills\": [],\n"
                    + "  \"artifactRefs\": [],\n"
                    + "  \"gates\": [],\n"
                    + "  \"openRisks\": []\n"
                    + "}\n");

                var import = RunCli(
                    cliPath,
                    projectRoot,
                    "workflow import --run " + beginResult.data.runId
                    + " --step handoff --schema SkillHandoff --file " + QuoteCliValue(payloadPath)
                    + " --raw");

                Assert.AreNotEqual(0, import.ExitCode, import.Stdout + import.Stderr);
                StringAssert.Contains("SkillHandoff.completedMode is required", import.Stdout + import.Stderr);
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                {
                    Directory.Delete(projectRoot, true);
                }
            }
        }

        private static void AssertRequiredGateStatus(WorkflowGateResultView[] gateResults, string gateId, string expectedStatus)
        {
            Assert.IsNotNull(gateResults, "Gate results are missing.");
            foreach (var gate in gateResults)
            {
                if (!string.Equals(gate.gateId, gateId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Assert.IsTrue(gate.required, gateId);
                Assert.AreEqual(expectedStatus, gate.status, gateId);
                return;
            }

            Assert.Fail("Gate was not found: " + gateId);
        }

        private static CliExecutionResult RunCli(string cliPath, string projectRoot, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = arguments,
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.EnvironmentVariables["UNITY_PROJECT_ROOT"] = projectRoot;
            startInfo.EnvironmentVariables["AIBRIDGE_PACKAGE_ROOT"] = GetPackageRoot();

            using (var process = new Process())
            using (var stdoutDone = new ManualResetEvent(false))
            using (var stderrDone = new ManualResetEvent(false))
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                process.StartInfo = startInfo;
                process.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data == null)
                    {
                        stdoutDone.Set();
                        return;
                    }

                    stdout.AppendLine(args.Data);
                };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data == null)
                    {
                        stderrDone.Set();
                        return;
                    }

                    stderr.AppendLine(args.Data);
                };

                Assert.IsTrue(process.Start(), "Failed to start AIBridgeCLI.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(15000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // 测试清理失败不影响原始超时断言。
                    }

                    Assert.Fail("AIBridgeCLI timed out: " + arguments);
                }

                stdoutDone.WaitOne(1000);
                stderrDone.WaitOne(1000);
                return new CliExecutionResult
                {
                    ExitCode = process.ExitCode,
                    Stdout = stdout.ToString(),
                    Stderr = stderr.ToString()
                };
            }
        }

        private static string CreateTemporaryUnityProject()
        {
            var projectRoot = Path.Combine(Path.GetTempPath(), "AIBridgeWorkflowCliTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "ProjectSettings"));
            File.WriteAllText(Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset"), "%YAML 1.1\n");
            return projectRoot;
        }

        private static string BuildLogGateRecipeJson()
        {
            return "{\n"
                + "  \"schemaVersion\": 1,\n"
                + "  \"name\": \"log-gate-test\",\n"
                + "  \"description\": \"Exercise console log gate behavior.\",\n"
                + "  \"phases\": [\n"
                + "    {\"id\": \"logs\", \"type\": \"serial\", \"steps\": [\n"
                + "      {\"id\": \"get-errors\", \"kind\": \"cli\", \"command\": \"get_logs --logType Error --count 50\"}\n"
                + "    ]}\n"
                + "  ],\n"
                + "  \"gates\": [\n"
                + "    {\"id\": \"console-errors\", \"kind\": \"consoleErrors\", \"required\": true, \"threshold\": {\"max\": 0}}\n"
                + "  ]\n"
                + "}\n";
        }

        private static string QuoteCliValue(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string ResolveCliPath(string packageRoot)
        {
#if UNITY_EDITOR_WIN
            return Path.Combine(packageRoot, "Tools~", "CLI", "win-x64", "AIBridgeCLI.exe");
#elif UNITY_EDITOR_OSX
            var rid = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";
            return Path.Combine(packageRoot, "Tools~", "CLI", rid, "AIBridgeCLI");
#elif UNITY_EDITOR_LINUX
            return Path.Combine(packageRoot, "Tools~", "CLI", "linux-x64", "AIBridgeCLI");
#else
            return null;
#endif
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

        private sealed class CliExecutionResult
        {
            public int ExitCode { get; set; }
            public string Stdout { get; set; }
            public string Stderr { get; set; }
        }

        [Serializable]
        private sealed class WorkflowCommandResultView
        {
            public bool success;
            public WorkflowDataView data;
        }

        [Serializable]
        private sealed class WorkflowDataView
        {
            public string runId;
            public string status;
            public string manifestPath;
            public WorkflowGateResultView[] gateResults;
        }

        [Serializable]
        private sealed class WorkflowGateResultView
        {
            public string gateId;
            public string status;
            public bool required;
        }
    }
}
