using System.Collections.Generic;

namespace AIBridge.Editor
{
    /// <summary>
    /// AI 助手集成目标注册表
    ///
    /// Skills 目录支持说明：
    /// - 默认只安装一份到 `.aibridge/skills`；Claude/Cursor/Codex 用项目根 plugin.json 发现。
    /// - Cline 没有等价 plugin 清单，勾选时再同步一份到原生 `.clinerules/skills`。
    /// - 自定义 Skill 根目录仍可覆盖共享实体路径。
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
                    SupportsPluginManifest = true,
                    PluginManifestDirectoryName = ".claude-plugin",
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
                    SupportsPluginManifest = true,
                    PluginManifestDirectoryName = ".codex-plugin",
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
                    SupportsPluginManifest = true,
                    PluginManifestDirectoryName = ".cursor-plugin",
                    UsesCursorPluginPayload = true,
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
