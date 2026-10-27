using System;
using System.IO;
using AIBridge.Runtime.Internal;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Settings 窗口历史遗留的辅助方法。
    /// AI 工具选择与安装 UI 已统一到 AIBridgeSetupWindow / AIBridgeWorkflowsWindow，
    /// 这里只保留仍被其它面板复用的项目模板路径与截图缓存方法。
    /// </summary>
    public partial class AIBridgeSettingsWindow
    {
        private const string GitRepositorySuffix = ".git";

        /// <summary>
        /// 把 Git 仓库地址转换为可打开的 Web 地址；测试与推荐库共用。
        /// </summary>
        internal static string GetRepositoryWebUrl(string repositoryUrl)
        {
            return repositoryUrl.EndsWith(GitRepositorySuffix, StringComparison.OrdinalIgnoreCase)
                ? repositoryUrl.Substring(0, repositoryUrl.Length - GitRepositorySuffix.Length)
                : repositoryUrl;
        }

        private void ClearScreenshotCache()
        {
            try
            {
                var result = AIBridgeCacheCleanup.ClearScreenshotCache(AIBridge.BridgeDirectory);
                Debug.Log(AIBridgeEditorText.T(
                    "[AIBridge] Cleared screenshot cache. Deleted files: "
                    + result.DeletedFiles
                    + ", errors: " + result.ErrorCount,
                    "[AIBridge] 已清理截图缓存。删除文件数: "
                    + result.DeletedFiles
                    + "，错误数：" + result.ErrorCount));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AIBridge] Failed to clear screenshot cache: " + ex.Message);
            }
        }

        /// <summary>
        /// 获取 Unity 项目 AGENTS.md 模板路径（兼容 Packages 和 PackageCache）
        /// </summary>
        internal static string GetSourceAgentsPath()
        {
            const string PACKAGE_NAME = "cn.lys.aibridge";
            const string AGENTS_FILE_NAME = "AGENTS.md";
            var projectTemplateRelativePath = GetProjectAgentsTemplateRelativePath(AIBridgeProjectSettings.Instance.EditorLanguage);
            var projectRoot = Path.GetDirectoryName(Application.dataPath);

            // 方法 1: 直接从 Packages 目录查找（本地/嵌入式包）
            var directPath = Path.Combine(projectRoot, "Packages", PACKAGE_NAME, projectTemplateRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(directPath))
            {
                return directPath;
            }

            // 方法 2: 使用 PackageInfo 解析路径（git/registry 包）
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PACKAGE_NAME}");
            if (packageInfo != null)
            {
                var packagePath = Path.Combine(packageInfo.resolvedPath, projectTemplateRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(packagePath))
                {
                    return packagePath;
                }
            }

            // 兼容旧版本包结构：历史上项目模板曾放在包根 AGENTS.md。
            var legacyDirectPath = Path.Combine(projectRoot, "Packages", PACKAGE_NAME, AGENTS_FILE_NAME);
            if (IsLegacyProjectAgentsTemplate(legacyDirectPath))
            {
                return legacyDirectPath;
            }

            if (packageInfo != null)
            {
                var legacyPackagePath = Path.Combine(packageInfo.resolvedPath, AGENTS_FILE_NAME);
                if (IsLegacyProjectAgentsTemplate(legacyPackagePath))
                {
                    return legacyPackagePath;
                }
            }

            return null;
        }

        internal static string GetProjectAgentsTemplateRelativePath(AIBridgeEditorLanguage language)
        {
            return language == AIBridgeEditorLanguage.SimplifiedChinese
                ? "Templates~/ProjectRules/AGENTS.zh-CN.md"
                : "Templates~/ProjectRules/AGENTS.en-US.md";
        }

        private static bool IsLegacyProjectAgentsTemplate(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                var content = File.ReadAllText(path);
                return !content.Contains("本文件只用于开发 `cn.lys.aibridge` 包自身");
            }
            catch
            {
                return false;
            }
        }
    }
}
