using System.Collections.Generic;

namespace AIBridge.Editor
{
    /// <summary>
    /// AI 助手集成目标注册表
    ///
        /// Skills 目录支持说明：
        /// - 默认只生成一份到 `.aibridge/skills`。
        /// - 已选工具再镜像到原生目录：`.claude/skills`、`.cursor/skills`、`.codex/skills`、`.clinerules/skills`。
        /// - 项目根 plugin.json 仅为可选辅助，不是自动发现路径。
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
