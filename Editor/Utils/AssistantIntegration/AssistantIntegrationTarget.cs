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
        public string SkillDirectoryRelativePath { get; set; }
        public string SkillFileName { get; set; }
        public string RootRuleTemplateRelativePath { get; set; }
        public MissingRootRuleStrategy MissingRootRuleStrategy { get; set; }
        public string TemplateId { get; set; }
        public string RuleTarget { get; set; }

        public string GetSkillFileRelativePath()
        {
            if (!SupportsSkillDirectory || string.IsNullOrEmpty(SkillDirectoryRelativePath) || string.IsNullOrEmpty(SkillFileName))
            {
                return null;
            }

            return SkillDirectoryRelativePath.TrimEnd('/', '\\') + "/" + SkillFileName;
        }
    }
}
