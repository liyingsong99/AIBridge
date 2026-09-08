using System.IO;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class AssistantIntegrationTargetTests : AssistantIntegrationTestFixture
    {
        [Test]
        public void DefaultSkillRootUsesSharedPluginDirectory()
        {
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ".agents"));
            var target = CreateTarget("codex", ".codex/skills/aibridge");

            Assert.AreEqual(".aibridge/skills", target.GetResolvedSkillRootDirectoryRelativePath(ProjectRoot));
            Assert.AreEqual(".aibridge/skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(ProjectRoot));
            Assert.AreEqual(".aibridge/skills/aibridge/SKILL.md", target.GetResolvedSkillFileRelativePath(ProjectRoot));
            Assert.AreEqual(".aibridge/skills/aibridge-prefab-patch/SKILL.md", target.GetResolvedSiblingSkillFileRelativePath(ProjectRoot, "aibridge-prefab-patch"));
            Assert.AreEqual(".codex/skills", target.GetDefaultSkillRootDirectoryRelativePath());
        }

        [Test]
        public void SharedSkillRootDoesNotDependOnDetectedToolDirectories()
        {
            var target = CreateTarget("codex", ".codex/skills/aibridge");

            Assert.AreEqual(".aibridge/skills", target.GetResolvedSkillRootDirectoryRelativePath(ProjectRoot));
            Assert.AreEqual(".aibridge/skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(ProjectRoot));
        }

        [Test]
        public void LegacyPerAssistantSkillRootDoesNotOverrideSharedDirectory()
        {
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ".agents"));
            AIBridgeProjectSettings.Instance.SetAssistantSkillRootDirectory("codex", ".legacy-skills");
            var target = CreateTarget("codex", ".codex/skills/aibridge");

            Assert.AreEqual(".aibridge/skills", target.GetResolvedSkillRootDirectoryRelativePath(ProjectRoot));
            Assert.AreEqual(".aibridge/skills/aibridge", target.GetResolvedSkillDirectoryRelativePath(ProjectRoot));
            Assert.AreEqual(".aibridge/skills/aibridge-prefab-patch/SKILL.md", target.GetResolvedSiblingSkillFileRelativePath(ProjectRoot, "aibridge-prefab-patch"));
        }

        [Test]
        public void ProjectSkillRootDirectoryCanUseCustomDirectory()
        {
            AIBridgeProjectSettings.Instance.SkillRootDirectory = ".skill";
            var target = CreateTarget("claude", ".claude/skills/aibridge");

            Assert.AreEqual(".skill", target.GetResolvedSkillRootDirectoryRelativePath(ProjectRoot));
            Assert.AreEqual(".skill/aibridge", target.GetResolvedSkillDirectoryRelativePath(ProjectRoot));
            Assert.AreEqual(".skill/aibridge-prefab-patch/SKILL.md", target.GetResolvedSiblingSkillFileRelativePath(ProjectRoot, "aibridge-prefab-patch"));
        }

        [Test]
        public void PluginAndNativeTargetsShareTheSameResolvedRoot()
        {
            var claude = CreateTarget("claude", ".claude/skills/aibridge");
            var cline = CreateTarget("cline", ".clinerules/skills/aibridge");

            Assert.AreEqual(".aibridge/skills", claude.GetResolvedSkillRootDirectoryRelativePath(ProjectRoot));
            Assert.AreEqual(".aibridge/skills", cline.GetResolvedSkillRootDirectoryRelativePath(ProjectRoot));
            Assert.AreEqual(".clinerules/skills", cline.GetDefaultSkillRootDirectoryRelativePath());
        }

        private static AssistantIntegrationTarget CreateTarget(string id, string skillDirectoryRelativePath)
        {
            return new AssistantIntegrationTarget
            {
                Id = id,
                SupportsSkillDirectory = true,
                SkillDirectoryRelativePath = skillDirectoryRelativePath,
                SkillFileName = "SKILL.md"
            };
        }
    }
}
