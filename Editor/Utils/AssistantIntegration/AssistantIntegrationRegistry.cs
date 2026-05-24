using System.Collections.Generic;

namespace AIBridge.Editor
{
    /// <summary>
    /// AI 助手集成目标注册表
    /// 
    /// Skills 目录支持说明：
    /// - AIBridge 默认安装到各工具自己的 skills 根目录，自定义目录仅作为高级覆盖项。
    /// </summary>
    internal static class AssistantIntegrationRegistry
    {
        private const string SharedRootRuleTemplateRelativePath = "Templates~/Rules/AIBridge.RootRule.md";

        public static IReadOnlyList<AssistantIntegrationTarget> GetTargets()
        {
            return new[]
            {
                new AssistantIntegrationTarget
                {
                    Id = "claude",
                    DisplayName = "Claude",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = "CLAUDE.md",
                    SkillDirectoryRelativePath = ".claude/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    DetectionDirectoryRelativePaths = new[] { ".claude", ".claude-plugin" },
                    RootRuleTemplateRelativePath = SharedRootRuleTemplateRelativePath,
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock
                },
                new AssistantIntegrationTarget
                {
                    Id = "codex",
                    DisplayName = "Codex",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = "AGENTS.md",
                    SkillDirectoryRelativePath = ".codex/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    DetectionDirectoryRelativePaths = new[] { ".agents", ".codex", ".codex-plugin" },
                    RootRuleTemplateRelativePath = SharedRootRuleTemplateRelativePath,
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock
                },
                new AssistantIntegrationTarget
                {
                    Id = "cursor",
                    DisplayName = "Cursor",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = ".cursor/rules/aibridge.mdc",
                    SkillDirectoryRelativePath = ".cursor/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    DetectionDirectoryRelativePaths = new[] { ".cursor", ".cursor-plugin" },
                    RootRuleTemplateRelativePath = SharedRootRuleTemplateRelativePath,
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock
                },
                new AssistantIntegrationTarget
                {
                    Id = "cline",
                    DisplayName = "Cline",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = ".clinerules/aibridge.md",
                    SkillDirectoryRelativePath = ".clinerules/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    DetectionDirectoryRelativePaths = new[] { ".clinerules" },
                    RootRuleTemplateRelativePath = SharedRootRuleTemplateRelativePath,
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock
                }
            };
        }
    }
}
