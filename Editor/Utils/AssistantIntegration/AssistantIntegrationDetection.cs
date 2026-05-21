using System.IO;

namespace AIBridge.Editor
{
    internal sealed class AssistantIntegrationDetection
    {
        public string TargetId { get; set; }
        public bool IsDetected { get; set; }
        public string Detail { get; set; }
        public int Priority { get; set; }
    }

    internal static class AssistantIntegrationDetector
    {
        public const int NoSignalPriority = 0;
        public const int DirectorySignalPriority = 50;
        public const int RootRuleSignalPriority = 100;

        public static AssistantIntegrationDetection Detect(string projectRoot, AssistantIntegrationTarget target)
        {
            var rootRuleFileName = target.RootRuleFileName;
            if (!string.IsNullOrEmpty(rootRuleFileName))
            {
                var rootRulePath = Path.Combine(projectRoot, rootRuleFileName);
                if (File.Exists(rootRulePath))
                {
                    return new AssistantIntegrationDetection
                    {
                        TargetId = target.Id,
                        IsDetected = true,
                        Detail = rootRuleFileName,
                        Priority = RootRuleSignalPriority
                    };
                }
            }

            var detectionDirectories = target.DetectionDirectoryRelativePaths;
            if (detectionDirectories != null)
            {
                for (var i = 0; i < detectionDirectories.Length; i++)
                {
                    var relativeDirectory = detectionDirectories[i];
                    if (string.IsNullOrEmpty(relativeDirectory))
                    {
                        continue;
                    }

                    var directoryPath = Path.Combine(projectRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(directoryPath))
                    {
                        return new AssistantIntegrationDetection
                        {
                            TargetId = target.Id,
                            IsDetected = true,
                            Detail = relativeDirectory,
                            Priority = DirectorySignalPriority
                        };
                    }
                }
            }

            // 共享 .skills 目录只表示 AIBridge Skill 已存在，不能作为 Claude/Codex 等具体工具的默认勾选依据。
            return new AssistantIntegrationDetection
            {
                TargetId = target.Id,
                IsDetected = false,
                Detail = BuildExpectedSignal(target),
                Priority = NoSignalPriority
            };
        }

        private static string BuildExpectedSignal(AssistantIntegrationTarget target)
        {
            if (!string.IsNullOrEmpty(target.RootRuleFileName))
            {
                var detectionDirectories = target.DetectionDirectoryRelativePaths;
                if (detectionDirectories != null && detectionDirectories.Length > 0)
                {
                    return target.RootRuleFileName + " or " + detectionDirectories[0];
                }

                return target.RootRuleFileName;
            }

            return target.DisplayName;
        }
    }
}
