# Harness Readiness Detail

前置：已读 `harness-readiness.md`。本文仅含探测矩阵、fallback、resume 与证据指针

## 最小探测矩阵

| 能力 | 何时探测 | 探测方式 | 失败时 fallback |
|---|---|---|---|
| 项目侧能力 | 进入 workflow | Root Rule + preferences | 以规则为准；勿为开关调 `harness status` |
| Skill 装载 | 需要 sibling Skill | 读 Skill 路径 | 使用 Root Rule 最小规则 |
| CLI 路径 | 任何 AIBridge 命令前 | 检查 `$CLI` 是否存在 | `rg`/文件读取；声明 CLI 未验证 |
| Unity Editor | 编译/资源/场景/Prefab/Inspector/日志 | `$CLI compile unity` 或目标命令 | 不用 `dotnet build` 冒充；报告未验证 |
| Code Index | 快速定位 C# 声明 | 仅 Root Rule/preferences 已启用时加载 `aibridge-code-index` | 直接读 `.cs` |
| Runtime target | Runtime/Player/UI/性能 | `$CLI runtime list_targets`；需端口扫描时加 `--probe true` | 静态/Editor 结论；深诊断用 `runtime diagnose` |
| Workflow run | recipe/长任务/跨 turn | 确认 run id 后 `$CLI workflow status --run <runId>` | 新建 run 前说明无可恢复状态 |
| 外部执行器 | `agent`/`manual`/多 agent | 当前 harness 能否创建子任务 | 导出 task pack 或主 agent 执行后 `workflow import` |

## 状态值

- `available` / `unavailable` / `unknown` / `not-needed` / `degraded`

## Fallback 规则

- 项目侧能力：信任 Root Rule / preferences
- CLI 不存在：只做 host 侧命令，明确 AIBridge/Unity 未验证
- Unity 超时或 modal 阻塞：先 `dialog status` 或 `--on-dialog`；仍失败则 blocked
- Code Index 不可用：不反复重试；改读源码；工具策略单独一行
- Runtime 缺失：不推断 Player 行为
- `agent`/`manual`：记录 `skipped_requires_external_executor`，完成后 `workflow import`

显式诊断（非默认 Preflight）：

```bash
$CLI harness status --detail full
$CLI harness status --include-snapshot true
```

## Resume 规则

- 长任务/recipe 前从 active-run 或用户输入确认 run id
- 续跑先 `workflow status --run <runId>`，再按缺失 gate/skipped step 推进
- 不用过期日志/截图/Runtime/command result 支撑新结论
- `workflow finish --status passed` 前刷新 gate/report；required gate 缺失不能通过

## 证据回传

结构化结果优先用 `aibridge-workflow-orchestration/references/evidence-schema.md`（`EvidenceRef`、`CommandEvidence`、`Finding`、`Verdict`、`PatchProposal`、`ValidationResult`、`SkillHandoff`）。Finding 等必须引用 evidence/artifact id。大结果存 artifact，主回复只引路径或 id
