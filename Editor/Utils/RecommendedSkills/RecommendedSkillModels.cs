using System.Collections.Generic;

namespace AIBridge.Editor
{
    internal enum RecommendedSkillInstallState
    {
        NotInstalled,
        Installed,
        UpdateAvailable
    }

    internal sealed class RecommendedSkillRepository
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string RepositoryUrl { get; set; }
        public string BranchOrTag { get; set; }
        public string ManifestRelativePath { get; set; }
        public string ScanRootRelativePath { get; set; }
        public string Description { get; set; }
    }

    internal sealed class RecommendedSkillInfo
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string SourceRelativePath { get; set; }
        public string RepositoryId { get; set; }
        public string RepositoryUrl { get; set; }
        public string BranchOrTag { get; set; }
        public string Commit { get; set; }
        public RecommendedSkillInstallState InstallState { get; set; }
    }

    internal sealed class RecommendedSkillInstallResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string InstalledDirectory { get; set; }
    }

    internal sealed class InstalledSkillRecord
    {
        public string Name;
        public string RepositoryId;
        public string RepositoryUrl;
        public string SourceRelativePath;
        public string BranchOrTag;
        public string Commit;
        public long InstalledAtUtcTicks;
    }

    internal sealed class InstalledSkillRecordList
    {
        public List<InstalledSkillRecord> Records = new List<InstalledSkillRecord>();
    }
}
