# AIBridge Workflow Context Compression

## 目标

`aibridge-development-workflow` 只保留任务入口、按需读取顺序、路由不变量、工具不变量和输出策略。分支细节、Harness 探测矩阵、fallback、resume 和证据 schema 放到 reference 文件中按需读取，避免每次 workflow 任务都加载完整说明。

## 规则

1. **短入口**：`SKILL.md` 不展开完整分支表、完整 Harness 表或长示例；只说明什么时候进入 workflow、先读哪些 reference、哪些工具规则不能被破坏。
2. **分支按需加载**：先读 `project-workflow-preferences.md`，再读 `branch-selection.md`；只加载当前分支的 `references/branches/<branch>.md` 和必要 checklist。
3. **生成版分流为安装事实**：安装到 assistant Skill 目录后的 `branch-selection.md` 由 `WorkflowPreferenceRenderer` 根据项目偏好生成。source fallback 只保留与生成版一致的通用口径，不能写旧的 released/next Skills 面向用户输出规则。
4. **Harness compact gate**：默认使用 RootRule 摘要或 `$CLI harness status` compact 输出。snapshot `fresh` 且不影响工具选择时不读取完整 snapshot，不输出泛化可用性宣告。
5. **细节后置**：只有 snapshot 缺失、过期、无效、resume、外部 executor，或任务需要未确认能力时，才读取 `harness-readiness-detail.md`。
6. **输出收敛**：入口块和模式块列当前 Skills；执行进度、Mode Exit 和最终回复不反复列 `使用 Skills`、已释放 Skills 或下一步建议 Skills。结构化续跑信息只放入 `SkillHandoff`。

## Drift Canary

相关测试必须覆盖以下不变量：

- 入口 `SKILL.md` 保持 compact，且引用 `harness-readiness-detail.md`。
- compact `harness-readiness.md` 不包含完整探测矩阵、fallback 或 resume 细节。
- detail 文件包含 `最小探测矩阵`、`Fallback 规则`、`Resume 规则`、`EvidenceRef` 和 `CommandEvidence`。
- source fallback `branch-selection.md` 的静态分流/Skill 列出策略与生成版保持一致。
- 安装生成版 `branch-selection.md` 不要求最终回复列 `使用 Skills`、已释放 Skills 或下一步建议 Skills。
- `compile dotnet` 不能替代 `$CLI compile unity`。

## 维护入口

- Source Skill：`Skill~/aibridge-development-workflow/SKILL.md`
- Harness compact：`Skill~/aibridge-development-workflow/references/harness-readiness.md`
- Harness detail：`Skill~/aibridge-development-workflow/references/harness-readiness-detail.md`
- Source fallback branch selection：`Skill~/aibridge-development-workflow/references/branch-selection.md`
- Generated branch selection：`Editor/Utils/WorkflowPreferences/WorkflowPreferenceRenderer.cs`
- Invariant tests：`Tests/Editor/AssistantIntegration/RuleTemplateTests.cs`、`Tests/Editor/AssistantIntegration/SkillInstallerWorkflowOrchestrationTests.cs`
