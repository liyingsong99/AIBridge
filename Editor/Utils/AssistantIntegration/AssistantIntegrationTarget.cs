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
        public string[] DetectionDirectoryRelativePaths { get; set; }
        public string RootRuleTemplateRelativePath { get; set; }
        public MissingRootRuleStrategy MissingRootRuleStrategy { get; set; }

        public string GetSkillFileRelativePath()
        {
            if (!SupportsSkillDirectory || string.IsNullOrEmpty(SkillDirectoryRelativePath) || string.IsNullOrEmpty(SkillFileName))
            {
                return null;
            }

            return SkillDirectoryRelativePath.TrimEnd('/', '\\') + "/" + SkillFileName;
        }

        public string GetDefaultSkillRootDirectoryRelativePath()
        {
            if (!SupportsSkillDirectory || string.IsNullOrEmpty(SkillDirectoryRelativePath))
            {
                return null;
            }

            var normalized = NormalizeRelativePath(SkillDirectoryRelativePath);
            var separatorIndex = normalized.LastIndexOf('/');
            return separatorIndex >= 0 ? normalized.Substring(0, separatorIndex) : string.Empty;
        }

        public string GetSkillDirectoryName()
        {
            if (string.IsNullOrEmpty(SkillDirectoryRelativePath))
            {
                return null;
            }

            var normalized = NormalizeRelativePath(SkillDirectoryRelativePath);
            var separatorIndex = normalized.LastIndexOf('/');
            return separatorIndex >= 0 ? normalized.Substring(separatorIndex + 1) : normalized;
        }

        public string GetResolvedSkillRootDirectoryRelativePath(string projectRoot)
        {
            if (!SupportsSkillDirectory)
            {
                return null;
            }

            var customSkillRootDirectory = AIBridgeProjectSettings.Instance.SkillRootDirectory;
            if (!string.IsNullOrEmpty(customSkillRootDirectory))
            {
                return customSkillRootDirectory;
            }

            // 默认使用各工具自己的 Skills 根目录，保证 Codex/Cursor/Claude 能按原生规则发现 Skill。
            return GetDefaultSkillRootDirectoryRelativePath();
        }

        public string GetResolvedSkillDirectoryRelativePath(string projectRoot)
        {
            var skillDirectoryName = GetSkillDirectoryName();
            if (string.IsNullOrEmpty(skillDirectoryName))
            {
                return null;
            }

            var skillRootDirectory = GetResolvedSkillRootDirectoryRelativePath(projectRoot);
            return string.IsNullOrEmpty(skillRootDirectory)
                ? skillDirectoryName
                : NormalizeRelativePath(skillRootDirectory) + "/" + skillDirectoryName;
        }

        public string GetResolvedSkillFileRelativePath(string projectRoot)
        {
            if (!SupportsSkillDirectory || string.IsNullOrEmpty(SkillFileName))
            {
                return null;
            }

            var skillDirectory = GetResolvedSkillDirectoryRelativePath(projectRoot);
            return string.IsNullOrEmpty(skillDirectory) ? null : skillDirectory + "/" + SkillFileName;
        }

        public string GetResolvedSiblingSkillFileRelativePath(string projectRoot, string skillDirectoryName)
        {
            if (!SupportsSkillDirectory || string.IsNullOrEmpty(skillDirectoryName) || string.IsNullOrEmpty(SkillFileName))
            {
                return null;
            }

            var skillRootDirectory = GetResolvedSkillRootDirectoryRelativePath(projectRoot);
            return string.IsNullOrEmpty(skillRootDirectory)
                ? skillDirectoryName + "/" + SkillFileName
                : NormalizeRelativePath(skillRootDirectory) + "/" + skillDirectoryName + "/" + SkillFileName;
        }

        public string GetSiblingSkillFileRelativePath(string skillDirectoryName)
        {
            if (!SupportsSkillDirectory || string.IsNullOrEmpty(SkillDirectoryRelativePath) || string.IsNullOrEmpty(SkillFileName))
            {
                return null;
            }

            var normalized = SkillDirectoryRelativePath.Replace('\\', '/').TrimEnd('/');
            var separatorIndex = normalized.LastIndexOf('/');
            var skillRoot = separatorIndex >= 0 ? normalized.Substring(0, separatorIndex) : string.Empty;
            return string.IsNullOrEmpty(skillRoot)
                ? skillDirectoryName + "/" + SkillFileName
                : skillRoot + "/" + skillDirectoryName + "/" + SkillFileName;
        }

        private static string NormalizeRelativePath(string value)
        {
            return value.Replace('\\', '/').Trim('/');
        }
    }
}
