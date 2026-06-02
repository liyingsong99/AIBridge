using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class SkillInstallerCliCacheTests : AssistantIntegrationTestFixture
    {
        [Test]
        public void FileCopyCheckDetectsSameLengthContentChangeWithOlderSourceTimestamp()
        {
            var sourceFile = Path.Combine(ProjectRoot, "source.dll");
            var targetFile = Path.Combine(ProjectRoot, "target.dll");
            File.WriteAllText(sourceFile, "new!");
            File.WriteAllText(targetFile, "old?");
            File.SetLastWriteTimeUtc(sourceFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(targetFile, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.IsTrue(SkillInstaller.IsFileCopyNeeded(sourceFile, targetFile));
        }

        [Test]
        public void FileCopyCheckSkipsIdenticalNewerTarget()
        {
            var sourceFile = Path.Combine(ProjectRoot, "source.dll");
            var targetFile = Path.Combine(ProjectRoot, "target.dll");
            File.WriteAllText(sourceFile, "same");
            File.WriteAllText(targetFile, "same");
            File.SetLastWriteTimeUtc(sourceFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(targetFile, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.IsFalse(SkillInstaller.IsFileCopyNeeded(sourceFile, targetFile));
        }

        [Test]
        public void FileCopyCheckRequiresMissingTarget()
        {
            var sourceFile = Path.Combine(ProjectRoot, "source.dll");
            var targetFile = Path.Combine(ProjectRoot, "missing.dll");
            File.WriteAllText(sourceFile, "source");

            Assert.IsTrue(SkillInstaller.IsFileCopyNeeded(sourceFile, targetFile));
        }

        [Test]
        public void CodeIndexDirectoryCheckDetectsMissingRuntimeSidecars()
        {
            var codeIndexDir = Path.Combine(ProjectRoot, "CodeIndex");
            Directory.CreateDirectory(codeIndexDir);
            File.WriteAllText(Path.Combine(codeIndexDir, GetCodeIndexExecutableName()), "exe");
            File.WriteAllText(Path.Combine(codeIndexDir, "AIBridgeCodeIndex.dll"), "dll");

            string[] missingFiles;
            Assert.IsFalse(SkillInstaller.IsCodeIndexDirectoryComplete(codeIndexDir, out missingFiles));
            CollectionAssert.Contains(missingFiles, "AIBridgeCodeIndex.deps.json");
            CollectionAssert.Contains(missingFiles, "AIBridgeCodeIndex.runtimeconfig.json");
        }

        [Test]
        public void CodeIndexCopyCheckRequiresIncompleteTargetRefresh()
        {
            var sourceCliDir = Path.Combine(ProjectRoot, "source-cli");
            var targetCliDir = Path.Combine(ProjectRoot, "target-cli");
            WriteCompleteCodeIndexDirectory(Path.Combine(sourceCliDir, "CodeIndex"), "source");
            Directory.CreateDirectory(Path.Combine(targetCliDir, "CodeIndex"));
            File.WriteAllText(Path.Combine(targetCliDir, "CodeIndex", GetCodeIndexExecutableName()), "old");

            Assert.IsTrue(SkillInstaller.IsCodeIndexCopyNeeded(sourceCliDir, targetCliDir));
        }

        [Test]
        public void CodeIndexCopyInstallsCompleteDirectoryAndRemovesStaleFiles()
        {
            var sourceCliDir = Path.Combine(ProjectRoot, "source-cli");
            var targetCliDir = Path.Combine(ProjectRoot, "target-cli");
            WriteCompleteCodeIndexDirectory(Path.Combine(sourceCliDir, "CodeIndex"), "source");
            WriteCompleteCodeIndexDirectory(Path.Combine(targetCliDir, "CodeIndex"), "old");
            File.WriteAllText(Path.Combine(targetCliDir, "CodeIndex", "stale.dll"), "stale");

            var copied = SkillInstaller.CopyCodeIndexToCache(sourceCliDir, targetCliDir);

            Assert.Greater(copied, 0);
            string[] missingFiles;
            Assert.IsTrue(SkillInstaller.IsCodeIndexDirectoryComplete(Path.Combine(targetCliDir, "CodeIndex"), out missingFiles));
            Assert.IsFalse(File.Exists(Path.Combine(targetCliDir, "CodeIndex", "stale.dll")));
            Assert.AreEqual("source:AIBridgeCodeIndex.runtimeconfig.json", File.ReadAllText(Path.Combine(targetCliDir, "CodeIndex", "AIBridgeCodeIndex.runtimeconfig.json")));
        }

        [Test]
        public void CodeIndexCopyKeepsExistingTargetWhenSourceIsIncomplete()
        {
            var sourceCliDir = Path.Combine(ProjectRoot, "source-cli");
            var targetCliDir = Path.Combine(ProjectRoot, "target-cli");
            Directory.CreateDirectory(Path.Combine(sourceCliDir, "CodeIndex"));
            File.WriteAllText(Path.Combine(sourceCliDir, "CodeIndex", GetCodeIndexExecutableName()), "source");
            WriteCompleteCodeIndexDirectory(Path.Combine(targetCliDir, "CodeIndex"), "old");

            var copied = SkillInstaller.CopyCodeIndexToCache(sourceCliDir, targetCliDir);

            Assert.AreEqual(0, copied);
            string[] missingFiles;
            Assert.IsTrue(SkillInstaller.IsCodeIndexDirectoryComplete(Path.Combine(targetCliDir, "CodeIndex"), out missingFiles));
            Assert.AreEqual("old:AIBridgeCodeIndex.runtimeconfig.json", File.ReadAllText(Path.Combine(targetCliDir, "CodeIndex", "AIBridgeCodeIndex.runtimeconfig.json")));
        }

        [Test]
        public void CodeIndexRefreshShutsDownDaemonBeforeReplacingDirectory()
        {
            var sourceCliDir = Path.Combine(ProjectRoot, "source-cli");
            var targetCliDir = Path.Combine(ProjectRoot, "target-cli");
            var targetRuntimeConfig = Path.Combine(targetCliDir, "CodeIndex", "AIBridgeCodeIndex.runtimeconfig.json");
            WriteCompleteCodeIndexDirectory(Path.Combine(sourceCliDir, "CodeIndex"), "source");
            WriteCompleteCodeIndexDirectory(Path.Combine(targetCliDir, "CodeIndex"), "old");
            var events = new List<string>();

            SkillInstaller.SetCodeIndexDaemonShutdownForTests((cleanupMode, timeoutMs) =>
            {
                events.Add(cleanupMode + ":" + timeoutMs);
                Assert.AreEqual("old:AIBridgeCodeIndex.runtimeconfig.json", File.ReadAllText(targetRuntimeConfig));
            });

            try
            {
                var copied = SkillInstaller.RefreshCodeIndexCache(sourceCliDir, targetCliDir);

                Assert.Greater(copied, 0);
                Assert.AreEqual(1, events.Count);
                Assert.AreEqual("processOnly:3000", events[0]);
                Assert.AreEqual("source:AIBridgeCodeIndex.runtimeconfig.json", File.ReadAllText(targetRuntimeConfig));
            }
            finally
            {
                SkillInstaller.ResetCodeIndexDaemonShutdownForTests();
            }
        }

        private static void WriteCompleteCodeIndexDirectory(string codeIndexDir, string marker)
        {
            Directory.CreateDirectory(codeIndexDir);
            File.WriteAllText(Path.Combine(codeIndexDir, GetCodeIndexExecutableName()), marker + ":" + GetCodeIndexExecutableName());
            foreach (var fileName in GetRequiredCodeIndexManagedFiles())
            {
                File.WriteAllText(Path.Combine(codeIndexDir, fileName), marker + ":" + fileName);
            }
        }

        private static IEnumerable<string> GetRequiredCodeIndexManagedFiles()
        {
            yield return "AIBridgeCodeIndex.dll";
            yield return "AIBridgeCodeIndex.deps.json";
            yield return "AIBridgeCodeIndex.runtimeconfig.json";
            yield return "Newtonsoft.Json.dll";
            yield return "Microsoft.CodeAnalysis.dll";
            yield return "Microsoft.CodeAnalysis.CSharp.dll";
            yield return "Microsoft.CodeAnalysis.Workspaces.dll";
            yield return "Microsoft.CodeAnalysis.CSharp.Workspaces.dll";
            yield return "System.Composition.AttributedModel.dll";
            yield return "System.Composition.Convention.dll";
            yield return "System.Composition.Hosting.dll";
            yield return "System.Composition.Runtime.dll";
            yield return "System.Composition.TypedParts.dll";
        }

        private static string GetCodeIndexExecutableName()
        {
#if UNITY_EDITOR_WIN
            return "AIBridgeCodeIndex.exe";
#else
            return "AIBridgeCodeIndex";
#endif
        }
    }
}
