using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AIBridge.Editor
{
    [Serializable]
    internal sealed class InjectedBlockMetadata
    {
        public string assistant;
        public string templateId;
        public int version;
        public string target;
    }

    internal sealed class InjectionBlockMatch
    {
        public int StartIndex { get; set; }
        public int Length { get; set; }
        public string RawBlock { get; set; }
        public string Body { get; set; }
        public InjectedBlockMetadata Metadata { get; set; }
    }

    internal static class InjectionBlockParser
    {
        private static readonly Regex BlockRegex = new Regex(
            @"<!-- AIBRIDGE:START (?<metadata>\{.*?\}) -->\s*(?<body>.*?)\s*<!-- AIBRIDGE:END -->",
            RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// 查找匹配的注入块（仅按 templateId 和 target 匹配，忽略 assistant）
        /// 这样可以实现同一 templateId 的互斥，避免重复注入
        /// </summary>
        public static InjectionBlockMatch FindMatchingBlock(string content, string templateId, string target)
        {
            var matches = BlockRegex.Matches(content);
            foreach (Match match in matches)
            {
                var metadata = TryParseMetadata(match.Groups["metadata"].Value);
                if (metadata == null)
                {
                    continue;
                }

                // 仅匹配 templateId 和 target，忽略 assistant 字段
                // 这样同一 templateId 只能有一个注入块，实现互斥
                if (metadata.templateId == templateId && metadata.target == target)
                {
                    return new InjectionBlockMatch
                    {
                        StartIndex = match.Index,
                        Length = match.Length,
                        RawBlock = match.Value,
                        Body = match.Groups["body"].Value.Trim(),
                        Metadata = metadata
                    };
                }
            }

            return null;
        }

        /// <summary>
        /// 查找所有匹配指定 templateId 和 target 的注入块（用于迁移旧的重复块）
        /// </summary>
        public static List<InjectionBlockMatch> FindAllMatchingBlocks(string content, string templateId, string target)
        {
            var results = new List<InjectionBlockMatch>();
            var matches = BlockRegex.Matches(content);
            foreach (Match match in matches)
            {
                var metadata = TryParseMetadata(match.Groups["metadata"].Value);
                if (metadata == null)
                {
                    continue;
                }

                if (metadata.templateId == templateId && metadata.target == target)
                {
                    results.Add(new InjectionBlockMatch
                    {
                        StartIndex = match.Index,
                        Length = match.Length,
                        RawBlock = match.Value,
                        Body = match.Groups["body"].Value.Trim(),
                        Metadata = metadata
                    });
                }
            }

            return results;
        }

        public static string BuildBlock(RuleTemplateMetadata metadata, string renderedBody)
        {
            var payload = new InjectedBlockMetadata
            {
                assistant = metadata.Assistant,
                templateId = metadata.TemplateId,
                version = metadata.Version,
                target = metadata.Target
            };

            return "<!-- AIBRIDGE:START " + JsonUtility.ToJson(payload) + " -->\n"
                + renderedBody.Trim() + "\n"
                + "<!-- AIBRIDGE:END -->";
        }

        private static InjectedBlockMetadata TryParseMetadata(string json)
        {
            try
            {
                return JsonUtility.FromJson<InjectedBlockMetadata>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
