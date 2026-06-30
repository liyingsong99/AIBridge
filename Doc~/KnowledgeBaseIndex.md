# AIBridge 知识库文档索引

## 状态

- 状态：当前索引
- 更新时间：2026-06-26
- 维护范围：`Packages/cn.lys.aibridge`

## 目的

这里不是功能方案页，而是 AIBridge 的功能总索引。

它的目标很直接：

- 防止功能迭代时漏同步文档
- 防止 README、Skill、模板和代码入口出现分叉
- 给后续开发一个固定的校验入口

## 维护规则

1. 功能变更先看代码和 CLI 输出，再更新本文档。
2. 新增入口要同步到本文档、专题文档和对应 Skill/模板。
3. 若文档与实现冲突，以当前代码、CLI 和 `workflow list` 输出为准。
4. 本文档只负责索引和对齐，不重复展开实现细节。

## 功能总览

| 层级 | 已确认入口 | 说明 |
|---|---|---|
| Unity Editor | `AIBridge/Settings`、`AIBridge/Workflows`、`AIBridge/Players`、`AIBridge/Workflow Graph`、`AIBridge/Screenshot Game View _F12`、`AIBridge/Record GIF _F11` | 覆盖基础设置、工作流配置、Runtime 目标查看、工作流图和截图快捷入口 |
| CLI | `focus`、`dialog`、`asset`、`batch`、`code`、`code_index`、`compile`、`editor`、`exec`、`gameobject`、`gameview`、`get_logs`、`harness`、`input`、`inspector`、`menu_item`、`multi`、`prefab`、`profiler`、`runtime`、`scene`、`screenshot`、`selection`、`test`、`transform`、`workflow`、`compile dotnet` | 当前 AIBridge CLI 的真实命令面 |
| Runtime Bridge | `Runtime/AIBridgeRuntime.cs`、`Runtime/Transports/*`、`runtime status/logs/screenshot/perf/handlers/call` | 连接已编译 Player 或 Play Mode 目标，HTTP 为默认控制面，File transport 为 HTTP 未运行时的兼容回退，采集证据和调用白名单 handler |
| Workflow | `Tools~/AIBridgeCLI/Workflow/*`、`Templates~/Workflows/*.aibridge-workflow.json`、`workflow list/validate/plan/init/begin/status/report/finish/import/export/clean` | 负责 recipes、run manifest、artifact、gate、report 和外部结果导入 |
| Skills | `Skill~/aibridge-development-workflow`、`Skill~/aibridge-workflow-orchestration`、`Skill~/aibridge-code-index`、`Skill~/aibridge-prefab-patch`、`Skill~/aibridge-batch-script`、`Skill~/unity-yaml-editing` | 分别覆盖 workflow 短入口/分支路由、编排、语义检索、Prefab patch、批处理和 YAML 兜底 |
| 文档 | `Doc~/README.md`、`Doc~/WorkflowsPanel.md`、`Doc~/WorkflowGraphPanel.md`、`Doc~/workflow-guide/README.md`、`Doc~/workflow-guide/ContextCompression.md`、`Doc~/workflow-guide/AIBridgeLoopsAnalysis.md` | 功能目录、面板定位、workflow 说明、上下文压缩策略和 FSM 分析 |
| 模板 | `Templates~/Rules/AIBridge.RootRule.md`、`Templates~/ProjectRules/AGENTS*.md`、`Templates~/Workflows/*.json` | RootRule、项目规则模板和内置 workflow recipes |
| 生成与缓存 | `.aibridge/harness/capabilities.json`、`.aibridge/test-runs/`、`.aibridge/workflows/active-run.json`、`.aibridge/workflows/runs/`、`.aibridge/code-index/snapshot/`、`.aibridge/code/`、`.aibridge/plan/` | 当前项目的能力快照、测试队列状态、运行产物、代码索引、临时代码和方案底稿 |

## CLI 功能目录

### Unity 和 Editor

- `editor`
- `compile unity`
- `get_logs`
- `test`
- `asset`
- `scene`
- `gameobject`
- `transform`
- `selection`
- `inspector`
- `prefab`
- `menu_item`
- `gameview`
- `screenshot`
- `input`
- `profiler`

### 搜索、分析和临时代码

- `code_index`
- `code`

### 工作流和基础设施

- `workflow`
- `harness`
- `runtime`
- `batch`
- `multi`
- `exec`
- `focus`
- `dialog`
- `compile dotnet`

## Editor 菜单

### `AIBridge/Settings`

当前页签：

- Basic
- GIF
- Logs
- Directories
- Scripts
- Runtime
- Code Index
- Cache
- Actions

### `AIBridge/Workflows`

当前页签：

- Skills
- Recommended Library
- Workflow Options

### 其他入口

- `AIBridge/Players`
- `AIBridge/Workflow Graph`
- `AIBridge/Screenshot Game View _F12`
- `AIBridge/Record GIF _F11`

## 内置 Recipes

`workflow list` 当前确认有 9 个内置 recipes：

1. `bug-hunter-loop`
2. `harness-readiness-check`
3. `performance-hotspot-investigation`
4. `prefab-asset-sweep`
5. `runtime-debug-investigation`
6. `runtime-target-sweep`
7. `runtime-ui-validation`
8. `unity-change-implementation`
9. `unity-sharded-review`

对应定位：

- `bug-hunter-loop`：迭代采集证据、验证候选原因、只做一个确认修复
- `harness-readiness-check`：先探测 harness、workflow、Unity 和 Runtime 能力
- `performance-hotspot-investigation`：一键采集 Editor Profiler、Runtime 日志/截图/perf 证据并输出热点排查报告
- `prefab-asset-sweep`：并行检查多个 Prefab / Scene / asset，再串行应用批准写入
- `runtime-debug-investigation`：收集 Editor / Runtime 证据并输出诊断
- `runtime-target-sweep`：汇总 Runtime target 健康状态
- `runtime-ui-validation`：验证 Play Mode / Player UI 路径
- `unity-change-implementation`：实施局部 Unity / AIBridge 改动并验证
- `unity-sharded-review`：分片审查并做对抗验证

## 当前对齐结论

- `README.md` 和 `README_CN.md` 已明确标注 Unity 2019.4+ ~ 6000.x 兼容范围，避免用户误读为不支持 Unity 6000.x。
- `AIBridge/Workflows` 仍然是用户配置面板，不是 recipe/run/debug 控制台。
- `AIBridge/Workflow Graph` 是独立高级入口，不应并回默认面板。
- `compile dotnet` 只是额外检查，不是 Unity 编译替代品。
- `test run` 必须在 Editor 处于 Edit Mode 时启动；若当前处于 Play Mode，会直接失败并明确提示先退出 Play Mode。
- `test run` 在并发请求下会持久化队列到 `.aibridge/test-runs/state.json`；已确认的 run 若后续变成 `unknown`，CLI 会快速失败并提示状态丢失，而不是一直等完整 timeout。
- `code_index` 仍是默认关闭的只读语义入口；daemon 启动默认执行语义预热，也可用 `warmupMode=light` 保留延迟 Roslyn 构建；默认忽略 `Unity.*` 程序集，以及 `Library/PackageCache/com.unity.*` / `Packages/com.unity.*` 源码路径，被排除源码程序集仍作为 metadata reference 保留。
- `workflow run-cli` 不会自动执行 `agent` / `manual`，这些步骤仍需要外部执行器回流。
- RootRule 必须简洁写明 `$CLI` 指向项目本地 AIBridge CLI 路径，并给出 PowerShell 调用方式。
- `exec run --stdin` / `exec batch --stdin` 只面向外部 host 工具；`harness status` 这类 AIBridge 命令直接调用。stdin 契约是 JSON 请求，`command` 只放可执行文件名，`args` / `queries` / `globs` / `paths` 承载参数。包含引号、反斜杠或正则等转义敏感内容时，优先用 PowerShell 对象 `ConvertTo-Json` 或 `--request-file`，不要手写 inline JSON 字符串。
- `aibridge-development-workflow` 使用短入口，Harness 采用 compact gate；完整探测矩阵、fallback、resume 和证据 schema 移入 `harness-readiness-detail.md` 按需加载。
- Runtime HTTP transport 正常运行时不再轮询 file command 目录，也不为 HTTP command 默认写 result 文件；旧 File transport 仅作为 HTTP 未运行时的兼容回退路径。
- Runtime HTTP command 成功、timeout、Runtime not-ready 和 CLI cleanup 都会关闭 pending id，迟到的 HTTP 异步结果不会回落到 file result 落盘。
- Runtime heartbeat 默认间隔为 2 秒；File heartbeat stale 判定保留 15 秒窗口，不会因默认心跳降频触发误判。
- Runtime UI snapshot/find 默认不做逐按钮 raycast；需要遮挡诊断时显式传入 `includeRaycastDetails=true`。

## 当前已知漂移

- 暂无已确认漂移。若 README、Skill、模板或 CLI 输出出现不一致，先核实当前代码和命令输出，再更新本索引与对应专题文档。
