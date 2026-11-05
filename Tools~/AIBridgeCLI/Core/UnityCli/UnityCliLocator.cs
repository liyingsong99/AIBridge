using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AIBridgeCLI.Core.UnityCli
{
    /// <summary>只定位独立的 unity CLI；不搜索或启动 Unity Editor。</summary>
    public static class UnityCliLocator
    {
        public static UnityCliDiagnostic Locate(UnityCliSettings settings)
        {
            var result = new UnityCliDiagnostic();
            if (!string.IsNullOrWhiteSpace(settings.ExecutablePath))
            {
                result.source = "explicit";
                result.executablePath = ResolveExecutable(settings.ProjectRoot, settings.ExecutablePath);
                return Validate(result);
            }
            result.source = "PATH";
            var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "unity.exe" : "unity";
            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                var directory = entry.Trim().Trim('"');
                if (!Path.IsPathRooted(directory)) continue;
                var candidate = Path.Combine(directory, name);
                if (!File.Exists(candidate) || IsEditor(candidate)) continue;
                result.executablePath = Path.GetFullPath(candidate);
                return Validate(result);
            }
            result.errorCode = "CLI_NOT_FOUND";
            result.error = "Unity CLI not found. Set --unity-cli-path or AIBRIDGE_UNITY_CLI_PATH.";
            return result;
        }

        private static string ResolveExecutable(string root, string path)
        {
            if (path.IndexOfAny(new[] { '\r', '\n', '\0', '"' }) >= 0)
                throw new ArgumentException("Unity CLI path must be an executable path, without quotes or arguments.");
            return UnityCliSettings.ResolvePath(root, path);
        }

        private static UnityCliDiagnostic Validate(UnityCliDiagnostic result)
        {
            if (!File.Exists(result.executablePath))
            {
                result.errorCode = "CLI_NOT_FOUND";
                result.error = "Unity CLI executable does not exist: " + result.executablePath;
            }
            else if (IsEditor(result.executablePath))
            {
                result.errorCode = "EDITOR_EXECUTABLE_REJECTED";
                result.error = "The path points to Unity Editor, not the standalone Unity CLI.";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && !string.Equals(Path.GetExtension(result.executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                result.errorCode = "UNSUPPORTED_EXECUTABLE";
                result.error = "Windows Unity CLI must be a native .exe; shell scripts are not executed.";
            }
            else result.available = true;
            return result;
        }

        private static bool IsEditor(string path)
        {
            // PATH 可能包含 Editor/Unity.exe；目录特征和相邻 Data 必须在运行探测前排除。
            var directory = Path.GetDirectoryName(path);
            return string.Equals(Path.GetFileName(directory), "Editor", StringComparison.OrdinalIgnoreCase)
                || directory != null && Directory.Exists(Path.Combine(directory, "Data"))
                || path.Replace('\\', '/').IndexOf("/Unity.app/Contents/MacOS/", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
