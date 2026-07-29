using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace AIBridgeEditorCapture
{
    internal static class PrintWindowCapture
    {
        private const uint PwRenderFullContent = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr value);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr value);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr deviceContext);

        public static Bitmap Capture(IntPtr window)
        {
            NativeRect rect;
            if (!GetWindowRect(window, out rect))
            {
                return null;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var windowDc = GetWindowDC(window);
            if (windowDc == IntPtr.Zero)
            {
                return null;
            }

            var memoryDc = IntPtr.Zero;
            var bitmapHandle = IntPtr.Zero;
            var previousObject = IntPtr.Zero;
            try
            {
                memoryDc = CreateCompatibleDC(windowDc);
                bitmapHandle = CreateCompatibleBitmap(windowDc, width, height);
                if (memoryDc == IntPtr.Zero || bitmapHandle == IntPtr.Zero)
                {
                    return null;
                }

                previousObject = SelectObject(memoryDc, bitmapHandle);
                if (!PrintWindow(window, memoryDc, PwRenderFullContent))
                {
                    return null;
                }

                using (var captured = Image.FromHbitmap(bitmapHandle))
                {
                    var result = new Bitmap(captured);
                    if (!HasVisualContent(result))
                    {
                        result.Dispose();
                        return null;
                    }

                    return result;
                }
            }
            finally
            {
                if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
                {
                    SelectObject(memoryDc, previousObject);
                }
                if (bitmapHandle != IntPtr.Zero)
                {
                    DeleteObject(bitmapHandle);
                }
                if (memoryDc != IntPtr.Zero)
                {
                    DeleteDC(memoryDc);
                }
                ReleaseDC(window, windowDc);
            }
        }

        private static bool HasVisualContent(Bitmap bitmap)
        {
            var minimum = 255;
            var maximum = 0;
            var stepX = Math.Max(1, bitmap.Width / 16);
            var stepY = Math.Max(1, bitmap.Height / 16);
            for (var y = 0; y < bitmap.Height; y += stepY)
            {
                for (var x = 0; x < bitmap.Width; x += stepX)
                {
                    var color = bitmap.GetPixel(x, y);
                    minimum = Math.Min(minimum, Math.Min(color.R, Math.Min(color.G, color.B)));
                    maximum = Math.Max(maximum, Math.Max(color.R, Math.Max(color.G, color.B)));
                    if (maximum - minimum >= 8)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
