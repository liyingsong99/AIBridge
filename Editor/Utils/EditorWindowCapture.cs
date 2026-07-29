using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AIBridge.Internal.Json;
using UnityEditor;
using UnityEngine;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace AIBridge.Editor
{
    internal static class EditorWindowCaptureErrorCodes
    {
        public const string TargetNotFound = "target_not_found";
        public const string TargetAmbiguous = "target_ambiguous";
        public const string TargetNotVisible = "target_not_visible";
        public const string PermissionDenied = "permission_denied";
        public const string CaptureFailed = "capture_failed";
        public const string UnsupportedPlatform = "unsupported_platform";
    }

    internal sealed class EditorWindowCaptureTarget
    {
        public bool CaptureMainEditor;
        public EditorWindow Window;
        public Rect ScreenRect;
        public Rect HostScreenRect;
    }

    internal sealed class EditorWindowCaptureResolution
    {
        public bool Success;
        public EditorWindowCaptureTarget Target;
        public string ErrorCode;
        public string ErrorMessage;

        public static EditorWindowCaptureResolution Succeeded(EditorWindowCaptureTarget target)
        {
            return new EditorWindowCaptureResolution
            {
                Success = true,
                Target = target
            };
        }

        public static EditorWindowCaptureResolution Failed(string code, string message)
        {
            return new EditorWindowCaptureResolution
            {
                Success = false,
                ErrorCode = code,
                ErrorMessage = message
            };
        }
    }

    [InitializeOnLoad]
    internal static class EditorWindowFocusTracker
    {
        private static EditorWindow _lastFocusedWindow;

        static EditorWindowFocusTracker()
        {
            EditorApplication.update += TrackFocusedWindow;
        }

        internal static EditorWindow LastFocusedWindow
        {
            get { return _lastFocusedWindow; }
            set { _lastFocusedWindow = value; }
        }

        private static void TrackFocusedWindow()
        {
            var focusedWindow = EditorWindow.focusedWindow;
            if (focusedWindow != null)
            {
                _lastFocusedWindow = focusedWindow;
            }
        }
    }

    internal static class EditorWindowCaptureTargetResolver
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static EditorWindowCaptureResolution Resolve(CommandRequest request)
        {
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var activeWindow = EditorWindow.focusedWindow != null
                ? EditorWindow.focusedWindow
                : EditorWindowFocusTracker.LastFocusedWindow;
            return Resolve(request, windows, activeWindow, resolveScreenRect: true);
        }

        internal static EditorWindowCaptureResolution Resolve(
            CommandRequest request,
            IList<EditorWindow> windows,
            EditorWindow activeWindow,
            bool resolveScreenRect)
        {
            var targetName = request.GetParam("target", string.Empty);
            var windowType = request.GetParam("windowType", string.Empty);
            var title = request.GetParam("title", string.Empty);
            var hasInstanceId = request.HasParam("instanceId");
            var instanceId = request.GetParam("instanceId", 0);
            var hasExplicitSelector = !string.IsNullOrWhiteSpace(windowType)
                || !string.IsNullOrWhiteSpace(title)
                || hasInstanceId;

            if (!string.IsNullOrWhiteSpace(targetName) && hasExplicitSelector)
            {
                return EditorWindowCaptureResolution.Failed(
                    EditorWindowCaptureErrorCodes.CaptureFailed,
                    "--target cannot be combined with --windowType, --title, or --instanceId.");
            }

            if (!string.IsNullOrWhiteSpace(targetName))
            {
                if (string.Equals(targetName, "editor", StringComparison.OrdinalIgnoreCase))
                {
                    return EditorWindowCaptureResolution.Succeeded(new EditorWindowCaptureTarget
                    {
                        CaptureMainEditor = true
                    });
                }

                if (!string.Equals(targetName, "active", StringComparison.OrdinalIgnoreCase))
                {
                    return EditorWindowCaptureResolution.Failed(
                        EditorWindowCaptureErrorCodes.CaptureFailed,
                        "--target must be 'editor' or 'active'.");
                }

                if (activeWindow == null)
                {
                    return EditorWindowCaptureResolution.Failed(
                        EditorWindowCaptureErrorCodes.TargetNotFound,
                        "No active Unity Editor window was found.");
                }

                return BuildWindowTarget(activeWindow, resolveScreenRect);
            }

            if (!hasExplicitSelector)
            {
                return EditorWindowCaptureResolution.Failed(
                    EditorWindowCaptureErrorCodes.CaptureFailed,
                    "Specify --target, --windowType, --title, or --instanceId.");
            }

            var candidates = new List<EditorWindow>();
            if (windows != null)
            {
                for (var i = 0; i < windows.Count; i++)
                {
                    if (windows[i] != null)
                    {
                        candidates.Add(windows[i]);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(windowType))
            {
                var fullNameMatches = FilterByType(candidates, windowType, useFullName: true);
                candidates = fullNameMatches.Count > 0
                    ? fullNameMatches
                    : FilterByType(candidates, windowType, useFullName: false);
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                candidates.RemoveAll(window => !string.Equals(
                    GetWindowTitle(window),
                    title,
                    StringComparison.OrdinalIgnoreCase));
            }

            if (hasInstanceId)
            {
                candidates.RemoveAll(window => window.GetInstanceID() != instanceId);
            }

            if (candidates.Count == 0)
            {
                return EditorWindowCaptureResolution.Failed(
                    EditorWindowCaptureErrorCodes.TargetNotFound,
                    "Editor window was not found.");
            }

            if (candidates.Count > 1)
            {
                return EditorWindowCaptureResolution.Failed(
                    EditorWindowCaptureErrorCodes.TargetAmbiguous,
                    "Multiple Editor windows matched. Use --instanceId to select one instance.");
            }

            return BuildWindowTarget(candidates[0], resolveScreenRect);
        }

        private static List<EditorWindow> FilterByType(List<EditorWindow> windows, string expected, bool useFullName)
        {
            var result = new List<EditorWindow>();
            for (var i = 0; i < windows.Count; i++)
            {
                var type = windows[i].GetType();
                var actual = useFullName ? type.FullName : type.Name;
                if (string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    result.Add(windows[i]);
                }
            }

            return result;
        }

        private static string GetWindowTitle(EditorWindow window)
        {
            return window.titleContent != null ? window.titleContent.text ?? string.Empty : string.Empty;
        }

        private static EditorWindowCaptureResolution BuildWindowTarget(EditorWindow window, bool resolveScreenRect)
        {
            if (!resolveScreenRect)
            {
                return EditorWindowCaptureResolution.Succeeded(new EditorWindowCaptureTarget
                {
                    Window = window
                });
            }

            Rect screenRect;
            Rect hostScreenRect;
            if (!TryGetHostScreenRects(window, out screenRect, out hostScreenRect))
            {
                return EditorWindowCaptureResolution.Failed(
                    EditorWindowCaptureErrorCodes.TargetNotVisible,
                    "The Editor window host area is unavailable or not visible.");
            }

            return EditorWindowCaptureResolution.Succeeded(new EditorWindowCaptureTarget
            {
                Window = window,
                ScreenRect = screenRect,
                HostScreenRect = hostScreenRect
            });
        }

        private static bool TryGetHostScreenRects(EditorWindow window, out Rect screenRect, out Rect hostScreenRect)
        {
            screenRect = default;
            hostScreenRect = default;
            if (window == null)
            {
                return false;
            }

            try
            {
                // HostView/DockArea 的 screenPosition 包含停靠标签区域，并统一隔离 Unity 内部 API 差异。
                var parentField = FindField(window.GetType(), "m_Parent");
                var hostView = parentField != null ? parentField.GetValue(window) : null;
                if (hostView == null)
                {
                    return false;
                }

                var screenPositionProperty = FindProperty(hostView.GetType(), "screenPosition");
                if (screenPositionProperty == null)
                {
                    return false;
                }

                var value = screenPositionProperty.GetValue(hostView, null);
                if (!(value is Rect rect) || rect.width <= 0f || rect.height <= 0f)
                {
                    return false;
                }

                var windowProperty = FindProperty(hostView.GetType(), "window");
                var containerWindow = windowProperty != null ? windowProperty.GetValue(hostView, null) : null;
                if (containerWindow == null)
                {
                    return false;
                }

                var positionProperty = FindProperty(containerWindow.GetType(), "position");
                var positionValue = positionProperty != null ? positionProperty.GetValue(containerWindow, null) : null;
                if (!(positionValue is Rect containerRect)
                    || containerRect.width <= 0f
                    || containerRect.height <= 0f)
                {
                    return false;
                }

                screenRect = rect;
                hostScreenRect = containerRect;
                return true;
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("Failed to resolve Editor window host rect: " + ex.Message);
                return false;
            }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            while (type != null && type != typeof(object))
            {
                var field = type.GetField(name, InstanceFlags);
                if (field != null)
                {
                    return field;
                }

                type = type.BaseType;
            }

            return null;
        }

        private static PropertyInfo FindProperty(Type type, string name)
        {
            while (type != null && type != typeof(object))
            {
                var property = type.GetProperty(name, InstanceFlags);
                if (property != null)
                {
                    return property;
                }

                type = type.BaseType;
            }

            return null;
        }
    }

    internal sealed class EditorWindowCaptureResult
    {
        public bool Success;
        public string RelativePath;
        public string AbsolutePath;
        public int Width;
        public int Height;
        public string ErrorCode;
        public string ErrorMessage;
        public string DiagnosticLog;
    }

    internal static class EditorWindowCaptureCoordinator
    {
        private const double RepaintTimeoutSeconds = 5d;
        private const int RequiredUpdateCount = 2;

        private static PendingCapture _pending;

        private sealed class PendingCapture
        {
            public string RequestId;
            public EditorWindowCaptureTarget Target;
            public EditorWindow PreviousWindow;
            public Action<CommandResult> WriteResult;
            public double StartedAt;
            public int UpdateCount;
            public Task<EditorWindowCaptureResult> CaptureTask;
        }

        public static CommandResult Begin(CommandRequest request, Action<CommandResult> writeResult)
        {
            if (Application.isBatchMode)
            {
                return CreateFailure(
                    request.id,
                    EditorWindowCaptureErrorCodes.TargetNotVisible,
                    "Editor window capture is unavailable in batch mode.");
            }

            if (_pending != null)
            {
                return CreateFailure(
                    request.id,
                    EditorWindowCaptureErrorCodes.CaptureFailed,
                    "Another Editor window capture is already in progress.");
            }

            var resolution = EditorWindowCaptureTargetResolver.Resolve(request);
            if (!resolution.Success)
            {
                return CreateFailure(request.id, resolution.ErrorCode, resolution.ErrorMessage);
            }

            var previousWindow = EditorWindow.focusedWindow != null
                ? EditorWindow.focusedWindow
                : EditorWindowFocusTracker.LastFocusedWindow;

            _pending = new PendingCapture
            {
                RequestId = request.id,
                Target = resolution.Target,
                PreviousWindow = previousWindow,
                WriteResult = writeResult,
                StartedAt = EditorApplication.timeSinceStartup
            };

            if (!resolution.Target.CaptureMainEditor && resolution.Target.Window != null)
            {
                resolution.Target.Window.Focus();
                resolution.Target.Window.Repaint();
            }

            EditorApplication.update += ContinueCapture;
            return null;
        }

        private static void ContinueCapture()
        {
            var pending = _pending;
            if (pending == null)
            {
                EditorApplication.update -= ContinueCapture;
                return;
            }

            if (pending.CaptureTask == null
                && EditorApplication.timeSinceStartup - pending.StartedAt > RepaintTimeoutSeconds)
            {
                Complete(CreateFailure(
                    pending.RequestId,
                    EditorWindowCaptureErrorCodes.CaptureFailed,
                    "Timed out while waiting for the Editor window to repaint."));
                return;
            }

            if (pending.CaptureTask == null)
            {
                pending.UpdateCount++;
                if (!pending.Target.CaptureMainEditor && pending.Target.Window != null)
                {
                    pending.Target.Window.Repaint();
                }

                if (pending.UpdateCount < RequiredUpdateCount)
                {
                    return;
                }

                pending.CaptureTask = EditorWindowCaptureBackend.CaptureAsync(pending.Target);
                return;
            }

            if (!pending.CaptureTask.IsCompleted)
            {
                return;
            }

            try
            {
                var capture = pending.CaptureTask.GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(capture.DiagnosticLog))
                {
                    if (capture.Success)
                    {
                        AIBridgeLogger.LogDebug("Editor capture helper: " + capture.DiagnosticLog);
                    }
                    else
                    {
                        AIBridgeLogger.LogWarning("Editor capture helper: " + capture.DiagnosticLog);
                    }
                }

                if (!capture.Success)
                {
                    Complete(CreateFailure(pending.RequestId, capture.ErrorCode, capture.ErrorMessage));
                    return;
                }

                AIBridgeLogger.LogInfo("Editor window screenshot saved: " + capture.AbsolutePath);
                Complete(CommandResult.Success(pending.RequestId, new
                {
                    path = capture.RelativePath,
                    width = capture.Width,
                    height = capture.Height
                }));
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError("Editor window capture failed: " + ex);
                Complete(CreateFailure(
                    pending.RequestId,
                    EditorWindowCaptureErrorCodes.CaptureFailed,
                    "Editor window capture failed. See the Unity Editor log for details."));
            }
        }

        private static void Complete(CommandResult result)
        {
            EditorApplication.update -= ContinueCapture;
            var pending = _pending;
            _pending = null;

            try
            {
                if (pending != null && pending.PreviousWindow != null && pending.PreviousWindow != pending.Target.Window)
                {
                    pending.PreviousWindow.Focus();
                    pending.PreviousWindow.Repaint();
                }
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("Failed to restore the previous Editor window focus: " + ex.Message);
            }

            if (pending != null && pending.WriteResult != null)
            {
                pending.WriteResult(result);
            }
        }

        internal static CommandResult CreateFailure(string requestId, string code, string message)
        {
            return CommandResult.Failure(requestId, code + ": " + message);
        }
    }

    internal static class EditorWindowCaptureBackend
    {
        private const string PackageName = "cn.lys.aibridge";
        private const string HelperDirectoryName = "EditorCapture";
        private const string WindowsHelperName = "AIBridgeEditorCapture.exe";
        private const string MacHelperName = "AIBridgeEditorCapture";
        private const int HelperTimeoutMilliseconds = 10000;

        private sealed class PreparedCapture
        {
            public string HelperPath;
            public string Arguments;
            public string FileName;
            public string FinalPath;
            public string TempPath;
        }

        public static Task<EditorWindowCaptureResult> CaptureAsync(EditorWindowCaptureTarget target)
        {
            string helperPath;
            var platformResult = TryGetHelperPath(out helperPath);
            if (platformResult != null)
            {
                return Task.FromResult(platformResult);
            }

            if (!File.Exists(helperPath))
            {
                return Task.FromResult(Failed(
                    EditorWindowCaptureErrorCodes.CaptureFailed,
                    "The Editor window capture helper is missing. Reinstall or rebuild AIBridge for this platform."));
            }

            ScreenshotHelper.EnsureScreenshotsDirectory();
            var timestamp = DateTime.Now;
            var fileName = "editor_window_" + timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png";
            var finalPath = Path.Combine(ScreenshotHelper.ScreenshotsDir, fileName);
            var tempPath = finalPath + ".tmp." + Guid.NewGuid().ToString("N");
            var prepared = new PreparedCapture
            {
                HelperPath = helperPath,
                Arguments = BuildArguments(target, tempPath),
                FileName = fileName,
                FinalPath = finalPath,
                TempPath = tempPath
            };
            return Task.Run(() => CapturePrepared(prepared));
        }

        private static EditorWindowCaptureResult CapturePrepared(PreparedCapture prepared)
        {
            var diagnosticLog = string.Empty;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = prepared.HelperPath,
                    Arguments = prepared.Arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return Failed(EditorWindowCaptureErrorCodes.CaptureFailed, "Failed to start the capture helper.");
                    }

                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(HelperTimeoutMilliseconds))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // Ignore cleanup errors after timeout.
                        }

                        return Failed(EditorWindowCaptureErrorCodes.CaptureFailed, "The capture helper timed out.");
                    }

                    Task.WaitAll(stdoutTask, stderrTask);
                    var stdout = stdoutTask.Result.Trim();
                    var stderr = stderrTask.Result.Trim();

                    if (process.ExitCode != 0)
                    {
                        var failure = ParseHelperFailure(stdout);
                        if (!string.IsNullOrEmpty(stderr))
                        {
                            failure.DiagnosticLog = stderr;
                        }
                        return failure;
                    }

                    if (!string.IsNullOrEmpty(stderr))
                    {
                        // 成功路径也保留 helper 诊断，但只写本地 Editor 日志，不进入命令结果。
                        diagnosticLog = stderr;
                    }
                }

                int width;
                int height;
                if (!TryReadPngDimensions(prepared.TempPath, out width, out height))
                {
                    return Failed(EditorWindowCaptureErrorCodes.CaptureFailed, "The capture helper did not produce a valid PNG.");
                }

                File.Move(prepared.TempPath, prepared.FinalPath);
                return new EditorWindowCaptureResult
                {
                    Success = true,
                    RelativePath = ".aibridge/screenshots/" + prepared.FileName,
                    AbsolutePath = prepared.FinalPath,
                    Width = width,
                    Height = height,
                    DiagnosticLog = diagnosticLog
                };
            }
            catch (UnauthorizedAccessException ex)
            {
                var failure = Failed(EditorWindowCaptureErrorCodes.PermissionDenied, "Window capture permission was denied.");
                failure.DiagnosticLog = CombineDiagnostics(diagnosticLog, ex.ToString());
                return failure;
            }
            catch (Exception ex)
            {
                var failure = Failed(
                    EditorWindowCaptureErrorCodes.CaptureFailed,
                    "The platform capture backend failed. See the Unity Editor log for details.");
                failure.DiagnosticLog = CombineDiagnostics(diagnosticLog, ex.ToString());
                return failure;
            }
            finally
            {
                try
                {
                    if (File.Exists(prepared.TempPath))
                    {
                        File.Delete(prepared.TempPath);
                    }
                }
                catch
                {
                    // Temp cleanup is best effort.
                }
            }
        }

        private static string BuildArguments(EditorWindowCaptureTarget target, string outputPath)
        {
            var builder = new System.Text.StringBuilder();
            var processId = Process.GetCurrentProcess().Id;
            AIBridgeLogger.LogInfo(
                "Starting Editor capture helper for Unity PID " + processId
                + ", mode=" + (target.CaptureMainEditor ? "editor" : "window") + ".");
            AppendArgument(builder, "--pid", processId.ToString(CultureInfo.InvariantCulture));
            AppendArgument(builder, "--mode", target.CaptureMainEditor ? "editor" : "window");
            AppendArgument(builder, "--output", outputPath);

            if (!target.CaptureMainEditor)
            {
                AIBridgeLogger.LogInfo(
                    "Editor capture logical rect: "
                    + target.ScreenRect.x.ToString("R", CultureInfo.InvariantCulture) + ","
                    + target.ScreenRect.y.ToString("R", CultureInfo.InvariantCulture) + " "
                    + target.ScreenRect.width.ToString("R", CultureInfo.InvariantCulture) + "x"
                    + target.ScreenRect.height.ToString("R", CultureInfo.InvariantCulture)
                    + ", host="
                    + target.HostScreenRect.x.ToString("R", CultureInfo.InvariantCulture) + ","
                    + target.HostScreenRect.y.ToString("R", CultureInfo.InvariantCulture) + " "
                    + target.HostScreenRect.width.ToString("R", CultureInfo.InvariantCulture) + "x"
                    + target.HostScreenRect.height.ToString("R", CultureInfo.InvariantCulture)
                    + ", pixelsPerPoint="
                    + EditorGUIUtility.pixelsPerPoint.ToString("R", CultureInfo.InvariantCulture) + ".");
                AppendArgument(builder, "--x", target.ScreenRect.x.ToString("R", CultureInfo.InvariantCulture));
                AppendArgument(builder, "--y", target.ScreenRect.y.ToString("R", CultureInfo.InvariantCulture));
                AppendArgument(builder, "--width", target.ScreenRect.width.ToString("R", CultureInfo.InvariantCulture));
                AppendArgument(builder, "--height", target.ScreenRect.height.ToString("R", CultureInfo.InvariantCulture));
                AppendArgument(builder, "--hostX", target.HostScreenRect.x.ToString("R", CultureInfo.InvariantCulture));
                AppendArgument(builder, "--hostY", target.HostScreenRect.y.ToString("R", CultureInfo.InvariantCulture));
                AppendArgument(builder, "--hostWidth", target.HostScreenRect.width.ToString("R", CultureInfo.InvariantCulture));
                AppendArgument(builder, "--hostHeight", target.HostScreenRect.height.ToString("R", CultureInfo.InvariantCulture));
                AppendArgument(
                    builder,
                    "--scale",
                    EditorGUIUtility.pixelsPerPoint.ToString("R", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static void AppendArgument(System.Text.StringBuilder builder, string name, string value)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(name);
            builder.Append(' ');
            builder.Append('"');
            builder.Append(value.Replace("\\", "\\\\").Replace("\"", "\\\""));
            builder.Append('"');
        }

        private static EditorWindowCaptureResult TryGetHelperPath(out string helperPath)
        {
            helperPath = null;
            string rid;
            string executableName;

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                rid = "win-x64";
                executableName = WindowsHelperName;
            }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            {
                rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
                executableName = MacHelperName;
            }
            else
            {
                return Failed(
                    EditorWindowCaptureErrorCodes.UnsupportedPlatform,
                    "Editor window capture is supported only on Windows and macOS.");
            }

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var cachedPath = Path.Combine(projectRoot, ".aibridge", "cli", HelperDirectoryName, executableName);
            if (File.Exists(cachedPath))
            {
                helperPath = cachedPath;
                return null;
            }

            var directPath = Path.Combine(
                projectRoot,
                "Packages",
                PackageName,
                "Tools~",
                "CLI",
                rid,
                HelperDirectoryName,
                executableName);
            if (File.Exists(directPath))
            {
                helperPath = directPath;
                return null;
            }

            var packageInfo = PackageManagerPackageInfo.FindForAssetPath("Packages/" + PackageName);
            if (packageInfo != null)
            {
                helperPath = Path.Combine(
                    packageInfo.resolvedPath,
                    "Tools~",
                    "CLI",
                    rid,
                    HelperDirectoryName,
                    executableName);
            }
            else
            {
                helperPath = directPath;
            }

            return null;
        }

        private static EditorWindowCaptureResult ParseHelperFailure(string json)
        {
            try
            {
                var data = AIBridgeJson.DeserializeObject(json);
                if (data != null)
                {
                    var code = data.TryGetValue("code", out var codeValue) ? codeValue as string : null;
                    var message = data.TryGetValue("message", out var messageValue) ? messageValue as string : null;
                    if (IsAllowedErrorCode(code) && !string.IsNullOrWhiteSpace(message))
                    {
                        return Failed(code, message);
                    }
                }
            }
            catch (Exception ex)
            {
                return new EditorWindowCaptureResult
                {
                    Success = false,
                    ErrorCode = EditorWindowCaptureErrorCodes.CaptureFailed,
                    ErrorMessage = "The platform capture helper returned an invalid response.",
                    DiagnosticLog = ex.ToString()
                };
            }

            return Failed(EditorWindowCaptureErrorCodes.CaptureFailed, "The platform capture helper failed.");
        }

        private static bool IsAllowedErrorCode(string code)
        {
            return string.Equals(code, EditorWindowCaptureErrorCodes.TargetNotVisible, StringComparison.Ordinal)
                || string.Equals(code, EditorWindowCaptureErrorCodes.PermissionDenied, StringComparison.Ordinal)
                || string.Equals(code, EditorWindowCaptureErrorCodes.CaptureFailed, StringComparison.Ordinal)
                || string.Equals(code, EditorWindowCaptureErrorCodes.UnsupportedPlatform, StringComparison.Ordinal);
        }

        private static bool TryReadPngDimensions(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (!File.Exists(path))
            {
                return false;
            }

            var header = new byte[24];
            using (var stream = File.OpenRead(path))
            {
                if (stream.Read(header, 0, header.Length) != header.Length)
                {
                    return false;
                }
            }

            var isPng = header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4e && header[3] == 0x47;
            if (!isPng)
            {
                return false;
            }

            width = ReadBigEndianInt32(header, 16);
            height = ReadBigEndianInt32(header, 20);
            return width > 0 && height > 0;
        }

        private static int ReadBigEndianInt32(byte[] data, int offset)
        {
            return (data[offset] << 24)
                | (data[offset + 1] << 16)
                | (data[offset + 2] << 8)
                | data[offset + 3];
        }

        private static EditorWindowCaptureResult Failed(string code, string message)
        {
            return new EditorWindowCaptureResult
            {
                Success = false,
                ErrorCode = code,
                ErrorMessage = message
            };
        }

        private static string CombineDiagnostics(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first))
            {
                return second;
            }
            if (string.IsNullOrWhiteSpace(second))
            {
                return first;
            }
            return first + Environment.NewLine + second;
        }
    }
}
