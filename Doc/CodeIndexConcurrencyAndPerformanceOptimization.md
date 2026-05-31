# Code Index 并发与性能优化方案

## 背景

当前 Code Index 已经具备常驻 daemon、Unity snapshot、语义查询和文本 fallback 能力。实测中，热态串行查询稳定，但冷态或多个重型语义查询并发进入 daemon 时，客户端可能在默认 HTTP timeout 内等待失败。

本方案目标是：

- 让并发语义查询进入可观测、可控的队列，而不是让调用方看到随机 timeout。
- 减少多次查询的 CLI 进程和 HTTP 往返开销。
- 借鉴 `codedb-mcp` 的 warm process、批量请求、懒加载 sidecar 和内存塑形策略，但保留 AIBridge 基于 Unity/Roslyn snapshot 的精确语义能力。
- 明确每个阶段的验收标准，便于拆分实现和回归测试。

参考仓库：

- `killop/codedb-mcp`：https://github.com/killop/codedb-mcp
- 调研 commit：`2712a1cc224bf6a05c8743a0ec5caa0550598dc2`
- 重点参考：`README.md` benchmark/architecture、`src/tools.rs`、`src/cache.rs`、`src/watcher.rs`

## 当前实现判断

| 模块 | 当前实现 | 风险 |
|---|---|---|
| daemon 并发入口 | `CodeIndexServer` 每个 TCP client 使用独立任务处理 | 请求能并发进入 |
| 语义查询执行 | `_queryGate = SemaphoreSlim(1, 1)` 串行执行 | 已有隐式排队，但不可观测 |
| CLI 等待策略 | `HttpClient.Timeout` 使用 CLI timeout | 排队等待和执行耗时混在一起，冷态容易超时 |
| workspace 加载 | warmup 先加载轻量 snapshot，语义 workspace 按需加载 | 首个语义查询承担大部分冷启动成本 |
| refresh | 后台构建新 workspace，短暂持有 `_queryGate` 原子替换 | 方向正确，但缺少队列指标 |
| 批量查询 | 无 `code_index batch` | 多个独立 CLI 调用重复进程和 HTTP 开销 |
| 内存策略 | snapshot name/token index、Roslyn workspace、语义 symbol table 按需加载 | token/result/路径字符串仍可进一步拆分和压缩 |

## codedb-mcp 可借鉴策略

| 策略 | codedb-mcp 做法 | AIBridge 是否借鉴 | 本仓落点 |
|---|---|---|---|
| warm process | MCP 进程持有已加载索引，warm 查询走内存 | 借鉴 | 继续强化 Code Index daemon，不改为一次性进程 |
| 读缓存 + 构建锁 | `RwLock<HashMap<project, Arc<Codebase>>>` 加 `build_lock` | 借鉴 | workspace generation 原子替换，构建和查询边界更清晰 |
| 批量工具 | `codedb_bundle` 最多执行 100 个工具 | 借鉴 | 增加 `code_index batch --stdin` |
| 查询批处理 | `codedb_search`、`codedb_callers` 支持数组参数 | 借鉴 | `symbol/references/callers/definition` 支持 batch payload |
| watcher debounce | 文件变动后 debounce，再判断内容变化并重建 | 部分借鉴 | Unity snapshot auto-refresh 增加 debounce 和合并 |
| 懒加载 sidecar | BM25、word hits、callers、deps、embedding 等按需加载 | 借鉴思想 | token postings、caller/reference 结果、symbol table 可 sidecar 化 |
| 内存塑形 | 去掉常驻全文、重复路径、重复 kind/lang 字符串 | 借鉴 | 内部使用 file id/string table，响应时再物化路径 |
| tree-sitter 多语言解析 | 使用 tree-sitter 统一抽取声明 | 不建议 | AIBridge 的核心价值是 Unity/Roslyn 精确语义，不能降级 |
| vector/graph/Louvain | 自然语言搜索、模块图、社区发现 | 暂不建议 | 超出 Code Index 当前定位，后续独立评估 |

## 并发队列方案

### 总体原则

1. `/status` 和 `/shutdown` 不进入语义查询队列，必须始终快速响应。
2. 语义查询默认串行执行，先保证稳定性；后续再审计可并行的只读路径。
3. 队列状态必须写入 status，调用方能知道是排队、执行还是 workspace 加载导致变慢。
4. timeout 必须拆分为排队 timeout 和执行 timeout，不再把所有等待都归因于 HTTP timeout。
5. 同一 snapshot generation 下的相同请求应合并等待者，避免并发重复查询。

### 调度器设计

新增 `CodeIndexQueryScheduler`，替代 `CodeIndexServer.ExecuteQueryAsync()` 中直接等待 `_queryGate` 的方式。

核心结构：

```text
CodeIndexServer
  /status  -> 直接返回 status + queue stats
  /query   -> CodeIndexQueryScheduler.EnqueueAsync(...)
  /batch   -> CodeIndexQueryScheduler.EnqueueBatchAsync(...)

CodeIndexQueryScheduler
  bounded queue
  active request
  duplicate in-flight map
  short-lived result cache
  single semantic worker
```

建议请求模型：

| 字段 | 说明 |
|---|---|
| `requestId` | daemon 内部生成，便于日志和 status 追踪 |
| `action` | `symbol/definition/references/implementations/derived/callers/diagnostics` |
| `parameters` | 原 query 参数 |
| `generationHash` | 当前 snapshot content hash |
| `enqueuedAt` | 入队时间 |
| `queueTimeoutMs` | 最大排队等待时间 |
| `executeTimeoutMs` | 最大执行时间 |
| `priority` | `normal/high/background`，第一阶段可只保留字段 |
| `cancellationToken` | 客户端断开或 timeout 时取消等待 |

建议 status 增加字段：

| 字段 | 说明 |
|---|---|
| `queueLength` | 当前待执行请求数 |
| `queueCapacity` | 队列容量 |
| `activeRequestId` | 当前执行请求 |
| `activeAction` | 当前执行 action |
| `activeStartedAt` | 当前请求开始执行时间 |
| `lastQueuedMs` | 最近一次请求排队耗时 |
| `lastExecutionMs` | 最近一次请求执行耗时 |
| `totalQueued` | daemon 生命周期累计入队数 |
| `totalCompleted` | daemon 生命周期累计完成数 |
| `totalTimedOut` | daemon 生命周期累计超时数 |
| `totalDeduplicated` | 合并等待的请求数 |

### timeout 策略

CLI 默认 timeout 建议按 action 调整：

| action | 默认 timeout | 原因 |
|---|---:|---|
| `status` | 1500ms | 只做状态探测 |
| `doctor` | 5000ms | 可能探测 daemon 和 snapshot |
| `warmup` | 30000ms | 冷态启动可能较慢 |
| `symbol` | 10000ms | 首次可能加载语义 symbol table |
| `definition` | 15000ms | 通常只需目标 assembly |
| `references` | 30000ms | 可能加载 reverse dependencies |
| `callers` | 30000ms | `SymbolFinder.FindCallersAsync` 较重 |
| `implementations` | 30000ms | 类型查询可能扩大 workspace |
| `derived` | 30000ms | 类型派生查询可能较重 |
| `diagnostics` | 30000ms，`--all` 60000ms | full diagnostics 可能很重 |
| `batch` | `max(child timeout sum, 30000ms)`，上限可配 | 批量执行需要更长窗口 |

错误信息必须区分：

- `queue_timeout`：请求未开始执行就超时。
- `execute_timeout`：请求已开始执行但超过执行限制。
- `queue_full`：队列已满，拒绝入队。
- `client_cancelled`：调用方断开或取消。

## Batch 方案

增加 `code_index batch --stdin`，一次提交多个 query。

输入示例：

```json
{
  "items": [
    { "action": "symbol", "parameters": { "query": "PlayerSystem" } },
    { "action": "definition", "parameters": { "file": "Packages/Foo.cs", "line": 12, "column": 20 } }
  ],
  "timing": true,
  "continueOnError": true
}
```

约束：

- 默认最多 100 项。
- 禁止 batch 嵌套。
- 默认串行执行，复用同一个 workspace 和同一次 HTTP 连接。
- 每项返回独立 `success/error/queuedMs/executionMs/items`。
- `continueOnError=false` 时遇到失败立即停止。

收益：

- 减少多个 CLI 进程启动成本。
- 减少 HTTP 往返。
- 在同一个 daemon worker 内复用已加载的 semantic workspace。
- 便于 agent 一次性提交多个 symbol/reference 查询。

## 内存优化方案

### 第一阶段：可观测与短期缓存

新增小型 LRU/TTL 结果缓存：

| 缓存项 | key | 建议 TTL |
|---|---|---:|
| symbol | `snapshotHash + action + query` | 5 分钟 |
| definition | `snapshotHash + file + line + column` | 5 分钟 |
| references | `snapshotHash + file + line + column` | 5 分钟 |
| callers | `snapshotHash + file + line + column` | 5 分钟 |
| implementations | `snapshotHash + type` | 5 分钟 |
| derived | `snapshotHash + type` | 5 分钟 |

缓存限制：

- 只缓存成功响应。
- 单条响应过大时不缓存。
- snapshot hash 变化立即失效。
- status 暴露 `queryCacheCount/queryCacheHits/queryCacheMisses`。

### 第二阶段：token index sidecar

当前 token document index 首次使用时可能把所有 token index 文件合并进内存。可参考 `codedb-mcp` 的 `word_index.bin + word_hits.bin`：

- 保存 token 到 postings offset 的小索引。
- hits 单独落盘。
- 查询 token 时只读取目标 token 的 hits。
- 常驻内存只保留 token -> offset/length，或使用按页缓存。

目标：

- 避免一次性构建全量 `Dictionary<string, HashSet<string>>`。
- 热 token 查询仍保持快速。
- 大项目 daemon 长驻内存更稳定。

### 第三阶段：内部结构压缩

建议将 daemon 内部常驻对象从“直接保存字符串”逐步改为 id 化：

| 当前常驻数据 | 优化方向 |
|---|---|
| 重复 file path 字符串 | file table + file id |
| 重复 symbol kind/name/container | string interning 或 symbol table |
| `CodeIndexItem` 内部直接存路径 | 内部存 file id，响应时再物化 |
| 语义 symbol table 全量常驻 | 按 snapshot hash 缓存，可配置上限或 sidecar |
| Roslyn source text | 尽量使用按需 TextLoader，避免无关文档提前持有全文 |

## 刷新与 debounce

现有后台 refresh 方向正确：先构建新 workspace，再短暂持锁替换。建议补充：

1. auto-refresh 触发时先 debounce，例如 1000-1500ms。
2. 同一时间只保留一个 pending refresh。
3. refresh 构建期间继续使用旧 workspace 服务查询。
4. 新 workspace 完成后只在 swap 时短暂进入独占区。
5. refresh 失败时保留旧 workspace，并在 status 中记录 `backgroundRefreshFailed`。

## 分阶段落地计划

| 阶段 | 内容 | 文件范围 |
|---|---|---|
| P0 | 固化并发压测脚本和基线结果 | `Tests` 或临时验证脚本 |
| P1 | 新增显式 FIFO scheduler、queue metrics、queue timeout | `Tools~/AIBridgeCodeIndex/CodeIndexServer.cs`，新增 scheduler/model 文件 |
| P2 | CLI action-specific timeout 和更清晰错误输出 | `Tools~/AIBridgeCLI/Commands/CodeIndexCommand.cs` |
| P3 | daemon `/batch` 与 CLI `code_index batch --stdin` | CLI + daemon model |
| P4 | LRU/TTL query result cache | daemon scheduler/workspace 层 |
| P5 | token postings sidecar 懒加载 | `CodeIndexWorkspace.cs`、snapshot utility |
| P6 | 审计后开放有限只读并发 | scheduler + workspace cache lock |

推荐提交拆分：

1. queue + status metrics。
2. CLI timeout/error message。
3. batch。
4. result cache。
5. token sidecar。
6. optional read concurrency。

不要把 P1-P6 合并成一次大改。

## 验收标准

### P1 并发队列验收

| 场景 | 标准 |
|---|---|
| 10 个并发 `references/callers/implementations` | 默认参数下全部成功，不出现 HTTP timeout |
| 30 个并发轻量 `symbol` | 无 crash，无 daemon 失联，status 始终可访问 |
| 队列可观测 | `code_index status` 返回 `queueLength/activeAction/lastQueuedMs/lastExecutionMs` |
| status 响应 | 查询排队期间 `status` 本地调用应小于 200ms |
| 队列上限 | 超过容量时返回明确 `queue_full`，不无限堆积 |
| 排队超时 | 人为设置低 `queueTimeoutMs` 时返回 `queue_timeout` |
| 客户端取消 | 调用方断开后未执行请求可从队列移除 |
| refresh 并发 | refresh 构建期间查询继续使用旧 workspace；swap 不破坏正在执行的查询 |

### P2 CLI timeout 验收

| 场景 | 标准 |
|---|---|
| 默认 `symbol` 冷态首查 | 不因 5s 默认 timeout 失败 |
| 默认重型查询 | `references/callers/derived/implementations` 默认 timeout 至少 30s |
| 用户显式 `--timeout` | 显式参数优先级高于 action 默认值 |
| 错误信息 | 能区分 `queue_timeout`、`execute_timeout`、daemon 不可达、workspace not ready |

### P3 Batch 验收

| 场景 | 标准 |
|---|---|
| 20 个 symbol batch | 比 20 个独立 CLI 调用更快 |
| 100 项 batch | 正常执行或按上限截断，并返回清晰提示 |
| batch 中单项失败 | `continueOnError=true` 时后续项继续执行 |
| 禁止嵌套 | batch 内调用 batch 返回明确错误 |
| timing | 每个子项包含 `queuedMs/executionMs`；单项响应包含 `totalLatencyMs` |

### P4 结果缓存验收

| 场景 | 标准 |
|---|---|
| 相同 query 重复执行 | 第二次命中 cache，`executionMs` 明显降低 |
| snapshot hash 变化 | 旧 cache 自动失效 |
| 大响应 | 超过大小阈值不缓存 |
| status | 暴露 cache hit/miss/count 指标 |

### P5 内存 sidecar 验收

| 场景 | 标准 |
|---|---|
| daemon 启动后只 status/warmup | 不加载全量 token postings |
| 首次 token 查询 | 只读取目标 token hits 或分页 postings |
| 大项目长驻 | 私有内存低于旧实现，且无明显查询回退 |
| snapshot 兼容 | schema version 增加，旧 snapshot 能明确提示重建 |

### P6 只读并发验收

仅在完成线程安全审计后执行。

| 场景 | 标准 |
|---|---|
| N 个只读查询并发 | 结果与串行一致 |
| refresh swap | swap 期间不出现 disposed workspace 访问 |
| lazy cache | 所有 lazy cache 均有线程安全保护 |
| 压测 | 连续 100 轮并发查询无异常、无结果漂移 |

## 验证命令

在 AIBridge 仓库中执行：

```powershell
dotnet build .\Tools~\AIBridgeCLI\AIBridgeCLI.csproj -o "$env:TEMP\aibridge-cli-build" -nologo
dotnet build .\Tools~\AIBridgeCodeIndex\AIBridgeCodeIndex.csproj -o "$env:TEMP\aibridge-codeindex-build" -nologo
git diff --check
```

在 Unity 验证项目中执行：

```powershell
.\.aibridge\cli\AIBridgeCLI.exe code_index status
.\.aibridge\cli\AIBridgeCLI.exe code_index doctor
.\.aibridge\cli\AIBridgeCLI.exe code_index warmup
```

并发验证建议覆盖：

```powershell
# 示例：并发触发多个重型语义查询，验证全部成功且 status 可观测。
# 具体脚本应固定到 Tests 或 Doc 附录中，避免每次手写。
```

如改动同步进入 Unity 项目包，还需要在目标 Unity 项目执行：

```powershell
.\.aibridge\cli\AIBridgeCLI.exe compile unity
.\.aibridge\cli\AIBridgeCLI.exe get_logs --logType Error
```

## 复测待优化清单（2026-05-31）

本节记录基于 `C:\lys-work\ET9-SNMSL` Unity 验证项目的复测结论，便于后续按项修复和回归测试。

复测环境：

- AIBridge 当前源码临时构建到 `%TEMP%\aibridge-perf-current`。
- 验证项目 snapshot：127 个 assembly，3691 个 source file，Unity 6000.0.51f1。
- 构建命令：`dotnet build .\Tools~\AIBridgeCLI\AIBridgeCLI.csproj -o "$env:TEMP\aibridge-perf-current" -nologo`。
- 构建命令：`dotnet build .\Tools~\AIBridgeCodeIndex\AIBridgeCodeIndex.csproj -o "$env:TEMP\aibridge-perf-current\CodeIndex" -nologo`。

### 当前复测结论

| 场景 | 复测结果 | 结论 |
|---|---:|---|
| CLI / CodeIndex 构建 | 0 error | 通过 |
| 普通 `code_index warmup` | 超过 60s 未返回，但 daemon 已启动 | 未达标 |
| 冷态默认 `symbol PlayerSystem` | `execute_timeout`，10000ms | 未达标 |
| 重复 `symbol PlayerSystem` | `cacheHit=true`，`executionMs=0` | 通过 |
| 20 个独立 `symbol` | 8452ms，20/20 成功 | 基线 |
| 20 个 `symbol` batch | 730ms，20/20 成功 | 通过，约 11.6x 更快 |
| 30 并发 `symbol` | 超过 90s，残留多个 `AIBridgeCodeIndex.exe` | 未达标 |
| 临时进程清理 | 压测后需手动清理 | 未达标 |

### 待优化列表

| 优先级 | 待优化项 | 复测证据 | 修复目标 |
|---|---|---|---|
| P0 | 修复并发 CLI 启动多个 `AIBridgeCodeIndex.exe` | 30 并发 `symbol` 后残留多个 daemon，status 只指向其中一个 | 同一 `projectRoot` 任意并发 CLI 下只能有 1 个有效 daemon |
| P0 | 增加 daemon 启动跨进程互斥 | 并发 CLI 疑似同时判定 daemon 不可用并各自启动 | 使用 named mutex 或 file lock；拿锁后必须二次检查 status |
| P0 | 修复普通 `code_index warmup` 不返回 | `warmup` 超过 60s 未返回，但 daemon 已 ready | daemon ready 后 CLI 稳定返回；建议本地 warmup < 5s |
| P0 | 增加 orphan daemon 清理 | 压测后需要手动杀临时 daemon | `reset` / `warmup` 能清理同一 projectRoot 的旧 marker 和 orphan 进程 |
| P1 | 修正 `queuedMs` 指标语义 | 响应中的 `queuedMs` 接近总耗时，不是纯排队耗时 | `queuedMs` 只表示排队等待；如需总耗时新增 `totalLatencyMs` |
| P1 | 调整冷态 `symbol` 默认策略 | 默认 `symbol PlayerSystem` 仍在 10s 超时 | 冷态默认 `symbol` 不失败；可提高 action timeout 或 warmup 预加载 semantic workspace |
| P1 | 优化 `status` 响应耗时 | 多次 `status.executionTime` 约 230-300ms | 若验收口径是 CLI，本地 `code_index status` 热态应 < 200ms |
| P1 | 固化 30 并发回归脚本 | 当前靠手工/临时脚本复现 | 固化到 `Tests` 或 `Doc` 附录，自动检查成功率、daemon 数量、status 可访问性 |
| P2 | 补齐队列边界用例 | `queue_full`、`queue_timeout`、client cancel 尚未稳定覆盖 | 增加可控慢查询或测试钩子，验证错误码和队列清理 |
| P2 | 补齐 10 重型并发用例 | P1 多 daemon 问题阻塞了稳定验证 | daemon 生命周期修复后，复测 10 个 `references/callers/implementations` 默认参数全成功 |
| P3 | 保留 batch 性能回归 | 20 batch 约 730ms，20 独立调用约 8452ms | batch 必须持续显著快于独立 CLI 调用 |
| P3 | 保留 cache 性能回归 | 重复 query `cacheHit=true` 且 `executionMs=0` | snapshot hash 不变时重复查询稳定命中 cache |

### 建议修复顺序

1. 先修 `warmup` 不返回和 daemon 启动互斥，确保单项目只有一个 daemon。
2. 再修 orphan daemon 清理，避免失败压测污染后续数据。
3. 修正 `queuedMs` 指标语义，否则后续性能分析会误判排队耗时。
4. 调整冷态 `symbol` 默认 timeout 或 warmup 预加载策略。
5. 固化 30 并发 symbol、20 symbol batch、cache hit、10 重型并发四组回归脚本。

### 修复后必须复测

```powershell
dotnet build .\Tools~\AIBridgeCLI\AIBridgeCLI.csproj -o "$env:TEMP\aibridge-perf-current" -nologo
dotnet build .\Tools~\AIBridgeCodeIndex\AIBridgeCodeIndex.csproj -o "$env:TEMP\aibridge-perf-current\CodeIndex" -nologo

# 目标 Unity 项目
$CLI = "$env:TEMP\aibridge-perf-current\AIBridgeCLI.exe"
& $CLI code_index reset --project-root C:\lys-work\ET9-SNMSL
& $CLI code_index warmup --project-root C:\lys-work\ET9-SNMSL
& $CLI code_index status --project-root C:\lys-work\ET9-SNMSL

# 必须额外检查：
# 1. 30 并发 symbol 全部成功。
# 2. 临时目录下 AIBridgeCodeIndex.exe 数量始终为 1。
# 3. status 在并发期间仍可访问。
# 4. 20 symbol batch 明显快于 20 次独立 CLI。
# 5. 重复 query 命中 cache，executionMs 明显降低。
```

## 最终建议

优先落地 P0：修复 daemon 启动互斥、`warmup` 有界返回和 orphan daemon 清理。否则并发复测会被多 daemon 污染，P1/P2 指标也无法稳定判断。

第二批落地 P1：修正 `queuedMs` 语义，新增 `totalLatencyMs`，并调整冷态 `symbol` 默认策略。

第三批固化 P1/P2/P3 回归脚本：30 并发 symbol、20 symbol batch、cache hit、队列边界和 10 重型并发。P4-P6 属于性能深化，应基于新的 queue metrics 和压测数据逐步推进。
