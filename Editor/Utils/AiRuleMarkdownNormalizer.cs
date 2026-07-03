using System;

namespace AIBridge.Editor
{
    internal static class AiRuleMarkdownNormalizer
    {
        public static string TrimLineEndPunctuation(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }

            var normalized = content.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');
            var inCodeBlock = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    inCodeBlock = !inCodeBlock;
                    continue;
                }

                if (inCodeBlock
                    || string.IsNullOrWhiteSpace(trimmed)
                    || trimmed.StartsWith("|", StringComparison.Ordinal)
                    || trimmed.StartsWith("#", StringComparison.Ordinal)
                    || trimmed.StartsWith("---", StringComparison.Ordinal))
                {
                    continue;
                }

                lines[i] = TrimRuleLine(lines[i]);
            }

            return string.Join("\n", lines);
        }

        private static string TrimRuleLine(string line)
        {
            var index = line.Length - 1;
            while (index >= 0 && char.IsWhiteSpace(line[index]))
            {
                index--;
            }

            if (index >= 0 && (line[index] == '。' || line[index] == '.'))
            {
                return line.Remove(index, 1);
            }

            return line;
        }
    }
}
