using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AIBridgeCLI.Commands;
using AIBridgeCLI.Core;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI
{
    partial class Program
    {
        private static int HandlePipelineCompile(ParsedArgs parsed, OutputMode outputMode)
        {
            CommandRequest request;
            try { request = new CompileCommandBuilder().Build(parsed.Action, parsed.Options); }
            catch (ArgumentException ex) { OutputFormatter.PrintError(ex.Message); return 1; }
            var sender = CreateCommandSender(DEFAULT_TIMEOUT, parsed, request);
            var timeout = parsed.Action == "start" ? Convert.ToInt32(request.@params["timeout"]) : DEFAULT_TIMEOUT;
            var clock = Stopwatch.StartNew();
            // 仅发送一次触发；所有后续请求只查询原requestId本地日志。
            var result = sender.SendCommand(request);
            if (parsed.Action != "start")
            { OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false); return result.success ? 0 : 1; }
            if (!result.success && !IsTransportTimeoutError(result.error))
            { OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false); return 1; }
            while (clock.ElapsedMilliseconds < timeout + 2000L)
            {
                var record = result.data as JObject;
                if (record != null && (string)record["requestId"] == request.id && (string)record["backend"] == "pipeline"
                    && ((string)record["status"] == "acknowledged" || (string)record["status"] == "unknown"
                        || (string)record["status"] == "not_submitted"))
                {
                    // 退出0只表示触发应答合法，绝不表示本次编译成功。
                    var acknowledged = (string)record["status"] == "acknowledged";
                    OutputFormatter.PrintResult(new CommandResult { success = acknowledged, data = record,
                        error = acknowledged ? null : (string)record["error"] ?? "Compile trigger outcome unknown." }, outputMode, includeIdInRaw: false);
                    return acknowledged ? 0 : 1;
                }
                Thread.Sleep(250);
                result = sender.SendCommand(new CommandRequest { id = PathHelper.GenerateCommandId(), type = "compile",
                    @params = new Dictionary<string, object> { { "backend", "pipeline" }, { "action", "status" }, { "requestId", request.id } } });
            }
            OutputFormatter.PrintResult(new CommandResult { success = false,
                error = "Compile trigger wait ended without confirmed acknowledgement. Query the original requestId; do not replay.",
                data = new JObject { ["backend"] = "pipeline", ["requestId"] = request.id, ["status"] = "unknown", ["busy"] = true,
                    ["compilationSuccess"] = JValue.CreateNull(), ["operationCorrelation"] = false, ["automaticReplay"] = false } }, outputMode, includeIdInRaw: false);
            return 1;
        }
    }
}
