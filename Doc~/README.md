# AIBridge 功能文档

这里是包级功能文档的统一目录。

## 目的

- 将面向用户的功能目标、边界和演进规则集中维护。
- 降低产品意图、界面行为和实际实现之间的漂移。
- 让后续功能变更始终更新同一份稳定依据，而不是依赖代码逆向理解或聊天记录回溯。

## 收录范围

这里应放置以下类型的文档：

- 功能规格说明
- 面板或窗口的产品定义
- 面向用户的工作流配置说明与使用教程（命令示例，不复制完整 CLI 参数参考）
- 变更功能前应先阅读的稳定行为约束

这里不应用于存放：

- 已经放在 `Skill~/references` 下的底层 CLI 命令说明
- 临时笔记
- 临时调试日志
- 不打算长期维护的实现草稿

## 更新规则

当面向用户的功能发生变化时，应在同一次改动中同步更新对应文档。

每一份功能文档至少应回答以下问题：

1. 这个功能是做什么的
2. 这个功能是给谁用的
3. 这个功能必须做什么
4. 这个功能明确不做什么
5. 哪些内容应放到单独的 admin 或 debug 面板

如果代码和文档出现不一致，不要默认接受这种漂移。应当二选一：

- 让实现重新对齐文档
- 在产品决策明确确认后更新文档

## 当前文档

- [Pipeline 编译观察与受限测试](PipelineDiagnostics.md)：显式后端、nullable计数、受限EditMode同步运行、原请求本地状态与unknown释放

- [中文 README](../README_CN.md) / [English README](../README.md)：产品概览、快速开始、可选 Unity CLI 增强与常用操作
- [命令示例与进阶用法](CommandExamples.md) / [English](CommandExamples.en.md)：从 README 迁出的完整操作示例；精确参数以 CLI help 和已安装 Skill references 为准
- `KnowledgeBaseIndex.md`：AIBridge 功能总索引，汇总 CLI、Editor 菜单、Runtime、workflow、Skills、模板、生成产物和文档映射，作为后续迭代的统一校验入口
- [原生 Player 构建](NativeBuild.md)：`build run/status/report/release`，确认/路径/纯 DTO、主线程阻塞期间 CLI 查询与 unknown 不重放；含本机真实失败报告及未验证范围
- [原生 Profiler 文件与 Memory 快照](NativeProfiling.md)：新增 `profiler native_capabilities/save_profile/load_profile` 与 `memory capture_snapshot/snapshot_status/release_snapshot`；保留旧 JSON 命令，区分共享 history 替换、异步捕获、unknown/迟到回调、本地 release，以及 `load_snapshot` unsupported；真实文件验收仅覆盖 Unity 6000.0.51f1
- [Unity CLI 可选增强](UnityCliIntegration.md) / [English](UnityCliIntegration.en.md)：只读官方 CLI 诊断、配置、能力检测，以及显式 Pipeline eval/run 的授权、源码快照、detached作业查询/合作取消和 unknown 不重放边界；[测试覆盖](UnityCliTesting.md) 区分契约测试与真实 Editor 验证，包含 CLI beta.16 / Pipeline 0.10 在 Unity 6000.0.51f1 的安装实测
- `SetupWizard.md`：`AIBridge/Setup` 首次设置向导的产品定义、首启行为、工具路径与跳过/重开规则
- `EditorWindowScreenshot.md`：Edit Mode 下 Unity 主窗口、内置窗口和自定义 `EditorWindow` 的窗口级截图边界、参数与平台要求
- `WorkflowsPanel.md`：`AIBridge/Workflows` 面板的产品定义文档
- `workflow-guide/README.md`：AIBridge Workflow 设计、需求讨论分支、`.aibridge/plan` 工作底稿、正式文档同步与证据校验总览；`README.md` 负责索引，完整展示页为 `index.html`
- `workflow-guide/ContextCompression.md`：AIBridge workflow / Skill 上下文压缩规则，说明短入口、按需 Harness 细节、生成版分流规则和 invariant 测试维护要求
- `workflow-guide/AIBridgeLoopsAnalysis.md`：AIBridge loops / FSM 分析、官方资料对照、缺口与优化建议

## Runtime 性能边界

- HTTP 是 Runtime 默认控制面。HTTP command 结果只经内存 pending 通道返回，不默认写入 file transport 的 `results` 目录；file transport 命令仍保留 result 文件兼容路径。
- HTTP command 在成功读取、timeout、Runtime not-ready 或 CLI cleanup 后都会关闭 pending id，迟到异步结果会被丢弃，避免结果文件和缓存继续增长。
- Runtime file transport 只在 HTTP transport 未运行时轮询命令目录，并限制最低轮询间隔，避免 10Hz 常驻目录扫描。
- `runtime.ui.snapshot` / `runtime.ui.find` 默认只返回可点击坐标和基础按钮状态；需要逐按钮 raycast 诊断时传入 `includeRaycastDetails=true`。
