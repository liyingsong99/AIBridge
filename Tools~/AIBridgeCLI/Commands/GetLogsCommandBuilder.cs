using System.Collections.Generic;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// GetLogs command builder: retrieve Unity console logs
    /// </summary>
    public class GetLogsCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "get_logs";
        public override string Description => "Get Unity console logs";

        public override string[] Actions => new[] { "get" };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["get"] = new List<ParameterInfo>
            {
                new ParameterInfo("count", "Maximum number of log entries", false, "50"),
                new ParameterInfo("logType", "Filter by type: all, Log, Warning, Error", false, "all"),
                new ParameterInfo("regex", "Filter by message regex", false)
            },
            [""] = new List<ParameterInfo>
            {
                new ParameterInfo("count", "Maximum number of log entries", false, "50"),
                new ParameterInfo("logType", "Filter by type: all, Log, Warning, Error", false, "all"),
                new ParameterInfo("regex", "Filter by message regex", false)
            }
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            // For get_logs, we don't need action parameter
            var request = new CommandRequest
            {
                id = PathHelper.GenerateCommandId(),
                type = Type,
                @params = new Dictionary<string, object>()
            };

            foreach (var kvp in options)
            {
                if (kvp.Key == "json" || kvp.Key == "stdin") continue;
                request.@params[kvp.Key] = ParseValue(kvp.Value);
            }

            return request;
        }
    }
}
