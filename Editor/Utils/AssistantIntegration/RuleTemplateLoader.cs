using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace AIBridge.Editor
{
    internal sealed class RuleTemplateMetadata
    {
        public string TemplateId { get; set; }
        public string Assistant { get; set; }
        public int Version { get; set; }
        public string Target { get; set; }
    }

    internal sealed class RuleTemplate
    {
        public RuleTemplateMetadata Metadata { get; set; }
        public string Body { get; set; }
        public string SourcePath { get; set; }
    }

    internal static class RuleTemplateLoader
    {
        private const string PackageName = "cn.lys.aibridge";

        public static RuleTemplate Load(string projectRoot, string relativePath)
        {
            var templatePath = ResolvePackageRelativePath(projectRoot, relativePath);
            if (string.IsNullOrEmpty(templatePath) || !File.Exists(templatePath))
            {
                throw new FileNotFoundException("Rule template not found.", relativePath);
            }

            var content = File.ReadAllText(templatePath, Encoding.UTF8);
            var metadata = new RuleTemplateMetadata();
            var body = content;

            if (content.StartsWith("---\n", StringComparison.Ordinal) || content.StartsWith("---\r\n", StringComparison.Ordinal))
            {
                var normalized = content.Replace("\r\n", "\n");
                var secondMarkerIndex = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
                if (secondMarkerIndex > 0)
                {
                    var frontmatter = normalized.Substring(4, secondMarkerIndex - 4);
                    body = normalized.Substring(secondMarkerIndex + 5).TrimStart('\n');
                    metadata = ParseFrontmatter(frontmatter);
                }
            }

            return new RuleTemplate
            {
                Metadata = metadata,
                Body = body,
                SourcePath = templatePath
            };
        }

        private static RuleTemplateMetadata ParseFrontmatter(string frontmatter)
        {
            var metadata = new RuleTemplateMetadata();
            var lines = frontmatter.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf(':');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separatorIndex).Trim();
                var value = line.Substring(separatorIndex + 1).Trim().Trim('"');

                switch (key)
                {
                    case "templateId":
                        metadata.TemplateId = value;
                        break;
                    case "assistant":
                        metadata.Assistant = value;
                        break;
                    case "version":
                        int version;
                        if (int.TryParse(value, out version))
                        {
                            metadata.Version = version;
                        }
                        break;
                    case "target":
                        metadata.Target = value;
                        break;
                }
            }

            return metadata;
        }

        private static string ResolvePackageRelativePath(string projectRoot, string relativePath)
        {
            var directPath = Path.Combine(projectRoot, "Packages", PackageName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(directPath))
            {
                return directPath;
            }

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + PackageName);
            if (packageInfo != null)
            {
                var resolvedPath = Path.Combine(packageInfo.resolvedPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(resolvedPath))
                {
                    return resolvedPath;
                }
            }

            Debug.LogWarning("[AIBridge] Unable to resolve template path: " + relativePath);
            return null;
        }
    }
}
