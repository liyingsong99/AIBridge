using System.Collections.Generic;

namespace AIBridge.Editor
{
    /// <summary>
    /// AI 助手集成目标注册表
    /// 
    /// Skills 目录支持说明：
    /// - AIBridge 默认统一安装到项目根目录 skills/，不同工具只写入规则或插件适配层。
    /// </summary>
    internal static class AssistantIntegrationRegistry
    {
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
                    RootRuleTemplateRelativePath = "Templates~/Rules/Claude.RootRule.md",
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    TemplateId = "unity-integration",
                    RuleTarget = "root-rule"
                },
                new AssistantIntegrationTarget
                {
                    Id = "codex",
                    DisplayName = "Codex",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = "AGENTS.md",
                    SkillDirectoryRelativePath = ".codex/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    RootRuleTemplateRelativePath = "Templates~/Rules/Codex.RootRule.md",
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    TemplateId = "unity-project-rules",
                    RuleTarget = "root-rule"
                },
                new AssistantIntegrationTarget
                {
                    Id = "cursor",
                    DisplayName = "Cursor",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = ".cursor/rules/aibridge.mdc",
                    SkillDirectoryRelativePath = ".cursor/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    RootRuleTemplateRelativePath = "Templates~/Rules/Cursor.RootRule.md",
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    TemplateId = "unity-project-rules",
                    RuleTarget = "root-rule"
                },
                new AssistantIntegrationTarget
                {
                    Id = "cline",
                    DisplayName = "Cline",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = ".clinerules/aibridge.md",
                    SkillDirectoryRelativePath = ".clinerules/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    RootRuleTemplateRelativePath = "Templates~/Rules/Cline.RootRule.md",
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    TemplateId = "unity-project-rules",
                    RuleTarget = "root-rule"
                }
            };
        }
    }
}
