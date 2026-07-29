using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows10.0.17134")]

namespace AIBridgeEditorCapture
{
    internal static class Program
    {
        private const int DwmwaExtendedFrameBounds = 9;
        private const int DwmwaCloaked = 14;
        private const int WgcAttemptTimeoutMilliseconds = 3500;
        private const int WgcMaxAttempts = 2;
        private const uint GwOwner = 4;

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private sealed class WindowCandidate
        {
            public IntPtr Handle;
            public NativeRect Bounds;
            public bool HasOwner;

            public long Area
            {
                get { return (long)(Bounds.Right - Bounds.Left) * (Bounds.Bottom - Bounds.Top); }
            }
        }

        private sealed class Options
        {
            public int ProcessId;
            public string Mode;
            public string OutputPath;
            public double X;
            public double Y;
            public double Width;
            public double Height;
            public double Scale = 1d;
            public double HostX;
            public double HostY;
            public double HostWidth;
            public double HostHeight;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

        private static int Main(string[] args)
        {
            try
            {
                SetProcessDpiAwarenessContext(new IntPtr(-4));
                var options = ParseOptions(args);
                var candidates = FindWindows(options.ProcessId);
                Console.Error.WriteLine(
                    "AIBridge Editor capture: pid=" + options.ProcessId
                    + ", visibleWindows=" + candidates.Count + ".");
                if (candidates.Count == 0)
                {
                    return Fail("target_not_visible", "No visible Unity window was found for the requested process.", 4);
                }

                NativeRect captureRect;
                var host = SelectWindow(options, candidates, out captureRect);
                if (host == null)
                {
                    return Fail("target_not_visible", "The requested Editor window is not inside a visible Unity window.", 4);
                }

                Console.Error.WriteLine(
                    "AIBridge Editor capture: host=" + FormatRect(host.Bounds)
                    + ", crop=" + FormatRect(captureRect) + ".");

                Bitmap capturedBitmap;
                try
                {
                    capturedBitmap = CaptureWithWgcRetry(host.Handle);
                }
                catch (ArgumentException ex)
                {
                    // Unity 浮动 ContainerWindow 在部分 Windows 版本上被 WGC 拒绝，只允许窗口级 PrintWindow 兼容路径。
                    Console.Error.WriteLine("WGC rejected the Unity floating window; trying window-only PrintWindow: " + ex.Message);
                    capturedBitmap = PrintWindowCapture.Capture(host.Handle);
                }

                using (var bitmap = capturedBitmap)
                {
                    if (bitmap == null)
                    {
                        return Fail("capture_failed", "Windows Graphics Capture did not return a frame.", 5);
                    }

                    using (var output = Crop(bitmap, host.Bounds, captureRect))
                    {
                        if (output.Width <= 0 || output.Height <= 0)
                        {
                            return Fail("target_not_visible", "The requested Editor window has an empty capture area.", 4);
                        }

                        output.Save(options.OutputPath, ImageFormat.Png);
                        WriteJson(new Dictionary<string, object>
                        {
                            { "success", true },
                            { "width", output.Width },
                            { "height", output.Height }
                        });
                    }
                }

                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return Fail("permission_denied", "Windows denied access to the Unity window capture.", 3);
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070005u)
            {
                return Fail("permission_denied", "Windows denied access to the Unity window capture.", 3);
            }
            catch (PlatformNotSupportedException ex)
            {
                Console.Error.WriteLine(ex);
                return Fail("unsupported_platform", "Windows Graphics Capture is unavailable on this system.", 2);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return Fail("capture_failed", "Windows Graphics Capture failed.", 5);
            }
        }

        private static Bitmap CaptureWithWgcRetry(IntPtr window)
        {
            for (var attempt = 1; attempt <= WgcMaxAttempts; attempt++)
            {
                var bitmap = WgcCapture.Capture(window, WgcAttemptTimeoutMilliseconds);
                if (bitmap != null)
                {
                    return bitmap;
                }

                if (attempt < WgcMaxAttempts)
                {
                    Console.Error.WriteLine("Windows Graphics Capture returned no frame; retrying once.");
                }
            }

            return null;
        }

        private static Options ParseOptions(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Capture helper arguments must use --name value pairs.");
                }

                values[args[i].Substring(2)] = args[i + 1];
            }

            var options = new Options
            {
                ProcessId = ParseInt(Required(values, "pid"), "pid"),
                Mode = Required(values, "mode"),
                OutputPath = Required(values, "output")
            };

            if (!string.Equals(options.Mode, "editor", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.Mode, "window", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("mode must be editor or window.");
            }

            if (string.Equals(options.Mode, "window", StringComparison.OrdinalIgnoreCase))
            {
                options.X = ParseDouble(Required(values, "x"), "x");
                options.Y = ParseDouble(Required(values, "y"), "y");
                options.Width = ParseDouble(Required(values, "width"), "width");
                options.Height = ParseDouble(Required(values, "height"), "height");
                options.Scale = ParseDouble(Required(values, "scale"), "scale");
                options.HostX = ParseDouble(Required(values, "hostX"), "hostX");
                options.HostY = ParseDouble(Required(values, "hostY"), "hostY");
                options.HostWidth = ParseDouble(Required(values, "hostWidth"), "hostWidth");
                options.HostHeight = ParseDouble(Required(values, "hostHeight"), "hostHeight");
                if (options.Width <= 0d
                    || options.Height <= 0d
                    || options.Scale <= 0d
                    || options.HostWidth <= 0d
                    || options.HostHeight <= 0d)
                {
                    throw new ArgumentException("The target capture rect and scale must be positive.");
                }
            }

            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
            if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
            {
                throw new DirectoryNotFoundException("The capture output directory does not exist.");
            }

            return options;
        }

        private static string Required(Dictionary<string, string> values, string key)
        {
            string value;
            if (!values.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Missing required argument --" + key + ".");
            }

            return value;
        }

        private static int ParseInt(string value, string name)
        {
            int result;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                throw new ArgumentException("Invalid integer for --" + name + ".");
            }

            return result;
        }

        private static double ParseDouble(string value, string name)
        {
            double result;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                throw new ArgumentException("Invalid number for --" + name + ".");
            }

            return result;
        }

        private static List<WindowCandidate> FindWindows(int processId)
        {
            var result = new List<WindowCandidate>();
            EnumWindows((window, parameter) =>
            {
                uint ownerProcessId;
                GetWindowThreadProcessId(window, out ownerProcessId);
                if (ownerProcessId != (uint)processId || !IsWindowVisible(window) || IsIconic(window))
                {
                    return true;
                }

                int cloaked;
                if (DwmGetWindowAttribute(window, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                {
                    return true;
                }

                NativeRect bounds;
                if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out bounds, Marshal.SizeOf<NativeRect>()) != 0)
                {
                    if (!GetWindowRect(window, out bounds))
                    {
                        return true;
                    }
                }

                if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
                {
                    return true;
                }

                result.Add(new WindowCandidate
                {
                    Handle = window,
                    Bounds = bounds,
                    HasOwner = GetWindow(window, GwOwner) != IntPtr.Zero
                });
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static WindowCandidate SelectWindow(Options options, List<WindowCandidate> candidates, out NativeRect captureRect)
        {
            if (string.Equals(options.Mode, "editor", StringComparison.OrdinalIgnoreCase))
            {
                var processMainWindow = System.Diagnostics.Process.GetProcessById(options.ProcessId).MainWindowHandle;
                var processMainCandidate = candidates.FirstOrDefault(candidate => candidate.Handle == processMainWindow);
                if (processMainCandidate != null)
                {
                    captureRect = processMainCandidate.Bounds;
                    return processMainCandidate;
                }

                var main = candidates.Where(candidate => !candidate.HasOwner)
                    .OrderByDescending(candidate => candidate.Area)
                    .FirstOrDefault() ?? candidates.OrderByDescending(candidate => candidate.Area).First();
                captureRect = main.Bounds;
                return main;
            }

            WindowCandidate best = null;
            var bestOverlap = 0d;
            var bestArea = long.MaxValue;
            var bestTargetRect = default(NativeRect);

            for (var i = 0; i < candidates.Count; i++)
            {
                var targetRect = ConvertTargetRect(candidates[i].Handle, options);
                var overlap = CalculateOverlapRatio(targetRect, candidates[i].Bounds);
                if (overlap < 0.5d)
                {
                    continue;
                }

                if (overlap > bestOverlap || (Math.Abs(overlap - bestOverlap) < 0.0001d && candidates[i].Area < bestArea))
                {
                    best = candidates[i];
                    bestOverlap = overlap;
                    bestArea = candidates[i].Area;
                    bestTargetRect = Intersect(targetRect, candidates[i].Bounds);
                }
            }

            captureRect = bestTargetRect;
            return best;
        }

        private static NativeRect ConvertTargetRect(IntPtr window, Options options)
        {
            NativeRect hostBounds;
            if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out hostBounds, Marshal.SizeOf<NativeRect>()) != 0
                && !GetWindowRect(window, out hostBounds))
            {
                throw new InvalidOperationException("Failed to read the Unity host window bounds.");
            }

            // Unity 返回逻辑点坐标。WGC 帧比 ContainerWindow 多出的高度是原生标题/菜单区，不能拉伸进面板。
            var hostPixelWidth = hostBounds.Right - hostBounds.Left;
            var hostPixelHeight = hostBounds.Bottom - hostBounds.Top;
            var contentPixelWidth = options.HostWidth * options.Scale;
            var contentPixelHeight = options.HostHeight * options.Scale;
            var horizontalFrameInset = Math.Max(0d, (hostPixelWidth - contentPixelWidth) / 2d);
            var topFrameInset = Math.Max(0d, hostPixelHeight - contentPixelHeight);
            var contentLeft = hostBounds.Left + horizontalFrameInset;
            var contentTop = hostBounds.Top + topFrameInset;
            var left = contentLeft + (options.X - options.HostX) * options.Scale;
            var top = contentTop + (options.Y - options.HostY) * options.Scale;
            var right = contentLeft + (options.X + options.Width - options.HostX) * options.Scale;
            var bottom = contentTop + (options.Y + options.Height - options.HostY) * options.Scale;

            return new NativeRect
            {
                Left = (int)Math.Round(Math.Min(left, right)),
                Top = (int)Math.Round(Math.Min(top, bottom)),
                Right = (int)Math.Round(Math.Max(left, right)),
                Bottom = (int)Math.Round(Math.Max(top, bottom))
            };
        }

        private static double CalculateOverlapRatio(NativeRect target, NativeRect host)
        {
            var intersection = Intersect(target, host);
            var intersectionArea = (long)Math.Max(0, intersection.Right - intersection.Left)
                * Math.Max(0, intersection.Bottom - intersection.Top);
            var targetArea = (long)Math.Max(0, target.Right - target.Left)
                * Math.Max(0, target.Bottom - target.Top);
            return targetArea > 0 ? intersectionArea / (double)targetArea : 0d;
        }

        private static NativeRect Intersect(NativeRect left, NativeRect right)
        {
            return new NativeRect
            {
                Left = Math.Max(left.Left, right.Left),
                Top = Math.Max(left.Top, right.Top),
                Right = Math.Min(left.Right, right.Right),
                Bottom = Math.Min(left.Bottom, right.Bottom)
            };
        }

        private static Bitmap Crop(Bitmap source, NativeRect hostBounds, NativeRect captureBounds)
        {
            var hostWidth = hostBounds.Right - hostBounds.Left;
            var hostHeight = hostBounds.Bottom - hostBounds.Top;
            if (hostWidth <= 0 || hostHeight <= 0)
            {
                throw new InvalidOperationException("The host window bounds are invalid.");
            }

            if (captureBounds.Left == hostBounds.Left
                && captureBounds.Top == hostBounds.Top
                && captureBounds.Right == hostBounds.Right
                && captureBounds.Bottom == hostBounds.Bottom)
            {
                return source.Clone(new Rectangle(0, 0, source.Width, source.Height), PixelFormat.Format32bppArgb);
            }

            var scaleX = source.Width / (double)hostWidth;
            var scaleY = source.Height / (double)hostHeight;
            var x = Math.Max(0, (int)Math.Round((captureBounds.Left - hostBounds.Left) * scaleX));
            var y = Math.Max(0, (int)Math.Round((captureBounds.Top - hostBounds.Top) * scaleY));
            var right = Math.Min(source.Width, (int)Math.Round((captureBounds.Right - hostBounds.Left) * scaleX));
            var bottom = Math.Min(source.Height, (int)Math.Round((captureBounds.Bottom - hostBounds.Top) * scaleY));
            var width = right - x;
            var height = bottom - y;
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("The requested crop is outside the captured Unity window.");
            }

            return source.Clone(new Rectangle(x, y, width, height), PixelFormat.Format32bppArgb);
        }

        private static int Fail(string code, string message, int exitCode)
        {
            WriteJson(new Dictionary<string, object>
            {
                { "success", false },
                { "code", code },
                { "message", message }
            });
            return exitCode;
        }

        private static string FormatRect(NativeRect rect)
        {
            return rect.Left + "," + rect.Top + " "
                + (rect.Right - rect.Left) + "x" + (rect.Bottom - rect.Top);
        }

        private static void WriteJson(Dictionary<string, object> values)
        {
            var parts = values.Select(pair => "\"" + Escape(pair.Key) + "\":" + JsonValue(pair.Value));
            Console.Out.WriteLine("{" + string.Join(",", parts) + "}");
        }

        private static string JsonValue(object value)
        {
            if (value is bool boolValue)
            {
                return boolValue ? "true" : "false";
            }

            if (value is int intValue)
            {
                return intValue.ToString(CultureInfo.InvariantCulture);
            }

            return "\"" + Escape(Convert.ToString(value, CultureInfo.InvariantCulture)) + "\"";
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
