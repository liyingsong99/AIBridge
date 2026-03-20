using System.Collections.Generic;

namespace AIBridge.Editor
{
    internal enum IntegrationAction
    {
        None,
        CreatedFile,
        InsertedBlock,
        UpdatedBlock,
        MigratedLegacyBlock,
        AlreadyUpToDate,
        SkippedMissing,
        SkippedDisabled,
        Failed
    }

    internal sealed class AssistantIntegrationResult
    {
        public string AssistantId { get; set; }
        public string RootRuleFilePath { get; set; }
        public string SkillFilePath { get; set; }
        public List<string> SkillFilePaths { get; set; }
        public List<string> InstalledSkillIds { get; set; }
        public IntegrationAction RootRuleAction { get; set; }
        public IntegrationAction SkillFileAction { get; set; }
        public string Message { get; set; }
    }
}
