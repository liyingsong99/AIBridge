using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using AIBridge.Internal.Json;

namespace AIBridge.Editor
{
    public partial class CodeCommand
    {
        private static readonly HashSet<string> PipelineRunParameters = new HashSet<string>(StringComparer.Ordinal)
        { "action", "backend", "file", "entry", "args", "dry-run", "confirm", "timeout", "job" };
        private static readonly Regex PipelineEntryPattern = new Regex(
            @"^[A-Za-z_][A-Za-z0-9_]*(?:[.+][A-Za-z_][A-Za-z0-9_]*)*\.[A-Za-z_][A-Za-z0-9_]*\z",
            RegexOptions.CultureInvariant);
        private const int MaxPipelineArgumentsChars = 32768;

        private static CommandResult ExecutePipelineRun(CommandRequest request)
        {
            if (!PipelineCodeProtocol.IsExecutionAuthorized())
                return CommandResult.Failure(request.id, "Code execution is disabled. Enable it in AIBridge/Settings -> Basic -> Enable Code Execution.");
            if (AIBridgeBuildTracker.Busy || ProfilerNativeGate.Busy || (PipelineTestTracker.HasBusy || PipelineCompileTracker.HasBusy))
                return CommandResult.Failure(request.id, "An AIBridge build, native capture or Pipeline test is active or unknown; resolve its original request before code run.");
            if (_activeOperation != null)
                return FailureWithData(request.id, "Another code execution is already running.", BuildBusyData());
            foreach (var key in request.@params.Keys)
                if (!PipelineRunParameters.Contains(key))
                    return CommandResult.Failure(request.id, "Unsupported code run parameter: " + key);
            if (request.HasParam("backend") && (!(request.@params["backend"] is string backend)
                || !string.Equals(backend, "pipeline", StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Failure(request.id, "code run supports only the explicit Pipeline backend.");
            if (!TryReadPipelineFlag(request, "dry-run", true, out var dryRun)
                || !TryReadPipelineFlag(request, "confirm", false, out var confirmed))
                return CommandResult.Failure(request.id, "Pipeline dry-run and confirm must be boolean values.");
            if (!TryReadPipelineFlag(request, "job", false, out var job))
                return CommandResult.Failure(request.id, "Pipeline job must be boolean.");
            if (job && !confirmed)
                return CommandResult.Failure(request.id, "Detached jobs have no upstream execution timeout and require --confirm true.");
            if (!dryRun && !confirmed)
                return CommandResult.Failure(request.id, "Pipeline code execution requires a per-request --confirm true.");
            if (!request.@params.TryGetValue("file", out var file) || !(file is string path) || string.IsNullOrWhiteSpace(path))
                return CommandResult.Failure(request.id, "code run requires a .cs --file directly under .aibridge/code.");
            if (!request.@params.TryGetValue("entry", out var entryValue) || !(entryValue is string entry)
                || entry.Length > 256 || !PipelineEntryPattern.IsMatch(entry))
                return CommandResult.Failure(request.id, "code run requires an explicit Type.Method or Namespace.Type.Method entry.");
            SourceText source;
            string argumentsJson;
            try
            {
                argumentsJson = ReadPipelineRunArguments(request);
                var fullPath = PipelineRunSource.ValidatePath(GetProjectRoot(), path);
                var original = PipelineRunSource.ReadSource(GetProjectRoot(), fullPath);
                // 完整类型不经过 snippet wrapper；只保留一次捕获的原始文本，适配器不再读取用户源文件。
                source = new SourceText { Kind = "file", Path = fullPath, OriginalCode = original, Target = CodeCompilationTarget.Editor };
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
            {
                // 不透出参数值/源码/IO 路径正文；参数本身可能包含敏感数据。
                return CommandResult.Failure(request.id, "code run rejected invalid source path, source size, entry or JSON args (" + ex.GetType().Name + ").");
            }
            return StartPipelineOperation(request, source, entry, argumentsJson);
        }

        private static string ReadPipelineRunArguments(CommandRequest request)
        {
            if (!request.@params.TryGetValue("args", out var value)) return "[]";
            string json;
            if (value is string text) json = text;
            else if (value is List<object>) json = AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(value));
            else throw new ArgumentException("args must be a JSON array.");
            if (json.Length > MaxPipelineArgumentsChars) throw new ArgumentException("args limit exceeded.");
            var wrapper = AIBridgeJson.DeserializeObjectStrict("{\"args\":" + json + "}", 16);
            if (wrapper == null || wrapper.Count != 1 || !(wrapper["args"] is List<object>))
                throw new ArgumentException("args must be one JSON array.");
            return AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(wrapper["args"]));
        }
    }
}
