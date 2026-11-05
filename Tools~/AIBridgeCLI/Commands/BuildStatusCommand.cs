using System;
using System.Collections.Generic;
using AIBridge.Runtime.Internal;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    /// <summary>只读共享持久协议；不依赖Unity主线程、官方CLI或网络。</summary>
    internal static class BuildStatusCommand
    {
        public static int Execute(string action, Dictionary<string, string> options, OutputMode mode)
        {
            action = string.IsNullOrWhiteSpace(action) ? "status" : action.ToLowerInvariant();
            var request = new BuildCommandBuilder().Build(action, options);
            var project = PathHelper.TryGetUnityProjectRoot();
            if (string.IsNullOrEmpty(project)) throw new InvalidOperationException("Unity project root was not found.");
            string id = null;
            if (request.@params.TryGetValue("requestId", out var idValue))
            {
                if (!(idValue is string text) || string.IsNullOrWhiteSpace(text) || text.Length > 256)
                    throw new ArgumentException("Invalid build requestId.");
                id = text;
            }
            var record = AIBridgeBuildStore.Load(project, id, out var unreadable);
            if (unreadable)
            {
                OutputFormatter.PrintResult(new CommandResult { success = false,
                    error = "Build record is corrupt or unreadable; do not replay automatically." }, mode, false);
                return 1;
            }
            if (record == null)
            {
                var querySuccess = action != "report";
                OutputFormatter.PrintResult(new CommandResult { success = querySuccess,
                    error = querySuccess ? null : "No registered completed BuildReport is available.",
                    data = new { status = "idle", reportAvailable = false, localJournal = true } }, mode, false);
                return querySuccess ? 0 : 1;
            }
            var available = record.status == "completed";
            var success = action != "report" || (available && record.success == true && record.report.result == "Succeeded");
            // 单独标识日志快照；queued/building是上次持久观察，不是实时liveness断言。
            OutputFormatter.PrintResult(new CommandResult { success = success,
                error = success ? null : available ? record.error ?? "Build ended without confirmed success." : "No confirmed completed BuildReport is available.",
                data = new { localJournal = true, reportAvailable = available,
                    record = Newtonsoft.Json.Linq.JObject.FromObject(record,
                        Newtonsoft.Json.JsonSerializer.Create(new Newtonsoft.Json.JsonSerializerSettings
                            { NullValueHandling = Newtonsoft.Json.NullValueHandling.Include })) } }, mode, false);
            return success ? 0 : 1;
        }
    }
}
