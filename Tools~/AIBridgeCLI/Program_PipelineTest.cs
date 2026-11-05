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
        private static int HandlePipelineTest(ParsedArgs parsed, OutputMode outputMode)
        {
            CommandRequest request;
            try { request = new TestCommandBuilder().Build(parsed.Action, parsed.Options); }
            catch (ArgumentException ex) { OutputFormatter.PrintError(ex.Message); return 1; }
            var timeout = parsed.Action == "run" ? Convert.ToInt32(request.@params["timeout"]) : DEFAULT_TIMEOUT;
            var sender = CreateCommandSender(DEFAULT_TIMEOUT, parsed, request);
            var clock = Stopwatch.StartNew();
            // Start 严格提交一次；应答丢失后只查该 requestId 的本地日志，不再次提交或读上游单槽。
            var result = sender.SendCommand(request);
            if (parsed.Action != "run")
            {
                OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false);
                return result.success ? 0 : 1;
            }
            if (!result.success && !IsTransportTimeoutError(result.error))
            {
                OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false);
                return 1;
            }
            while (clock.ElapsedMilliseconds < timeout + 2000L)
            {
                var record = result.success ? result.data as JObject : null;
                if (record != null && (string)record["requestId"] == request.id
                    && (string)record["backend"] == "pipeline" && (bool?)record["busy"] == false)
                    return PrintPipelineTestResult(record, outputMode);
                if (record != null && (string)record["requestId"] == request.id && (string)record["status"] == "unknown")
                    return PrintPipelineTestResult(record, outputMode);
                Thread.Sleep(250);
                result = sender.SendCommand(new CommandRequest
                {
                    id = PathHelper.GenerateCommandId(), type = "test", @params = new Dictionary<string, object>
                    { { "action", "status" }, { "backend", "pipeline" }, { "requestId", request.id } }
                });
            }
            var unknown = new CommandResult { success = false, error = "Pipeline test wait ended without a confirmed result. Query the original requestId; do not replay.",
                data = new JObject { ["backend"] = "pipeline", ["requestId"] = request.id, ["status"] = "unknown", ["busy"] = true,
                    ["success"] = JValue.CreateNull(), ["executionEnded"] = false, ["remoteCancellationConfirmed"] = false } };
            OutputFormatter.PrintResult(unknown, outputMode, includeIdInRaw: false);
            return 1;
        }

        private static int PrintPipelineTestResult(JObject record, OutputMode outputMode)
        {
            var passed = (string)record["status"] == "passed" && (bool?)record["success"] == true
                && (bool?)record["executionEnded"] == true && (bool?)record["busy"] == false;
            var result = new CommandResult { success = passed, data = record,
                error = passed ? null : (string)record["error"] ?? "Pipeline tests did not confirm a passing run." };
            OutputFormatter.PrintResult(result, outputMode, includeIdInRaw: false);
            return passed ? 0 : 1;
        }
    }
}
