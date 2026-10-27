using System;
using System.Collections.Generic;
using System.Linq;

namespace AIBridge.Editor
{
    /// <summary>
    /// 适配组：共用同一个 RootRule 文件与同一个 Skill 根的多个工具只需要安装一次。
    /// 典型场景：Codex / DeepSeek Harness / ZCode 都读取 AGENTS.md 且都支持 .agents/skills。
    /// </summary>
    internal sealed class AssistantIntegrationGroup
    {
        public string Key { get; set; }
        public string RootRuleFileName { get; set; }
        public string SkillRootDirectoryRelativePath { get; set; }
        public AssistantIntegrationTarget Representative { get; set; }
        public List<AssistantIntegrationTarget> Targets { get; } = new List<AssistantIntegrationTarget>();
    }

    internal static class AssistantIntegrationGroupBuilder
    {
        public static List<AssistantIntegrationGroup> Build(string projectRoot, IEnumerable<AssistantIntegrationTarget> targets)
        {
            var groups = new List<AssistantIntegrationGroup>();
            var groupByKey = new Dictionary<string, AssistantIntegrationGroup>(StringComparer.OrdinalIgnoreCase);
            var targetList = (targets ?? Enumerable.Empty<AssistantIntegrationTarget>()).Where(target => target != null);
            foreach (var target in targetList)
            {
                var key = BuildKey(projectRoot, target);
                AssistantIntegrationGroup group;
                if (!groupByKey.TryGetValue(key, out group))
                {
                    group = new AssistantIntegrationGroup
                    {
                        Key = key,
                        RootRuleFileName = NormalizeRelativePath(target.RootRuleFileName),
                        SkillRootDirectoryRelativePath = NormalizeRelativePath(target.GetResolvedSkillRootDirectoryRelativePath(projectRoot)),
                        Representative = target
                    };
                    groupByKey[key] = group;
                    groups.Add(group);
                }

                group.Targets.Add(target);
            }

            return groups;
        }

        public static string BuildKey(string projectRoot, AssistantIntegrationTarget target)
        {
            if (target == null)
            {
                return string.Empty;
            }

            // 使用解析后的 Skill 根：自定义 Skills 目录下不同原始配置也会折叠为同一组。
            return NormalizeRelativePath(target.RootRuleFileName)
                + "|"
                + NormalizeRelativePath(target.GetResolvedSkillRootDirectoryRelativePath(projectRoot));
        }

        public static string NormalizeRelativePath(string value)
        {
            return string.IsNullOrEmpty(value)
                ? string.Empty
                : value.Replace('\\', '/').Trim('/');
        }

        public static bool IsRootRuleShared(string rootRuleFileName)
        {
            var normalized = NormalizeRelativePath(rootRuleFileName);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            return AssistantIntegrationRegistry.GetTargets().Count(target =>
                target != null
                && string.Equals(NormalizeRelativePath(target.RootRuleFileName), normalized, StringComparison.OrdinalIgnoreCase)) > 1;
        }

        public static string GetSharedRootRuleOwners(string rootRuleFileName)
        {
            var normalized = NormalizeRelativePath(rootRuleFileName);
            return string.Join(
                ", ",
                AssistantIntegrationRegistry.GetTargets()
                    .Where(target => target != null
                        && string.Equals(NormalizeRelativePath(target.RootRuleFileName), normalized, StringComparison.OrdinalIgnoreCase))
                    .Select(target => target.DisplayName)
                    .ToArray());
        }
    }
}
