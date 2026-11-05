using System.Collections.Generic;

namespace AIBridge.Editor
{
    /// <summary>
    /// AI 助手集成目标注册表
    ///
    /// 路径约定（2026-10 官方文档核实）：
    /// - Codex / DeepSeek Harness / ZCode 都读取项目根 AGENTS.md，且都支持项目级 .agents/skills；
    ///   三者共用同一个注入块与技能目录，安装器按“RootRule 文件 + Skill 根”自动分组，避免互相覆盖。
    /// - Claude Code 读取 CLAUDE.md（存在时不再读 AGENTS.md），技能目录为 .claude/skills。
    /// - WorkBuddy（腾讯 CodeBuddy）读取 CODEBUDDY.md / .codebuddy/rules，技能目录为 .codebuddy/skills。
    /// - Codex 的 .codex/skills 仅对应用户级 $CODEX_HOME/skills，不作为项目级安装目录。
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
                    Id = "codex",
                    DisplayName = "Codex",
                    ShortDescription = "AGENTS.md + .agents/skills",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = "AGENTS.md",
                    SkillDirectoryRelativePath = ".agents/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    DetectionDirectoryRelativePaths = new[] { ".codex", ".codex-plugin" },
                    RootRuleTemplateRelativePath = SharedRootRuleTemplateRelativePath,
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    LegacySkillRootDirectoryRelativePaths = new[] { ".codex/skills" },
                    DocsUrl = "https://developers.openai.com/codex/guides/agents-md",
                    SortOrder = 10,
                    IsCommon = true
                },
                new AssistantIntegrationTarget
                {
                    Id = "claude",
                    DisplayName = "Claude Code",
                    ShortDescription = "CLAUDE.md + .claude/skills",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = "CLAUDE.md",
                    SkillDirectoryRelativePath = ".claude/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    DetectionDirectoryRelativePaths = new[] { ".claude", ".claude-plugin" },
                    RootRuleTemplateRelativePath = SharedRootRuleTemplateRelativePath,
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    DocsUrl = "https://code.claude.com/docs/en/memory",
                    SortOrder = 20,
                    IsCommon = true
                },
                new AssistantIntegrationTarget
                {
                    Id = "cursor",
                    DisplayName = "Cursor",
                    ShortDescription = ".cursor/rules/aibridge.mdc + .cursor/skills",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = ".cursor/rules/aibridge.mdc",
                    SkillDirectoryRelativePath = ".cursor/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    DetectionDirectoryRelativePaths = new[] { ".cursor", ".cursor-plugin" },
                    RootRuleTemplateRelativePath = SharedRootRuleTemplateRelativePath,
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    DocsUrl = "https://docs.cursor.com/context/rules",
                    SortOrder = 30,
                    IsCommon = true
                },
                new AssistantIntegrationTarget
                {
                    Id = "deepseek-harness",
                    DisplayName = "DeepSeek Harness",
                    ShortDescription = "AGENTS.md + .agents/skills",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = "AGENTS.md",
                    SkillDirectoryRelativePath = ".agents/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    DetectionDirectoryRelativePaths = new[] { ".dsh" },
                    RootRuleTemplateRelativePath = SharedRootRuleTemplateRelativePath,
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    DocsUrl = "https://github.com/deepseek-ai/deepseek-harness",
                    SortOrder = 40,
                    IsCommon = true
                },
                new AssistantIntegrationTarget
                {
                    Id = "zcode",
                    DisplayName = "ZCode",
                    ShortDescription = "AGENTS.md + .agents/skills",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = "AGENTS.md",
                    SkillDirectoryRelativePath = ".agents/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    DetectionDirectoryRelativePaths = new[] { ".zcode" },
                    RootRuleTemplateRelativePath = SharedRootRuleTemplateRelativePath,
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    DocsUrl = "https://github.com/zai-org/ZCode",
                    SortOrder = 50,
                    IsCommon = true
                },
                new AssistantIntegrationTarget
                {
                    Id = "workbuddy",
                    DisplayName = "WorkBuddy",
                    ShortDescription = "CODEBUDDY.md + .codebuddy/skills",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = "CODEBUDDY.md",
                    SkillDirectoryRelativePath = ".codebuddy/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    DetectionDirectoryRelativePaths = new[] { ".codebuddy" },
                    RootRuleTemplateRelativePath = SharedRootRuleTemplateRelativePath,
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    DocsUrl = "https://www.workbuddy.cn/docs/",
                    SortOrder = 60,
                    IsCommon = true
                },
                new AssistantIntegrationTarget
                {
                    Id = "cline",
                    DisplayName = "Cline",
                    ShortDescription = ".clinerules/aibridge.md + .clinerules/skills",
                    SupportsSkillDirectory = true,
                    RootRuleFileName = ".clinerules/aibridge.md",
                    SkillDirectoryRelativePath = ".clinerules/skills/aibridge",
                    SkillFileName = "SKILL.md",
                    DetectionDirectoryRelativePaths = new[] { ".clinerules" },
                    RootRuleTemplateRelativePath = SharedRootRuleTemplateRelativePath,
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    DocsUrl = "https://docs.cline.bot/features/cline-rules",
                    SortOrder = 70,
                    IsCommon = false
                }
            };
        }
    }
}
