using System.Collections.Generic;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Game view command builder: manage Game window resolution (get, set, list)
    /// </summary>
    public class GameViewCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "gameview";
        public override string Description => "Game view resolution management (get, set, list)";

        public override string[] Actions => new[]
        {
            "get_resolution",
            "set_resolution",
            "list_resolutions"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["get_resolution"] = new List<ParameterInfo>(),
            ["set_resolution"] = new List<ParameterInfo>
            {
                new ParameterInfo("width", "Resolution width in pixels (1-8192)", true),
                new ParameterInfo("height", "Resolution height in pixels (1-8192)", true)
            },
            ["list_resolutions"] = new List<ParameterInfo>()
        };
    }
}
