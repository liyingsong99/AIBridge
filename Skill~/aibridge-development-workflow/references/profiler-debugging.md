# Profiler 调试按需规则

## 触发条件

只在用户明确涉及 Profiler、性能瓶颈、FPS、帧耗时、hitch、GC、内存、渲染或脚本耗时时读取本文件

## 工具选择

- 常规热点报告：优先 `$CLI workflow run-cli --recipe performance-hotspot-investigation --inputs ".aibridge/workflows/perf-inputs.json" --timeout 30000`
- Editor / Play Mode Profiler 证据：优先 `$CLI profiler <action>`
- Player / Runtime 目标证据：优先 `$CLI runtime perf --target latest --duration 5s --interval 100ms`
- 多目标或平台差异：结合 `runtime-target-sweep` 或 `runtime-debug-investigation` recipe，并保留 target id、URL、日志、截图和 perf artifact
- 函数级脚本耗时、深度渲染分解或 Unity 原生 Profiler timeline：使用 Unity Profiler UI、Performance Tests 或项目专用采样；AIBridge v1 只返回稳定派生统计

## 常用命令

```bash
$CLI profiler start
$CLI profiler get_status
$CLI profiler list_modules
$CLI profiler capture_frame
$CLI profiler get_memory_stats
$CLI profiler get_rendering_stats
$CLI profiler get_script_stats
$CLI profiler save_data --path ".aibridge/profiler/latest.json"
$CLI profiler load_data --path ".aibridge/profiler/latest.json"
$CLI profiler stop
```

详细参数读取 `profiler-api-reference.md`

## 证据规则

- 报告中区分 Editor Profiler 证据和 Runtime perf 证据
- `unsupported` 表示当前目标或 Unity API 不稳定/不可用，不等同命令失败
- `save_data` / `load_data` 使用 AIBridge JSON 快照，不承诺兼容 Unity 原生 `.data` Profiler 文件
- Runtime 不支持打开 Profiler 窗口、切换 Unity Profiler 模块、清除 Editor Profiler 帧历史或读取 Editor Profiler 数据
