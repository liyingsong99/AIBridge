<!-- AIBRIDGE:GENERATED COMMAND REFERENCE - DO NOT EDIT MANUALLY -->
# AIBridge Command Reference

此文件由 AIBridge 自动生成。需要修改命令说明时，请修改对应 ICommand 的 SkillDoc/SkillDescription。
`$CLI` 表示当前平台的 AIBridge CLI 调用方式，Windows 项目通常是 `./.aibridge/cli/AIBridgeCLI.exe`。

### `profiler` - Editor Profiler Diagnostics

Use only for performance/Profiler debugging. The command returns AIBridge JSON snapshots; `save_data` / `load_data` do not read or write Unity native `.data` Profiler files.

```bash
$CLI profiler start
$CLI profiler get_status
$CLI profiler list_modules
$CLI profiler enable_module --module "Memory" --enabled true
$CLI profiler capture_frame
$CLI profiler get_memory_stats
$CLI profiler get_rendering_stats
$CLI profiler get_script_stats
$CLI profiler save_data --path ".aibridge/profiler/latest.json"
$CLI profiler load_data --path ".aibridge/profiler/latest.json"
$CLI profiler stop
$CLI profiler native_capabilities
$CLI profiler save_profile --path ".aibridge/profiler/native/capture.data" --confirm true
$CLI profiler load_profile --path ".aibridge/profiler/native/capture.data" --confirm true
```

Native `.data` actions use exact Unity ProfilerDriver APIs; recording must already be stopped. Save never overwrites; load replaces shared history. No automatic recording, target switch or JSON fallback. Unknown native outcome retains the local gate until domain reload. Memory `.snap` uses the separate `memory` command.

Unsupported or unstable Unity Profiler surfaces are reported in the `unsupported` array instead of being silently treated as available. Runtime Player performance evidence still uses `runtime perf`.
