using System;
using System.IO;
using System.Text.RegularExpressions;

namespace AIBridgeCLI.Core.UnityCli
{
    /// <summary>只执行不会打开项目的版本与帮助探测，能力必须来自实际成功探测。</summary>
    public sealed class UnityCliDiagnostics
    {
        private readonly UnityCliProcessRunner runner = new UnityCliProcessRunner();

        public UnityCliDiagnostic Probe(UnityCliSettings settings, bool includeHelp, bool includePipeline)
        {
            var result = UnityCliLocator.Locate(settings);
            if (!result.available) return result;
            result.available = false;
            result.versionProbe = Run(settings, result.executablePath, "--version");
            if (!AcceptProbe(result, result.versionProbe)) return result;
            var versionOutput = result.versionProbe.stdout ?? string.Empty;
            var version = Regex.Match(versionOutput, @"\b\d+\.\d+(?:\.\d+)?(?:[-+][\w.-]+)?\b");
            if (!version.Success)
            {
                result.errorCode = "UNRECOGNIZED_VERSION";
                result.error = "Unity CLI version output did not contain a recognizable version.";
                return result;
            }

            // 仅有 SemVer 和退出码不足以证明可执行文件是 Unity CLI，避免误把 dotnet 等工具当成增强后端。
            var executableName = Path.GetFileNameWithoutExtension(result.executablePath ?? string.Empty);
            var identityInOutput = Regex.IsMatch(versionOutput, @"\bUnity\b.*\bCLI\b|\bCLI\b.*\bUnity\b", RegexOptions.IgnoreCase);
            var identityInName = string.Equals(executableName, "unity", StringComparison.OrdinalIgnoreCase);
            if (!identityInOutput && !identityInName)
            {
                result.errorCode = "NOT_UNITY_CLI";
                result.error = "The executable did not identify itself as Unity CLI.";
                return result;
            }

            result.version = version.Value;
            result.identityConfirmed = true;
            result.available = true;
            result.capabilities.Add("unity.version");
            if (!includeHelp) return result;
            result.helpProbe = Run(settings, result.executablePath, "--help");
            if (!AcceptProbe(result, result.helpProbe)) return result;
            result.capabilities.Add("unity.help");
            if (includePipeline) ProbePipeline(settings, result);
            return result;
        }

        private void ProbePipeline(UnityCliSettings settings, UnityCliDiagnostic result)
        {
            // 官方帮助可能把别名写作 pipeline|pipe；仍匹配完整命令名，避免误认 pipeline-other。
            var declared = Regex.IsMatch(result.helpProbe.stdout, @"^\s*pipeline(?:\|[\w-]+)*(?:\s|$)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (!declared)
            {
                result.pipelineAvailable = false;
                result.warnings.Add("Root help did not advertise pipeline; pipeline was not executed.");
                return;
            }
            result.pipelineProbe = Run(settings, result.executablePath, "pipeline", "--help");
            result.pipelineAvailable = result.pipelineProbe.success
                && Regex.IsMatch(result.pipelineProbe.stdout, @"\bpipeline\b", RegexOptions.IgnoreCase);
            if (result.pipelineAvailable == true) result.capabilities.Add("unity.pipeline.help");
            else
            {
                result.warnings.Add("Pipeline help probe failed or returned unrecognized help.");
                AcceptProbe(result, result.pipelineProbe);
            }
        }

        private UnityCliProcessResult Run(UnityCliSettings settings, string executable, params string[] arguments)
        {
            return runner.Execute(new UnityCliProcessRequest
            {
                ExecutablePath = executable,
                WorkingDirectory = settings.ProjectRoot,
                Arguments = arguments,
                TimeoutMs = settings.ProbeTimeoutMs,
                OutputFormat = UnityCliOutputFormat.Text
            });
        }

        private static bool AcceptProbe(UnityCliDiagnostic diagnostic, UnityCliProcessResult probe)
        {
            if (probe.success) return true;
            diagnostic.retryable = probe.retryable;
            diagnostic.errorCode = probe.errorCode;
            diagnostic.error = probe.error;
            return false;
        }
    }
}
