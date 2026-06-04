using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AIBridge.Internal.Json;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// 生成 AI harness 可直接读取的项目能力快照，减少每轮对 CLI/Skill/Workflow 的重复探测。
    /// </summary>
    internal static class HarnessCapabilitySnapshot
    {
        private const int SchemaVersion = 1;
        private const int DefaultFreshnessSeconds = 3600;
        private const string PackageName = "cn.lys.aibridge";
        private const string SnapshotRelativePath = ".aibridge/harness/capabilities.json";

        public static string GetSnapshotPath(string projectRoot)
        {
            return Path.Combine(projectRoot, SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public static string GetSnapshotDisplayPath()
        {
            return SnapshotRelativePath;
        }

        public static void WriteNoThrow(string projectRoot, IEnumerable<AssistantIntegrationTarget> selectedTargets)
        {
            try
            {
                Write(projectRoot, selectedTargets);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("[Harness] Failed to write capability snapshot: " + ex.Message);
            }
        }

        public static void Write(string projectRoot, IEnumerable<AssistantIntegrationTarget> selectedTargets)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return;
            }

            var snapshotPath = GetSnapshotPath(projectRoot);
            var directory = Path.GetDirectoryName(snapshotPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var targets = (selectedTargets ?? Enumerable.Empty<AssistantIntegrationTarget>()).Where(item => item != null).ToList();
            var snapshot = BuildSnapshot(projectRoot, targets);
            File.WriteAllText(snapshotPath, AIBridgeJson.Serialize(snapshot, pretty: true));
        }

        public static string BuildRootRuleSummary(string projectRoot, IEnumerable<AssistantIntegrationTarget> selectedTargets, AIBridgeEditorLanguage language)
        {
            var settings = AIBridgeProjectSettings.Instance;
            var targets = (selectedTargets ?? Enumerable.Empty<AssistantIntegrationTarget>()).Where(item => item != null).ToList();
            var skillRoots = GetSkillRoots(projectRoot, targets);
            var selectedIds = targets.Select(target => target.Id).ToArray();
            var selectedText = selectedIds.Length == 0 ? "none" : string.Join(", ", selectedIds);
            var skillRootText = skillRoots.Count == 0 ? "unknown" : string.Join(", ", skillRoots);
            var codeIndexText = settings.CodeIndex.EnableCodeIndex ? "enabled" : "disabled";

            if (language == AIBridgeEditorLanguage.SimplifiedChinese)
            {
                return "Harness 能力快照：`" + SnapshotRelativePath + "`。RootRule 只提供 compact 摘要；工作流任务需要确认能力时先用 `$CLI harness status` compact 输出，仅在缺失、过期或任务需要未确认能力时读取完整 snapshot 或运行完整探测。"
                    + "已选助手：" + selectedText + "。"
                    + "Skill 根目录：" + skillRootText + "。"
                    + "Code Index：" + codeIndexText + "。"
                    + "外部 agent/sub-agent 能力：Unity 无法判断，按 unknown 处理。";
            }

            return "Harness capability snapshot: `" + SnapshotRelativePath + "`. RootRule only provides a compact summary; for workflow tasks that need capability confirmation, use compact `$CLI harness status` first and read the full snapshot or run full probes only when it is missing, stale, or the task needs an unconfirmed capability. "
                + "Selected assistants: " + selectedText + ". "
                + "Skill root: " + skillRootText + ". "
                + "Code Index: " + codeIndexText + ". "
                + "External agent/sub-agent capability: unknown to Unity.";
        }

        private static Dictionary<string, object> BuildSnapshot(string projectRoot, List<AssistantIntegrationTarget> selectedTargets)
        {
            var settings = AIBridgeProjectSettings.Instance;
            return new Dictionary<string, object>
            {
                { "schemaVersion", SchemaVersion },
                { "generatedAtUtc", DateTime.UtcNow.ToString("o") },
                { "freshnessSeconds", DefaultFreshnessSeconds },
                { "snapshotPath", SnapshotRelativePath },
                { "projectRoot", NormalizePath(projectRoot) },
                { "package", BuildPackageInfo(projectRoot) },
                { "unity", BuildUnityInfo() },
                { "cli", BuildCliInfo(projectRoot) },
                { "assistants", BuildAssistantInfo(projectRoot, selectedTargets) },
                { "skills", BuildSkillInfo(projectRoot, selectedTargets) },
                { "codeIndex", BuildCodeIndexInfo(settings) },
                { "workflow", BuildWorkflowInfo(projectRoot) },
                { "runtime", BuildRuntimeInfo(settings) },
                { "harness", BuildHarnessInfo() },
                { "recommendation", BuildRecommendation() }
            };
        }

        private static Dictionary<string, object> BuildPackageInfo(string projectRoot)
        {
            var packageRoots = ResolvePackageRoots(projectRoot);
            var packageRoot = packageRoots.FirstOrDefault();
            var logicalRoot = "Packages/" + PackageName;
            var reportedRoots = packageRoots.Select(root => (object)NormalizePath(root)).ToList();
            if (!reportedRoots.Any(root => string.Equals(Convert.ToString(root), logicalRoot, StringComparison.OrdinalIgnoreCase)))
            {
                reportedRoots.Add(logicalRoot);
            }

            return new Dictionary<string, object>
            {
                { "name", PackageName },
                { "version", ReadPackageVersion(packageRoot) },
                { "root", NormalizePath(packageRoot) },
                { "logicalRoot", logicalRoot },
                { "roots", reportedRoots }
            };
        }

        private static Dictionary<string, object> BuildUnityInfo()
        {
            return new Dictionary<string, object>
            {
                { "editorAvailable", true },
                { "version", Application.unityVersion },
                { "isCompiling", EditorApplication.isCompiling },
                { "isUpdating", EditorApplication.isUpdating },
                { "isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode },
                { "compileStatus", "unknown" }
            };
        }

        private static Dictionary<string, object> BuildCliInfo(string projectRoot)
        {
            var relativePath = ".aibridge/cli/" + GetCliExecutableName();
            var fullPath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return new Dictionary<string, object>
            {
                { "path", relativePath },
                { "exists", File.Exists(fullPath) },
                { "sha256", File.Exists(fullPath) ? ComputeSha256(fullPath) : null },
                { "status", File.Exists(fullPath) ? "available" : "missing" }
            };
        }

        private static List<object> BuildAssistantInfo(string projectRoot, List<AssistantIntegrationTarget> selectedTargets)
        {
            var result = new List<object>();
            foreach (var target in selectedTargets)
            {
                result.Add(new Dictionary<string, object>
                {
                    { "id", target.Id },
                    { "displayName", target.DisplayName },
                    { "rootRule", target.RootRuleFileName },
                    { "skillRoot", target.GetResolvedSkillRootDirectoryRelativePath(projectRoot) },
                    { "mainSkill", target.GetResolvedSkillFileRelativePath(projectRoot) }
                });
            }

            return result;
        }

        private static Dictionary<string, object> BuildSkillInfo(string projectRoot, List<AssistantIntegrationTarget> selectedTargets)
        {
            var roots = GetSkillRoots(projectRoot, selectedTargets);
            var installed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                var fullRoot = Path.Combine(projectRoot, root.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(fullRoot))
                {
                    continue;
                }

                foreach (var directory in Directory.GetDirectories(fullRoot))
                {
                    if (File.Exists(Path.Combine(directory, "SKILL.md")))
                    {
                        installed.Add(Path.GetFileName(directory));
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "roots", roots.Cast<object>().ToList() },
                { "installed", installed.Cast<object>().ToList() },
                { "status", roots.Count == 0 ? "unknown" : "available" }
            };
        }

        private static Dictionary<string, object> BuildCodeIndexInfo(AIBridgeProjectSettings settings)
        {
            return new Dictionary<string, object>
            {
                { "enabled", settings.CodeIndex.EnableCodeIndex },
                { "skillExpected", settings.CodeIndex.EnableCodeIndex },
                { "status", settings.CodeIndex.EnableCodeIndex ? "enabled" : "disabled" },
                { "fallback", "rg-and-file-read" }
            };
        }

        private static Dictionary<string, object> BuildWorkflowInfo(string projectRoot)
        {
            var packageRoot = ResolvePackageRoot(projectRoot);
            var builtInDirectory = string.IsNullOrWhiteSpace(packageRoot)
                ? null
                : Path.Combine(packageRoot, "Templates~", "Workflows");
            var logicalBuiltInDirectory = "Packages/" + PackageName + "/Templates~/Workflows";
            var builtInDirectories = ResolvePackageRoots(projectRoot)
                .Select(root => Path.Combine(root, "Templates~", "Workflows"))
                .Where(Directory.Exists)
                .Select(directory => (object)NormalizePath(directory))
                .ToList();
            if (!builtInDirectories.Any(directory => string.Equals(Convert.ToString(directory), logicalBuiltInDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                builtInDirectories.Add(logicalBuiltInDirectory);
            }
            var projectDirectory = Path.Combine(projectRoot, ".aibridge", "workflows", "recipes");

            return new Dictionary<string, object>
            {
                { "builtInRecipeDirectory", NormalizePath(builtInDirectory) },
                { "builtInRecipeLogicalDirectory", logicalBuiltInDirectory },
                { "builtInRecipeDirectories", builtInDirectories },
                { "builtInRecipeCount", CountRecipeFiles(builtInDirectory) },
                { "projectRecipeDirectory", NormalizePath(projectDirectory) },
                { "projectRecipeCount", CountRecipeFiles(projectDirectory) },
                { "status", Directory.Exists(builtInDirectory) ? "available" : "unknown" },
                { "agentManualSteps", "external-executor-required" }
            };
        }

        private static Dictionary<string, object> BuildRuntimeInfo(AIBridgeProjectSettings settings)
        {
            var runtime = settings.RuntimeBridge;
            return new Dictionary<string, object>
            {
                { "bridgeEnabled", runtime.EnableRuntimeBridge },
                { "autoInjectEditorPlayMode", runtime.AutoInjectRuntimeBridgeInEditorPlayMode },
                { "autoInjectDevelopmentBuild", runtime.AutoInjectRuntimeBridgeInDevelopmentBuild },
                { "httpTransportEnabled", runtime.EnableHttpTransport },
                { "lanDiscoveryEnabled", runtime.EnableLanDiscovery },
                { "status", runtime.EnableRuntimeBridge ? "configured" : "disabled" },
                { "targetStatus", "unknown" }
            };
        }

        private static Dictionary<string, object> BuildHarnessInfo()
        {
            return new Dictionary<string, object>
            {
                { "externalExecutor", "unknown" },
                { "subAgents", "unknown" },
                { "shellPermissions", "unknown" },
                { "note", "Unity can snapshot project capabilities, but only the active AI harness can know executor permissions." }
            };
        }

        private static Dictionary<string, object> BuildRecommendation()
        {
            return new Dictionary<string, object>
            {
                { "readSnapshotFirst", true },
                { "runFullProbeWhen", "missing-stale-or-required-capability-unknown" },
                { "fallbackWhenMissing", "Use RootRule minimum workflow and report unverified Unity/Runtime capabilities." }
            };
        }

        private static List<string> GetSkillRoots(string projectRoot, List<AssistantIntegrationTarget> targets)
        {
            var roots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                var root = target.GetResolvedSkillRootDirectoryRelativePath(projectRoot);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    roots.Add(NormalizePath(root));
                }
            }

            return roots.ToList();
        }

        private static int CountRecipeFiles(string directory)
        {
            return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.aibridge-workflow.json").Length
                : 0;
        }

        private static string ResolvePackageRoot(string projectRoot)
        {
            return ResolvePackageRoots(projectRoot).FirstOrDefault();
        }

        private static List<string> ResolvePackageRoots(string projectRoot)
        {
            var roots = new List<string>();
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + PackageName);
            if (packageInfo != null
                && IsPackageRoot(packageInfo.resolvedPath)
                && !IsProjectPackagesPath(projectRoot, packageInfo.resolvedPath))
            {
                AddPackageRoot(roots, packageInfo.resolvedPath);
            }

            // Git/UPM 包有时会让 PackageInfo 返回逻辑 Packages 路径，能力快照需要真实文件系统路径。
            var packageCache = Path.Combine(projectRoot, "Library", "PackageCache");
            if (Directory.Exists(packageCache))
            {
                var directCachePackage = Path.Combine(packageCache, PackageName);
                if (IsPackageRoot(directCachePackage))
                {
                    AddPackageRoot(roots, directCachePackage);
                }

                foreach (var directory in Directory.EnumerateDirectories(packageCache, PackageName + "@*", SearchOption.TopDirectoryOnly))
                {
                    if (IsPackageRoot(directory))
                    {
                        AddPackageRoot(roots, directory);
                    }
                }
            }

            var embedded = Path.Combine(projectRoot, "Packages", PackageName);
            if (IsPackageRoot(embedded))
            {
                AddPackageRoot(roots, embedded);
            }

            if (packageInfo != null && IsPackageRoot(packageInfo.resolvedPath))
            {
                AddPackageRoot(roots, packageInfo.resolvedPath);
            }

            if (IsPackageRoot(projectRoot))
            {
                AddPackageRoot(roots, projectRoot);
            }

            return roots;
        }

        private static void AddPackageRoot(List<string> roots, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var fullPath = Path.GetFullPath(path);
            if (!roots.Any(root => string.Equals(Path.GetFullPath(root), fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(path);
            }
        }

        private static bool IsPackageRoot(string directory)
        {
            return !string.IsNullOrWhiteSpace(directory)
                && Directory.Exists(directory)
                && File.Exists(Path.Combine(directory, "package.json"))
                && Directory.Exists(Path.Combine(directory, "Skill~"));
        }

        private static bool IsProjectPackagesPath(string projectRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var packagesRoot = Path.GetFullPath(Path.Combine(projectRoot, "Packages"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(packagesRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadPackageVersion(string packageRoot)
        {
            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                return "unknown";
            }

            var packageJson = Path.Combine(packageRoot, "package.json");
            if (!File.Exists(packageJson))
            {
                return "unknown";
            }

            try
            {
                var json = AIBridgeJson.DeserializeObject(File.ReadAllText(packageJson));
                object version;
                return json != null && json.TryGetValue("version", out version) ? Convert.ToString(version) : "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private static string GetCliExecutableName()
        {
#if UNITY_EDITOR_WIN
            return "AIBridgeCLI.exe";
#else
            return "AIBridgeCLI";
#endif
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }
    }
}
