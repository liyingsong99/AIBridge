using System.Collections.Generic;

namespace AIBridge.Editor
{
    internal static class RecommendedSkillRepositories
    {
        public static IReadOnlyList<RecommendedSkillRepository> GetDefaultRepositories()
        {
            return new[]
            {
                new RecommendedSkillRepository
                {
                    Id = "mattpocock-skills",
                    DisplayName = "Matt Pocock Skills",
                    RepositoryUrl = "https://github.com/mattpocock/skills.git",
                    BranchOrTag = "main",
                    ManifestRelativePath = ".claude-plugin/plugin.json",
                    ScanRootRelativePath = "skills",
                    Description = "TypeScript、测试、诊断、PRD 等通用 AI Skills。"
                }
            };
        }
    }
}
