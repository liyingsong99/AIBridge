using System;
using System.Collections.Generic;
using System.Text;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Multi command builder: execute multiple CLI commands through the Unity-side batch script protocol.
    /// </summary>
    public class MultiCommandBuilder : BaseCommandBuilder
    {
        private const string BatchCallPrefix = "call ";

        private static readonly HashSet<string> BatchNativeCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "call",
            "delay",
            "log",
            "menu"
        };

        public override string Type => "multi";
        public override string Description => "Execute multiple commands by generating a batch from_text script";

        public override string[] Actions => new[] { "run" };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["run"] = new List<ParameterInfo>
            {
                new ParameterInfo("commands", "Commands separated by & or newline", false)
            }
        };

        public CommandRequest BuildFromCommands(string[] commandLines, Dictionary<string, string> globalOptions)
        {
            var scriptText = BuildBatchScriptText(commandLines);
            var batchOptions = BuildBatchOptions(globalOptions, scriptText);
            return new BatchCommandBuilder().Build("from_text", batchOptions);
        }

        /// <summary>
        /// Converts multi input to a batch script that matches the Unity-side BatchCommand from_text protocol.
        /// </summary>
        private static string BuildBatchScriptText(string[] commandLines)
        {
            var script = new StringBuilder();
            foreach (var rawLine in commandLines)
            {
                var line = NormalizeLine(rawLine);
                if (line.Length == 0)
                {
                    continue;
                }

                var batchLine = IsBatchNativeLine(line) ? line : BatchCallPrefix + line;
                script.AppendLine(batchLine);
            }

            if (script.Length == 0)
            {
                throw new ArgumentException("No valid commands provided");
            }

            return script.ToString();
        }

        private static string NormalizeLine(string rawLine)
        {
            if (rawLine == null)
            {
                return string.Empty;
            }

            return rawLine.Trim().TrimStart('\uFEFF');
        }

        /// <summary>
        /// Only forwards BatchCommandBuilder options so multi --stdin is not mistaken for batch from_text --stdin.
        /// </summary>
        private static Dictionary<string, string> BuildBatchOptions(Dictionary<string, string> globalOptions, string scriptText)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["text"] = scriptText
            };

            if (globalOptions == null)
            {
                return options;
            }

            CopyOptionIfPresent(globalOptions, options, "name");
            CopyOptionIfPresent(globalOptions, options, "keep-file");
            CopyOptionIfPresent(globalOptions, options, "output-dir");
            return options;
        }

        private static void CopyOptionIfPresent(Dictionary<string, string> source, Dictionary<string, string> target, string key)
        {
            string value;
            if (source.TryGetValue(key, out value))
            {
                target[key] = value;
            }
        }

        private static bool IsBatchNativeLine(string line)
        {
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                return true;
            }

            var firstToken = GetFirstToken(line);
            return BatchNativeCommands.Contains(firstToken);
        }

        private static string GetFirstToken(string line)
        {
            for (var i = 0; i < line.Length; i++)
            {
                if (char.IsWhiteSpace(line[i]))
                {
                    return line.Substring(0, i);
                }
            }

            return line;
        }

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            throw new ArgumentException("Use BuildFromCommands method for multi command");
        }
    }
}
