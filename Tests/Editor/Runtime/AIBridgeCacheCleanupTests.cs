using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AIBridge.Runtime.Internal;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class AIBridgeCacheCleanupTests
    {
        private string _tempRoot;
        private string _bridgeDirectory;
        private DateTime _nowUtc;

        [SetUp]
        public void SetUp()
        {
            _nowUtc = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
            _tempRoot = Path.Combine(Path.GetTempPath(), "AIBridgeCacheCleanupTests_" + Guid.NewGuid().ToString("N"));
            _bridgeDirectory = Path.Combine(_tempRoot, ".aibridge");
            Directory.CreateDirectory(_bridgeDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, true);
                }
            }
            catch
            {
            }
        }

        [Test]
        public void CleanupExpired_RemovesOldScreenshotImagesAndKeepsRecentFiles()
        {
            var oldPng = WriteFile("screenshots/old.png", _nowUtc.AddDays(-31));
            var oldJpg = WriteFile("screenshots/old.jpg", _nowUtc.AddDays(-31));
            var oldGif = WriteFile("screenshots/old.gif", _nowUtc.AddDays(-31));
            var recentJpeg = WriteFile("screenshots/recent.jpeg", _nowUtc.AddDays(-5));
            var gitignore = WriteFile("screenshots/.gitignore", _nowUtc.AddDays(-90));

            var result = Cleanup();

            Assert.That(File.Exists(oldPng), Is.False);
            Assert.That(File.Exists(oldJpg), Is.False);
            Assert.That(File.Exists(oldGif), Is.False);
            Assert.That(File.Exists(recentJpeg), Is.True);
            Assert.That(File.Exists(gitignore), Is.True);
            Assert.That(result.DeletedFiles, Is.EqualTo(3));
        }

        [Test]
        public void CleanupExpired_PreservesOnlineRuntimeTargetAndDeletesStaleTarget()
        {
            var onlineTarget = ResolvePath("runtime/targets/online");
            Directory.CreateDirectory(onlineTarget);
            WriteHeartbeat(onlineTarget, _nowUtc.AddSeconds(-10));
            var oldRuntimeScreenshot = WriteFile("runtime/targets/online/screenshots/old.png", _nowUtc.AddDays(-31));

            var staleTarget = ResolvePath("runtime/targets/stale");
            Directory.CreateDirectory(staleTarget);
            WriteHeartbeat(staleTarget, _nowUtc.AddDays(-40));
            WriteFile("runtime/targets/stale/results/old.json", _nowUtc.AddDays(-40));
            Directory.SetLastWriteTimeUtc(staleTarget, _nowUtc.AddDays(-40));

            Cleanup();

            Assert.That(Directory.Exists(onlineTarget), Is.True);
            Assert.That(File.Exists(oldRuntimeScreenshot), Is.False);
            Assert.That(Directory.Exists(staleTarget), Is.False);
        }

        [Test]
        public void CleanupExpired_RemovesOldWorkflowRunsButKeepsActiveRun()
        {
            var failedRun = ResolvePath("workflows/runs/wf_failed");
            Directory.CreateDirectory(failedRun);
            WriteFile("workflows/runs/wf_failed/manifest.json", _nowUtc.AddDays(-31), "{\"status\":\"failed\"}");
            AIBridgeCacheCleanup.TouchLastUsed(failedRun, _nowUtc.AddDays(-31));

            var activeRun = ResolvePath("workflows/runs/wf_active");
            Directory.CreateDirectory(activeRun);
            WriteFile("workflows/runs/wf_active/manifest.json", _nowUtc.AddDays(-31), "{\"status\":\"blocked\"}");
            AIBridgeCacheCleanup.TouchLastUsed(activeRun, _nowUtc.AddDays(-31));
            WriteFile("workflows/active-run.json", _nowUtc, "{\"runId\":\"wf_active\"}");

            Cleanup();

            Assert.That(Directory.Exists(failedRun), Is.False);
            Assert.That(Directory.Exists(activeRun), Is.True);
        }

        [Test]
        public void CleanupExpired_PreservesActiveCodeIndexAndCleansInactiveCodeIndex()
        {
            var codeIndex = ResolvePath("code-index");
            Directory.CreateDirectory(codeIndex);
            AIBridgeCacheCleanup.TouchLastUsed(codeIndex, _nowUtc.AddDays(-31));
            WriteFile("code-index/snapshot/manifest.json", _nowUtc.AddDays(-31));
            WriteFile("code-index/cache/cache.bin", _nowUtc.AddDays(-31));
            WriteFile("code-index/status.json", _nowUtc.AddDays(-31), "{\"daemonPid\":" + Process.GetCurrentProcess().Id + "}");
            Directory.SetLastWriteTimeUtc(ResolvePath("code-index/snapshot"), _nowUtc.AddDays(-31));
            Directory.SetLastWriteTimeUtc(ResolvePath("code-index/cache"), _nowUtc.AddDays(-31));

            Cleanup();

            Assert.That(Directory.Exists(ResolvePath("code-index/snapshot")), Is.True);
            Assert.That(Directory.Exists(ResolvePath("code-index/cache")), Is.True);

            File.Delete(ResolvePath("code-index/status.json"));
            Cleanup();

            Assert.That(Directory.Exists(ResolvePath("code-index/snapshot")), Is.False);
            Assert.That(Directory.Exists(ResolvePath("code-index/cache")), Is.False);
        }

        [Test]
        public void CleanupExpired_CleansMiscCachesAndKeepsProtectedFiles()
        {
            var httpFile = WriteFile("runtime-cache/http/player/artifact.bin", _nowUtc.AddDays(-31));
            Directory.SetLastWriteTimeUtc(ResolvePath("runtime-cache/http/player"), _nowUtc.AddDays(-31));

            var skillRepoFile = WriteFile("skill-library/cache/repo/.git/config", _nowUtc.AddDays(-31));
            Directory.SetLastWriteTimeUtc(ResolvePath("skill-library/cache/repo"), _nowUtc.AddDays(-31));
            var installed = WriteFile("skill-library/installed.json", _nowUtc.AddDays(-90));

            var oldScript = WriteFile("scripts/old.txt", _nowUtc.AddDays(-31));
            var activeScript = WriteFile("scripts/active.txt", _nowUtc.AddDays(-31));
            WriteFile("script-state.json", _nowUtc, "{\"ScriptPath\":\"" + EscapeJson(activeScript) + "\"}");

            var profiler = WriteFile("profiler/old.json", _nowUtc.AddDays(-31));
            var compiledFile = WriteFile("code/.compiled/session/output.dll", _nowUtc.AddDays(-31));
            Directory.SetLastWriteTimeUtc(ResolvePath("code/.compiled/session"), _nowUtc.AddDays(-31));

            var cliTemp = WriteFile("cli/CodeIndex.tmp.123", _nowUtc.AddDays(-31));
            var cliTool = WriteFile("cli/AIBridgeCLI.exe", _nowUtc.AddDays(-90));

            Cleanup();

            Assert.That(File.Exists(httpFile), Is.False);
            Assert.That(File.Exists(skillRepoFile), Is.False);
            Assert.That(File.Exists(installed), Is.True);
            Assert.That(File.Exists(oldScript), Is.False);
            Assert.That(File.Exists(activeScript), Is.True);
            Assert.That(File.Exists(profiler), Is.False);
            Assert.That(File.Exists(compiledFile), Is.False);
            Assert.That(File.Exists(cliTemp), Is.False);
            Assert.That(File.Exists(cliTool), Is.True);
        }

        [Test]
        public void ClearScreenshotCache_DoesNotUpdateAutoCleanupState()
        {
            var oldPng = WriteFile("screenshots/old.png", _nowUtc.AddDays(-31));
            var statePath = ResolvePath(AIBridgeCacheCleanup.StateFileName);

            AIBridgeCacheCleanup.ClearScreenshotCache(_bridgeDirectory);

            Assert.That(File.Exists(oldPng), Is.False);
            Assert.That(File.Exists(statePath), Is.False);
        }

        private AIBridgeCacheCleanupResult Cleanup()
        {
            return AIBridgeCacheCleanup.CleanupExpired(
                _bridgeDirectory,
                new AIBridgeCacheCleanupSettings
                {
                    EnableAutoCleanup = true,
                    RetentionDays = 30
                },
                _nowUtc);
        }

        private string WriteFile(string relativePath, DateTime lastWriteUtc, string content = "x")
        {
            var path = ResolvePath(relativePath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, content, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            return path;
        }

        private void WriteHeartbeat(string targetDirectory, DateTime lastHeartbeatUtc)
        {
            var path = System.IO.Path.Combine(targetDirectory, "heartbeat.json");
            var json = "{\"targetId\":\"" + System.IO.Path.GetFileName(targetDirectory) + "\",\"lastHeartbeatUtc\":\"" + lastHeartbeatUtc.ToString("o") + "\"}";
            File.WriteAllText(path, json, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, lastHeartbeatUtc);
        }

        private string ResolvePath(string relativePath)
        {
            return System.IO.Path.Combine(_bridgeDirectory, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
