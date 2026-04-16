using System.Collections.Generic;
using System.Text;

namespace AIBridge.Editor
{
    /// <summary>
    /// Generates SKILL.md content from a template and registered command descriptions.
    /// The template should contain a <!-- AIBRIDGE:COMMANDS --> marker where
    /// dynamic command sections will be inserted.
    /// </summary>
    public static class SkillDocumentGenerator
    {
        private const string CommandsMarker = "<!-- AIBRIDGE:COMMANDS -->";

        /// <summary>
        /// Generate SKILL.md content by replacing the commands marker in the template
        /// with dynamically collected SkillDescription from all commands.
        /// If no marker is found, returns the template unchanged.
        /// </summary>
        public static string Generate(string templateContent, IEnumerable<ICommand> commands)
        {
            var markerIndex = templateContent.IndexOf(CommandsMarker);
            if (markerIndex < 0)
            {
                return templateContent;
            }

            var header = templateContent.Substring(0, markerIndex).TrimEnd();
            var footer = templateContent.Substring(markerIndex + CommandsMarker.Length).TrimStart('\r', '\n');
            var commandSection = BuildCommandSection(commands);

            var sb = new StringBuilder();
            sb.Append(header);
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("## Command Reference");
            sb.AppendLine();
            sb.Append(commandSection);

            if (!string.IsNullOrWhiteSpace(footer))
            {
                sb.AppendLine();
                sb.Append(footer);
            }

            return sb.ToString();
        }

        private static string BuildCommandSection(IEnumerable<ICommand> commands)
        {
            var sb = new StringBuilder();

            foreach (var command in commands)
            {
                var desc = command.SkillDescription;
                if (string.IsNullOrWhiteSpace(desc))
                    continue;

                sb.AppendLine(desc.Trim());
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
    }
}
