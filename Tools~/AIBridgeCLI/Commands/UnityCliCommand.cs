using System;
using System.Collections.Generic;
using System.IO;
using AIBridgeCLI.Core;
using AIBridgeCLI.Core.UnityCli;
using Newtonsoft.Json;

namespace AIBridgeCLI.Commands
{
    public static class UnityCliCommand
    {
        public static int Execute(string type, string action, Dictionary<string, string> options, OutputMode outputMode)
        {
            var pipeline = string.Equals(type, "pipeline", StringComparison.OrdinalIgnoreCase);
            var builder = pipeline ? (ICommandBuilder)new PipelineCapabilityCommandBuilder() : new UnityCliCommandBuilder();
            action = string.IsNullOrWhiteSpace(action) ? (pipeline ? "capabilities" : "doctor") : action.ToLowerInvariant();
            if (action == "help")
            {
                Console.WriteLine(builder.GetHelp());
                return 0;
            }
            var format = UnityCliSettings.ReadOption(options, "format") ?? (outputMode == OutputMode.Pretty ? "text" : "json");
            try
            {
                ValidateOptions(options);
                if (format != "json" && format != "text") throw new ArgumentException("--format must be json or text.");
                if (Array.IndexOf(builder.Actions, action) < 0) throw new ArgumentException("Unknown " + type + " action: " + action);
                var settings = UnityCliSettings.Load(options);
                var diagnostic = new UnityCliDiagnostics().Probe(settings, action != "version", action != "version");
                var capability = RequiredCapability(pipeline, action);
                var selection = UnityCliBackendSelector.Select(settings.Backend, capability, diagnostic);
                var success = diagnostic.available && diagnostic.errorCode == null && selection.success
                    && diagnostic.capabilities.Contains(capability)
                    && (!pipeline || diagnostic.pipelineAvailable == true);
                var errorCode = diagnostic.errorCode ?? selection.errorCode;
                var error = diagnostic.error;
                if (!success && errorCode == null)
                {
                    errorCode = "CAPABILITY_UNAVAILABLE";
                    error = "The requested Unity CLI capability was not confirmed.";
                }
                var response = new
                {
                    success,
                    // available 表示本次请求的能力可用；CLI 进程本身状态见 data.diagnostic.available。
                    available = success,
                    retryable = diagnostic.retryable || selection.retryable,
                    errorCode,
                    error,
                    data = new { configPath = settings.ConfigPath, selection, diagnostic }
                };
                Print(response, format);
                return success ? 0 : 1;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                Print(new { success = false, available = false, retryable = false, errorCode = "INVALID_CONFIGURATION", error = ex.Message }, format);
                return 1;
            }
        }

        private static string RequiredCapability(bool pipeline, string action)
        {
            if (pipeline)
            {
                return "unity.pipeline.help";
            }

            // doctor/capabilities 要求官方根帮助探测成功；Pipeline 帮助是附加诊断信息，单独入口才是硬要求。
            if (string.Equals(action, "capabilities", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "doctor", StringComparison.OrdinalIgnoreCase))
            {
                return "unity.help";
            }

            return "unity.version";
        }

        private static void ValidateOptions(Dictionary<string, string> options)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "backend", "unity-cli-path", "config", "timeout", "format", "raw", "pretty", "quiet", "help" };
            foreach (var key in options.Keys)
                if (!allowed.Contains(key)) throw new ArgumentException("Unsupported Unity CLI diagnostic option: --" + key);
        }

        private static void Print(object result, string format)
        {
            if (string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
            {
                var json = Newtonsoft.Json.Linq.JObject.FromObject(result);
                Console.WriteLine("success: " + json.Value<bool>("success"));
                Console.WriteLine("available: " + json.Value<bool>("available"));
                Console.WriteLine("retryable: " + json.Value<bool>("retryable"));
                Console.WriteLine("errorCode: " + (json.Value<string>("errorCode") ?? ""));
                Console.WriteLine("error: " + (json.Value<string>("error") ?? ""));
                return;
            }

            Console.WriteLine(JsonConvert.SerializeObject(result, Formatting.None));
        }
    }
}
