using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Internal.Json;
using UnityEditor;

namespace AIBridge.Editor
{
    /// <summary>最小原生构建计划：不切换目标、不改PlayerSettings、不覆盖已有产物。</summary>
    internal sealed class AIBridgeBuildPlan
    {
        internal const int MaxScenes = 128;
        internal const int MaxScenesJsonChars = 32768;
        public BuildTarget Target;
        public bool Development;
        public bool DryRun;
        public string[] Scenes;

        public static AIBridgeBuildPlan Parse(CommandRequest request)
        {
            foreach (var key in request.@params.Keys)
                if (key != "action" && key != "target" && key != "scenes" && key != "development"
                    && key != "dry-run" && key != "confirm")
                    throw new ArgumentException("Unsupported build run parameter: " + key);
            var dryRun = Flag(request, "dry-run", true);
            var confirmed = Flag(request, "confirm", false);
            if (!dryRun && !confirmed) throw new ArgumentException("Player build requires --dry-run false --confirm true.");
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (request.@params.TryGetValue("target", out var targetValue))
            {
                if (!(targetValue is string name) || !Enum.TryParse(name, false, out BuildTarget parsed)
                    || parsed.ToString() != name || parsed != target)
                    throw new ArgumentException("Only the exact current active BuildTarget is supported; no target switching.");
            }
            if (!AIBridgeRuntimeBuildProcessor.AreRuntimeBootstrapDefinesSynchronized(BuildPipeline.GetBuildTargetGroup(target)))
                throw new ArgumentException("AIBridge Runtime build symbols are not synchronized; synchronize project settings and compile before building.");
            if (target.ToString() == "Android" && (EditorUserBuildSettings.buildAppBundle || EditorUserBuildSettings.exportAsGoogleAndroidProject))
                throw new ArgumentException("This bounded Android build supports the current APK configuration only; app bundle/export requires a separate plan.");
            if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target))
                throw new ArgumentException("The active build target module is unavailable.");
            // 名称按字符串隔离：避免为2019.4引用后续新增的枚举值。
            if (target.ToString() != "StandaloneWindows" && target.ToString() != "StandaloneWindows64"
                && target.ToString() != "StandaloneOSX" && target.ToString() != "StandaloneLinux64"
                && target.ToString() != "Android" && target.ToString() != "iOS" && target.ToString() != "WebGL")
                throw new ArgumentException("Active target is not supported by this bounded build API.");
            var scenes = new List<string>();
            if (request.@params.TryGetValue("scenes", out var sceneValue))
            {
                if (!(sceneValue is string json) || json.Length > MaxScenesJsonChars)
                    throw new ArgumentException("scenes must be a bounded JSON string array.");
                var wrapper = AIBridgeJson.DeserializeObjectStrict("{\"scenes\":" + json + "}", 4);
                if (wrapper == null || wrapper.Count != 1 || !(wrapper["scenes"] is List<object> list) || list.Count > MaxScenes)
                    throw new ArgumentException("scenes must be one JSON array with at most 128 paths.");
                foreach (var value in list)
                {
                    if (!(value is string scene)) throw new ArgumentException("Scene paths must be strings.");
                    scenes.Add(scene);
                }
            }
            else foreach (var scene in EditorBuildSettings.scenes) if (scene.enabled) scenes.Add(scene.path);
            if (scenes.Count == 0 || scenes.Count > MaxScenes) throw new ArgumentException("Build requires 1-128 saved scene assets.");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scene in scenes)
            {
                if (scene.Length > 8192 || !scene.StartsWith("Assets/", StringComparison.Ordinal)
                    || !scene.EndsWith(".unity", StringComparison.Ordinal) || scene.Contains("\\") || scene.Contains("//")
                    || scene.Contains("/../") || scene.Contains("/./") || !unique.Add(scene)
                    || AssetDatabase.LoadAssetAtPath<SceneAsset>(scene) == null)
                    throw new ArgumentException("Every scene must be a unique saved SceneAsset under Assets: " + scene);
            }
            return new AIBridgeBuildPlan { Target = target, Development = Flag(request, "development", false),
                DryRun = dryRun, Scenes = scenes.ToArray() };
        }

        internal static bool Flag(CommandRequest request, string key, bool fallback)
        {
            if (!request.@params.TryGetValue(key, out var value)) return fallback;
            if (!(value is bool flag)) throw new ArgumentException(key + " must be boolean.");
            return flag;
        }

        public static string OutputPath(string projectRoot, string buildId, string target)
        {
            var directory = OutputDirectory(projectRoot, buildId);
            switch (target)
            {
                case "StandaloneWindows": case "StandaloneWindows64": return Path.Combine(directory, "Player.exe");
                case "StandaloneOSX": return Path.Combine(directory, "Player.app");
                case "StandaloneLinux64": return Path.Combine(directory, "Player.x86_64");
                case "Android": return Path.Combine(directory, "Player.apk");
                case "iOS": case "WebGL": return Path.Combine(directory, "Player");
                default: throw new ArgumentException("Unsupported target.");
            }
        }

        public static string OutputDirectory(string projectRoot, string buildId)
        {
            return Path.GetFullPath(Path.Combine(projectRoot, ".aibridge/builds", buildId));
        }

        public BuildPlayerOptions Options(string outputPath)
        {
            return new BuildPlayerOptions { scenes = (string[])Scenes.Clone(), target = Target,
                locationPathName = outputPath, options = Development ? BuildOptions.Development : BuildOptions.None };
        }
    }
}
