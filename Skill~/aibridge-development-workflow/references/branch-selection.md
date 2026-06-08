# 任务分流规则

## 目标

`aibridge-development-workflow` 是兼容入口，不直接假设所有任务都是实现任务。进入工作流后先执行 Preflight / Skill 路由步骤，选择一个主分支，再进入对应模式生命周期。

安装到 assistant Skill 目录后，本文件会由 `AIBridge/Workflows` 根据项目偏好生成。生成版本只把启用分支列为默认可选分支。若存在 `project-workflow-preferences.md`，必须先读取该文件，再读取本文件。

## 工作流生命周期

```text
Preflight / Skill Routing
  -> 需求讨论分支（如有必要）
  -> Mode Enter
  -> Mode Execute
  -> Mode Exit / SkillHandoff / Release
  -> Transition Preflight
```

- Preflight / Skill Routing 是入口步骤，不是业务模式；它只选择主分支并计算 Skill 状态。
- Harness 判定是 Preflight gate，不是业务分支固定步骤；fresh 且不影响工具选择时不单独输出。
- 只有缺失、过期、降级、阻塞、用户要求说明，或能力状态改变工具选择时，才在入口块简短展开 Harness 状态。
- 当需求边界、验收标准或方案方向不清晰，或用户要求先分析/先确认时，先进入需求讨论分支，确认后再进入正式主分支。
- 需求讨论分支是 Preflight 的前置分支，不替代实施、调试、审查、验证或编排分支。
- 若用户要求，或者项目中有相应的功能文档归类，确认后的方案必须先写入 `.aibridge/plan` 工作底稿，再按需同步到正式文档位置。
- 方案文档默认优先写 Markdown 工作底稿；当方案包含流程图、决策树、对比表，或更适合开发者浏览时，再在每个落点目录内同步生成 HTML 展示页。Markdown 和 HTML 应保持同目录、同 basename。
- Mode Enter 激活当前模式真正需要的 Skill，并读取必要 reference。
- Mode Execute 执行当前模式的业务步骤。
- Mode Exit 生成 `SkillHandoff`，并释放下一模式不需要的模式专用 Skill。
- Transition Preflight 只在模式切换时轻量执行，用上一模式 handoff 计算下一模式的 Skill delta。

## 需求讨论分支

当需求目标、边界、验收标准或方案方向尚未锁定时，先进入需求讨论分支，再重新执行 Preflight / Skill Routing。

```text
【模式：需求讨论分支】
Skills：aibridge-development-workflow
已加载规范：requirements.md、risk-gates.md
输出目标：收敛需求边界并输出 `.aibridge/plan` 工作底稿。
```

## 分支选择

| 主分支 | 触发信号 | 默认目标 | 常用 Skills / 工具 |
|---|---|---|---|
| 实施分支 | 创建、修改、修复、重构、生成、迁移、提交 | 改动当前工作树并验证 | `references/branches/implementation.md` |
| 调试诊断分支 | 排查、诊断、复现、为什么、追踪、日志、Runtime、Player、Play Mode、性能、UI 异常 | 收集证据并给出根因判断 | `references/branches/debug.md` |
| 审查分支 | review、audit、检查风险、设计评审、只读分析 | 输出 confirmed findings 和剩余风险 | `references/branches/review.md` |
| 验证分支 | 编译、日志、截图、测试、Runtime/UI 验证、回归确认 | 给出可重复验证结果 | `references/branches/validation.md` |
| 编排分支 | workflow recipe、多 Agent、并行 sweep、对抗验证、结构化 artifact | 设计或执行结构化 workflow | `references/branches/orchestration.md` |

进入某个分支后，只读取该分支文档和必要 checklist，不预加载其它分支文档。项目偏好禁用的分支不能自动进入；如果用户明确要求禁用分支，先说明该分支已关闭并请求确认。

## 交接规则

- 调试诊断分支发现 confirmed 根因且用户要求修复时，交接到实施分支；交接内容必须包含症状、证据、候选根因状态和建议修改范围。
- 实施分支完成改动后，按风险选择验证分支补充 Runtime、截图、UI 或多目标证据。
- 审查分支发现问题后，未得到修复授权前不直接改文件。
- 编排分支只定义流程、角色、artifact 和 gate；具体 Unity 对象修改仍由实施分支串行完成。
- Mode Exit 或分支交接时同步交接 Skill 作用域：列出已释放的模式专用 Skill、下一分支建议加载的 Skill、必要 artifact refs、gate 状态和未关闭风险。
- 进入模式、phase 或 step 时必须输出当前模式和 active Skills，方便开发者追踪执行阶段；不要把 Skill 作用域只写进内部状态或最终 report。

## Handoff 摘要

Mode Exit、phase 结束或 step 交接时，必须输出 `SkillHandoff` compact handoff，而不是继续携带上一模式的完整 Skill 细节。

```json
{
  "completedMode": "prefab-patch",
  "releasedSkills": ["aibridge-prefab-patch"],
  "nextRecommendedSkills": ["aibridge"],
  "summary": "已应用 Prefab patch，等待 Unity 编译验证。",
  "artifactRefs": ["art_patch_proposal_001"],
  "gates": [
    {
      "id": "unity-compile",
      "status": "pending"
    }
  ],
  "openRisks": []
}
```

## 输出格式

```text
【入口：Preflight / Skill 路由】
baselineSkills：aibridge-development-workflow
activeSkills：aibridge、aibridge-workflow-orchestration
deferredSkills：aibridge-code-index（仅 C# 语义查询时）
guardedSkills：aibridge-prefab-patch（仅复杂 Prefab 修改时）
主分支：调试诊断分支
辅助分支：编排分支（需要 Runtime 多目标 sweep 时）
理由：用户目标是排查运行时异常，当前验收是证据和根因结论，不是立即修改代码。

【模式：调试诊断分支】
Skills：aibridge-development-workflow、aibridge
已加载规范：debug-investigation-workflow、debug-investigation-checklist
输出目标：收集证据并给出根因判断。
```
