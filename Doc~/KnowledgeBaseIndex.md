# AIBridge 知识库文档索引

## 状态

- 状态：当前索引
- 更新时间：2026-06-23
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
| CLI | `focus`、`dialog`、`asset`、`batch`、`code`、`code_index`、`compile`、`editor`、`exec`、`gameobject`、`gameview`、`get_logs`、`harness`、`input`、`inspector`、`menu_item`、`multi`、`prefab`、`profiler`、`runtime`、`scene`、`screenshot`、`selection`、`test`、`text_index`、`transform`、`workflow`、`compile dotnet` | 当前 AIBridge CLI 的真实命令面 |
| Runtime Bridge | `Runtime/AIBridgeRuntime.cs`、`Runtime/Transports/*`、`runtime status/logs/screenshot/perf/handlers/call` | 连接已编译 Player 或 Play Mode 目标，采集证据和调用白名单 handler |
| Workflow | `Tools~/AIBridgeCLI/Workflow/*`、`Templates~/Workflows/*.aibridge-workflow.json`、`workflow list/validate/plan/init/begin/status/report/finish/import/export/clean` | 负责 recipes、run manifest、artifact、gate、report 和外部结果导入 |
| Skills | `Skill~/aibridge-development-workflow`、`Skill~/aibridge-workflow-orchestration`、`Skill~/aibridge-code-index`、`Skill~/aibridge-prefab-patch`、`Skill~/aibridge-batch-script`、`Skill~/unity-yaml-editing` | 分别覆盖 workflow 路由、编排、语义检索、Prefab patch、批处理和 YAML 兜底 |
| 文档 | `Doc~/README.md`、`Doc~/WorkflowsPanel.md`、`Doc~/WorkflowGraphPanel.md`、`Doc~/workflow-guide/README.md`、`Doc~/workflow-guide/AIBridgeLoopsAnalysis.md` | 功能目录、面板定位、workflow 说明和 FSM 分析 |
| 模板 | `Templates~/Rules/AIBridge.RootRule.md`、`Templates~/ProjectRules/AGENTS*.md`、`Templates~/Workflows/*.json` | RootRule、项目规则模板和内置 workflow recipes |
| 生成与缓存 | `.aibridge/harness/capabilities.json`、`.aibridge/workflows/active-run.json`、`.aibridge/workflows/runs/`、`.aibridge/text-index/`、`.aibridge/code-index/snapshot/`、`.aibridge/code/`、`.aibridge/plan/` | 当前项目的能力快照、运行产物、文本索引、代码索引、临时代码和方案底稿 |

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

- `text_index`
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

`workflow list` 当前确认有 8 个内置 recipes：

1. `bug-hunter-loop`
2. `harness-readiness-check`
3. `prefab-asset-sweep`
4. `runtime-debug-investigation`
5. `runtime-target-sweep`
6. `runtime-ui-validation`
7. `unity-change-implementation`
8. `unity-sharded-review`

对应定位：

- `bug-hunter-loop`：迭代采集证据、验证候选原因、只做一个确认修复
- `harness-readiness-check`：先探测 harness、workflow、Unity 和 Runtime 能力
- `prefab-asset-sweep`：并行检查多个 Prefab / Scene / asset，再串行应用批准写入
- `runtime-debug-investigation`：收集 Editor / Runtime 证据并输出诊断
- `runtime-target-sweep`：汇总 Runtime target 健康状态
- `runtime-ui-validation`：验证 Play Mode / Player UI 路径
- `unity-change-implementation`：实施局部 Unity / AIBridge 改动并验证
- `unity-sharded-review`：分片审查并做对抗验证

## 当前对齐结论

- `AIBridge/Workflows` 仍然是用户配置面板，不是 recipe/run/debug 控制台。
- `AIBridge/Workflow Graph` 是独立高级入口，不应并回默认面板。
- `compile dotnet` 只是额外检查，不是 Unity 编译替代品。
- `code_index` 仍是默认关闭的只读语义入口。
- `workflow run-cli` 不会自动执行 `agent` / `manual`，这些步骤仍需要外部执行器回流。

## 当前已知漂移

- `README.md` 和 `README_CN.md` 旧版 recipes 列表少了 `harness-readiness-check`，已在本次同步修正。

