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
                    Id = "obra-superpowers",
                    DisplayName = "Superpowers",
                    RepositoryUrl = "https://github.com/obra/superpowers.git",
                    BranchOrTag = "main",
                    ManifestRelativePath = ".claude-plugin/plugin.json",
                    ScanRootRelativePath = "skills",
                    Description = AIBridgeEditorText.T(
                        "Claude Code workflow Skills for TDD, debugging, collaboration, code review, and other development practices.",
                        "Claude Code 工作流 Skills，包含 TDD、调试、协作、代码审查等开发实践。")
                },
                new RecommendedSkillRepository
                {
                    Id = "mattpocock-skills",
                    DisplayName = "Matt Pocock Skills",
                    RepositoryUrl = "https://github.com/mattpocock/skills.git",
                    BranchOrTag = "main",
                    ManifestRelativePath = ".claude-plugin/plugin.json",
                    ScanRootRelativePath = "skills",
                    Description = AIBridgeEditorText.T(
                        "General AI Skills for TypeScript, testing, diagnosis, PRDs, and related workflows.",
                        "TypeScript、测试、诊断、PRD 等通用 AI Skills。")
                },
                new RecommendedSkillRepository
                {
                    Id = "anthropic-skills",
                    DisplayName = "Anthropic Skills",
                    RepositoryUrl = "https://github.com/anthropics/skills.git",
                    BranchOrTag = "main",
                    ManifestRelativePath = ".claude-plugin/marketplace.json",
                    ScanRootRelativePath = "skills",
                    Description = AIBridgeEditorText.T(
                        "Anthropic example Skills for documents, frontend work, MCP, APIs, and other general capabilities.",
                        "Anthropic 示例 Skills，包含文档、前端、MCP、API 等通用能力。")
                }
            };
        }
    }
}
