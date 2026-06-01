namespace AIBridge.Editor
{
    using System.Collections.Generic;

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
        SkippedUnsafePath,
        Failed
    }

    internal sealed class AssistantIntegrationResult
    {
        public string AssistantId { get; set; }
        public string RootRuleFilePath { get; set; }
        public string SkillFilePath { get; set; }
        public List<string> AdditionalSkillFilePaths { get; } = new List<string>();
        public IntegrationAction RootRuleAction { get; set; }
        public IntegrationAction SkillFileAction { get; set; }
        public string Message { get; set; }
    }
}
