using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Workflow
{
    public static class WorkflowPathHelper
    {
        public const string RecipeExtension = ".aibridge-workflow.json";
        private const string TemplatesDirectoryName = "Templates~";
        private const string WorkflowsDirectoryName = "Workflows";
        private const string WorkflowRootName = "workflows";
        private const string PackageName = "cn.lys.aibridge";

        public static string GetPackageRoot()
        {
            var envPackageRoot = Environment.GetEnvironmentVariable("AIBRIDGE_PACKAGE_ROOT");
            if (IsPackageRoot(envPackageRoot))
            {
                return Path.GetFullPath(envPackageRoot);
            }

            var candidates = new List<string>
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Directory.GetCurrentDirectory()
            };

            foreach (var candidate in candidates)
            {
                var found = SearchUpwardsForPackageRoot(candidate);
                if (!string.IsNullOrEmpty(found))
                {
                    return found;
                }
            }

            var projectRoot = FindUnityProjectRoot();
            var packageRoot = FindPackageRootFromUnityProject(projectRoot);
            if (!string.IsNullOrEmpty(packageRoot))
            {
                return packageRoot;
            }

            return Directory.GetCurrentDirectory();
        }

        public static string GetBuiltInRecipesDirectory()
        {
            return Path.Combine(GetPackageRoot(), TemplatesDirectoryName, WorkflowsDirectoryName);
        }

        public static string GetWorkflowRootDirectory()
        {
            return Path.Combine(PathHelper.GetExchangeDirectory(), WorkflowRootName);
        }

        public static string GetProjectRecipesDirectory()
        {
            return Path.Combine(GetWorkflowRootDirectory(), "recipes");
        }

        public static string GetRunsDirectory()
        {
            return Path.Combine(GetWorkflowRootDirectory(), "runs");
        }

        public static string GetProjectRoot()
        {
            var exchange = Path.GetFullPath(PathHelper.GetExchangeDirectory());
            var exchangeInfo = new DirectoryInfo(exchange.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (exchangeInfo.Name.Equals(".aibridge", StringComparison.OrdinalIgnoreCase) && exchangeInfo.Parent != null)
            {
                return exchangeInfo.Parent.FullName;
            }

            return Directory.GetCurrentDirectory();
        }

        public static string GenerateRunId()
        {
            return "wf_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public static List<string> FindBuiltInRecipeFiles()
        {
            return FindRecipeFiles(GetBuiltInRecipesDirectory());
        }

        public static List<string> FindProjectRecipeFiles()
        {
            return FindRecipeFiles(GetProjectRecipesDirectory());
        }

        public static string ResolveRecipePath(string fileOrRecipe)
        {
            if (string.IsNullOrWhiteSpace(fileOrRecipe))
            {
                throw new ArgumentException("Missing workflow recipe path or name.");
            }

            var direct = ResolvePath(fileOrRecipe);
            if (File.Exists(direct))
            {
                return direct;
            }

            var name = Path.GetFileNameWithoutExtension(fileOrRecipe);
            if (name.EndsWith(".aibridge-workflow", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - ".aibridge-workflow".Length);
            }

            var fileName = name + RecipeExtension;
            var project = Path.Combine(GetProjectRecipesDirectory(), fileName);
            if (File.Exists(project))
            {
                return project;
            }

            var builtIn = Path.Combine(GetBuiltInRecipesDirectory(), fileName);
            if (File.Exists(builtIn))
            {
                return builtIn;
            }

            throw new FileNotFoundException("Workflow recipe was not found: " + fileOrRecipe);
        }

        public static string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
        }

        public static string ToDisplayPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var fullPath = Path.GetFullPath(path);
            var projectRoot = GetProjectRoot();
            var relative = TryMakeRelative(projectRoot, fullPath);
            return NormalizeSeparators(relative ?? fullPath);
        }

        public static string NormalizeSeparators(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }

        public static void EnsureWorkflowDirectories()
        {
            Directory.CreateDirectory(GetProjectRecipesDirectory());
            Directory.CreateDirectory(GetRunsDirectory());
        }

        private static List<string> FindRecipeFiles(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return new List<string>();
            }

            return Directory.EnumerateFiles(directory, "*" + RecipeExtension, SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string SearchUpwardsForPackageRoot(string startPath)
        {
            if (string.IsNullOrWhiteSpace(startPath))
            {
                return null;
            }

            var current = new DirectoryInfo(Path.GetFullPath(startPath));
            while (current != null)
            {
                if (IsPackageRoot(current.FullName))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        private static bool IsPackageRoot(string directory)
        {
            return !string.IsNullOrWhiteSpace(directory)
                && Directory.Exists(directory)
                && File.Exists(Path.Combine(directory, "package.json"))
                && Directory.Exists(Path.Combine(directory, TemplatesDirectoryName));
        }

        private static string FindUnityProjectRoot()
        {
            var envProjectRoot = Environment.GetEnvironmentVariable("UNITY_PROJECT_ROOT");
            if (IsUnityProjectRoot(envProjectRoot))
            {
                return Path.GetFullPath(envProjectRoot);
            }

            var fromCwd = SearchUpwardsForUnityProjectRoot(Directory.GetCurrentDirectory());
            if (!string.IsNullOrEmpty(fromCwd))
            {
                return fromCwd;
            }

            return SearchUpwardsForUnityProjectRoot(AppDomain.CurrentDomain.BaseDirectory);
        }

        private static string SearchUpwardsForUnityProjectRoot(string startPath)
        {
            if (string.IsNullOrWhiteSpace(startPath))
            {
                return null;
            }

            var current = new DirectoryInfo(Path.GetFullPath(startPath));
            while (current != null)
            {
                if (IsUnityProjectRoot(current.FullName))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        private static bool IsUnityProjectRoot(string directory)
        {
            return !string.IsNullOrWhiteSpace(directory)
                && Directory.Exists(directory)
                && Directory.Exists(Path.Combine(directory, "Assets"))
                && File.Exists(Path.Combine(directory, "ProjectSettings", "ProjectSettings.asset"));
        }

        private static string FindPackageRootFromUnityProject(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return null;
            }

            var embeddedPackage = Path.Combine(projectRoot, "Packages", PackageName);
            if (IsPackageRoot(embeddedPackage))
            {
                return embeddedPackage;
            }

            var packageCache = Path.Combine(projectRoot, "Library", "PackageCache");
            if (!Directory.Exists(packageCache))
            {
                return null;
            }

            var directCachePackage = Path.Combine(packageCache, PackageName);
            if (IsPackageRoot(directCachePackage))
            {
                return directCachePackage;
            }

            foreach (var directory in Directory.EnumerateDirectories(packageCache, PackageName + "@*", SearchOption.TopDirectoryOnly))
            {
                if (IsPackageRoot(directory))
                {
                    return directory;
                }
            }

            return null;
        }

        private static string TryMakeRelative(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedPath = Path.GetFullPath(path);
            if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return normalizedPath.Substring(normalizedRoot.Length);
        }
    }
}
