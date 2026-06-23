using System.Collections.Generic;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Selection command builder: get, set, clear, add, remove
    /// </summary>
    public class SelectionCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "selection";
        public override string Description => "Selection operations (get, set, clear, add, remove)";

        public override string[] Actions => new[]
        {
            "get", "set", "clear", "add", "remove"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["get"] = new List<ParameterInfo>
            {
                new ParameterInfo("includeComponents", "Include component info in results", false, "false")
            },
            ["set"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject", false),
                new ParameterInfo("assetPath", "Path to an asset", false),
                new ParameterInfo("entityId", "Entity ID of the object on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("entityIds", "Multiple entity IDs (comma-separated)", false),
                new ParameterInfo("instanceIds", "Multiple entity IDs on Unity 6000.4+ or legacy instance IDs on older Unity", false)
            },
            ["clear"] = new List<ParameterInfo>(),
            ["add"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject", false),
                new ParameterInfo("assetPath", "Path to an asset", false),
                new ParameterInfo("entityId", "Entity ID of the object on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false)
            },
            ["remove"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject", false),
                new ParameterInfo("assetPath", "Path to an asset", false),
                new ParameterInfo("entityId", "Entity ID of the object on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false)
            }
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            var request = base.Build(action, options);
            CopyParam(request.@params, "entityId", "instanceId");
            CopyParam(request.@params, "entityIds", "instanceIds");
            return request;
        }

        private static void CopyParam(Dictionary<string, object> @params, string sourceKey, string targetKey)
        {
            if (@params == null || !@params.ContainsKey(sourceKey) || @params.ContainsKey(targetKey))
            {
                return;
            }

            @params[targetKey] = @params[sourceKey];
        }
    }
}
