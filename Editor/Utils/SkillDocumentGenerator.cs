using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIBridge.Editor
{
    /// <summary>
    /// Generates Skill reference files from registered command documentation.
    /// </summary>
    public static class SkillDocumentGenerator
    {
        public const string ReferencesDirectoryName = "references";
        public const string DefaultReferenceFileName = CommandSkillDoc.DefaultReferenceFileName;

        private const string GeneratedHeader = "<!-- AIBRIDGE:GENERATED COMMAND REFERENCE - DO NOT EDIT MANUALLY -->";

        public static void GenerateReferenceFiles(string targetSkillRoot, IEnumerable<ICommand> commands)
        {
            if (string.IsNullOrEmpty(targetSkillRoot) || commands == null)
            {
                return;
            }

            var groups = BuildDocumentGroups(commands);
            foreach (var group in groups)
            {
                var skillDirectory = ResolveTargetSkillDirectory(targetSkillRoot, group.Key.TargetSkillName);
                var referenceDirectory = Path.Combine(skillDirectory, ReferencesDirectoryName);
                if (!Directory.Exists(referenceDirectory))
                {
                    Directory.CreateDirectory(referenceDirectory);
                }

                var referenceFileName = NormalizeReferenceFileName(group.Key.TargetReferenceFileName);
                var targetPath = Path.Combine(referenceDirectory, referenceFileName);
                File.WriteAllText(targetPath, BuildReferenceFileContent(group), Encoding.UTF8);
            }
        }

        private static List<IGrouping<CommandSkillDocTarget, CommandSkillDoc>> BuildDocumentGroups(IEnumerable<ICommand> commands)
        {
            return commands
                .Select(CreateDoc)
                .Where(doc => doc != null && !string.IsNullOrWhiteSpace(doc.Content))
                .OrderBy(doc => doc.Order)
                .ThenBy(doc => ExtractHeading(doc.Content), StringComparer.Ordinal)
                .GroupBy(doc => new CommandSkillDocTarget(
                    NormalizeSkillName(doc.TargetSkillName),
                    NormalizeReferenceFileName(doc.TargetReferenceFileName)))
                .OrderBy(group => group.Key.TargetSkillName, StringComparer.Ordinal)
                .ThenBy(group => group.Key.TargetReferenceFileName, StringComparer.Ordinal)
                .ToList();
        }

        private static CommandSkillDoc CreateDoc(ICommand command)
        {
            var provider = command as ICommandSkillDocProvider;
            if (provider != null && provider.SkillDoc != null)
            {
                return provider.SkillDoc;
            }

            if (string.IsNullOrWhiteSpace(command.SkillDescription))
            {
                return null;
            }

            return new CommandSkillDoc(command.SkillDescription);
        }

        private static string ResolveTargetSkillDirectory(string targetSkillRoot, string targetSkillName)
        {
            var normalizedTargetSkillName = NormalizeSkillName(targetSkillName);
            var rootDirectoryName = new DirectoryInfo(targetSkillRoot).Name;
            if (string.Equals(rootDirectoryName, normalizedTargetSkillName, StringComparison.OrdinalIgnoreCase))
            {
                return targetSkillRoot;
            }

            var parentDirectory = Directory.GetParent(targetSkillRoot);
            return parentDirectory == null
                ? Path.Combine(targetSkillRoot, normalizedTargetSkillName)
                : Path.Combine(parentDirectory.FullName, normalizedTargetSkillName);
        }

        private static string BuildReferenceFileContent(IEnumerable<CommandSkillDoc> docs)
        {
            var sb = new StringBuilder();
            sb.AppendLine(GeneratedHeader);
            sb.AppendLine("# AIBridge Command Reference");
            sb.AppendLine();
            sb.AppendLine("此文件由 AIBridge 自动生成。需要修改命令说明时，请修改对应 ICommand 的 SkillDoc/SkillDescription。");
            sb.AppendLine("`$CLI` 表示当前平台的 AIBridge CLI 调用方式，Windows 项目通常是 `./AIBridgeCache/CLI/AIBridgeCLI.exe`。");
            sb.AppendLine();

            foreach (var doc in docs)
            {
                sb.AppendLine(doc.Content.Trim());
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd() + "\n";
        }

        private static string NormalizeSkillName(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? CommandSkillDoc.DefaultTargetSkillName
                : value.Trim();
        }

        private static string ExtractHeading(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("###", StringComparison.Ordinal))
                    {
                        return trimmed;
                    }
                }
            }

            return content.Trim();
        }

        private static string NormalizeReferenceFileName(string value)
        {
            var fileName = string.IsNullOrWhiteSpace(value)
                ? DefaultReferenceFileName
                : value.Trim().Replace('\\', '/');

            fileName = Path.GetFileName(fileName);
            return string.IsNullOrWhiteSpace(fileName) ? DefaultReferenceFileName : fileName;
        }

        private sealed class CommandSkillDocTarget : IEquatable<CommandSkillDocTarget>
        {
            public CommandSkillDocTarget(string targetSkillName, string targetReferenceFileName)
            {
                TargetSkillName = targetSkillName;
                TargetReferenceFileName = targetReferenceFileName;
            }

            public string TargetSkillName { get; private set; }
            public string TargetReferenceFileName { get; private set; }

            public bool Equals(CommandSkillDocTarget other)
            {
                return other != null
                    && string.Equals(TargetSkillName, other.TargetSkillName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(TargetReferenceFileName, other.TargetReferenceFileName, StringComparison.OrdinalIgnoreCase);
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as CommandSkillDocTarget);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (StringComparer.OrdinalIgnoreCase.GetHashCode(TargetSkillName) * 397)
                        ^ StringComparer.OrdinalIgnoreCase.GetHashCode(TargetReferenceFileName);
                }
            }
        }
    }
}
