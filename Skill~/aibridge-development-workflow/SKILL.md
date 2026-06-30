---
name: aibridge-development-workflow
description: "AIBridge/Unity 多分支开发工作流入口。Use when a task requires code or Unity asset changes, persistent AGENTS/Skill/workflow rule changes, root-cause debugging, Runtime/log evidence, risk review, validation verdicts, or workflow recipes. Do not use for pure Q&A, simple explanation, simple search/display, or read-only analysis with no changes and no verdict"
---

# AIBridge Development Workflow

## 入口边界

如误入本 Skill（纯问答/解释/只读分析），直接回答，停止展开分支和 Harness 探测。

## 必读顺序

1. 若存在 `references/project-workflow-preferences.md`，先读取它。
2. 读取 `references/branch-selection.md`，选择启用的主分支。
3. 只读取当前分支的 `references/branches/<branch>.md`。
4. 进入开发、调试、审查、验证或 workflow 任务前读取 `references/harness-readiness.md`；只有 snapshot 缺失、过期、无效、resume、外部 executor、或任务需要未确认能力时，才继续读 `references/harness-readiness-detail.md`。
5. 仅在当前动作需要时读取：`risk-gates.md`、`coding-rules.md`、`checklist.md`、`debug-investigation-workflow.md`、`debug-investigation-checklist.md`、`editor-generation.md`、`profiler-debugging.md`。

## 路由不变量

- Skill 路由是 Preflight，不是业务模式；只计算 `baselineSkills`、`activeSkills`、`deferredSkills`、`guardedSkills`。
- 主分支只能从项目偏好启用的分支中自动选择；用户明确要求禁用分支时先请求确认。
- 需求边界、验收标准或方案方向不清晰时，先进入需求讨论分支；方案型或跨模块调整先写 `.aibridge/plan/<slug>.md` 工作底稿。
- `aibridge-development-workflow` 是常驻入口；其它 Skill 只在当前分支、phase 或具体操作内有效。Mode Exit 后停止引用已释放 Skill 的详细规则，只保留 artifact refs、gate 状态和必要命令。
- Workflow report/manifest 默认作为 artifact ref 或路径交接；不要为常规状态读取全文。

## 工具不变量

- Harness Preflight gate 走 compact-first：优先 RootRule 摘要或 `$CLI harness status` 默认输出；fresh 且不影响当前工具选择时默认不单独输出，也不读取完整 snapshot。
- 不把 stale snapshot、静态检查、`dotnet build` 或推断说成当前能力或 Unity 验证通过；`compile dotnet` 只能作为额外检查，不能替代 `$CLI compile unity`。
- C# 语义关系查询仅在 `aibridge-code-index` 已安装且项目规则启用 Code Index 时加载该 Skill；否则使用宿主自带的搜索/读取工具。
- Unity 已导入资源路径查找优先 `$CLI asset search/find --format paths`（经 Unity 资源数据库解析）；普通仓库文件和路径正则用宿主自带的文件列举/搜索工具（如 `rg --files`）。
- 字面量、注释、配置、YAML、Prefab/Scene 文本和非 C# 内容搜索使用宿主自带的文本搜索与文件读取工具（如 ripgrep `rg -n`）。
- `agent` / `manual` step 需要当前 AI harness、外部执行器或人工完成，并用 `workflow import` 回传结构化结果；AIBridge CLI 不会自动执行这些步骤。

## 输出策略

进入 workflow 时输出一个入口块；进入业务模式时输出一个模式块。active Skills 没变化时不要反复列。

```text
【入口：Preflight / Skill 路由】
baselineSkills：aibridge-development-workflow
activeSkills：<当前分支 Skills>
主分支：<启用分支之一>
理由：<进入依据>

【模式：<分支>】
Skills：<当前模式 Skills>
已加载规范：<当前 reference>
输出目标：<本模式验收目标>
```

执行进度、检查清单、Mode Exit 和最终用户回复不列 `使用 Skills`、已释放 Skills 或下一步建议 Skills。只有跨模式续跑、外部 agent 交接或 `workflow import` 需要结构化结果时，才在 `SkillHandoff` 数据中记录 releasedSkills / nextRecommendedSkills。

最终回复只报告实际改动、验证结果、失败/阻塞项、未验证原因和剩余风险；不重复完整流程表。
