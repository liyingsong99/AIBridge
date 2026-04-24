using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AIBridge.Editor
{
    internal static class RuleFileInstaller
    {
        private static readonly Regex LegacyClaudeBlockRegex = new Regex(
            @"## AIBridge Unity Integration\s+.*?\*\*Skill Documentation\*\*: \[AIBridge Skill\]\(/\.claude/skills/aibridge/SKILL\.md\)\s*",
            RegexOptions.Singleline | RegexOptions.Compiled);

        public static IntegrationAction Install(
            string projectRoot,
            AssistantIntegrationTarget target,
            RuleTemplate template,
            IDictionary<string, string> tokens,
            out string ruleFilePath)
        {
            ruleFilePath = Path.Combine(projectRoot, target.RootRuleFileName);
            var renderedBody = RuleTemplateRenderer.Render(template.Body, tokens);
            var renderedBlock = InjectionBlockParser.BuildBlock(template.Metadata, renderedBody);

            if (!File.Exists(ruleFilePath))
            {
                EnsureParentDirectory(ruleFilePath);
                switch (target.MissingRootRuleStrategy)
                {
                    case MissingRootRuleStrategy.Skip:
                        return IntegrationAction.SkippedMissing;
                    case MissingRootRuleStrategy.CreateMinimalFile:
                        File.WriteAllText(ruleFilePath, BuildMinimalFile(renderedBlock), Encoding.UTF8);
                        return IntegrationAction.CreatedFile;
                    default:
                        File.WriteAllText(ruleFilePath, renderedBlock + "\n", Encoding.UTF8);
                        return IntegrationAction.CreatedFile;
                }
            }

            var content = File.ReadAllText(ruleFilePath, Encoding.UTF8);
            
            // 迁移逻辑：清理旧的重复块（同一 templateId 的多个 assistant 版本）
            var allMatchingBlocks = InjectionBlockParser.FindAllMatchingBlocks(content, template.Metadata.TemplateId, template.Metadata.Target);
            if (allMatchingBlocks.Count > 1)
            {
                // 发现重复块，保留第一个，移除其他
                for (int i = allMatchingBlocks.Count - 1; i > 0; i--)
                {
                    var blockToRemove = allMatchingBlocks[i];
                    content = content.Substring(0, blockToRemove.StartIndex)
                        + content.Substring(blockToRemove.StartIndex + blockToRemove.Length);
                }
                // 清理多余的空行
                content = Regex.Replace(content, @"\n{3,}", "\n\n");
            }
            
            // 使用新的二元组匹配逻辑（templateId + target），忽略 assistant
            var existingBlock = InjectionBlockParser.FindMatchingBlock(content, template.Metadata.TemplateId, template.Metadata.Target);
            if (existingBlock != null)
            {
                if (existingBlock.Metadata.version == template.Metadata.Version && Normalize(existingBlock.RawBlock) == Normalize(renderedBlock))
                {
                    return IntegrationAction.AlreadyUpToDate;
                }

                var updated = content.Substring(0, existingBlock.StartIndex)
                    + renderedBlock
                    + content.Substring(existingBlock.StartIndex + existingBlock.Length);
                File.WriteAllText(ruleFilePath, NormalizeTrailingWhitespace(updated), Encoding.UTF8);
                return IntegrationAction.UpdatedBlock;
            }

            if (target.Id == "claude")
            {
                string migratedContent;
                if (TryMigrateLegacyClaudeBlock(content, renderedBlock, out migratedContent))
                {
                    File.WriteAllText(ruleFilePath, NormalizeTrailingWhitespace(migratedContent), Encoding.UTF8);
                    return IntegrationAction.MigratedLegacyBlock;
                }
            }

            var appended = AppendBlock(content, renderedBlock);
            File.WriteAllText(ruleFilePath, NormalizeTrailingWhitespace(appended), Encoding.UTF8);
            return IntegrationAction.InsertedBlock;
        }

        private static bool TryMigrateLegacyClaudeBlock(string content, string renderedBlock, out string migratedContent)
        {
            var match = LegacyClaudeBlockRegex.Match(content);
            if (match.Success)
            {
                migratedContent = content.Substring(0, match.Index)
                    + renderedBlock
                    + content.Substring(match.Index + match.Length);
                return true;
            }

            migratedContent = null;
            return false;
        }

        private static string AppendBlock(string content, string block)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return block + "\n";
            }

            return content.TrimEnd() + "\n\n" + block + "\n";
        }

        private static void EnsureParentDirectory(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static string BuildMinimalFile(string renderedBlock)
        {
            return "# Project Instructions\n\n" + renderedBlock + "\n";
        }

        private static string Normalize(string value)
        {
            return value.Replace("\r\n", "\n").Trim();
        }

        private static string NormalizeTrailingWhitespace(string value)
        {
            return value.Replace("\r\n", "\n").TrimEnd() + "\n";
        }

        /// <summary>
        /// 从文件中移除指定的 AIBridge 注入块
        /// </summary>
        /// <returns>如果找到并移除了块返回 true，否则返回 false</returns>
        public static bool RemoveBlock(
            string projectRoot,
            AssistantIntegrationTarget target,
            RuleTemplate template)
        {
            var ruleFilePath = Path.Combine(projectRoot, target.RootRuleFileName);
            if (!File.Exists(ruleFilePath))
            {
                return false;
            }

            var content = File.ReadAllText(ruleFilePath, Encoding.UTF8);
            // 使用新的二元组匹配逻辑（templateId + target），忽略 assistant
            var existingBlock = InjectionBlockParser.FindMatchingBlock(content, template.Metadata.TemplateId, template.Metadata.Target);
            if (existingBlock == null)
            {
                return false;
            }

            // 移除注入块
            var updated = content.Substring(0, existingBlock.StartIndex)
                + content.Substring(existingBlock.StartIndex + existingBlock.Length);

            // 清理多余的空行（最多保留两个连续换行）
            updated = Regex.Replace(updated, @"\n{3,}", "\n\n");

            File.WriteAllText(ruleFilePath, NormalizeTrailingWhitespace(updated), Encoding.UTF8);
            return true;
        }
    }
}
