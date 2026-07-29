using System.Collections.Generic;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Screenshot command builder: capture Game view, Scene view, Editor windows and GIF recordings
    /// </summary>
    public class ScreenshotCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "screenshot";
        public override string Description => "Capture Game, Scene view, Unity Editor window screenshots and GIF recordings";

        public override string[] Actions => new[]
        {
            "game",
            "scene_view",
            "editor_window",
            "gif"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["game"] = new List<ParameterInfo>(),
            ["scene_view"] = new List<ParameterInfo>
            {
                new ParameterInfo("width", "Output image width (1-8192, requires --height when set)", false),
                new ParameterInfo("height", "Output image height (1-8192, requires --width when set)", false)
            },
            ["editor_window"] = new List<ParameterInfo>
            {
                new ParameterInfo("target", "Unity target: editor or active", false),
                new ParameterInfo("windowType", "EditorWindow full type name or simple type name", false),
                new ParameterInfo("title", "Exact window title (case-insensitive)", false),
                new ParameterInfo("instanceId", "EditorWindow instance ID used to disambiguate matches", false)
            },
            ["gif"] = new List<ParameterInfo>
            {
                new ParameterInfo("frameCount", "Number of frames to capture (1-200)", true),
                new ParameterInfo("fps", "Frames per second (10-30)", false, "20"),
                new ParameterInfo("scale", "Resolution scale factor (0.25-1.0)", false, "0.5"),
                new ParameterInfo("colorCount", "GIF palette color count (64-256)", false, "256")
            }
        };
    }
}
