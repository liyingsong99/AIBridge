# 任务分流规则

本文件是 source Skill 的 fallback。安装到 assistant Skill 目录后，`AIBridge/Workflows` 会根据 `project-workflow-preferences.md` 生成同名文件；生成版本只列启用分支，并以项目偏好为准

## 工作流生命周期

```text
Preflight / Skill Routing
  -> Mode Enter
  -> Mode Execute
  -> Mode Exit / SkillHandoff / Release
  -> Transition Preflight
```

- Preflight / Skill Routing 是入口步骤，不是业务模式；它只选择主分支并计算 Skill 状态
- 项目侧能力以 Root Rule / preferences 为准；勿用 harness status 做 enablement 预检
- 只有运行时任务命令失败、降级、阻塞，或能力状态改变工具选择时，才在当前业务分支输出中简短补充工具策略
- 如果需求边界、验收标准或方案方向不清晰，先进入需求讨论分支，确认后再继续正式分支选择
- Mode Enter 只激活当前分支真正需要的 Skill，并读取该分支文档
- Mode Exit 生成 `SkillHandoff`，并释放下一模式不需要的模式专用 Skill

## 需求讨论分支

需求讨论分支是 Preflight 的前置分支，不是可选主分支。需求不清晰、边界待定、方案分歧，或用户要求先分析/先确认时触发

- 目标是收敛目标、边界、非目标、约束、方案选项和确认结论
- 方案写入规则见 `references/branches/requirements.md`（默认 `.aibridge/plan/<slug>.md`）

## 可选主分支

下表是 source fallback；安装后的生成版会按 `EnableCodeIndex` 从 Skills 列表中移除 `aibridge-code-index`。以生成版与 Root Rule 为准

| 主分支 | 触发信号 | 默认目标 | 进入后读取 | 常用 Skills / 工具 |
|---|---|---|---|---|
| 实施分支 | 创建、修改、修复、重构、生成、迁移、提交 | 改动当前工作树并验证 | `references/branches/implementation.md` | `aibridge、aibridge-code-index、aibridge-prefab-patch、unity-yaml-editing、aibridge-batch-script` |
| 调试诊断分支 | 排查、诊断、复现、为什么、追踪、日志、Runtime、Player、Play Mode、性能、UI 异常 | 收集证据并给出根因判断 | `references/branches/debug.md` | `aibridge、aibridge-code-index、aibridge-workflow-orchestration、aibridge-batch-script` |
| 审查分支 | review、audit、检查风险、设计评审、只读分析 | 输出 confirmed findings 和剩余风险 | `references/branches/review.md` | `aibridge-code-index、按需 aibridge-workflow-orchestration` |
| 验证分支 | 编译、日志、截图、测试、Runtime/UI 验证、回归确认 | 给出可重复验证结果 | `references/branches/validation.md` | `aibridge、现有 workflow recipe` |
| 编排分支 | workflow recipe、多 Agent、并行 sweep、对抗验证、结构化 artifact | 设计或执行结构化 workflow | `references/branches/orchestration.md` | `aibridge-workflow-orchestration` |

进入某个分支后，只读取该分支文档和必要 checklist，不预加载其它分支文档。项目偏好禁用的分支不能自动进入；如果用户明确要求禁用分支，先说明该分支已关闭并请求确认

## 交接规则

- 调试诊断分支发现 confirmed 根因且用户要求修复时，交接到实施分支；如果实施分支被禁用，先请求用户确认
- 实施分支完成改动后，按风险选择验证分支补充 Runtime、截图、UI 或多目标证据；如果验证分支被禁用，说明剩余风险
- 审查分支发现问题后，未得到修复授权前不直接改文件
- 编排分支只定义流程、角色、artifact 和 gate；具体 Unity 对象修改仍由实施分支串行完成
- Mode Exit 或分支交接时同步交接上下文：面向用户只列关键产出、必要 artifact refs、gate 状态、未关闭风险和下一步动作

## Skill 列出策略

- `Preflight / Skill 路由` 只做内部选路，不要求对用户显式输出 `baselineSkills`、`activeSkills`、`deferredSkills` 或 `guardedSkills`
- `【模式：...】` 进入时列当前 `Skills`，仅在 active Skills 变化时重新列
- 执行进度、检查清单、Mode Exit 和面向用户的最终回复不列 `使用 Skills`、已释放 Skills 或下一步建议 Skills
- 只有跨模式续跑、外部 agent 交接或 `workflow import` 需要结构化结果时，才在 `SkillHandoff` 数据中记录 releasedSkills / nextRecommendedSkills

## 输出格式

```text
【模式：需求讨论分支】
Skills：aibridge-development-workflow
已加载规范：requirements.md、risk-gates.md
输出目标：收敛需求边界并输出 `.aibridge/plan` 工作底稿。

【模式：<启用分支之一>】
Skills：<当前分支 Skills>
已加载规范：<当前分支文档>
输出目标：<本模式的验收目标>
```
