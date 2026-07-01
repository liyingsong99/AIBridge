# Harness Readiness Detail

只有 compact gate 判断为 `missing`、`stale`、`invalid`，需要 resume，遇到外部 executor，或任务需要未确认能力时，才读取本文件。不要把本文件作为每个 workflow 任务的默认上下文。

## 最小探测矩阵

| 能力 | 何时探测 | 探测方式 | 失败时 fallback |
|---|---|---|---|
| Snapshot | 所有开发、调试、workflow、Skill 任务 | RootRule 摘要或 `$CLI harness status` compact 输出 | 缺失/过期/无效时才读取完整 snapshot 或进入完整探测 |
| Skill 装载 | 快照缺失或需要确认 sibling Skill | 读取当前 Skill 和 sibling Skill 路径 | 使用 RootRule 最小规则，不反复尝试同一路径 |
| CLI 路径 | 任何 AIBridge 命令前 | 检查 `$CLI` 或 `./.aibridge/cli/AIBridgeCLI.exe` 是否存在 | 静态检查、`rg`、文件读取；声明 AIBridge CLI 未验证 |
| Unity Editor | 编译、资源、场景、Prefab、Inspector、日志任务 | `$CLI compile unity` 或目标命令结果 | 不用 `dotnet build` 冒充 Unity 编译；报告 Unity 未验证 |
| Code Index | 快速定位 C# 声明文件或声明位置 | 依据 harness snapshot、项目设置和查询结果判断是否可用；需要时直接执行 `code_index symbol/definition` | 直接阅读相关文件并继续分析 |
| Runtime target | Runtime、Player、Play Mode、UI、性能任务 | 先用 quick `$CLI runtime list_targets`；只有需要本机端口扫描时加 `--probe true`，再选 target | 只给静态或 Editor 结论，Runtime 状态标记未验证；深诊断显式用 `$CLI runtime diagnose` |
| Workflow run | recipe、长任务、跨 turn 任务 | 从用户、上一轮输出或 `.aibridge/workflows/active-run.json` 确认 run id 后执行 `$CLI workflow status --run <runId>` | 新建 run 前说明未发现可恢复状态 |
| 外部执行器 | `agent` / `manual` step、多 agent 协作 | 当前 harness 是否能创建子任务或人工步骤 | 导出 task pack 或由主 agent 执行并 `workflow import` |

## 状态值

- `available`：已确认可用。
- `unavailable`：已确认不可用。
- `unknown`：尚未探测，且当前阶段不强制需要。
- `not-needed`：本任务不需要该能力。
- `degraded`：能力部分可用，但需要 fallback 或剩余风险说明。

## Fallback 规则

- Snapshot fresh：直接按 compact summary 分流，不读取完整 snapshot，不执行完整探测；除非用户要求或影响工具选择，否则不输出 harness 状态。
- Snapshot missing/stale/invalid：执行 `$CLI harness status`，必要时读取完整 snapshot；只补测任务必需能力，不把 stale snapshot 内容当作当前事实。
- Skill 不可读：继续使用 RootRule 的 CLI 路径、通用验证和路由规则；不要假设隐藏 Skill 内容。
- CLI 不存在：只执行 host 侧命令，如 `rg`、文件读取、`dotnet build`；最终明确 AIBridge/Unity 验证未执行。
- Unity 命令超时或被 modal dialog 阻塞：先检查 `dialog status` 或使用显式 `--on-dialog` 策略；仍失败时报告 blocked。
- Code Index disabled/unavailable/not-ready/stale：不要反复重试同一查询；直接改用相邻文件和常规源码阅读；如需说明，单独写成工具策略，不混入 harness 可用性宣告。
- Runtime target 缺失：不要推断 Player 行为；列出需要用户启动 Player/Play Mode 或提供 target。
- workflow `agent` / `manual` step：AIBridge CLI 只记录为 `skipped_requires_external_executor`；外部执行完成后用 `workflow import` 导入结构化结果。

需要完整 snapshot JSON 时，明确使用：

```bash
$CLI harness status --detail full
$CLI harness status --include-snapshot true
```

## Resume 规则

- 长任务或 workflow recipe 任务开始前，优先从 active-run 指针或用户输入确认 run id。
- 继续已有 run 时，先读取 `workflow status --run <runId>`，再根据缺失 gate 或 skipped step 决定下一步。
- 不使用明显过期的日志、截图、Runtime target 或 command result 支撑新结论；必要时重新采集。
- `workflow finish --status passed` 前必须刷新 gate/report；required gate 缺失时不能标记通过。

## 证据回传

外部 harness 或子 agent 输出结构化结果时，优先使用 `aibridge-workflow-orchestration/references/evidence-schema.md` 中的 schema。

常见 schema 包括 `EvidenceRef`、`CommandEvidence`、`Finding`、`Verdict`、`PatchProposal`、`ValidationResult` 和 `SkillHandoff`。

`Finding`、`Verdict`、`PatchProposal`、`ValidationResult` 必须引用 evidence id 或 artifact id，避免粘贴大段日志。

大日志、截图、GIF、性能采样和完整 JSON 结果保存为 artifact，主回复只引用路径或 id。
