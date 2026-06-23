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
- 面向用户的工作流配置说明
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

- `KnowledgeBaseIndex.md`：AIBridge 功能总索引，汇总 CLI、Editor 菜单、Runtime、workflow、Skills、模板、生成产物和文档映射，作为后续迭代的统一校验入口
- `WorkflowsPanel.md`：`AIBridge/Workflows` 面板的产品定义文档
- `WorkflowGraphPanel.md`：`AIBridge/Workflow Graph` 高级面板方案，定义用行为树风格图形展示 workflow 分支、recipe、run、gate 和 handoff 的产品边界；同名展示页为 `WorkflowGraphPanel.html`
- `workflow-guide/README.md`：AIBridge Workflow 设计、需求讨论分支、`.aibridge/plan` 工作底稿、正式文档同步与证据校验总览；`README.md` 负责索引，完整展示页为 `index.html`
- `workflow-guide/AIBridgeLoopsAnalysis.md`：AIBridge loops / FSM 分析、官方资料对照、缺口与优化建议
