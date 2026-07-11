---
name: aibridge-development-workflow
description: "AIBridge/Unity 多分支开发工作流入口。Use when a task requires code or Unity asset changes, persistent AGENTS/Skill/workflow rule changes, root-cause debugging, Runtime/log evidence, risk review, validation verdicts, or workflow recipes. Do not use for pure Q&A, simple explanation, simple search/display, or read-only analysis with no changes and no verdict"
---

# AIBridge Development Workflow

## 入口边界

如误入本 Skill（纯问答/解释/只读分析），直接回答，停止展开分支和 Harness 探测

## 必读顺序

1. 若存在 `references/project-workflow-preferences.md`，先读取它
2. 读取 `references/branch-selection.md`，选择启用的主分支
3. 只读取当前分支的 `references/branches/<branch>.md`
4. 进入开发、调试、审查、验证或 workflow 任务前读取 `references/harness-readiness.md`；只有运行时探测、resume、外部 executor、或业务命令失败需要 fallback 时，才继续读 `references/harness-readiness-detail.md`
5. 仅在当前动作需要时读取：`risk-gates.md`、`coding-rules.md`、`checklist.md`、`debug-investigation-workflow.md`、`debug-investigation-checklist.md`、`editor-generation.md`、`profiler-debugging.md`

## 路由不变量

- Skill 路由是 Preflight，不是业务模式
- 主分支只能从 preferences 启用的分支中自动选择；用户要求禁用分支时先确认
- 需求不清晰时先进入需求讨论分支；方案写入规则见 `branches/requirements.md`
- `aibridge-development-workflow` 常驻；其它 Skill 仅在当前分支/phase 内有效，Mode Exit 后只保留 artifact refs 与 gate
- Workflow report/manifest 默认作路径交接，不为常规状态读全文

## 工具不变量

- 项目侧能力以 Root Rule 与 preferences 为准；勿用 `$CLI harness status` 做 enablement/freshness 预检
- `compile dotnet` 不能替代 `$CLI compile unity`
- 仅当 Root Rule / preferences 声明 Code Index 已启用且需定位 C# 声明时，才加载 `aibridge-code-index`
- `agent` / `manual` step 需外部执行器或人工，并用 `workflow import` 回传；AIBridge CLI 不会自动执行

## 输出策略

输出格式与 Skill 列出策略见 `branch-selection.md`。最终回复不列 `使用 Skills`、已释放 Skills 或下一步建议 Skills；结构化续跑信息只放入 `SkillHandoff`
