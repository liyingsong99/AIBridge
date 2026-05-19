using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Hotkey handlers for screenshot and GIF recording.
    /// F12: Screenshot
    /// F11: GIF Recording
    /// </summary>
    public static class ScreenshotHotkey
    {
        /// <summary>
        /// Capture screenshot with F12 hotkey.
        /// </summary>
        [MenuItem("AIBridge/Screenshot Game View _F12")]
        private static void CaptureScreenshot()
        {
            var result = ScreenshotHelper.CaptureGameView(checkPlayMode: true);

            if (result.Success)
            {
                Debug.Log($"[AIBridge] Screenshot saved: {result.ImagePath}");
            }
            else
            {
                Debug.LogWarning($"[AIBridge] Screenshot failed: {result.Error}");
            }
        }

        [MenuItem("AIBridge/Screenshot Game View _F12", true)]
        private static bool ValidateCaptureScreenshot()
        {
            return EditorApplication.isPlaying;
        }

        /// <summary>
        /// Record GIF with F11 hotkey.
        /// Press once to start, press again to stop early.
        /// </summary>
        [MenuItem("AIBridge/Record GIF _F11")]
        private static void RecordGif()
        {
            if (GifRecorder.IsRecording)
            {
                Debug.Log("[AIBridge] Stopping GIF recording...");
                GifRecorder.StopRecording();
                return;
            }

            int frameCount = GifRecorderSettings.DefaultFrameCount;
            int fps = GifRecorderSettings.DefaultFps;
            float scale = GifRecorderSettings.DefaultScale;
            int colorCount = GifRecorderSettings.DefaultColorCount;
            float startDelay = GifRecorderSettings.DefaultStartDelay;

            Debug.Log($"[AIBridge] Starting GIF recording: {frameCount} frames @ {fps} fps (delay {startDelay:F1}s)...");

            GifRecorder.StartRecording(
                frameCount,
                fps,
                scale,
                colorCount,
                startDelay,
                onComplete: result =>
                {
                    EditorUtility.ClearProgressBar();

                    if (result.Success)
                    {
                        Debug.Log($"[AIBridge] GIF saved: {result.GifPath} ({result.FileSize / 1024}KB, {result.FrameCount} frames, {result.Duration:F1}s)");
                    }
                    else
                    {
                        Debug.LogWarning($"[AIBridge] GIF recording failed: {result.Error}");
                    }
                },
                onProgress: (current, total) =>
                {
                    EditorUtility.DisplayProgressBar(
                        "Recording GIF",
                        $"Capturing frame {current}/{total}",
                        (float)current / total);
                }
            );
        }

        [MenuItem("AIBridge/Record GIF _F11", true)]
        private static bool ValidateRecordGif()
        {
            return EditorApplication.isPlaying;
        }

    }
}
