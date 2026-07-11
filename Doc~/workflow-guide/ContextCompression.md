# AIBridge Workflow Context Compression

## 目标

`aibridge-development-workflow` 只保留任务入口、必读顺序与短不变量。分流细节、输出模板、Harness 矩阵与专题 reference 按需加载，避免跨层复述占用上下文。

## 权威分层（SSOT）

| 主题 | 唯一详述 | 其它文件 |
|------|----------|----------|
| 项目侧能力 / 禁止 harness status 预检 | Root Rule | 一行指针 |
| Code Index 查询面 | `aibridge-code-index` Skill | Root Rule 短开关句 |
| 验证级别 | 生成版 preferences | 分支「见 preferences」 |
| `.aibridge/plan` 完整规则 | `branches/requirements.md` | 触发条件一行 |
| 生命周期 / Skill 列出 / 输出模板 | 生成版 `branch-selection.md` | workflow SKILL 指针 |
| 探测矩阵 / fallback / resume | `harness-readiness-detail.md` | compact 只保留 gate |

## 规则

1. **短入口**：`SKILL.md` 不展开完整分支表、完整 Harness 表或长输出模板；只说明何时进入、先读哪些 reference、哪些硬约束不能破。
2. **分支按需加载**：先读 preferences，再读 `branch-selection.md`；只加载当前分支文档和必要 checklist。
3. **生成版分流为安装事实**：安装后的 `branch-selection.md` 由 `WorkflowPreferenceRenderer` 生成；source fallback 与生成版生命周期/Skill 列出策略保持一致。
4. **项目侧能力权威**：信任 Root Rule 与 preferences；勿用 `$CLI harness status` 做 enablement/freshness 预检。
5. **细节后置**：仅运行时探测、resume、外部 executor 或命令失败时读 `harness-readiness-detail.md`。
6. **输出收敛**：输出格式见 `branch-selection.md`；最终回复不列 Skills；结构化续跑只放 `SkillHandoff`。
7. **编排 CLI 按需**：写/跑 recipe 时再读 `workflow-cli-reference.md`；`recipe-schema.md` 只保留 shape/gates。

## Drift Canary

相关测试必须覆盖：

- 入口 `SKILL.md` 保持 compact（约 ≤1600 字符或测试上限），引用 `harness-readiness-detail.md`，不含完整输出模板代码块。
- compact `harness-readiness.md` 不含完整探测矩阵/Fallback/Resume 细节，且不要求 harness status 做默认 Preflight。
- detail 含 `最小探测矩阵`、`Fallback 规则`、`Resume 规则`、`EvidenceRef`、`CommandEvidence`。
- source fallback `branch-selection.md` 与生成版的生命周期/Skill 列出策略一致；生成版含输出格式。
- Root Rule Code Index 为短开关句，不含声明名类型枚举。
- `compile dotnet` 不能替代 `$CLI compile unity`。
- `WORKFLOW_SKILL_RULE` 使用「reads harness-readiness as Preflight gate」，不用「probes harness readiness」。

## 维护入口

- Source Skill：`Skill~/aibridge-development-workflow/SKILL.md`
- Harness compact/detail：`references/harness-readiness.md`、`harness-readiness-detail.md`
- Source/Generated branch selection：`references/branch-selection.md`、`Editor/Utils/WorkflowPreferences/WorkflowPreferenceRenderer.cs`
- Recipe schema / CLI：`Skill~/aibridge-workflow-orchestration/references/recipe-schema.md`、`workflow-cli-reference.md`
- Invariant tests：`Tests/Editor/AssistantIntegration/RuleTemplateTests.cs`、`SkillInstallerWorkflowOrchestrationTests.cs`
