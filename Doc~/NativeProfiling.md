# 原生 Profiler 文件与 Memory 快照

这两组命令直接使用当前 Unity Editor 的原生 API，不依赖官方 Unity CLI、Pipeline 或 Memory Profiler 包。它们用于保存已有 Profiler history 和捕获本地 Editor 内存证据，不提供自动性能分析、Player 捕获或跨版本文件转换。

## 选择正确的文件格式

| 入口 | 产物/用途 | 主要边界 |
|---|---|---|
| `profiler save_data/load_data` | AIBridge JSON 诊断快照 | 保留既有语义，不是 Unity 原生 `.data`，不加载原生 history |
| `profiler native_capabilities` | 原生 API 签名与当前连接状态 | 只读探测；能力可用不代表 history 非空或本次 IO 一定成功 |
| `profiler save_profile/load_profile` | Unity Profiler `.data` | 显式确认、停止录制，load 替换共享 history |
| `memory capture_snapshot/snapshot_status/release_snapshot` | Unity Memory `.snap` 及持久操作状态 | 本地 Editor、异步捕获、unknown 不重放；release 仅本地释放 |
| `memory load_snapshot` | 预留的快照分析入口 | 当前结构化返回 `unsupported`，没有已核实的包级分析加载 API |

`.data` 与 `.snap` 是不同产物，不能互换。快照可能含对象名称、托管字符串和其他敏感堆数据；确认前应评估磁盘空间、暂停影响与分享范围。

## Profiler：保存与加载现有 history

```powershell
$CLI = './.aibridge/cli/AIBridgeCLI.exe'
& $CLI profiler native_capabilities
& $CLI profiler save_profile --confirm true
# 使用上一步实际返回的 path；加载会替换当前共享 history
& $CLI profiler load_profile --path '.aibridge/profiler/native/<saved-file>.data' --confirm true
```

### 前置条件与确认

- save/load 均要求本次请求的字面布尔值 `--confirm true`。
- 必须确认原生 `ProfilerDriver` 的 recording 为 false；AIBridge JSON 中的运行状态不能替代该检查。
- Editor 不得处于 Play Mode 或切换中、编译、资源更新、Player 构建，以及 AIBridge build/code 活动或 unknown 状态；原生 Profiler/Memory 共用 gate 必须空闲。
- 命令不自动开始或停止录制、不清空 history、不改 deep profiling、不切连接目标，也不自动打开 Profiler 窗口。
- 保存需要已有原生 history。请先在 Unity Profiler 中录制所需 Editor 数据，再停止录制并确认当前连接。`profiler start` 或一次 AIBridge JSON 采样不保证已经积累可保存的 Editor history；空 history 不能被当作保存成功。

### 路径、签名与文件回执

`save_profile` 默认生成随机、直属的 `.aibridge/profiler/native/<guid>.data`。自定义 `--path` 也必须为该目录的直属 `.data` 文件，拒绝重解析点/符号链接祖先、目录穿越和已有目标，不提供覆盖。`load_profile` 要求同一目录内已有、非空且不超过 2 GiB 的文件。

适配器只接受精确静态签名 `SaveProfile(string)` 返回 `void` 或 `bool`，以及 `LoadProfile(string, bool)` 返回 `bool`；没有匹配签名则报告 unsupported，不猜测重载、不回退 JSON。原生 SaveProfile 实际会独占打开输出，因此调用期间不持有托管文件句柄；使用随机新建暂存文件，返回后重新打开并核验文件身份、非空长度，最后不覆盖发布。`void` 返回本身不构成成功，`bool false`、空文件或文件共享/身份校验失败均不能成功。

成功数据包含 `path`、`size`、`format=unity-profiler-data`、`unityVersion`、`currentTargetGuid`、`currentTargetName` 与 `historyProvenance=unknown`，不包含文件正文。当前连接只是调用时的观察，不能据此证明 history 中每帧的来源。

### 加载副作用与失败

加载固定调用 `LoadProfile(path, false)`，即替换而非追加当前 Editor 的共享 Profiler history。成功也不代表完成性能分析；失败、返回 false 或异常不能证明旧 history 未改变，更不提供回滚。

原生调用抛出且无法确认返回时，结果可为 `native_outcome_unknown`，保留原生 gate 和相关临时资源；文件未发布不等于没有副作用。检查原请求及保留路径，勿自动再次保存/加载，也不要用旧 JSON 命令重放原生操作。外部 Profiler UI 和其他插件不受 AIBridge 进程内 gate 控制。

## Memory：捕获本地 Editor 快照

```powershell
& $CLI memory capture_snapshot --confirm true --target editor --timeout 30000
# 保存第一次回执的原始 requestId；排队成功不是捕获完成
& $CLI memory snapshot_status --requestId '<original-requestId>'
# 仅在核实 unknown 后，按精确原 ID 显式释放本地占位
& $CLI memory release_snapshot --requestId '<unknown-requestId>' --confirm true
```

### 捕获范围与开销

- 捕获逐次要求字面布尔值 `--confirm true`；只支持 `--target editor`，且当前原生连接必须为本地 Editor（guid=-1），不自动切目标。Player 目标明确 unsupported。
- 同样要求原生 Profiler 停止录制；Play Mode/切换、编译、资源更新、build/code 活动或 unknown、其他原生 Profiler/Memory 操作均会阻止捕获。
- 精确探测内置 `TakeSnapshot(string, Action<string,bool>, CaptureFlags)`，兼容已知新旧命名空间。固定 flags 为 `ManagedObjects | NativeObjects`；不开放任意 flags，不主动 GC 或截图，不自动安装包。
- “不主动 GC”不表示 Unity 原生捕获无内部整理、额外内存或暂停。捕获可能长时间阻塞 Editor，文件也可能很大；不要把回调式接口理解为无卡顿或可强停。

### 产物与异步状态

输出默认是 `.aibridge/memory/<guid>.snap`；可选 path 也必须为该目录直属、32位小写十六进制 GUID 文件名及 `.snap` 扩展名。不覆盖已有文件，拒绝路径穿越和重解析点。使用独立暂存文件，只有匹配的成功回调和非空文件通过校验后才不覆盖发布。

原始 requestId、operationId、项目、Editor PID/世代、目标与路径绑定到 `.aibridge/memory-operations/`。提交前先原子记录意图；同一 requestId 不会再次调用原生 API。日志损坏、不可读或存在未解决 busy 状态时保守阻止新捕获，不能当作空闲。

| 状态 | 含义 |
|---|---|
| `queued` | 已登记意图，等待 Editor update；尚未证明原生调用开始 |
| `capturing` | 原生调用已提交，等待回调和发布 |
| `completed` | 已确认成功回调、非空文件与状态发布；`result=true`、`busy=false` |
| `failed` | 已确认失败结果；不能当作成功文件 |
| `unknown` | 超时、reload、身份/路径/持久化异常等导致无法确认；保持 busy，禁止自动重放 |

原生回调只入队，Editor update 负责校验与单次终结，陈旧或重复回调不能覆盖其他请求。`snapshot_status` 的外层 success 表示查询成功，捕获结果要看操作记录的 `status/result/busy`，不能仅凭退出码或文件存在判断完成。status 本身仍经 Editor 命令队列；主线程被原生捕获阻塞时，不承诺即时响应。

### timeout、迟到结果与 release

`--timeout` 接受 1000–60000ms，只限制本地等待，不向原生 API 发送取消。CLI capture 构造器默认向 Editor 传入 30000ms；当前通用 CLI 传输缺省等待仍为 5000ms，因此示例显式指定 `--timeout 30000`，同时固定两层预算。直接构造 Editor 请求且省略 timeout 时，不应套用 CLI 默认值。

等待耗尽可能成为 `unknown` 并保留 busy、原始回调与临时文件。同一世代、未 release 的迟到回调仍可完成原请求；应查询原始 requestId，不提交第二次 capture。Editor reload/restart 不重建原生 delegate、不重放捕获，旧活动记录恢复 unknown；日志不能证明原生工作已停止。

`release_snapshot` 只接受精确登记的 unknown requestId 和 `--confirm true`，不释放 queued/capturing 或已完成记录。它只将本地占位标记为 released；`nativeCancellationConfirmed=false`，不停止原生操作、不删除或回滚副作用，也不把 unknown 改成成功。release 后的迟到回调不再发布为该请求的完成结果。仍须核实原生工作和保留文件，不能把本地可再次排队当作远端安全保证。

### 快照分析不等于捕获

```powershell
& $CLI memory load_snapshot --path '.aibridge/memory/<snapshot-guid>.snap' --confirm true
```

该入口当前返回结构化 unsupported：没有已核实的 Memory Profiler 包/版本分析加载 API。它不自动安装依赖、不猜测 OpenSnapshot、不打开任意 UI，也不解析 `.snap` 文件内容。没有 Memory Profiler 包不影响内置捕获能力；已生成 `.snap` 也不等于 AIBridge 已分析其内容。

## 本机验收与未验证范围

2026-11-05，Unity 6000.0.51f1 的真实验收已保存并加载 `.aibridge/profiler/native/acceptance-20261105.data`，文件大小 3,343,124 bytes。早期空 history 和文件共享失败均未被报告为成功，随后使用已有 Editor history 及符合原生独占打开要求的保存流程成功。

同日 Memory 原始请求 `cmd_1793856449783_9a2a866c78bf4c` 已观察到 `completed/result=true/busy=false`，产物大小 1,559,242,224 bytes。这只是当前工程的实际大小，不是体积或耗时上限。

真实 `.data` save/load 和 `.snap` capture 证据仅覆盖上述 Unity 6000.0 Editor。Unity 2019.4.40f1c1、当前 6000.0.51f1 与 6000.6.0a7 已完成含P3同步测试核心的编译复验；不能据此宣称三版本原生文件实测、跨版本文件互读或其他平台验收通过。null 投影、部分写入恢复与日志容量修复已纳入当前工程回归；6000.6空工程仍有依赖saved scene的fixture跳过，2019.4隔离工程未发现测试程序集（0项不计通过）。此文不宣称整份增强方案完成。

最新测试、最终编译与剩余 gate 以项目实施底稿 `.aibridge/plan/native-profiler-memory-implementation.md` 及 `.aibridge/plan/pipeline-api-enhancement-analysis.md` 的最终证据为准；不以本文固化一个会随后续修复变化的测试总数。
