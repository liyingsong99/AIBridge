using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace AIBridge.Editor
{
    public static class WorkflowBuildSupport
    {
        public static string ProjectRoot => Path.GetDirectoryName(UnityEngine.Application.dataPath);

        public static string[] GetEnabledScenes()
        {
            var scenes = new List<string>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled)
                {
                    scenes.Add(scene.path);
                }
            }

            return scenes.ToArray();
        }

        public static string NormalizeOutputPath(string outputPath)
        {
            return Path.IsPathRooted(outputPath)
                ? outputPath
                : Path.Combine(ProjectRoot, outputPath);
        }

        public static bool? TryGetBool(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (bool.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }

            return null;
        }

        public static string GetRequiredString(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value.ToString();
        }

        public static void EnsureParentDirectory(string absoluteFilePath)
        {
            var outputDirectory = Path.GetDirectoryName(absoluteFilePath);
            if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }
        }

        public static void EnsureDirectory(string absoluteDirectoryPath)
        {
            if (!Directory.Exists(absoluteDirectoryPath))
            {
                Directory.CreateDirectory(absoluteDirectoryPath);
            }
        }

        public static bool IsMacEditor()
        {
            return UnityEngine.Application.platform == UnityEngine.RuntimePlatform.OSXEditor;
        }

        public static bool IsIosBuildSupportInstalled()
        {
            return BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.iOS, BuildTarget.iOS);
        }

        public static string GetIosBundleIdentifier()
        {
            return PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.iOS);
        }

        public static bool IsDirectoryStyleOutputPath(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return false;
            }

            var lower = outputPath.ToLowerInvariant();
            if (lower.EndsWith(".ipa") || lower.EndsWith(".app") || lower.EndsWith(".xcarchive") || lower.EndsWith(".zip"))
            {
                return false;
            }

            var extension = Path.GetExtension(lower);
            return string.IsNullOrEmpty(extension);
        }

        public static bool IsDirectoryMissingOrEmpty(string absoluteDirectoryPath)
        {
            if (!Directory.Exists(absoluteDirectoryPath))
            {
                return true;
            }

            return Directory.GetFileSystemEntries(absoluteDirectoryPath).Length == 0;
        }
    }
}
