using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using AIBridge.Internal.Json;
using AIBridge.Runtime.Diagnostics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace AIBridge.Editor
{
    public sealed class ProfilerCommand : ICommand, ICommandSkillDocProvider
    {
        private const string ModulePrefsPrefix = "AIBridge.Profiler.Module.";
        private const string DefaultSnapshotDirectory = ".aibridge/profiler";
        private const string ProfilerWindowTypeName = "UnityEditor.ProfilerWindow,UnityEditor";

        private static readonly string[] KnownModules =
        {
            "CPU Usage",
            "Rendering",
            "Memory",
            "Audio",
            "Video",
            "Physics",
            "Physics 2D",
            "Network Messages",
            "UI",
            "UI Details",
            "Global Illumination"
        };

        public string Type => "profiler";
        public bool RequiresRefresh => false;

        public CommandSkillDoc SkillDoc => new CommandSkillDoc(SkillDescription)
        {
            TargetSkillName = "aibridge-development-workflow",
            TargetReferenceFileName = "profiler-api-reference.md"
        };

        public string SkillDescription => @"### `profiler` - Editor Profiler Diagnostics

Use only for performance/Profiler debugging. The command returns AIBridge JSON snapshots; `save_data` / `load_data` do not read or write Unity native `.data` Profiler files.

```bash
$CLI profiler start
$CLI profiler get_status
$CLI profiler list_modules
$CLI profiler enable_module --module ""Memory"" --enabled true
$CLI profiler capture_frame
$CLI profiler get_memory_stats
$CLI profiler get_rendering_stats
$CLI profiler get_script_stats
$CLI profiler save_data --path "".aibridge/profiler/latest.json""
$CLI profiler load_data --path "".aibridge/profiler/latest.json""
$CLI profiler stop
$CLI profiler native_capabilities
$CLI profiler save_profile --path "".aibridge/profiler/native/capture.data"" --confirm true
$CLI profiler load_profile --path "".aibridge/profiler/native/capture.data"" --confirm true
```

Native `.data` actions use exact Unity ProfilerDriver APIs; recording must already be stopped. Save never overwrites; load replaces shared history. No automatic recording, target switch or JSON fallback. Unknown native outcome retains the local gate until domain reload. Memory `.snap` uses the separate `memory` command.

Unsupported or unstable Unity Profiler surfaces are reported in the `unsupported` array instead of being silently treated as available. Runtime Player performance evidence still uses `runtime perf`.";

        public CommandResult Execute(CommandRequest request)
        {
            var action = NormalizeAction(request.GetParam("action", "get_status"));
            if ((action == "start" || action == "stop" || action == "clear_data")
                && (ProfilerNativeGate.Busy || AIBridgeBuildTracker.Busy || (PipelineTestTracker.HasBusy || PipelineCompileTracker.HasBusy)))
                return CommandResult.Failure(request.id, "Native capture, build or Pipeline test is active or unknown; resolve its original request first.");
            if (action == "native_capabilities" || action == "save_profile" || action == "load_profile")
            {
                if (action != "native_capabilities" && (AIBridgeBuildTracker.Busy || CodeCommand.HasActiveCodeOperation || (PipelineTestTracker.HasBusy || PipelineCompileTracker.HasBusy)))
                    return CommandResult.Failure(request.id, "An AIBridge build, code or Pipeline test is active or unknown; resolve its original request before native Profiler IO.");
                return ProfilerNativeFiles.Execute(request, action);
            }
            switch (action)
            {
                case "start":
                    return CommandResult.Success(request.id, StartProfiler());
                case "stop":
                    return CommandResult.Success(request.id, StopProfiler());
                case "get_status":
                    return CommandResult.Success(request.id, CaptureSnapshot("status"));
                case "list_modules":
                    return CommandResult.Success(request.id, new
                    {
                        source = "editor",
                        timestampUtc = UtcNow(),
                        modules = BuildModules()
                    });
                case "enable_module":
                    return CommandResult.Success(request.id, EnableModule(request));
                case "clear_data":
                    return CommandResult.Success(request.id, ClearData());
                case "capture_frame":
                    return CommandResult.Success(request.id, CaptureSnapshot("frame"));
                case "get_memory_stats":
                    return CommandResult.Success(request.id, CaptureSnapshot("memory"));
                case "get_rendering_stats":
                    return CommandResult.Success(request.id, CaptureSnapshot("rendering"));
                case "get_script_stats":
                    return CommandResult.Success(request.id, CaptureSnapshot("script"));
                case "save_data":
                    return CommandResult.Success(request.id, SaveData(request));
                case "load_data":
                    return CommandResult.Success(request.id, LoadData(request));
                default:
                    return CommandResult.Failure(request.id, "Unknown profiler action: " + action);
            }
        }

        private static string NormalizeAction(string action)
        {
            return string.IsNullOrWhiteSpace(action)
                ? "get_status"
                : action.Trim().ToLowerInvariant();
        }

        private static object StartProfiler()
        {
            var warnings = new List<string>();
            Profiler.enabled = true;
            FocusProfilerWindow(warnings);
            var snapshot = CaptureSnapshot("status", warnings);
            return new
            {
                started = Profiler.enabled,
                snapshot = snapshot
            };
        }

        private static object StopProfiler()
        {
            Profiler.enabled = false;
            return new
            {
                stopped = !Profiler.enabled,
                snapshot = CaptureSnapshot("status")
            };
        }

        private static object EnableModule(CommandRequest request)
        {
            var module = request.GetParam("module", request.GetParam("name", string.Empty));
            if (string.IsNullOrWhiteSpace(module))
            {
                return new
                {
                    source = "editor",
                    timestampUtc = UtcNow(),
                    enabled = false,
                    unsupported = new[]
                    {
                        new ProfilerUnsupportedItem("module", "Missing --module.")
                    },
                    modules = BuildModules()
                };
            }

            var enabled = request.GetParam("enabled", request.GetParam("enable", true));
            var normalizedModule = ResolveModuleName(module);
            EditorPrefs.SetBool(ModulePrefsPrefix + normalizedModule, enabled);
            return new
            {
                source = "editor",
                timestampUtc = UtcNow(),
                module = normalizedModule,
                enabled = enabled,
                modules = BuildModules(),
                unsupported = new[]
                {
                    new ProfilerUnsupportedItem("unityProfilerModuleToggle", "AIBridge stores a local module flag; Unity does not expose a stable public API for toggling every Profiler module across supported versions.")
                }
            };
        }

        private static object ClearData()
        {
            string error;
            var cleared = TryCallProfilerDriverClear(out error);
            var unsupported = cleared
                ? new ProfilerUnsupportedItem[0]
                : new[]
                {
                    new ProfilerUnsupportedItem("clearEditorProfilerData", error)
                };

            return new
            {
                source = "editor",
                timestampUtc = UtcNow(),
                cleared = cleared,
                unsupported = unsupported,
                snapshot = CaptureSnapshot("status")
            };
        }

        private static object SaveData(CommandRequest request)
        {
            var path = request.GetParam("path", string.Empty);
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(DefaultSnapshotDirectory, "profiler-snapshot-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
            }

            var fullPath = ResolveProjectPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var snapshot = CaptureSnapshot("all");
            File.WriteAllText(fullPath, AIBridgeJson.Serialize(snapshot, pretty: true), System.Text.Encoding.UTF8);
            return new
            {
                source = "editor",
                timestampUtc = UtcNow(),
                path = fullPath,
                snapshot = snapshot
            };
        }

        private static object LoadData(CommandRequest request)
        {
            var path = request.GetParam("path", string.Empty);
            if (string.IsNullOrWhiteSpace(path))
            {
                return new
                {
                    source = "editor",
                    timestampUtc = UtcNow(),
                    loaded = false,
                    unsupported = new[] { new ProfilerUnsupportedItem("path", "Missing --path.") }
                };
            }

            var fullPath = ResolveProjectPath(path);
            if (!File.Exists(fullPath))
            {
                return new
                {
                    source = "editor",
                    timestampUtc = UtcNow(),
                    loaded = false,
                    path = fullPath,
                    unsupported = new[] { new ProfilerUnsupportedItem("path", "Profiler snapshot file does not exist.") }
                };
            }

            var json = File.ReadAllText(fullPath, System.Text.Encoding.UTF8);
            return new
            {
                source = "editor",
                timestampUtc = UtcNow(),
                loaded = true,
                path = fullPath,
                snapshot = AIBridgeJson.Deserialize(json)
            };
        }

        private static ProfilerSnapshot CaptureSnapshot(string scope)
        {
            return CaptureSnapshot(scope, null);
        }

        private static ProfilerSnapshot CaptureSnapshot(string scope, List<string> warnings)
        {
            var unsupported = new List<ProfilerUnsupportedItem>();
            var stats = new ProfilerStats
            {
                status = BuildStatusStats(),
                modules = BuildModules()
            };

            if (scope == "all" || scope == "status" || scope == "frame" || scope == "rendering" || scope == "script")
            {
                stats.frame = BuildFrameStats();
            }

            if (scope == "all" || scope == "memory" || scope == "script")
            {
                stats.memory = BuildMemoryStats(unsupported);
            }

            if (scope == "all" || scope == "rendering")
            {
                stats.rendering = BuildRenderingStats();
            }

            if (scope == "all" || scope == "script")
            {
                stats.script = BuildScriptStats(stats.frame ?? BuildFrameStats(), stats.memory ?? BuildMemoryStats(unsupported), unsupported);
            }

            return new ProfilerSnapshot
            {
                source = "editor",
                timestampUtc = UtcNow(),
                targetId = "editor",
                stats = stats,
                unsupported = unsupported.ToArray(),
                warnings = warnings == null ? new string[0] : warnings.ToArray()
            };
        }

        private static ProfilerStatusStats BuildStatusStats()
        {
            return new ProfilerStatusStats
            {
                profilerEnabled = Profiler.enabled,
                isEditor = true,
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                supportsRuntimeProfiler = true,
                supportsEditorProfiler = true
            };
        }

        private static ProfilerModuleStats[] BuildModules()
        {
            var modules = new ProfilerModuleStats[KnownModules.Length];
            for (var i = 0; i < KnownModules.Length; i++)
            {
                var name = KnownModules[i];
                modules[i] = new ProfilerModuleStats
                {
                    name = name,
                    enabled = EditorPrefs.GetBool(ModulePrefsPrefix + name, true),
                    supported = true,
                    source = "aibridge-local"
                };
            }

            return modules;
        }

        private static ProfilerFrameStats BuildFrameStats()
        {
            var deltaTimeMs = Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime * 1000d : 0d;
            return new ProfilerFrameStats
            {
                frameCount = Time.frameCount,
                deltaTimeMs = deltaTimeMs,
                fps = deltaTimeMs > 0d ? 1000d / deltaTimeMs : 0d,
                realtimeSinceStartup = Time.realtimeSinceStartup,
                sampleSource = "Time.unscaledDeltaTime"
            };
        }

        private static ProfilerMemoryStats BuildMemoryStats(List<ProfilerUnsupportedItem> unsupported)
        {
            var graphicsDriverBytes = SafeProfilerLongMethod("GetAllocatedMemoryForGraphicsDriver", unsupported, "graphicsDriverBytes");
            return new ProfilerMemoryStats
            {
                totalReservedBytes = SafeProfilerValue(Profiler.GetTotalReservedMemoryLong),
                totalAllocatedBytes = SafeProfilerValue(Profiler.GetTotalAllocatedMemoryLong),
                totalUnusedReservedBytes = SafeProfilerValue(Profiler.GetTotalUnusedReservedMemoryLong),
                monoUsedBytes = SafeProfilerValue(Profiler.GetMonoUsedSizeLong),
                monoHeapBytes = SafeProfilerValue(Profiler.GetMonoHeapSizeLong),
                gcUsedBytes = GC.GetTotalMemory(false),
                systemUsedBytes = 0L,
                graphicsDriverBytes = graphicsDriverBytes
            };
        }

        private static ProfilerRenderingStats BuildRenderingStats()
        {
            var frame = BuildFrameStats();
            return new ProfilerRenderingStats
            {
                frameTimeMs = frame.deltaTimeMs,
                fps = frame.fps,
                vSyncCount = QualitySettings.vSyncCount,
                targetFrameRate = Application.targetFrameRate,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                screenWidth = Screen.width,
                screenHeight = Screen.height,
                renderPipeline = GetRenderPipelineName()
            };
        }

        private static ProfilerScriptStats BuildScriptStats(ProfilerFrameStats frame, ProfilerMemoryStats memory, List<ProfilerUnsupportedItem> unsupported)
        {
            unsupported.Add(new ProfilerUnsupportedItem("scriptFunctionTimings", "Unity does not expose stable public script function timing data through this command; use Unity Profiler UI or Performance Tests for function-level attribution."));
            return new ProfilerScriptStats
            {
                mainThreadFrameTimeMs = frame.deltaTimeMs,
                gcAllocatedBytesDelta = 0L,
                gcCollectionCount0Delta = 0,
                monoUsedBytes = memory.monoUsedBytes,
                gcUsedBytes = memory.gcUsedBytes,
                timingSource = "Time.unscaledDeltaTime"
            };
        }

        private static void FocusProfilerWindow(List<string> warnings)
        {
            try
            {
                var type = System.Type.GetType(ProfilerWindowTypeName);
                if (type == null)
                {
                    warnings.Add("ProfilerWindow type was not found; profiler enabled without focusing the window.");
                    return;
                }

                var window = EditorWindow.GetWindow(type);
                if (window != null)
                {
                    window.Show();
                    window.Focus();
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Failed to focus Profiler window: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool TryCallProfilerDriverClear(out string error)
        {
            error = null;
            try
            {
                var type = System.Type.GetType("UnityEditorInternal.ProfilerDriver,UnityEditor");
                if (type == null)
                {
                    error = "UnityEditorInternal.ProfilerDriver type was not found.";
                    return false;
                }

                var names = new[] { "ClearAllFrames", "ClearAllProfilingData", "Clear" };
                for (var i = 0; i < names.Length; i++)
                {
                    var method = type.GetMethod(names[i], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, System.Type.EmptyTypes, null);
                    if (method == null)
                    {
                        continue;
                    }

                    method.Invoke(null, null);
                    return true;
                }

                error = "No compatible ProfilerDriver clear method was found.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static string ResolveModuleName(string module)
        {
            for (var i = 0; i < KnownModules.Length; i++)
            {
                if (string.Equals(KnownModules[i], module, StringComparison.OrdinalIgnoreCase))
                {
                    return KnownModules[i];
                }
            }

            return module.Trim();
        }

        private static string ResolveProjectPath(string path)
        {
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            return Path.GetFullPath(Path.Combine(GetProjectRoot(), path.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static string GetRenderPipelineName()
        {
            var pipeline = GetGraphicsSettingsPipeline("defaultRenderPipeline")
                ?? GetGraphicsSettingsPipeline("renderPipelineAsset")
                ?? GetGraphicsSettingsPipeline("currentRenderPipeline");
            return pipeline != null ? pipeline.GetType().FullName : "Built-in";
        }

        private static object GetGraphicsSettingsPipeline(string propertyName)
        {
            try
            {
                var property = typeof(UnityEngine.Rendering.GraphicsSettings).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                return property != null ? property.GetValue(null, null) : null;
            }
            catch
            {
                return null;
            }
        }

        private static long SafeProfilerValue(Func<long> getter)
        {
            try
            {
                return getter == null ? 0L : getter();
            }
            catch
            {
                return 0L;
            }
        }

        private static long SafeProfilerLongMethod(string methodName, List<ProfilerUnsupportedItem> unsupported, string feature)
        {
            try
            {
                var method = typeof(Profiler).GetMethod(methodName, System.Type.EmptyTypes);
                if (method == null || method.ReturnType != typeof(long))
                {
                    unsupported.Add(new ProfilerUnsupportedItem(feature, "Profiler." + methodName + " is unavailable in this Unity version."));
                    return 0L;
                }

                return (long)method.Invoke(null, null);
            }
            catch (Exception ex)
            {
                unsupported.Add(new ProfilerUnsupportedItem(feature, ex.GetType().Name + ": " + ex.Message));
                return 0L;
            }
        }

        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }
    }
}
