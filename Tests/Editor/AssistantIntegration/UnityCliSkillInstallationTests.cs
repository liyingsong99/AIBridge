using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace AIBridge.Editor.Tests
{
    public class UnityCliSkillInstallationTests : AssistantIntegrationTestFixture
    {
        [TestCase("codex")]
        [TestCase("claude")]
        [TestCase("cursor")]
        public void UnityCliReferenceInstallsWithPrimarySkillWithoutOptionalPackage(string targetId)
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == targetId);

            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });

            var skillDirectory = Path.Combine(ProjectRoot, target.GetResolvedSkillDirectoryRelativePath(ProjectRoot));
            var referencePath = Path.Combine(skillDirectory, "references", "unity-cli-reference.md");
            Assert.IsTrue(File.Exists(referencePath), "Optional diagnostic guidance must be available even when Pipeline is absent.");
            StringAssert.Contains("references/unity-cli-reference.md", File.ReadAllText(Path.Combine(skillDirectory, "SKILL.md")));

            var packageInfo = PackageInfo.FindForAssembly(typeof(AIBridgeProjectSettings).Assembly);
            Assert.IsNotNull(packageInfo, "AIBridge package must be discoverable for this installation test.");
            var sourcePath = Path.Combine(packageInfo.resolvedPath, "Skill~", "references", "unity-cli-reference.md");
            Assert.AreEqual(Normalize(File.ReadAllText(sourcePath)), Normalize(File.ReadAllText(referencePath)),
                "Installation must preserve the optional integration policy and invocation reference.");
            Assert.IsFalse(File.Exists(referencePath + ".meta"));
        }

        [Test]
        public void ReinstallUpdatesReferenceAndPreservesUnownedAndGeneratedFiles()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });
            var references = Path.Combine(ProjectRoot, target.GetResolvedSkillDirectoryRelativePath(ProjectRoot), "references");
            var referencePath = Path.Combine(references, "unity-cli-reference.md");
            var commandPath = Path.Combine(references, "command-reference.md");
            var inspectorPath = Path.Combine(references, "inspector-property-reference.md");
            var userPath = Path.Combine(references, "project-notes.md");
            var commandContent = File.ReadAllText(commandPath);
            var inspectorContent = File.ReadAllText(inspectorPath);
            File.WriteAllText(referencePath, "outdated reference");
            File.WriteAllText(userPath, "project-owned notes");

            var result = SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target }).Single();

            Assert.AreEqual(IntegrationAction.UpdatedBlock, result.SkillFileAction);
            var packageInfo = PackageInfo.FindForAssembly(typeof(AIBridgeProjectSettings).Assembly);
            var sourcePath = Path.Combine(packageInfo.resolvedPath, "Skill~", "references", "unity-cli-reference.md");
            Assert.AreEqual(Normalize(File.ReadAllText(sourcePath)), Normalize(File.ReadAllText(referencePath)));
            Assert.AreEqual(commandContent, File.ReadAllText(commandPath));
            Assert.AreEqual(inspectorContent, File.ReadAllText(inspectorPath));
            Assert.AreEqual("project-owned notes", File.ReadAllText(userPath));
        }

        [Test]
        public void ReinstallWithoutChangesKeepsReferenceTimestamp()
        {
            var target = AssistantIntegrationRegistry.GetTargets().First(item => item.Id == "codex");
            SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target });
            var referencePath = Path.Combine(ProjectRoot, target.GetResolvedSkillDirectoryRelativePath(ProjectRoot), "references", "unity-cli-reference.md");
            var packageInfo = PackageInfo.FindForAssembly(typeof(AIBridgeProjectSettings).Assembly);
            var sourcePath = Path.Combine(packageInfo.resolvedPath, "Skill~", "references", "unity-cli-reference.md");
            File.SetLastWriteTimeUtc(referencePath, File.GetLastWriteTimeUtc(sourcePath).AddDays(1));
            var expectedTimestamp = File.GetLastWriteTimeUtc(referencePath);
            var commandPath = Path.Combine(Path.GetDirectoryName(referencePath), "command-reference.md");
            var commandTimestamp = expectedTimestamp.AddDays(1);
            File.SetLastWriteTimeUtc(commandPath, commandTimestamp);
            commandTimestamp = File.GetLastWriteTimeUtc(commandPath);
            var commandBytes = File.ReadAllBytes(commandPath);

            var result = SkillInstaller.InstallAssistantIntegrations(ProjectRoot, new[] { target }).Single();

            Assert.AreEqual(IntegrationAction.AlreadyUpToDate, result.SkillFileAction);
            Assert.AreEqual(expectedTimestamp, File.GetLastWriteTimeUtc(referencePath));
            Assert.AreEqual(commandTimestamp, File.GetLastWriteTimeUtc(commandPath));
            CollectionAssert.AreEqual(commandBytes, File.ReadAllBytes(commandPath));
        }

        private static string Normalize(string text)
        {
            return text.Replace("\r\n", "\n").TrimStart('\uFEFF').TrimEnd();
        }
    }
}
