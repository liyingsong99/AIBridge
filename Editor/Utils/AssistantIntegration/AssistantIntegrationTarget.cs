using System;

namespace AIBridge.Editor
{
    internal enum MissingRootRuleStrategy
    {
        Skip,
        CreateMinimalFile,
        CreateWithInjectedBlock
    }

    internal sealed class AssistantIntegrationTarget
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public bool SupportsSkillDirectory { get; set; }
        public string RootRuleFileName { get; set; }
        public string SkillRootRelativePath { get; set; }
        public string PrimarySkillId { get; set; }
        public string RootRuleTemplateRelativePath { get; set; }
        public MissingRootRuleStrategy MissingRootRuleStrategy { get; set; }
        public string TemplateId { get; set; }
        public string RuleTarget { get; set; }

        public string GetPrimarySkillFileRelativePath()
        {
            return GetSkillFileRelativePath(PrimarySkillId);
        }

        public string GetSkillFileRelativePath(string skillId)
        {
            if (!SupportsSkillDirectory || string.IsNullOrEmpty(SkillRootRelativePath) || string.IsNullOrEmpty(skillId))
            {
                return null;
            }

            return SkillRootRelativePath.TrimEnd('/', '\\') + "/" + skillId + "/SKILL.md";
        }
    }
}
