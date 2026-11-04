using System;

namespace AIBridgeCLI.Core.UnityCli
{
    /// <summary>能力级选择，不替换现有命令发送器，也不对失败操作重试另一后端。</summary>
    public static class UnityCliBackendSelector
    {
        public static UnityCliBackendSelection Select(string requested, string capability, UnityCliDiagnostic diagnostic)
        {
            requested = (requested ?? "native").ToLowerInvariant();
            if (requested != "native" && requested != "auto" && requested != "unity-cli")
                throw new ArgumentException("Unknown backend: " + requested);
            if (requested == "native")
                return new UnityCliBackendSelection { success = true, requested = requested, selected = "native",
                    available = true, reason = "Existing CommandSender remains the default backend." };
            var supported = diagnostic != null && diagnostic.available && diagnostic.capabilities.Contains(capability);
            if (supported)
                return new UnityCliBackendSelection { success = true, requested = requested, selected = "unity-cli",
                    available = true, reason = "The requested diagnostic capability was successfully probed." };
            if (requested == "auto")
                return new UnityCliBackendSelection { success = true, requested = requested, selected = "native",
                    available = true, reason = "Unity CLI capability unavailable; keep existing CommandSender." };
            return new UnityCliBackendSelection { requested = requested, available = false,
                retryable = diagnostic != null && diagnostic.retryable,
                errorCode = diagnostic?.errorCode ?? "CAPABILITY_UNAVAILABLE",
                reason = "Unity CLI does not provide the requested probed capability: " + capability };
        }
    }
}
