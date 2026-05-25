using System;
using System.Collections.Generic;

namespace AIBridge.Editor.ScriptExecution.Commands
{
    /// <summary>
    /// 声明 batch 后续步骤遇到 Unity 模态弹窗时由 CLI 自动点击。
    /// </summary>
    public class DialogClickCommand : IScriptCommand
    {
        private readonly List<string> _targets;

        public DialogClickCommand(List<string> targets)
        {
            _targets = targets ?? new List<string>();
        }

        public string Type => "dialog_click";

        public List<string> Targets
        {
            get { return _targets; }
        }

        public ScriptCommandResult Execute(ScriptExecutionContext context)
        {
            // Unity 模态弹窗会阻塞主线程，真正点击必须由等待 batch 结果的 CLI 进程执行。
            context.Log("[DialogClick] 自动弹窗点击声明: " + string.Join(" | ", _targets.ToArray()));
            return ScriptCommandResult.Ok("Dialog auto-click declaration registered");
        }

        public static bool TryParse(string line, out DialogClickCommand command, out string error)
        {
            command = null;
            error = null;

            string arguments;
            if (!TryGetDialogClickArguments(line, out arguments))
            {
                return false;
            }

            var targets = ParseTargets(arguments);
            if (targets.Count == 0)
            {
                error = "dialog click 需要至少一个 choice 或按钮文本";
                return false;
            }

            command = new DialogClickCommand(targets);
            return true;
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

        private static List<string> ParseTargets(string arguments)
        {
            var targets = new List<string>();
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

        private static void AddTarget(List<string> targets, string value)
        {
            var normalized = ScriptTextUtility.StripOptionalQuotes(value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                targets.Add(normalized.Trim());
            }
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
    }
}
