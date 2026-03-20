using System.Collections.Generic;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Asset command builder: search, find, get_path, load, import, refresh, read_text (fallback)
    /// </summary>
    public class AssetCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "asset";
        public override string Description => "AssetDatabase operations (search, find, get_path, load, import, refresh, and fallback read_text)";

        public override string[] Actions => new[]
        {
            "find", "search", "import", "refresh", "get_path", "load", "read_text"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["find"] = new List<ParameterInfo>
            {
                new ParameterInfo("filter", "Search filter (e.g., 't:Prefab', 't:Texture2D')", true),
                new ParameterInfo("format", "Response format: full (default) or paths for Unity asset paths only", false, "full"),
                new ParameterInfo("searchInFolders", "Folders to search in (comma-separated)", false),
                new ParameterInfo("maxResults", "Maximum number of results", false, "100")
            },
            ["search"] = new List<ParameterInfo>
            {
                new ParameterInfo("mode", "Search mode: all, prefab, scene, script, texture, material, audio, animation, shader, font, model, so", false, "all"),
                new ParameterInfo("filter", "Custom Unity filter (overrides mode)", false),
                new ParameterInfo("keyword", "Search keyword (appended to filter)", false),
                new ParameterInfo("format", "Response format: full (default) or paths for Unity asset paths only", false, "full"),
                new ParameterInfo("searchInFolders", "Folders to search in (comma-separated)", false),
                new ParameterInfo("maxResults", "Maximum number of results", false, "100")
            },
            ["import"] = new List<ParameterInfo>
            {
                new ParameterInfo("assetPath", "Path to the asset to reimport", true),
                new ParameterInfo("forceUpdate", "Force update the asset", false, "false")
            },
            ["refresh"] = new List<ParameterInfo>
            {
                new ParameterInfo("forceUpdate", "Force update all assets", false, "false")
            },
            ["get_path"] = new List<ParameterInfo>
            {
                new ParameterInfo("guid", "GUID of the asset", true)
            },
            ["load"] = new List<ParameterInfo>
            {
                new ParameterInfo("assetPath", "Path to the asset", true)
            },
            ["read_text"] = new List<ParameterInfo>
            {
                new ParameterInfo("assetPath", "Asset path to read (e.g. Assets/Configs/GameConfig.asset)", true),
                new ParameterInfo("startLine", "1-based line number to start reading from", false, "1"),
                new ParameterInfo("maxLines", "Maximum number of lines to return", false, "200"),
                new ParameterInfo("maxChars", "Maximum number of characters to return", false, "12000")
            }
        };
    }
}
