using System;
using System.Collections.Generic;
using System.IO;
using AIBridgeCLI.Core;
using Newtonsoft.Json;

namespace AIBridgeCLI.Commands
{
    public sealed class BatchDialogAutoClickPlan
    {
        public const string RequestParamName = "__cliDialogAutoClick";

        private const string ScriptStateFileName = "script-state.json";

        public BatchDialogAutoClickPlan()
        {
            Rules = new List<BatchDialogAutoClickRule>();
        }

        public List<BatchDialogAutoClickRule> Rules { get; set; }

        public bool HasRules
        {
            get { return Rules != null && Rules.Count > 0; }
        }

        public static void AttachToRequest(CommandRequest request, string scriptText)
        {
            if (request == null || request.@params == null)
            {
                return;
            }

            var plan = Parse(scriptText);
            if (plan.HasRules)
            {
                request.@params[RequestParamName] = plan;
            }
        }

        public static BatchDialogAutoClickPlan ExtractFromRequest(CommandRequest request)
        {
            if (request == null || request.@params == null)
            {
                return null;
            }

            object value;
            if (!request.@params.TryGetValue(RequestParamName, out value))
            {
                return null;
            }

            request.@params.Remove(RequestParamName);
            return value as BatchDialogAutoClickPlan;
        }

        public static bool IsDialogClickDirectiveLine(string line)
        {
            string arguments;
            return TryGetDialogClickArguments(line, out arguments);
        }

        public static BatchDialogAutoClickPlan Parse(string scriptText)
        {
            var plan = new BatchDialogAutoClickPlan();
            if (string.IsNullOrEmpty(scriptText))
            {
                return plan;
            }

            var commandIndex = 0;
            var lines = scriptText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = NormalizeExecutableLine(lines[i]);
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                string arguments;
                if (TryGetDialogClickArguments(line, out arguments))
                {
                    var targets = ParseTargets(arguments);
                    if (targets.Count == 0)
                    {
                        throw new ArgumentException("dialog click requires at least one choice or button text.");
                    }

                    plan.Rules.Add(new BatchDialogAutoClickRule
                    {
                        CommandIndex = commandIndex,
                        Source = line,
                        Targets = targets
                    });
                }

                commandIndex++;
            }

            return plan;
        }

        public List<DialogAutoClickTarget> GetActiveTargets(string requestId, bool isPreflight)
        {
            if (!HasRules)
            {
                return null;
            }

            BatchDialogAutoClickRule activeRule = null;
            if (isPreflight)
            {
                activeRule = GetLastRuleAtFirstCommand();
            }
            else
            {
                // 通过 Unity 侧落盘的 CurrentLine 判断声明是否已经执行，保证只影响后续步骤。
                int currentLine;
                if (!TryReadCurrentBatchLine(requestId, out currentLine))
                {
                    return null;
                }

                activeRule = GetLastRuleBeforeCurrentLine(currentLine);
            }

            return activeRule != null ? activeRule.Targets : null;
        }

        private BatchDialogAutoClickRule GetLastRuleAtFirstCommand()
        {
            BatchDialogAutoClickRule activeRule = null;
            foreach (var rule in Rules)
            {
                if (rule != null && rule.CommandIndex == 0)
                {
                    activeRule = rule;
                }
            }

            return activeRule;
        }

        private BatchDialogAutoClickRule GetLastRuleBeforeCurrentLine(int currentLine)
        {
            BatchDialogAutoClickRule activeRule = null;
            foreach (var rule in Rules)
            {
                if (rule != null && rule.CommandIndex < currentLine)
                {
                    // 重复声明时使用最新已执行规则，避免不同阶段的按钮策略互相叠加。
                    activeRule = rule;
                }
            }

            return activeRule;
        }

        private static bool TryReadCurrentBatchLine(string requestId, out int currentLine)
        {
            currentLine = 0;
            if (string.IsNullOrEmpty(requestId))
            {
                return false;
            }

            var statePath = Path.Combine(PathHelper.GetExchangeDirectory(), ScriptStateFileName);
            if (!File.Exists(statePath))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(statePath);
                var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (data == null)
                {
                    return false;
                }

                var batchRequestId = GetString(data, "BatchRequestId");
                if (!string.Equals(batchRequestId, requestId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var status = GetString(data, "Status");
                if (!string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(status, "Paused", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                object lineValue;
                if (!data.TryGetValue("CurrentLine", out lineValue) || lineValue == null)
                {
                    return false;
                }

                currentLine = Convert.ToInt32(lineValue);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) && value != null ? value.ToString() : null;
        }

        private static bool TryGetDialogClickArguments(string line, out string arguments)
        {
            arguments = null;
            string rest;
            if (!TryConsumeWord(line, "dialog", out rest))
            {
                return false;
            }

            if (!TryConsumeWord(rest, "click", out rest))
            {
                return false;
            }

            arguments = rest.Trim();
            return true;
        }

        private static bool TryConsumeWord(string text, string word, out string rest)
        {
            rest = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.Trim();
            if (!trimmed.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (trimmed.Length > word.Length && !char.IsWhiteSpace(trimmed[word.Length]))
            {
                return false;
            }

            rest = trimmed.Length > word.Length ? trimmed.Substring(word.Length).TrimStart() : string.Empty;
            return true;
        }

        private static string NormalizeExecutableLine(string rawLine)
        {
            if (rawLine == null)
            {
                return null;
            }

            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                return null;
            }

            // 保持与 Unity 侧 ScriptParser 的行内注释规则一致，避免 CLI 和 Editor 解析结果错位。
            var commentIndex = line.IndexOf('#');
            if (commentIndex > 0)
            {
                line = line.Substring(0, commentIndex).Trim();
            }

            return line.Length == 0 ? null : line;
        }

        private static List<DialogAutoClickTarget> ParseTargets(string arguments)
        {
            var targets = new List<DialogAutoClickTarget>();
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return targets;
            }

            var optionTokens = SplitCommandLine(arguments);
            var parsedOptions = false;
            for (var i = 0; i < optionTokens.Count; i++)
            {
                var token = optionTokens[i];
                if (!IsTargetOption(token))
                {
                    continue;
                }

                parsedOptions = true;
                if (i + 1 >= optionTokens.Count)
                {
                    continue;
                }

                AddTarget(targets, optionTokens[i + 1]);
                i++;
            }

            if (parsedOptions)
            {
                return targets;
            }

            var alternatives = SplitAlternatives(arguments);
            foreach (var alternative in alternatives)
            {
                AddTarget(targets, alternative);
            }

            return targets;
        }

        private static bool IsTargetOption(string token)
        {
            return string.Equals(token, "--choice", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "--button", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "--click", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddTarget(List<DialogAutoClickTarget> targets, string value)
        {
            var normalized = StripOptionalQuotes(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            targets.Add(new DialogAutoClickTarget { Value = normalized.Trim() });
        }

        private static List<string> SplitAlternatives(string text)
        {
            var result = new List<string>();
            var current = string.Empty;
            var inQuote = false;
            var quoteChar = ' ';

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (!inQuote && (c == '"' || c == '\''))
                {
                    inQuote = true;
                    quoteChar = c;
                    current += c;
                    continue;
                }

                if (inQuote && c == quoteChar)
                {
                    inQuote = false;
                    quoteChar = ' ';
                    current += c;
                    continue;
                }

                if (!inQuote && c == '|')
                {
                    result.Add(current.Trim());
                    current = string.Empty;
                    continue;
                }

                current += c;
            }

            result.Add(current.Trim());
            return result;
        }

        private static List<string> SplitCommandLine(string commandLine)
        {
            var result = new List<string>();
            var current = string.Empty;
            var inQuote = false;
            var quoteChar = ' ';

            for (var i = 0; i < commandLine.Length; i++)
            {
                var c = commandLine[i];
                if (!inQuote && (c == '"' || c == '\''))
                {
                    inQuote = true;
                    quoteChar = c;
                    continue;
                }

                if (inQuote && c == quoteChar)
                {
                    inQuote = false;
                    quoteChar = ' ';
                    continue;
                }

                if (!inQuote && char.IsWhiteSpace(c))
                {
                    if (current.Length > 0)
                    {
                        result.Add(current);
                        current = string.Empty;
                    }

                    continue;
                }

                current += c;
            }

            if (current.Length > 0)
            {
                result.Add(current);
            }

            return result;
        }

        private static string StripOptionalQuotes(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var trimmed = value.Trim();
            if (trimmed.Length >= 2 &&
                ((trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"') ||
                 (trimmed[0] == '\'' && trimmed[trimmed.Length - 1] == '\'')))
            {
                return trimmed.Substring(1, trimmed.Length - 2);
            }

            return trimmed;
        }
    }

    public sealed class BatchDialogAutoClickRule
    {
        public int CommandIndex { get; set; }
        public string Source { get; set; }
        public List<DialogAutoClickTarget> Targets { get; set; }
    }

    public sealed class DialogAutoClickTarget
    {
        public string Value { get; set; }
    }
}
