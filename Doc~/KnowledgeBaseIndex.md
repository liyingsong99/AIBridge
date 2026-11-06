# AIBridge 知识库文档索引

## 状态

- 状态：当前索引
- 更新时间：2026-11-06
- 对象 ID 展示：CLI help / Skill 按 Unity 版本只暴露主键参数名（6000.4+ 为 `entityId`，更旧为 `instanceId`）；兼容策略只在 RootRule 写一行，不在每个 Skill reference / CLI action help 重复；线协议仍双接受
- 维护范围：`Packages/cn.lys.aibridge`（知识库维护规则仅写在包内 `AGENTS.md` / 本文，不得写入 RootRule 或用户项目 `AGENTS` 模板）

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
| Unity Editor | `AIBridge/Setup`、`AIBridge/Settings`、`AIBridge/Workflows`、`AIBridge/Players`、`AIBridge/Screenshot Game View _F12`、`AIBridge/Record GIF _F11` | 首次设置向导、基础设置、工作流配置、Runtime 目标查看和截图快捷入口 |
| CLI | `focus`、`dialog`、`asset`、`batch`、`build`、`code`、`code_index`、`compile`、`editor`、`gameobject`、`gameview`、`get_logs`、`harness`、`input`、`inspector`、`menu_item`、`memory`、`multi`、`prefab`、`profiler`、`runtime`、`scene`、`screenshot`、`selection`、`test`、`transform`、`workflow`、`compile dotnet` | 当前 AIBridge CLI 的真实命令面 |
| Runtime Bridge | `Runtime/AIBridgeRuntime.cs`、`Runtime/Transports/*`、`runtime status/logs/screenshot/perf/handlers/call` | 连接已编译 Player 或 Play Mode 目标，HTTP 为默认控制面，File transport 为 HTTP 未运行时的兼容回退，采集证据和调用白名单 handler |
| Workflow | `Tools~/AIBridgeCLI/Workflow/*`、`Templates~/Workflows/*.aibridge-workflow.json`、`workflow list/validate/plan/init/begin/status/report/finish/import/export/clean` | 负责 recipes、run manifest、artifact、gate、report 和外部结果导入 |
| Skills | `Skill~/aibridge-development-workflow`、`Skill~/aibridge-workflow-orchestration`、`Skill~/aibridge-code-index`、`Skill~/aibridge-prefab-patch`、`Skill~/aibridge-batch-script`、`Skill~/unity-yaml-editing` | 分别覆盖 workflow 短入口/分支路由、编排、轻量声明名检索、Prefab patch、批处理和 YAML 兜底 |
| 文档 | `Doc~/README.md`、`Doc~/SetupWizard.md`、`Doc~/NativeBuild.md`、`Doc~/NativeProfiling.md`、`Doc~/EditorWindowScreenshot.md`、`Doc~/WorkflowsPanel.md`、`Doc~/workflow-guide/README.md`、`Doc~/workflow-guide/ContextCompression.md`、`Doc~/workflow-guide/AIBridgeLoopsAnalysis.md` | 功能目录、首次设置向导、原生构建与诊断产物、Editor 窗口截图、面板定位、workflow 说明、上下文压缩策略和 FSM 分析 |
| 模板 | `Templates~/Rules/AIBridge.RootRule.md`、`Templates~/ProjectRules/AGENTS*.md`、`Templates~/Workflows/*.json` | RootRule、项目规则模板和内置 workflow recipes |
| 生成与缓存 | `.aibridge/harness/capabilities.json`、`.aibridge/test-runs/`、`.aibridge/pipeline-test-runs/`、`.aibridge/pipeline-compile-requests/`、`.aibridge/workflows/active-run.json`、`.aibridge/workflows/runs/`、`.aibridge/code-index/snapshot/`、`.aibridge/code/`、`.aibridge/profiler/native/`、`.aibridge/memory/`、`.aibridge/memory-operations/`、`.aibridge/tmp/`、`.aibridge/plan/`、`.aibridge/local-test-projects.md` | 当前项目的能力快照、测试队列状态、运行产物、代码索引、临时代码、原生诊断文件与捕获日志、Agent/脚本短生命周期输出、方案底稿及本机 Unity 测试工程路径/同步状态说明 |
| Editor 实例状态 | `%LOCALAPPDATA%/AIBridge/instances/<projectKey>/editor-instance.json`（可用 `AIBRIDGE_STATE_DIR` 覆盖根目录） | Editor 心跳与 `focus` 多实例解析；高频状态写在项目外，避免触发 Git 工作区监听；CLI 仍兼容读取遗留 `.aibridge/editor-instance.json` |

## CLI 功能目录

### Unity 和 Editor

- `editor`
- `compile unity`：默认native验证；显式Pipeline `start --confirm true`只确认一次触发应答并保持busy，`status --requestId`仅本地原记录，`release --requestId --confirm true`仅释放本地门；不带ID的Pipeline status仍为无归属全局观察，见 `PipelineDiagnostics.md`
- `build run/status/report/release`：原生 Player 构建，默认验证，实际逐次确认；纯 DTO/原子状态，CLI status/report 不依赖主线程响应；见 `NativeBuild.md`
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
- `profiler`：旧 `save_data/load_data` 保持 AIBridge JSON；新 `native_capabilities/save_profile/load_profile` 保存/加载原生 `.data`，逐次确认、停录、专用路径、不覆盖；load 固定替换共享 history，见 [原生诊断产物](NativeProfiling.md)
- `memory capture_snapshot/snapshot_status/release_snapshot/load_snapshot`：本地 Editor 内置 `.snap` 捕获，原子意图/异步回调、unknown 不重放；当前实现阻止 Play Mode，且没有体积估算/磁盘空间预检；release 仅本地，分析 load 当前结构化 unsupported（可人工用 Unity Memory Profiler 窗口打开），见 [原生诊断产物](NativeProfiling.md)

### 搜索、分析和临时代码

- `code_index`
- `code`

### 工作流和基础设施

- `unity_cli`（`version` / `capabilities` / `doctor`）与 `pipeline capabilities`，可选官方 CLI 只读诊断
- `workflow`
- `harness`
- `runtime`
- `batch`
- `multi`
- `focus`
- `dialog`
- `compile dotnet`

## Editor 菜单

### `AIBridge/Setup`

首次使用设置向导（`AIBridgeSetupWindow`）：

- 语言选择
- AI 工具多选（Codex、Claude Code、Cursor、DeepSeek Harness、ZCode、WorkBuddy；更多：Cline）
- 写入预览、安装选项与结果回显
- 关闭 / “稍后配置”等价于完成引导并执行一次默认安装

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

### `AIBridge/Install Unity CLI Dependencies`

- 按当前 Editor 的 UPM 兼容版本安装可选 `com.unity.pipeline`，不安装独立 Unity CLI；要求 Unity 6.0 LTS+。
- 已安装则保留；异步查询/安装、重复点击防护、域重载后核对实际版本；未知写结果不自动重放。
- 详见双语 `UnityCliIntegration.md` / `UnityCliIntegration.en.md`。

### 其他入口

- `AIBridge/Players`
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

## Unity CLI 可选增强

- 用户文档：[简体中文](UnityCliIntegration.md) / [English](UnityCliIntegration.en.md)；测试设计：[UnityCliTesting.md](UnityCliTesting.md)
- AI 按需参考：`Skill~/references/unity-cli-reference.md`，覆盖可选依赖、AIBridge 诊断、官方 Editor 连接检查、安装授权边界和结果安全规则；安装器会同步到已选助手的 Skill 根，跳过 `.meta` 并保留生成/用户文件；不扩充 RootRule 为官方命令全集
- 配置：`.aibridge/unity-cli.json`（`backend=native|auto|unity-cli`、`executablePath`、`probeTimeoutMs`）；默认 `native`
- 首轮公开入口：`unity_cli version`、`unity_cli capabilities`、`unity_cli doctor`、`pipeline capabilities`；仅只读调用官方版本与帮助，不提供任意命令转发；`unity_cli help` 为本地命令帮助
- `code execute --dry-run true` 默认原生编译；显式 `--backend pipeline` 在相同授权、来源限制和 single-flight 下调用 Pipeline 0.10.0-exp.1 eval；执行必须 `--dry-run false --confirm true`，未确认在提交前拒绝。`.csx` 读取为原始 code；双层回执/HTTP 一致性和有界严格 JSON 校验，解码后键值脱敏与碰撞拒绝；结构化结果（含显式 null）/耗时与终态持久化到 `.aibridge/code-operations/`，缺失索引恢复单请求占位；上游 output 为空时不承诺日志捕获，超时/取消/reload 不重放；`code status --requestId` 查询，unknown 占位只由显式 cancel 释放。`code run --file --entry --args` 已接入固定 Pipeline ephemeral `script run`，默认只编译（不验证入口/参数），确认执行/快照/unknown 语义沿用共享通道；只接受直属 .cs 和有界 JSON 数组，拒绝链接/任意转发。unknown 保留 `.aibridge/tmp/pipeline-run` 临时源码与当前进程只读锁，reload 后不保证锁。显式 `code run --job true --confirm true` 已接固定 detached 提交/原子意图与jobId绑定、同世代串行GET/取消和独立业务终态。此模式上游执行无超时，timeout仅本地等待；`code status`读最近记录，`job_list`仅本地登记，`job_wait`有界观察，`job_cancel --requestId --confirm true`仅同世代已绑定活动作业且不代表回滚。IO/404/身份变化/坏响应保持unknown，不重复POST；reload不重建会话。默认job=false/eval语义不变。hotpatch未实现。P3首片新增 `compile status --backend pipeline` 与 `test list --backend pipeline` 严格只读诊断；编译全局状态不关联本次操作，缺失计数/耗时显式null；新增受限EditMode `test run --backend pipeline --pipeline-filter --filter-type`，单次POST/本地日志归属/严格业务计数；`test status --backend pipeline --requestId`仅本地，`test release --backend pipeline --requestId --confirm true`只释放unknown且不停止远端。native队列/真实UTF probe/Build/code/native capture共同门禁，缺探针失败关闭；零测试/全跳过不算pass。Pipeline编译start仅单次应答确认，所有ack保持busy、原IDstatus/release仅本地、不认证本次结果；异步test/remote cancel未开放且无native fallback，见 [PipelineDiagnostics.md](PipelineDiagnostics.md)。P2a已新增独立原生build API/纯DTO与状态恢复，见 `NativeBuild.md`；P2b/P2c已新增独立原生 Profiler `.data` 与 Memory `.snap` 命令，真实文件验收仅Unity6000.0.51f1，三版本核心编译通过；2019测试无有效程序集，6000.6空场景/可选adapter测试跳过，不代表跨版本原生产物或整体所有验收项完成；详见 [NativeProfiling.md](NativeProfiling.md)。
- 根 `available` 表示本次请求能力已确认；`doctor/capabilities` 需 CLI 身份、版本与根帮助探测通过，`pipeline capabilities` 还需 Pipeline 帮助探测通过
- `auto` 首轮只参与诊断与能力报告，既有 `compile unity`、`test run`、`code execute` 和 Runtime 命令保持原生路由
- 缺少 CLI/Pipeline 不影响原生功能；显式官方请求报告能力缺失，已发送或结果未知的操作不得换后端重放
- 可选 Pipeline 适配按包存在生成宏（versionDefines 空 expression）并做运行时检测；校验 descriptor PID/项目路径/Unity版本；`serverReady` 仅在后台 bearer-auth `/api/status` 返回 ready 时为 true；`evalToken` 不暴露。
- 2026-11-04 已实装 CLI 1.0.0-beta.16 / Pipeline 0.10.0-exp.1，在 Unity 6000.0.51f1 验证当前工程服务 ready 与 CLI 能力；其他版本与业务委托仍需独立验证

## 当前对齐结论

- `README.md` / `README_CN.md` 面向用户，按能做什么、使用优势、快速开始、常用 API、实践示例、可选增强组织。首页不展开内部工作流程、Skills 路径表、适配层或验证版本记录；设置与实现细节通过专题文档访问，进阶示例见 `Doc~/CommandExamples.md` / `CommandExamples.en.md`。
- README 明确 Unity CLI/Pipeline 非强制依赖：原生能力独立可用；安装后补充官方环境管理和实时 Editor 工具，当前 AIBridge 仅集成诊断与可选适配，业务命令不会自动转发。Unity 2019.4+ 基础兼容与 Pipeline 的 Unity 6.0 LTS+ 要求分别说明。
- `AIBridge/Workflows` 仍然是用户配置面板，不是 recipe/run/debug 控制台。
- `Doc~/WorkflowsPanel.md` 只定义默认 `Workflows` 面板的产品边界。
- `compile dotnet` 只是额外检查，不是 Unity 编译替代品。
- `editor` 的状态查询 action 是 `get_state`（不是 `status`）；CLI builder 已列出该 action，调用方应以 `$CLI editor --help` 的 action 列表为准。
- `code execute` 的临时编译引用来自当前 AppDomain 已加载程序集，不会凭命名空间自动补齐任意 Unity 模块；涉及引擎类型时先确认程序集已加载，必要时按完整类型名反射。原生编译器兼容旧 Roslyn 布局，并探测新版 `DotNetSdk/sdk/<version>/Roslyn/bincore/csc.dll`，按 SDK 版本降序选择，保留 Unity 2019 的优先路径。
- Skill 安装不把带生成标记的包内命令参考快照覆盖到目标，命令参考由当前 Editor 按版本重建；无变化时不写文件，避免 Unity 6000.4+ 对象 ID 参数名导致重装误报更新。Pipeline 先核验持久状态再探测可选 adapter，缺包不能掩盖损坏日志/busy 占位；恢复的失败回执不等于重放任务。本批修复以 1.6.1 发布；补丁后三版本编译/定向复验及6000.0全量基线比较见 `UnityCliTesting.md`，保留实测时 1.6.0 的版本记录；missing-latest测试使用隔离输入，生产恢复保守校验不变。2019.4首次导入曾出现CSharpNameParser异常，后续未复现，按用户决定暂不单独排查，保留记录且不视为根因已关闭。
- `test run` 必须在 Editor 处于 Edit Mode 时启动；若当前处于 Play Mode，会直接失败并明确提示先退出 Play Mode。
- `test run` 在并发请求下会持久化队列到 `.aibridge/test-runs/state.json`；已确认的 run 若后续变成 `unknown`，CLI 会快速失败并提示状态丢失，而不是一直等完整 timeout。
- `screenshot editor_window` 可在 Edit Mode 截取 Unity 主窗口或按活动窗口、类型、标题、对象 ID 定位的 `EditorWindow`；Unity 6000.4+ 对象标识走 `GetEntityId`/`EntityId`（`instanceId` 为兼容别名）；Windows 主路径使用 WGC，被 WGC 拒绝的 Unity 浮动容器仅回退到带非空校验的窗口级 PrintWindow，macOS 使用 ScreenCaptureKit，不回退到桌面截图，默认成功数据仅返回 `path`、`width`、`height`。
- Unity 6000.5+：`GetInstanceID` 与带 `FindObjectsSortMode` 的查找 API 会触发编译错误或弃用；包内统一经 `AIBridgeObjectIdentity` / `AIBridgeObjectQuery` 做版本分支。
- AI 面对象 ID：CLI help 与 Skill `command-reference` 只按当前 Unity 版本展示主键参数名（6000.4+ `entityId`，更旧 `instanceId`）；对象 ID 兼容策略仅出现在 RootRule 一行，Skill/CLI help 不重复整段说明；线协议继续双接受。
- `code_index` 是默认关闭的只读轻量入口：`symbol`、`definition` 用于 C# 声明名定位，`status`、`doctor` 用于诊断；后续分析由 AI 直接读取 `.cs` 文件完成。Unity 已导入资源名称/类型查找继续使用 `asset search/find --format paths`，其中 `asset search <keyword>` 是 `asset search --keyword <keyword>` 的兼容简写。
- `workflow run-cli` 不会自动执行 `agent` / `manual`，这些步骤仍需要外部执行器回流。
- RootRule 必须简洁写明 `$CLI` 指向项目本地 AIBridge CLI 路径，并给出 PowerShell 调用方式；包维护用知识库规则不得写入 RootRule 或用户项目 `AGENTS` 模板。
- `workflow run-cli` 和 `compile dotnet` 对子进程 stdout/stderr 使用有界流式读取；输出超限不会再使 CLI 内存随原始输出量线性增长。
- `aibridge-development-workflow` 使用短入口，Harness 采用 compact gate；Preflight / Skill Routing 只作为内部选路步骤，对外默认直接进入业务分支输出；完整探测矩阵、fallback、resume 和证据 schema 移入 `harness-readiness-detail.md` 按需加载。
- Skill 和 RootRule 的 AI 规则 Markdown 不保留普通规则行末尾的 `。` / `.`；源模板、安装副本和生成器输出保持一致
- Runtime HTTP transport 正常运行时不再轮询 file command 目录，也不为 HTTP command 默认写 result 文件；旧 File transport 仅作为 HTTP 未运行时的兼容回退路径。
- Runtime HTTP command 成功、timeout、Runtime not-ready 和 CLI cleanup 都会关闭 pending id，迟到的 HTTP 异步结果不会回落到 file result 落盘。
- Runtime heartbeat 默认间隔为 5 秒；Editor 实例心跳同样为 5 秒；File heartbeat stale 判定保留 15 秒窗口，不会因默认心跳降频触发误判。
- Editor 实例心跳写在项目外用户状态目录（Windows 默认 `%LOCALAPPDATA%/AIBridge/instances/<projectKey>/editor-instance.json`，可用 `AIBRIDGE_STATE_DIR` 覆盖）；`.aibridge` 只保留命令交换与项目产物；禁用 AIBridge 后不再刷新心跳；CLI `focus` 优先读外部路径，并兼容遗留 `.aibridge/editor-instance.json`。
- Runtime UI snapshot/find 默认不做逐按钮 raycast；需要遮挡诊断时显式传入 `includeRaycastDetails=true`。
- HTTP Runtime quick health probe 使用 500ms 基础超时和有限退避重试；本地端口扫描与缓存目标探测保持单次短时预算；自动发现遵循 `runtime-config.json` 的 `discovery.udpPort`；CLI 与 Editor Players 面板共用 discovery cache，均通过跨进程锁串行更新、合并仍新鲜的历史目标并原子发布，因此并发 CLI/`AIBridge/Players` 发现不会互相覆盖远程或手机 Player；自动发现失败时回退已有新鲜缓存，保证 `--target latest` 稳定；HTTP 命令队列上限为 128，重复活动/近期 id 返回 409、队列满返回 429，health/status 暴露当前队列深度。
- Agent 和脚本的短生命周期输出统一写入 `.aibridge/tmp/`，由 Cache 清理按保留期清理；兼容清理仅处理过期的顶层 `tmp_*` / `tmp-*` 文件，不会扫描或删除其他顶层产物。

## Code Index 收口

- 当前公开动作：`symbol`、`definition`、`status`、`doctor`；前两项用于 C# 声明名定位，后两项用于诊断
- 推荐使用方式：先用 `symbol` 或 `definition` 找到候选 `.cs` 文件，再直接读取文件分析上下文和真实行为
- 内部生命周期：`warmup`、`reset`、`build_snapshot` 仅供内部生命周期使用，不是普通 CLI 或 AI 用户能力
- 非目标：引用、调用者、继承、实现、全工程关系图，以及注释、文本或非 C# 查询

## 当前已知漂移

- 暂无已确认漂移。若 README、Skill、模板或 CLI 输出出现不一致，先核实当前代码和命令输出，再更新本索引与对应专题文档。
