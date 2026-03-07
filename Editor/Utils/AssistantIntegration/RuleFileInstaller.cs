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
            var existingBlock = InjectionBlockParser.FindMatchingBlock(content, target.Id, template.Metadata.TemplateId, template.Metadata.Target);
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
    }
}
