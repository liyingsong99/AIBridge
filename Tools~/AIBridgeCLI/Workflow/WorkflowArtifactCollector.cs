using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Workflow
{
    public class WorkflowArtifactCollector
    {
        private const long DefaultCopyLimitBytes = 50L * 1024L * 1024L;
        private readonly WorkflowRunStore _store;

        public WorkflowArtifactCollector(WorkflowRunStore store)
        {
            _store = store;
        }

        public List<WorkflowArtifactRef> CollectForCommand(
            string command,
            WorkflowCommandExecution execution,
            string commandResultPath)
        {
            var artifacts = new List<WorkflowArtifactRef>();
            var commandArtifact = CreateCommandResultArtifact(command, execution, commandResultPath);
            artifacts.Add(commandArtifact);

            var semanticKind = DetectSemanticKind(command);
            if (!string.Equals(semanticKind, "command-result", StringComparison.OrdinalIgnoreCase))
            {
                artifacts.Add(CreateReferenceArtifact(semanticKind, command, execution, commandResultPath));
            }

            var fileArtifacts = CollectFileArtifacts(command, execution);
            artifacts.AddRange(fileArtifacts);

            foreach (var artifact in artifacts)
            {
                _store.SaveArtifact(artifact);
            }

            return artifacts;
        }

        private WorkflowArtifactRef CreateCommandResultArtifact(
            string command,
            WorkflowCommandExecution execution,
            string commandResultPath)
        {
            var artifactId = CreateArtifactId("command-result", execution.CommandId, 0);
            return new WorkflowArtifactRef
            {
                ArtifactId = artifactId,
                Kind = "command-result",
                Path = WorkflowPathHelper.ToDisplayPath(commandResultPath),
                SourceCommand = command,
                SourceCommandId = execution.CommandId,
                ContentType = "application/json",
                Summary = "Archived raw CLI command result.",
                Copied = true,
                CreatedAtUtc = DateTime.UtcNow.ToString("o")
            };
        }

        private WorkflowArtifactRef CreateReferenceArtifact(
            string kind,
            string command,
            WorkflowCommandExecution execution,
            string commandResultPath)
        {
            return new WorkflowArtifactRef
            {
                ArtifactId = CreateArtifactId(kind, execution.CommandId, 0),
                Kind = kind,
                Path = WorkflowPathHelper.ToDisplayPath(commandResultPath),
                SourceCommand = command,
                SourceCommandId = execution.CommandId,
                ContentType = "application/json",
                Summary = "Evidence extracted from CLI result for " + kind + ".",
                Copied = true,
                CreatedAtUtc = DateTime.UtcNow.ToString("o")
            };
        }

        private List<WorkflowArtifactRef> CollectFileArtifacts(string command, WorkflowCommandExecution execution)
        {
            var artifacts = new List<WorkflowArtifactRef>();
            if (execution == null || execution.Result == null)
            {
                return artifacts;
            }

            var candidates = new List<FileArtifactCandidate>();
            AddCandidate(candidates, execution.Result, "imagePath", DetectImageKind(command), "imagePath");
            AddCandidate(candidates, execution.Result, "gifPath", "gif", "gifPath");
            AddCandidate(candidates, execution.Result, "pcPath", DetectImageKind(command), "pcPath");
            AddCandidate(candidates, execution.Result, "output", DetectImageKind(command), "output");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 1;
            foreach (var candidate in candidates)
            {
                var fullPath = ResolveArtifactPath(candidate.Path);
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath) || !seen.Add(Path.GetFullPath(fullPath)))
                {
                    continue;
                }

                artifacts.Add(CreateFileArtifact(command, execution, candidate.Kind, fullPath, candidate.SourceField, index));
                index++;
            }

            return artifacts;
        }

        private WorkflowArtifactRef CreateFileArtifact(
            string command,
            WorkflowCommandExecution execution,
            string kind,
            string sourcePath,
            string sourceField,
            int index)
        {
            var artifactId = CreateArtifactId(kind, execution.CommandId, index);
            var directory = _store.GetArtifactDirectory(artifactId);
            var extension = Path.GetExtension(sourcePath);
            var payloadPath = Path.Combine(directory, "payload" + extension);
            var fileInfo = new FileInfo(sourcePath);
            var copied = !ShouldReferenceExistingFile(kind) && fileInfo.Length <= DefaultCopyLimitBytes;
            string artifactPath;
            string sha256 = ReadSha256(execution.Result) ?? ComputeSha256(sourcePath);

            if (copied)
            {
                File.Copy(sourcePath, payloadPath, true);
                artifactPath = payloadPath;
                sha256 = ComputeSha256(payloadPath);
            }
            else
            {
                artifactPath = sourcePath;
            }

            // 截图/GIF 已由 AIBridge 缓存到 .aibridge 下；workflow 默认只登记引用，避免重复复制大图像产物。
            var summary = copied
                ? "File artifact copied from result field `" + sourceField + "`."
                : "File artifact referenced from result field `" + sourceField + "`.";

            return new WorkflowArtifactRef
            {
                ArtifactId = artifactId,
                Kind = kind,
                Path = WorkflowPathHelper.ToDisplayPath(artifactPath),
                SourcePath = WorkflowPathHelper.ToDisplayPath(sourcePath),
                SourceCommand = command,
                SourceCommandId = execution.CommandId,
                Sha256 = sha256,
                ContentType = GuessContentType(sourcePath),
                Summary = summary,
                Copied = copied,
                CreatedAtUtc = DateTime.UtcNow.ToString("o")
            };
        }

        private static void AddCandidate(List<FileArtifactCandidate> candidates, JToken root, string key, string kind, string sourceField)
        {
            foreach (var value in FindStringValues(root, key))
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    candidates.Add(new FileArtifactCandidate
                    {
                        Kind = kind,
                        Path = value,
                        SourceField = sourceField
                    });
                }
            }
        }

        private static IEnumerable<string> FindStringValues(JToken token, string key)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                foreach (var property in obj.Properties())
                {
                    if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)
                        && property.Value.Type == JTokenType.String)
                    {
                        yield return property.Value.Value<string>();
                    }

                    foreach (var nested in FindStringValues(property.Value, key))
                    {
                        yield return nested;
                    }
                }
            }

            var array = token as JArray;
            if (array != null)
            {
                foreach (var item in array)
                {
                    foreach (var nested in FindStringValues(item, key))
                    {
                        yield return nested;
                    }
                }
            }
        }

        private static string ResolveArtifactPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            var cwdPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
            if (File.Exists(cwdPath))
            {
                return cwdPath;
            }

            var projectPath = Path.GetFullPath(Path.Combine(WorkflowPathHelper.GetProjectRoot(), path));
            return projectPath;
        }

        private static string DetectSemanticKind(string command)
        {
            var normalized = (command ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.StartsWith("get_logs"))
            {
                return "console-log";
            }

            if (normalized.StartsWith("runtime logs"))
            {
                return "runtime-log";
            }

            if (normalized.StartsWith("runtime status") || normalized.StartsWith("runtime ping"))
            {
                return "runtime-status";
            }

            if (normalized.StartsWith("runtime perf"))
            {
                return "runtime-perf";
            }

            if (normalized.StartsWith("runtime call") || normalized.StartsWith("runtime handlers"))
            {
                return "runtime-handler-result";
            }

            if (normalized.StartsWith("code_index"))
            {
                return "code-index-result";
            }

            if (normalized.StartsWith("test run") || normalized.StartsWith("test status"))
            {
                return "validation-report";
            }

            return "command-result";
        }

        private static string DetectImageKind(string command)
        {
            var normalized = (command ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.StartsWith("runtime screenshot"))
            {
                return "runtime-screenshot";
            }

            if (normalized.StartsWith("screenshot gif"))
            {
                return "gif";
            }

            return "screenshot";
        }

        private static bool ShouldReferenceExistingFile(string kind)
        {
            return string.Equals(kind, "screenshot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "gif", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "runtime-screenshot", StringComparison.OrdinalIgnoreCase);
        }

        private static string CreateArtifactId(string kind, string commandId, int index)
        {
            var normalizedKind = (kind ?? "artifact").Replace("-", "_");
            var suffix = string.IsNullOrWhiteSpace(commandId) ? Guid.NewGuid().ToString("N").Substring(0, 8) : commandId.Replace("-", "_");
            return "art_" + normalizedKind + "_" + suffix + (index > 0 ? "_" + index.ToString(CultureInfo.InvariantCulture) : string.Empty);
        }

        private static string ReadSha256(JObject result)
        {
            foreach (var value in FindStringValues(result, "sha256"))
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private static string GuessContentType(string path)
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                return "image/png";
            }

            if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return "image/jpeg";
            }

            if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                return "image/gif";
            }

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                return "application/json";
            }

            if (extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                return "text/plain";
            }

            return "application/octet-stream";
        }

        private class FileArtifactCandidate
        {
            public string Kind { get; set; }
            public string Path { get; set; }
            public string SourceField { get; set; }
        }
    }
}
