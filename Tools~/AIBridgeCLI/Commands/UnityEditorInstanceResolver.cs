using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AIBridge.Runtime.Internal;
using AIBridgeCLI.Core;
using Newtonsoft.Json;

namespace AIBridgeCLI.Commands
{
    internal static class UnityEditorInstanceResolver
    {
        public static bool TryResolve(out Process process, out string error)
        {
            process = null;
            error = null;

            var exchangeDirectory = PathHelper.GetExchangeDirectory();
            var expectedProjectRoot = Path.GetDirectoryName(exchangeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var projectNameHints = new List<string>();
            AddProjectNameHint(projectNameHints, expectedProjectRoot);

            var metadataPath = ResolveMetadataPath(expectedProjectRoot);
            if (string.IsNullOrEmpty(metadataPath))
            {
                return TryFallbackResolve(
                    projectNameHints,
                    "Unity Editor metadata for the current project was not found. Make sure this project's Unity Editor is open and AIBridge is active.",
                    out process,
                    out error);
            }

            EditorInstanceMetadata metadata;
            try
            {
                var json = File.ReadAllText(metadataPath);
                metadata = JsonConvert.DeserializeObject<EditorInstanceMetadata>(json);
            }
            catch (Exception ex)
            {
                return TryFallbackResolve(
                    projectNameHints,
                    $"Failed to read Unity Editor metadata: {ex.Message}",
                    out process,
                    out error);
            }

            if (metadata == null)
            {
                return TryFallbackResolve(
                    projectNameHints,
                    "Unity Editor metadata is empty or invalid.",
                    out process,
                    out error);
            }

            AddProjectNameHint(projectNameHints, metadata.projectName);
            AddProjectNameHint(projectNameHints, metadata.projectRoot);
            if (!PathsEqual(metadata.projectRoot, expectedProjectRoot))
            {
                return TryFallbackResolve(
                    projectNameHints,
                    "Unity Editor metadata does not match the current project root.",
                    out process,
                    out error);
            }

            if (metadata.processId <= 0)
            {
                return TryFallbackResolve(
                    projectNameHints,
                    "Unity Editor metadata does not contain a valid process ID.",
                    out process,
                    out error);
            }

            try
            {
                var candidate = Process.GetProcessById(metadata.processId);
                candidate.Refresh();

                if (!candidate.ProcessName.Equals("Unity", StringComparison.OrdinalIgnoreCase))
                {
                    return TryFallbackResolve(
                        projectNameHints,
                        $"Resolved process {metadata.processId} is '{candidate.ProcessName}', not Unity.",
                        out process,
                        out error);
                }

                if (candidate.MainWindowHandle == IntPtr.Zero)
                {
                    return TryFallbackResolve(
                        projectNameHints,
                        "The Unity Editor for the current project is running, but its main window is not ready yet.",
                        out process,
                        out error);
                }

                process = candidate;
                return true;
            }
            catch (ArgumentException)
            {
                return TryFallbackResolve(
                    projectNameHints,
                    $"Unity Editor process {metadata.processId} for the current project is no longer running.",
                    out process,
                    out error);
            }
            catch (Exception ex)
            {
                return TryFallbackResolve(
                    projectNameHints,
                    $"Failed to inspect Unity Editor process {metadata.processId}: {ex.Message}",
                    out process,
                    out error);
            }
        }

        private static string ResolveMetadataPath(string projectRoot)
        {
            var candidates = AIBridgeEditorInstancePaths.GetMetadataCandidatePaths(projectRoot);
            for (var i = 0; i < candidates.Count; i++)
            {
                var path = candidates[i];
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private static bool TryFallbackResolve(List<string> projectNameHints, string primaryError, out Process process, out string error)
        {
            if (TryResolveByWindowTitle(projectNameHints, out process, out var fallbackError))
            {
                error = null;
                return true;
            }

            error = CombineResolveErrors(primaryError, fallbackError);
            return false;
        }

        private static bool TryResolveByWindowTitle(List<string> projectNameHints, out Process process, out string error)
        {
            process = null;
            error = null;

            var candidates = new List<Process>();
            Process[] unityProcesses;
            try
            {
                unityProcesses = Process.GetProcessesByName("Unity");
            }
            catch (Exception ex)
            {
                error = $"Failed to enumerate Unity processes: {ex.Message}";
                return false;
            }

            foreach (var candidate in unityProcesses)
            {
                if (!TryPrepareUnityProcess(candidate, out var title))
                {
                    continue;
                }

                if (TitleMatchesProject(title, projectNameHints))
                {
                    process = candidate;
                    return true;
                }

                candidates.Add(candidate);
            }

            if (candidates.Count == 1)
            {
                // 元数据损坏时，单 Unity 进程场景下允许兜底；多实例时必须匹配项目名，避免误连其它项目。
                process = candidates[0];
                return true;
            }

            error = candidates.Count == 0
                ? "No running Unity Editor process with a ready main window was found."
                : "Unity Editor metadata could not be used, and multiple Unity instances are open without a unique project-title match.";
            return false;
        }

        private static bool TryPrepareUnityProcess(Process candidate, out string title)
        {
            title = null;
            try
            {
                candidate.Refresh();
                if (!candidate.ProcessName.Equals("Unity", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (candidate.MainWindowHandle == IntPtr.Zero)
                {
                    return false;
                }

                title = candidate.MainWindowTitle;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TitleMatchesProject(string title, List<string> projectNameHints)
        {
            if (string.IsNullOrWhiteSpace(title) || projectNameHints == null)
            {
                return false;
            }

            foreach (var hint in projectNameHints)
            {
                if (string.IsNullOrWhiteSpace(hint))
                {
                    continue;
                }

                if (title.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddProjectNameHint(List<string> hints, string pathOrName)
        {
            if (hints == null || string.IsNullOrWhiteSpace(pathOrName))
            {
                return;
            }

            var name = pathOrName;
            try
            {
                var normalizedPath = NormalizePath(pathOrName);
                if (!string.IsNullOrWhiteSpace(normalizedPath))
                {
                    name = Path.GetFileName(normalizedPath);
                }
            }
            catch
            {
                name = pathOrName;
            }

            if (string.IsNullOrWhiteSpace(name) ||
                string.Equals(name, "null", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var existing in hints)
            {
                if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            hints.Add(name);
        }

        private static string CombineResolveErrors(string primaryError, string fallbackError)
        {
            if (string.IsNullOrWhiteSpace(fallbackError))
            {
                return primaryError;
            }

            return primaryError + " Fallback process scan also failed: " + fallbackError;
        }

        private static bool PathsEqual(string left, string right)
        {
            var normalizedLeft = NormalizePath(left);
            var normalizedRight = NormalizePath(right);

            if (string.IsNullOrEmpty(normalizedLeft) || string.IsNullOrEmpty(normalizedRight))
            {
                return false;
            }

            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private class EditorInstanceMetadata
        {
            public int schemaVersion { get; set; }
            public int processId { get; set; }
            public string projectRoot { get; set; }
            public string projectName { get; set; }
            public string windowTitle { get; set; }
            public string lastUpdatedUtc { get; set; }
        }
    }
}
