using System;
using System.IO;
using AIBridge.Internal.Json;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Screenshot command: capture Game view screenshots and GIF recordings.
    /// Supports runtime (Play mode) screenshots and animated GIF capture.
    /// </summary>
    public class ScreenshotCommand : ICommand
    {
        public string Type => "screenshot";
        public bool RequiresRefresh => false;

        public string SkillDescription => @"### `screenshot` - Screenshot & GIF Recording (Play Mode)

**Requires Play mode.** Files saved to `AIBridgeCache/screenshots/`.

```bash
$CLI screenshot game  # Capture Game view screenshot (JPG)
$CLI screenshot gif --frameCount 50  # Record GIF
$CLI screenshot gif --frameCount 100 --fps 25 --scale 0.5 --colorCount 128
```

**GIF Parameters:**

| Parameter | Range | Default | Description |
|-----------|-------|---------|-------------|
| `--frameCount` | 1-200 | Required | Number of frames to capture |
| `--fps` | 10-30 | 25 | Frames per second |
| `--scale` | 0.25-1.0 | 0.5 | Resolution scale factor |
| `--colorCount` | 64-256 | 128 | GIF palette color count |
| `--startDelay` | 0-5 seconds | 0 | Delay before capture starts |

**Estimated File Sizes:**

| Frames | Duration | Resolution | Size |
|--------|----------|------------|------|
| 25 | 1s | 480x270 | 200KB - 800KB |
| 50 | 2s | 480x270 | 400KB - 1.5MB |
| 100 | 4s | 480x270 | 800KB - 3MB |
| 200 | 8s | 480x270 | 1.5MB - 6MB |";

        // For async GIF recording - stores the command ID for deferred result writing
        private static string _pendingGifCommandId;
        private static bool _gifRecordingInProgress;

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "game");

            try
            {
                switch (action.ToLower())
                {
                    case "game":
                        return CaptureGameView(request);
                    case "gif":
                        return CaptureGif(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: game, gif");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult CaptureGameView(CommandRequest request)
        {
            // Use shared helper
            var result = ScreenshotHelper.CaptureGameView(checkPlayMode: true);

            if (!result.Success)
            {
                return CommandResult.Failure(request.id, result.Error);
            }

            return CommandResult.Success(request.id, new
            {
                action = "game",
                imagePath = result.ImagePath,
                width = result.Width,
                height = result.Height,
                timestamp = result.Timestamp,
                filename = result.Filename
            });
        }

        private CommandResult CaptureGif(CommandRequest request)
        {
            // Parse parameters
            int frameCount = request.GetParam("frameCount", 0);
            if (frameCount <= 0)
            {
                return CommandResult.Failure(request.id, "Parameter 'frameCount' is required and must be > 0.");
            }

            frameCount = Mathf.Clamp(frameCount, 1, GifRecorder.MaxFrameCount);
            int fps = request.GetParam("fps", 20);
            float scale = request.GetParam("scale", 0.5f);
            int colorCount = request.GetParam("colorCount", 128);
            float startDelay = request.GetParam("startDelay", 0f);

            // Check if already recording
            if (GifRecorder.IsRecording || _gifRecordingInProgress)
            {
                return CommandResult.Failure(request.id, "GIF recording already in progress.");
            }

            // Check Play mode
            if (!EditorApplication.isPlaying)
            {
                return CommandResult.Failure(request.id, "GIF recording requires Play mode. Please start the game first.");
            }

            // Store the command ID for deferred result writing
            _pendingGifCommandId = request.id;
            _gifRecordingInProgress = true;

            // Start recording - result will be written asynchronously when complete
            GifRecorder.StartRecording(
                frameCount,
                fps,
                scale,
                colorCount,
                startDelay,
                onComplete: OnGifRecordingComplete
            );

            // Return null to indicate the command will write its own result
            // The CommandWatcher will not write a result file for null returns
            return null;
        }

        /// <summary>
        /// Called when GIF recording completes. Writes the result file directly.
        /// </summary>
        private static void OnGifRecordingComplete(GifRecordResult gifResult)
        {
            _gifRecordingInProgress = false;

            if (string.IsNullOrEmpty(_pendingGifCommandId))
            {
                AIBridgeLogger.LogError("GIF recording completed but no pending command ID found.");
                return;
            }

            var commandId = _pendingGifCommandId;
            _pendingGifCommandId = null;

            CommandResult result;

            if (gifResult == null || !gifResult.Success)
            {
                result = CommandResult.Failure(commandId, gifResult?.Error ?? "Unknown error during GIF recording.");
            }
            else
            {
                result = CommandResult.Success(commandId, new
                {
                    action = "gif",
                    gifPath = gifResult.GifPath,
                    filename = gifResult.Filename,
                    frameCount = gifResult.FrameCount,
                    width = gifResult.Width,
                    height = gifResult.Height,
                    duration = gifResult.Duration,
                    fileSize = gifResult.FileSize,
                    timestamp = gifResult.Timestamp
                });
            }

            // Write result file directly
            WriteResultFile(result);
        }

        /// <summary>
        /// Write result file directly to the results directory.
        /// </summary>
        private static void WriteResultFile(CommandResult result)
        {
            try
            {
                var resultsDir = Path.Combine(AIBridge.BridgeDirectory, "results");
                if (!Directory.Exists(resultsDir))
                {
                    Directory.CreateDirectory(resultsDir);
                }

                var filePath = Path.Combine(resultsDir, $"{result.id}.json");
                var json = AIBridgeJson.Serialize(result, pretty: true);
                File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);

                AIBridgeLogger.LogInfo($"GIF recording result written: {result.id}, success={result.success}");
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"Failed to write GIF result for {result.id}: {ex.Message}");
            }
        }
    }
}
