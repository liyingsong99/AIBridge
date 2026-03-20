using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;

namespace AIBridge.Editor
{
    public class WorkflowJobCommand : ICommand
    {
        private const string CompileUnityJobType = "compile.unity";
        private const string AndroidPreflightJobType = "android.preflight";
        private const string BuildAndroidJobType = "build.android";
        private const string IosPreflightJobType = "ios.preflight";
        private const string BuildIosJobType = "build.ios";
        private const string SceneBulkCreateJobType = "scene.bulk_create";
        private const string VersionBumpJobType = "version.bump";

        public string Type => "workflow_job";
        public bool RequiresRefresh => false;

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "start");

            try
            {
                WorkflowJobCacheManager.CleanupOldJobs();

                switch (action.ToLowerInvariant())
                {
                    case "start":
                        return StartJob(request);
                    case "status":
                        return GetStatus(request);
                    case "result":
                        return GetResult(request);
                    case "cancel":
                        return CommandResult.Failure(request.id, "workflow_job cancel is not supported in the MVP.");
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: start, status, result, cancel");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult StartJob(CommandRequest request)
        {
            var jobType = request.GetParam<string>("jobType");
            if (!string.Equals(jobType, CompileUnityJobType, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(jobType, AndroidPreflightJobType, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(jobType, BuildAndroidJobType, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(jobType, IosPreflightJobType, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(jobType, BuildIosJobType, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(jobType, SceneBulkCreateJobType, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(jobType, VersionBumpJobType, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Failure(request.id, $"Unsupported workflow job type: {jobType}. Supported: {CompileUnityJobType}, {AndroidPreflightJobType}, {BuildAndroidJobType}, {IosPreflightJobType}, {BuildIosJobType}, {SceneBulkCreateJobType}, {VersionBumpJobType}");
            }

            var activeJob = WorkflowJobCacheManager.LoadActive(jobType);
            if (activeJob != null && string.Equals(activeJob.status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return BuildStateResponse(request.id, activeJob, true);
            }

            var globalActiveJob = WorkflowJobCacheManager.LoadGlobalActive();
            if (globalActiveJob != null && string.Equals(globalActiveJob.status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Failure(request.id, $"Another workflow job is already running: {globalActiveJob.jobType} ({globalActiveJob.jobId}).");
            }

            if (string.Equals(jobType, BuildAndroidJobType, StringComparison.OrdinalIgnoreCase))
            {
                return StartAndroidBuildJob(request);
            }

            if (string.Equals(jobType, BuildIosJobType, StringComparison.OrdinalIgnoreCase))
            {
                return StartIosBuildJob(request);
            }

            if (string.Equals(jobType, AndroidPreflightJobType, StringComparison.OrdinalIgnoreCase))
            {
                return StartAndroidPreflightJob(request);
            }

            if (string.Equals(jobType, IosPreflightJobType, StringComparison.OrdinalIgnoreCase))
            {
                return StartIosPreflightJob(request);
            }

            if (string.Equals(jobType, VersionBumpJobType, StringComparison.OrdinalIgnoreCase))
            {
                return StartVersionBumpJob(request);
            }

            if (string.Equals(jobType, SceneBulkCreateJobType, StringComparison.OrdinalIgnoreCase))
            {
                return StartSceneBulkCreateJob(request);
            }

            if (BuildPipeline.isBuildingPlayer)
            {
                return CommandResult.Failure(request.id, "Cannot start compile.unity while Unity is building a player.");
            }

            var requestedJobId = request.GetParam<string>("jobId");
            var jobId = string.IsNullOrWhiteSpace(requestedJobId)
                ? "job_" + Guid.NewGuid().ToString("N")
                : requestedJobId;

            var compileRequest = new CommandRequest
            {
                id = request.id,
                type = "compile",
                @params = new Dictionary<string, object>
                {
                    ["action"] = "start"
                }
            };

            var compileResult = new CompileCommand().Execute(compileRequest);
            if (!compileResult.success)
            {
                return compileResult;
            }

            var compileStartPayload = compileResult.data as Dictionary<string, object>;
            if (compileStartPayload == null)
            {
                compileStartPayload = AIBridge.Internal.Json.AIBridgeJson.DeserializeObject(AIBridge.Internal.Json.AIBridgeJson.Serialize(compileResult.data));
            }

            var alreadyCompiling = compileStartPayload != null && compileStartPayload.TryGetValue("alreadyCompiling", out var alreadyCompilingValue) && ToBool(alreadyCompilingValue);
            if (alreadyCompiling)
            {
                return CommandResult.Failure(request.id, "Unity compilation is already running outside workflow_job ownership. Wait for it to finish or poll compile status directly.");
            }

            var state = new WorkflowJobState
            {
                jobId = jobId,
                jobType = jobType,
                status = "running",
                phase = "compile",
                startedAtUtc = DateTime.UtcNow.ToString("O"),
                inputs = ExtractInputs(request),
                result = compileStartPayload ?? new Dictionary<string, object>()
            };

            WorkflowJobCacheManager.Save(state);
            WorkflowJobCacheManager.SaveActive(jobType, state.jobId);
            WorkflowJobCacheManager.SaveGlobalActive(jobType, state.jobId);
            return BuildStateResponse(request.id, state, false);
        }

        private CommandResult StartAndroidBuildJob(CommandRequest request)
        {
            var requestedJobId = request.GetParam<string>("jobId");
            var jobId = string.IsNullOrWhiteSpace(requestedJobId)
                ? "job_" + Guid.NewGuid().ToString("N")
                : requestedJobId;

            if (!TryValidateAndroidBuildInputs(request, out var outputPath, out var buildAppBundle, out var validationError))
            {
                return CommandResult.Failure(request.id, validationError);
            }

            if (EditorApplication.isCompiling)
            {
                return CommandResult.Failure(request.id, "Cannot start build.android while Unity is compiling.");
            }

            if (BuildPipeline.isBuildingPlayer)
            {
                return CommandResult.Failure(request.id, "Cannot start build.android while Unity is already building a player.");
            }

            var state = new WorkflowJobState
            {
                jobId = jobId,
                jobType = BuildAndroidJobType,
                status = "running",
                phase = "queued",
                startedAtUtc = DateTime.UtcNow.ToString("O"),
                inputs = ExtractInputs(request),
                result = new Dictionary<string, object>
                {
                    ["status"] = "running",
                    ["outputPath"] = outputPath
                }
            };

            WorkflowJobCacheManager.Save(state);
            WorkflowJobCacheManager.SaveActive(BuildAndroidJobType, state.jobId);
            WorkflowJobCacheManager.SaveGlobalActive(BuildAndroidJobType, state.jobId);
            WorkflowJobBuildRunner.StartAndroidBuild(state.jobId);
            return BuildStateResponse(request.id, state, false);
        }

        private CommandResult StartIosBuildJob(CommandRequest request)
        {
            var requestedJobId = request.GetParam<string>("jobId");
            var jobId = string.IsNullOrWhiteSpace(requestedJobId)
                ? "job_" + Guid.NewGuid().ToString("N")
                : requestedJobId;

            if (!TryValidateIosBuildInputs(request, out var outputPath, out var validationError))
            {
                return CommandResult.Failure(request.id, validationError);
            }

            if (EditorApplication.isCompiling)
            {
                return CommandResult.Failure(request.id, "Cannot start build.ios while Unity is compiling.");
            }

            if (BuildPipeline.isBuildingPlayer)
            {
                return CommandResult.Failure(request.id, "Cannot start build.ios while Unity is already building a player.");
            }

            var state = new WorkflowJobState
            {
                jobId = jobId,
                jobType = BuildIosJobType,
                status = "running",
                phase = "queued",
                startedAtUtc = DateTime.UtcNow.ToString("O"),
                inputs = ExtractInputs(request),
                result = new Dictionary<string, object>
                {
                    ["status"] = "running",
                    ["outputPath"] = outputPath
                }
            };

            WorkflowJobCacheManager.Save(state);
            WorkflowJobCacheManager.SaveActive(BuildIosJobType, state.jobId);
            WorkflowJobCacheManager.SaveGlobalActive(BuildIosJobType, state.jobId);
            WorkflowJobBuildRunner.StartIosBuild(state.jobId);
            return BuildStateResponse(request.id, state, false);
        }

        private CommandResult StartAndroidPreflightJob(CommandRequest request)
        {
            if (!TryValidateAndroidBuildInputs(request, out var outputPath, out var buildAppBundle, out var validationError))
            {
                return CommandResult.Failure(request.id, validationError);
            }

            if (EditorApplication.isCompiling)
            {
                return CommandResult.Failure(request.id, "Cannot start android.preflight while Unity is compiling.");
            }

            if (BuildPipeline.isBuildingPlayer)
            {
                return CommandResult.Failure(request.id, "Cannot start android.preflight while Unity is already building a player.");
            }

            var requestedJobId = request.GetParam<string>("jobId");
            var jobId = string.IsNullOrWhiteSpace(requestedJobId)
                ? "job_" + Guid.NewGuid().ToString("N")
                : requestedJobId;

            var projectRoot = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath);
            var absoluteOutputPath = System.IO.Path.IsPathRooted(outputPath)
                ? outputPath
                : System.IO.Path.Combine(projectRoot, outputPath);
            var outputDirectory = System.IO.Path.GetDirectoryName(absoluteOutputPath);
            var enabledSceneCount = 0;
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled)
                {
                    enabledSceneCount++;
                }
            }

            var checks = new Dictionary<string, object>
            {
                ["activeBuildTargetIsAndroid"] = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android,
                ["enabledScenesFound"] = enabledSceneCount > 0,
                ["enabledSceneCount"] = enabledSceneCount,
                ["outputPathProvided"] = !string.IsNullOrWhiteSpace(outputPath),
                ["outputExtensionValid"] = buildAppBundle == true
                    ? absoluteOutputPath.EndsWith(".aab", StringComparison.OrdinalIgnoreCase)
                    : absoluteOutputPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || absoluteOutputPath.EndsWith(".aab", StringComparison.OrdinalIgnoreCase),
                ["outputPath"] = absoluteOutputPath,
                ["outputDirectory"] = outputDirectory ?? string.Empty,
                ["outputDirectoryExists"] = !string.IsNullOrWhiteSpace(outputDirectory) && System.IO.Directory.Exists(outputDirectory),
                ["buildAppBundle"] = buildAppBundle ?? absoluteOutputPath.EndsWith(".aab", StringComparison.OrdinalIgnoreCase),
                ["bundleVersion"] = PlayerSettings.bundleVersion,
                ["androidVersionCode"] = PlayerSettings.Android.bundleVersionCode
            };

            var passed =
                ToBool(checks["activeBuildTargetIsAndroid"]) &&
                ToBool(checks["enabledScenesFound"]) &&
                ToBool(checks["outputPathProvided"]) &&
                ToBool(checks["outputExtensionValid"]);

            var state = new WorkflowJobState
            {
                jobId = jobId,
                jobType = AndroidPreflightJobType,
                status = passed ? "success" : "failed",
                phase = "completed",
                startedAtUtc = DateTime.UtcNow.ToString("O"),
                completedAtUtc = DateTime.UtcNow.ToString("O"),
                error = passed ? null : "android.preflight failed. Inspect result checks for details.",
                inputs = ExtractInputs(request),
                result = new Dictionary<string, object>
                {
                    ["status"] = passed ? "success" : "failed",
                    ["checks"] = checks
                }
            };

            WorkflowJobCacheManager.Save(state);
            return BuildStateResponse(request.id, state, false);
        }

        private CommandResult StartVersionBumpJob(CommandRequest request)
        {
            if (EditorApplication.isCompiling)
            {
                return CommandResult.Failure(request.id, "Cannot start version.bump while Unity is compiling.");
            }

            if (BuildPipeline.isBuildingPlayer)
            {
                return CommandResult.Failure(request.id, "Cannot start version.bump while Unity is already building a player.");
            }

            var requestedJobId = request.GetParam<string>("jobId");
            var jobId = string.IsNullOrWhiteSpace(requestedJobId)
                ? "job_" + Guid.NewGuid().ToString("N")
                : requestedJobId;

            var bundleVersion = request.GetParam<string>("bundleVersion");
            var androidVersionCode = TryGetOptionalInt(request, "androidVersionCode");

            if (string.IsNullOrWhiteSpace(bundleVersion) && !androidVersionCode.HasValue)
            {
                return CommandResult.Failure(request.id, "version.bump requires bundleVersion and/or androidVersionCode.");
            }

            if (androidVersionCode.HasValue && androidVersionCode.Value <= 0)
            {
                return CommandResult.Failure(request.id, "version.bump requires androidVersionCode > 0 when provided.");
            }

            var previousBundleVersion = PlayerSettings.bundleVersion;
            var previousAndroidVersionCode = PlayerSettings.Android.bundleVersionCode;

            if (!string.IsNullOrWhiteSpace(bundleVersion))
            {
                PlayerSettings.bundleVersion = bundleVersion;
            }

            if (androidVersionCode.HasValue)
            {
                PlayerSettings.Android.bundleVersionCode = androidVersionCode.Value;
            }

            var state = new WorkflowJobState
            {
                jobId = jobId,
                jobType = VersionBumpJobType,
                status = "success",
                phase = "completed",
                startedAtUtc = DateTime.UtcNow.ToString("O"),
                completedAtUtc = DateTime.UtcNow.ToString("O"),
                inputs = ExtractInputs(request),
                result = new Dictionary<string, object>
                {
                    ["status"] = "success",
                    ["previousBundleVersion"] = previousBundleVersion,
                    ["previousAndroidVersionCode"] = previousAndroidVersionCode,
                    ["bundleVersion"] = PlayerSettings.bundleVersion,
                    ["androidVersionCode"] = PlayerSettings.Android.bundleVersionCode
                }
            };

            WorkflowJobCacheManager.Save(state);
            return BuildStateResponse(request.id, state, false);
        }

        private CommandResult StartSceneBulkCreateJob(CommandRequest request)
        {
            if (EditorApplication.isCompiling)
            {
                return CommandResult.Failure(request.id, "Cannot start scene.bulk_create while Unity is compiling.");
            }

            if (BuildPipeline.isBuildingPlayer)
            {
                return CommandResult.Failure(request.id, "Cannot start scene.bulk_create while Unity is already building a player.");
            }

            var requestedJobId = request.GetParam<string>("jobId");
            var jobId = string.IsNullOrWhiteSpace(requestedJobId)
                ? "job_" + Guid.NewGuid().ToString("N")
                : requestedJobId;

            var state = WorkflowSceneBulkCreateRunner.Execute(jobId, ExtractInputs(request));
            WorkflowJobCacheManager.Save(state);
            return BuildStateResponse(request.id, state, false);
        }

        private CommandResult StartIosPreflightJob(CommandRequest request)
        {
            if (!TryValidateIosBuildInputs(request, out var outputPath, out var validationError))
            {
                return CommandResult.Failure(request.id, validationError);
            }

            if (EditorApplication.isCompiling)
            {
                return CommandResult.Failure(request.id, "Cannot start ios.preflight while Unity is compiling.");
            }

            if (BuildPipeline.isBuildingPlayer)
            {
                return CommandResult.Failure(request.id, "Cannot start ios.preflight while Unity is already building a player.");
            }

            var requestedJobId = request.GetParam<string>("jobId");
            var jobId = string.IsNullOrWhiteSpace(requestedJobId)
                ? "job_" + Guid.NewGuid().ToString("N")
                : requestedJobId;

            var absoluteOutputPath = WorkflowBuildSupport.NormalizeOutputPath(outputPath);
            var outputDirectoryExists = System.IO.Directory.Exists(absoluteOutputPath);
            var outputDirectoryClean = WorkflowBuildSupport.IsDirectoryMissingOrEmpty(absoluteOutputPath);
            var enabledScenes = WorkflowBuildSupport.GetEnabledScenes();
            var bundleIdentifier = WorkflowBuildSupport.GetIosBundleIdentifier();

            var checks = new Dictionary<string, object>
            {
                ["isMacEditor"] = WorkflowBuildSupport.IsMacEditor(),
                ["iosBuildSupportInstalled"] = WorkflowBuildSupport.IsIosBuildSupportInstalled(),
                ["activeBuildTargetIsIos"] = EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS,
                ["enabledScenesFound"] = enabledScenes.Length > 0,
                ["enabledSceneCount"] = enabledScenes.Length,
                ["outputPathProvided"] = !string.IsNullOrWhiteSpace(outputPath),
                ["outputPathLooksLikeDirectory"] = WorkflowBuildSupport.IsDirectoryStyleOutputPath(outputPath),
                ["outputPath"] = absoluteOutputPath,
                ["outputDirectoryExists"] = outputDirectoryExists,
                ["outputDirectoryMissingOrEmpty"] = outputDirectoryClean,
                ["bundleIdentifierConfigured"] = !string.IsNullOrWhiteSpace(bundleIdentifier),
                ["bundleIdentifier"] = bundleIdentifier ?? string.Empty
            };

            var passed =
                ToBool(checks["isMacEditor"]) &&
                ToBool(checks["iosBuildSupportInstalled"]) &&
                ToBool(checks["activeBuildTargetIsIos"]) &&
                ToBool(checks["enabledScenesFound"]) &&
                ToBool(checks["outputPathProvided"]) &&
                ToBool(checks["outputPathLooksLikeDirectory"]) &&
                ToBool(checks["outputDirectoryMissingOrEmpty"]) &&
                ToBool(checks["bundleIdentifierConfigured"]);

            var state = new WorkflowJobState
            {
                jobId = jobId,
                jobType = IosPreflightJobType,
                status = passed ? "success" : "failed",
                phase = "completed",
                startedAtUtc = DateTime.UtcNow.ToString("O"),
                completedAtUtc = DateTime.UtcNow.ToString("O"),
                error = passed ? null : "ios.preflight failed. Inspect result checks for details.",
                inputs = ExtractInputs(request),
                result = new Dictionary<string, object>
                {
                    ["status"] = passed ? "success" : "failed",
                    ["checks"] = checks
                }
            };

            WorkflowJobCacheManager.Save(state);
            return BuildStateResponse(request.id, state, false);
        }

        private CommandResult GetStatus(CommandRequest request)
        {
            var state = ResolveJob(request);
            if (state == null)
            {
                return CommandResult.Failure(request.id, "Workflow job not found.");
            }

            if (string.Equals(state.jobType, CompileUnityJobType, StringComparison.OrdinalIgnoreCase))
            {
                return GetCompileUnityStatus(request, state);
            }

            if (string.Equals(state.jobType, BuildAndroidJobType, StringComparison.OrdinalIgnoreCase))
            {
                return BuildStateResponse(request.id, state, false);
            }

            if (string.Equals(state.jobType, BuildIosJobType, StringComparison.OrdinalIgnoreCase))
            {
                return BuildStateResponse(request.id, state, false);
            }

            if (string.Equals(state.jobType, AndroidPreflightJobType, StringComparison.OrdinalIgnoreCase))
            {
                return BuildStateResponse(request.id, state, false);
            }

            if (string.Equals(state.jobType, IosPreflightJobType, StringComparison.OrdinalIgnoreCase))
            {
                return BuildStateResponse(request.id, state, false);
            }

            if (string.Equals(state.jobType, VersionBumpJobType, StringComparison.OrdinalIgnoreCase))
            {
                return BuildStateResponse(request.id, state, false);
            }

            if (string.Equals(state.jobType, SceneBulkCreateJobType, StringComparison.OrdinalIgnoreCase))
            {
                return BuildStateResponse(request.id, state, false);
            }

            return CommandResult.Failure(request.id, $"Unsupported workflow job type: {state.jobType}");
        }

        private CommandResult GetResult(CommandRequest request)
        {
            var state = ResolveJob(request);
            if (state == null)
            {
                return CommandResult.Failure(request.id, "Workflow job not found.");
            }

            return BuildStateResponse(request.id, state, false);
        }

        private CommandResult GetCompileUnityStatus(CommandRequest request, WorkflowJobState state)
        {
            if (IsTerminal(state.status))
            {
                return BuildStateResponse(request.id, state, false);
            }

            var statusRequest = new CommandRequest
            {
                id = request.id,
                type = "compile",
                @params = new Dictionary<string, object>
                {
                    ["action"] = "status",
                    ["includeDetails"] = true
                }
            };

            var compileStatus = new CompileCommand().Execute(statusRequest);
            if (!compileStatus.success)
            {
                state.status = "failed";
                state.error = compileStatus.error;
                state.completedAtUtc = DateTime.UtcNow.ToString("O");
                WorkflowJobCacheManager.Save(state);
                WorkflowJobCacheManager.ClearActive(CompileUnityJobType);
                WorkflowJobCacheManager.ClearGlobalActive(state.jobId);
                return compileStatus;
            }

            var payload = compileStatus.data as Dictionary<string, object>;
            if (payload == null)
            {
                payload = AIBridge.Internal.Json.AIBridgeJson.DeserializeObject(AIBridge.Internal.Json.AIBridgeJson.Serialize(compileStatus.data));
            }

            var compileStatusValue = payload != null && payload.TryGetValue("status", out var statusValue) && statusValue != null
                ? statusValue.ToString()
                : "unknown";

            state.phase = "compile";
            state.result = payload ?? new Dictionary<string, object>();

            switch (compileStatusValue)
            {
                case "compiling":
                    state.status = "running";
                    break;
                case "success":
                    state.status = "success";
                    state.completedAtUtc = DateTime.UtcNow.ToString("O");
                    state.error = null;
                    WorkflowJobCacheManager.ClearActive(CompileUnityJobType);
                    WorkflowJobCacheManager.ClearGlobalActive(state.jobId);
                    break;
                case "failed":
                    state.status = "failed";
                    state.completedAtUtc = DateTime.UtcNow.ToString("O");
                    state.error = "Unity compilation failed.";
                    WorkflowJobCacheManager.ClearActive(CompileUnityJobType);
                    WorkflowJobCacheManager.ClearGlobalActive(state.jobId);
                    break;
                case "idle":
                default:
                    state.status = "running";
                    break;
            }

            WorkflowJobCacheManager.Save(state);
            return BuildStateResponse(request.id, state, false);
        }

        private static WorkflowJobState ResolveJob(CommandRequest request)
        {
            var jobId = request.GetParam<string>("jobId");
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                return WorkflowJobCacheManager.Load(jobId);
            }

            return WorkflowJobCacheManager.LoadLast();
        }

        private static Dictionary<string, object> ExtractInputs(CommandRequest request)
        {
            var inputs = new Dictionary<string, object>();
            if (request.@params == null)
            {
                return inputs;
            }

            foreach (var pair in request.@params)
            {
                if (pair.Key == "action" || pair.Key == "jobType" || pair.Key == "jobId")
                {
                    continue;
                }

                inputs[pair.Key] = pair.Value;
            }

            return inputs;
        }

        private static CommandResult BuildStateResponse(string requestId, WorkflowJobState state, bool reusedExistingJob)
        {
            return CommandResult.Success(requestId, new
            {
                jobId = state.jobId,
                jobType = state.jobType,
                status = state.status,
                phase = state.phase,
                error = state.error,
                startedAtUtc = state.startedAtUtc,
                updatedAtUtc = state.updatedAtUtc,
                completedAtUtc = state.completedAtUtc,
                result = state.result,
                reusedExistingJob = reusedExistingJob
            });
        }

        private static bool IsTerminal(string status)
        {
            return string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ToBool(object value)
        {
            if (value is bool boolValue)
            {
                return boolValue;
            }

            return bool.TryParse(value?.ToString(), out var parsed) && parsed;
        }

        private static bool? TryGetOptionalBool(CommandRequest request, string key)
        {
            if (request.@params == null || !request.@params.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return ToBool(value);
        }

        private static bool TryValidateAndroidBuildInputs(CommandRequest request, out string outputPath, out bool? buildAppBundle, out string error)
        {
            outputPath = request.GetParam<string>("outputPath");
            buildAppBundle = TryGetOptionalBool(request, "buildAppBundle");
            error = null;

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                error = "Android build workflows require outputPath.";
                return false;
            }

            if (buildAppBundle == true && !outputPath.EndsWith(".aab", StringComparison.OrdinalIgnoreCase))
            {
                error = "Android build workflows with buildAppBundle=true require outputPath to end with .aab";
                return false;
            }

            if (buildAppBundle != true && !outputPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && !outputPath.EndsWith(".aab", StringComparison.OrdinalIgnoreCase))
            {
                error = "Android build workflows require outputPath to end with .apk or .aab";
                return false;
            }

            return true;
        }

        private static bool TryValidateIosBuildInputs(CommandRequest request, out string outputPath, out string error)
        {
            outputPath = request.GetParam<string>("outputPath");
            error = null;

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                error = "iOS build workflows require outputPath.";
                return false;
            }

            if (!WorkflowBuildSupport.IsDirectoryStyleOutputPath(outputPath))
            {
                error = "iOS build workflows require outputPath to point to an Xcode project directory, not an .ipa/.app/.xcarchive file path.";
                return false;
            }

            return true;
        }

        private static int? TryGetOptionalInt(CommandRequest request, string key)
        {
            if (request.@params == null || !request.@params.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                if (longValue > int.MaxValue || longValue < int.MinValue)
                {
                    return null;
                }

                return (int)longValue;
            }

            return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
        }
    }
}
