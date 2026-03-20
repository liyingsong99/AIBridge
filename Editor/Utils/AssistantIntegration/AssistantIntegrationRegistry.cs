using System.Collections.Generic;

namespace AIBridge.Editor
{
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
                    SkillRootRelativePath = ".claude/skills",
                    PrimarySkillId = "aibridge",
                    RootRuleTemplateRelativePath = "Templates~/Rules/Claude.RootRule.md",
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    TemplateId = "unity-integration",
                    RuleTarget = "root-rule"
                },
                new AssistantIntegrationTarget
                {
                    Id = "codex",
                    DisplayName = "Codex",
                    SupportsSkillDirectory = false,
                    RootRuleFileName = "AGENTS.md",
                    SkillRootRelativePath = null,
                    PrimarySkillId = "aibridge",
                    RootRuleTemplateRelativePath = "Templates~/Rules/Codex.RootRule.md",
                    MissingRootRuleStrategy = MissingRootRuleStrategy.CreateWithInjectedBlock,
                    TemplateId = "unity-project-rules",
                    RuleTarget = "root-rule"
                }
            };
        }
    }
}
