using System;
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
    }
}
