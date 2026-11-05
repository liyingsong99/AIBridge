using System;

namespace AIBridge.Editor
{
    /// <summary>内置Memory捕获能力与可选分析能力分离；未知回调不得重放。</summary>
    public sealed class MemoryCommand : ICommand
    {
        public string Type => "memory";
        public bool RequiresRefresh => false;
        public string SkillDescription => @"### `memory` - Native Editor memory snapshot

```bash
$CLI memory capture_snapshot --confirm true
$CLI memory snapshot_status --requestId <original-request-id>
$CLI memory release_snapshot --requestId <unknown-request-id> --confirm true
$CLI memory load_snapshot --path "".aibridge/memory/example.snap"" --confirm true
```

Capture uses built-in TakeSnapshot independently of the optional Memory Profiler package. Local Editor only, stopped Profiler, ManagedObjects+NativeObjects, no GC/screenshot/target switching. New `.aibridge/memory/*.snap` only, no overwrite. Capture may stall Editor and contain sensitive heap data. Timeout/reload is unknown, never auto-replay or native cancellation; late callback can finish original request. Explicit release only frees local state. load_snapshot reports unsupported until a package/version-specific analysis API is verified; it never installs a package or opens arbitrary UI.";

        public CommandResult Execute(CommandRequest request)
        {
            try
            {
                return NativeMemoryCapture.Execute(request, request.GetParam("action", "snapshot_status").ToLowerInvariant());
            }
            catch (Exception)
            {
                return CommandResult.Failure(request.id, "Memory state/validation failed; inspect the original snapshot request before resubmitting.");
            }
        }
    }
}
