using System.Collections.Generic;

namespace AIBridgeCLI.Core
{
    public interface IRuntimeTransportClient
    {
        RuntimeTransportKind Kind { get; }
        IReadOnlyList<RuntimeTargetInfo> ListTargets(RuntimeTargetQueryOptions options = null);
        RuntimeTargetInfo ResolveTarget(string target, RuntimeTargetQueryOptions options = null);
        RuntimeSendResult Send(RuntimeTargetInfo target, CommandRequest request);
        RuntimeReceiveResult WaitResult(RuntimeTargetInfo target, string commandId, int timeoutMs, int pollIntervalMs);
        void CleanupCommand(RuntimeTargetInfo target, string commandId);
        RuntimeDiagnosticReport Diagnose(string target, RuntimeCommandTrace commandTrace = null);
    }

    public sealed class RuntimeTargetQueryOptions
    {
        public static RuntimeTargetQueryOptions Quick => new RuntimeTargetQueryOptions
        {
            ProbeLocalPorts = false
        };

        public static RuntimeTargetQueryOptions Probe => new RuntimeTargetQueryOptions
        {
            ProbeLocalPorts = true
        };

        public bool ProbeLocalPorts { get; set; }

        public string mode => ProbeLocalPorts ? "probe" : "quick";
    }

    public sealed class RuntimeSendResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string CommandPath { get; set; }
    }

    public sealed class RuntimeReceiveResult
    {
        public bool Success { get; set; }
        public CommandResult Result { get; set; }
        public string Error { get; set; }
        public bool TimedOut { get; set; }
    }

    public sealed class RuntimeCommandTrace
    {
        public string CommandId { get; set; }
        public string Action { get; set; }
        public string CommandPath { get; set; }
        public string ResultPath { get; set; }
        public string SentAtUtc { get; set; }
    }
}
