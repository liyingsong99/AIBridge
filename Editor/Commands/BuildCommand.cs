using System;

namespace AIBridge.Editor
{
    /// <summary>原生Player构建入口；长操作先返回已持久化的队列身份。</summary>
    public sealed class BuildCommand : ICommand
    {
        public string Type => "build";
        public bool RequiresRefresh => false;
        public string SkillDescription => @"### `build` - Native Player build with persistent report

```bash
$CLI build run --dry-run true
$CLI build run --scenes '[""Assets/Scenes/Main.unity""]' --dry-run false --confirm true
$CLI build status --requestId <original-request-id>
$CLI build report --requestId <original-request-id>
$CLI build release --requestId <unknown-request-id> --confirm true
```

Uses current active target only; no target/settings changes or overwrite. Output: `.aibridge/builds/<buildId>/`; records: `.aibridge/build-operations/`. Run defaults to validation; confirmed build queues then blocks the Editor main thread. CLI status/report read bounded pure DTOs directly while Editor is busy. Timeout/reload/unknown never retries BuildPlayer. Release only frees registered unknown state, not cancellation. Report success requires a validated completed Succeeded report; counts are not inferred from missing fields.";

        public CommandResult Execute(CommandRequest request)
        {
            try
            {
                switch (request.GetParam("action", "status"))
                {
                    case "run": return AIBridgeBuildTracker.Start(request);
                    case "status": return AIBridgeBuildTracker.Query(request, false);
                    case "report": return AIBridgeBuildTracker.Query(request, true);
                    case "release": return AIBridgeBuildTracker.Release(request);
                    default: return CommandResult.Failure(request.id, "Unknown build action. Supported: run, status, report, release.");
                }
            }
            catch (Exception)
            {
                return CommandResult.Failure(request.id, "Build validation/state operation failed; no automatic retry was made. Inspect the original status before resubmitting.");
            }
        }
    }
}
