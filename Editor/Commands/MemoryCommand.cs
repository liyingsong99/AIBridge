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

Capture uses built-in TakeSnapshot independently of the optional Memory Profiler package. Edit/Play Mode supported by default, no extra flag. Wait for mode entry to settle; compilation/import/build/code/Pipeline and shared-gate checks still apply. Keep mode/connection stable through completion. Stop Profiler recording and select the local Editor (guid=-1); --target editor does not switch the connection. ManagedObjects+NativeObjects, no GC/screenshot. New `.aibridge/memory/*.snap` only, no overwrite or disk-space preflight. Capture may stall Editor and expose sensitive runtime heap data. Timeout is unknown; a late callback can finish only in the original generation before release. Domain reload/restart restores active records as unknown/busy and retains the shared Profiler/Memory gate; no callback reconstruction or replay. Query the original requestId, inspect retained files, then release_snapshot --requestId <original-request-id> --confirm true only for unknown. Release is local, not native cancellation or proof capture stopped; never delete journals or use code execute to bypass capture guards. load_snapshot is unsupported; use the Unity Memory Profiler window manually for analysis. No package install or arbitrary UI opening";

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
