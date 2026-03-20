using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace AIBridge.Editor
{
    [InitializeOnLoad]
    public static class WorkflowJobBuildRunner
    {
        private const string BuildAndroidJobType = "build.android";
        private const string BuildIosJobType = "build.ios";

        static WorkflowJobBuildRunner()
        {
        }

        public static void StartAndroidBuild(string jobId)
        {
            EditorApplication.delayCall += () => ExecuteAndroidBuild(jobId);
        }

        public static void StartIosBuild(string jobId)
        {
            EditorApplication.delayCall += () => ExecuteIosBuild(jobId);
        }

        private static void ExecuteAndroidBuild(string jobId)
        {
            var state = WorkflowJobCacheManager.Load(jobId);
            if (state == null)
            {
                return;
            }

            try
            {
                state.phase = "building";
                state.status = "running";
                WorkflowJobCacheManager.Save(state);

                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                {
                    Fail(state, BuildAndroidJobType, "build.android requires the active build target to already be Android in the MVP.");
                    return;
                }

                var scenes = WorkflowBuildSupport.GetEnabledScenes();
                if (scenes.Length == 0)
                {
                    Fail(state, BuildAndroidJobType, "No enabled scenes found in EditorBuildSettings.");
                    return;
                }

                var outputPath = WorkflowBuildSupport.GetRequiredString(state.inputs, "outputPath");
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    Fail(state, BuildAndroidJobType, "build.android requires outputPath.");
                    return;
                }

                var absoluteOutputPath = WorkflowBuildSupport.NormalizeOutputPath(outputPath);
                WorkflowBuildSupport.EnsureParentDirectory(absoluteOutputPath);

                var requestedBundle = WorkflowBuildSupport.TryGetBool(state.inputs, "buildAppBundle");
                var previousBuildAppBundle = EditorUserBuildSettings.buildAppBundle;
                var useAppBundle = requestedBundle ?? absoluteOutputPath.EndsWith(".aab", StringComparison.OrdinalIgnoreCase);
                EditorUserBuildSettings.buildAppBundle = useAppBundle;

                try
                {
                    var buildOptions = BuildOptions.None;
                    if (WorkflowBuildSupport.TryGetBool(state.inputs, "development") == true)
                    {
                        buildOptions |= BuildOptions.Development;
                    }

                    var buildPlayerOptions = new BuildPlayerOptions
                    {
                        scenes = scenes,
                        locationPathName = absoluteOutputPath,
                        target = BuildTarget.Android,
                        options = buildOptions
                    };

                    var report = BuildPipeline.BuildPlayer(buildPlayerOptions);
                    var summary = report.summary;
                    var success = summary.result == BuildResult.Succeeded;

                    state.completedAtUtc = DateTime.UtcNow.ToString("O");
                    state.status = success ? "success" : "failed";
                    state.error = success ? null : "Android build failed.";
                    state.result = new Dictionary<string, object>
                    {
                        ["status"] = state.status,
                        ["outputPath"] = absoluteOutputPath,
                        ["totalErrors"] = summary.totalErrors,
                        ["totalWarnings"] = summary.totalWarnings,
                        ["totalSize"] = (long)summary.totalSize,
                        ["buildTimeSeconds"] = summary.totalTime.TotalSeconds,
                        ["buildTarget"] = summary.platform.ToString(),
                        ["buildAppBundle"] = useAppBundle,
                        ["exists"] = File.Exists(absoluteOutputPath)
                    };

                    WorkflowJobCacheManager.Save(state);
                    WorkflowJobCacheManager.ClearActive(BuildAndroidJobType);
                    WorkflowJobCacheManager.ClearGlobalActive(state.jobId);
                }
                finally
                {
                    EditorUserBuildSettings.buildAppBundle = previousBuildAppBundle;
                }
            }
            catch (Exception ex)
            {
                Fail(state, BuildAndroidJobType, ex.Message);
            }
        }

        private static void ExecuteIosBuild(string jobId)
        {
            var state = WorkflowJobCacheManager.Load(jobId);
            if (state == null)
            {
                return;
            }

            try
            {
                state.phase = "building";
                state.status = "running";
                WorkflowJobCacheManager.Save(state);

                if (!WorkflowBuildSupport.IsMacEditor())
                {
                    Fail(state, BuildIosJobType, "build.ios requires macOS Unity Editor in the MVP.");
                    return;
                }

                if (!WorkflowBuildSupport.IsIosBuildSupportInstalled())
                {
                    Fail(state, BuildIosJobType, "build.ios requires the Unity iOS Build Support module.");
                    return;
                }

                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS)
                {
                    Fail(state, BuildIosJobType, "build.ios requires the active build target to already be iOS in the MVP.");
                    return;
                }

                var scenes = WorkflowBuildSupport.GetEnabledScenes();
                if (scenes.Length == 0)
                {
                    Fail(state, BuildIosJobType, "No enabled scenes found in EditorBuildSettings.");
                    return;
                }

                var outputPath = WorkflowBuildSupport.GetRequiredString(state.inputs, "outputPath");
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    Fail(state, BuildIosJobType, "build.ios requires outputPath.");
                    return;
                }

                if (!WorkflowBuildSupport.IsDirectoryStyleOutputPath(outputPath))
                {
                    Fail(state, BuildIosJobType, "build.ios expects outputPath to be an Xcode project directory, not an .ipa/.app/.xcarchive file path.");
                    return;
                }

                var bundleIdentifier = WorkflowBuildSupport.GetIosBundleIdentifier();
                if (string.IsNullOrWhiteSpace(bundleIdentifier))
                {
                    Fail(state, BuildIosJobType, "build.ios requires a non-empty iOS bundle identifier.");
                    return;
                }

                var absoluteOutputPath = WorkflowBuildSupport.NormalizeOutputPath(outputPath);
                if (File.Exists(absoluteOutputPath))
                {
                    Fail(state, BuildIosJobType, "build.ios outputPath points to a file; expected an Xcode project directory path.");
                    return;
                }

                if (!WorkflowBuildSupport.IsDirectoryMissingOrEmpty(absoluteOutputPath))
                {
                    Fail(state, BuildIosJobType, "build.ios requires outputPath to be a missing or empty directory to avoid stale Xcode project contents.");
                    return;
                }

                WorkflowBuildSupport.EnsureDirectory(absoluteOutputPath);

                var buildOptions = BuildOptions.None;
                if (WorkflowBuildSupport.TryGetBool(state.inputs, "development") == true)
                {
                    buildOptions |= BuildOptions.Development;
                }

                var buildPlayerOptions = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = absoluteOutputPath,
                    target = BuildTarget.iOS,
                    options = buildOptions
                };

                var report = BuildPipeline.BuildPlayer(buildPlayerOptions);
                var summary = report.summary;
                var success = summary.result == BuildResult.Succeeded;

                state.completedAtUtc = DateTime.UtcNow.ToString("O");
                state.status = success ? "success" : "failed";
                state.error = success ? null : "iOS build export failed.";
                state.result = new Dictionary<string, object>
                {
                    ["status"] = state.status,
                    ["outputPath"] = summary.outputPath,
                    ["totalErrors"] = summary.totalErrors,
                    ["totalWarnings"] = summary.totalWarnings,
                    ["totalSize"] = (long)summary.totalSize,
                    ["buildTimeSeconds"] = summary.totalTime.TotalSeconds,
                    ["buildTarget"] = summary.platform.ToString(),
                    ["bundleIdentifier"] = bundleIdentifier,
                    ["exists"] = Directory.Exists(summary.outputPath)
                };

                WorkflowJobCacheManager.Save(state);
                WorkflowJobCacheManager.ClearActive(BuildIosJobType);
                WorkflowJobCacheManager.ClearGlobalActive(state.jobId);
            }
            catch (Exception ex)
            {
                Fail(state, BuildIosJobType, ex.Message);
            }
        }

        private static void Fail(WorkflowJobState state, string jobType, string error)
        {
            state.phase = "building";
            state.status = "failed";
            state.error = error;
            state.completedAtUtc = DateTime.UtcNow.ToString("O");
            state.result = new Dictionary<string, object>
            {
                ["status"] = "failed",
                ["error"] = error
            };
            WorkflowJobCacheManager.Save(state);
            WorkflowJobCacheManager.ClearActive(jobType);
            WorkflowJobCacheManager.ClearGlobalActive(state.jobId);
        }
    }
}
