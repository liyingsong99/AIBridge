# Harness Preflight gate

## 目标

在进入 AIBridge workflow 前，把 harness 判定作为 Preflight gate 处理，而不是业务分支内的固定步骤。优先使用 RootRule 中的 compact 能力摘要或 `$CLI harness status` compact 输出，避免每轮 AI 读取完整 snapshot 或重复探测。fresh 且不影响当前工具选择时默认静默；完整 snapshot 和完整探测只在快照缺失、过期，或任务需要未确认能力时使用。

## 默认入口

1. 先使用 RootRule 的能力摘要；需要确认状态或 freshness 时执行 `$CLI harness status`，使用默认 compact 输出。
2. 如果状态是 `fresh`，按 compact summary 分流；不读取完整 snapshot，不再重复探测 CLI/Skill/Code Index/workflow，且默认不单独输出 harness 状态。
3. 如果状态是 `missing`、`stale` 或 `invalid`，再按下方矩阵做最小补测；过期 snapshot 只能作为待确认线索，不能写成当前事实。
4. 如果 harness 状态改变工具选择，把工具策略单独写出；不要把搜索收窄、文件读取、证据定位等执行进度混进 harness 状态句子。

快照由 Unity Editor/SkillInstaller 自动生成，只包含项目侧可确认能力。sub-agent、shell、sandbox、网络等 harness 原生权限必须视为 `unknown`，除非当前 harness 明确提供。

## 最小探测矩阵

| 能力 | 何时探测 | 探测方式 | 失败时 fallback |
|---|---|---|---|
| Snapshot | 所有开发、调试、workflow、Skill 任务 | RootRule 摘要或 `$CLI harness status` compact 输出 | 缺失/过期/无效时才读取完整 snapshot 或进入完整探测 |
| Skill 装载 | 快照缺失或需要确认 sibling Skill | 读取当前 Skill 和 sibling Skill 路径 | 使用 RootRule 最小规则，不反复尝试同一路径 |
| CLI 路径 | 任何 AIBridge 命令前 | 检查 `$CLI` 或 `./.aibridge/cli/AIBridgeCLI.exe` 是否存在 | 静态检查、`rg`、文件读取；声明 AIBridge CLI 未验证 |
| Unity Editor | 编译、资源、场景、Prefab、Inspector、日志任务 | `$CLI compile unity` 或目标命令结果 | 不用 `dotnet build` 冒充 Unity 编译；报告 Unity 未验证 |
| Code Index | C# 符号、定义、引用、调用者、诊断查询 | `$CLI code_index status` | 使用 `rg` 和直接文件读取 |
| Runtime target | Runtime、Player、Play Mode、UI、性能任务 | 先用 quick `$CLI runtime list_targets`；只有需要本机端口扫描时加 `--probe true`，再选 target | 只给静态或 Editor 结论，Runtime 状态标记未验证；深诊断显式用 `$CLI runtime diagnose` |
| Workflow run | recipe、长任务、跨 turn 任务 | 从用户、上一轮输出或 `.aibridge/workflows/active-run.json` 确认 run id 后执行 `$CLI workflow status --run <runId>` | 新建 run 前说明未发现可恢复状态 |
| 外部执行器 | `agent` / `manual` step、多 agent 协作 | 当前 harness 是否能创建子任务或人工步骤 | 导出 task pack 或由主 agent 执行并 `workflow import` |

## 输出规则

Preflight 阶段只在 harness 状态会影响执行、安全边界或用户可见结论时输出；不必向用户输出完整探测表，也不要把 harness 判定写成每个业务分支的固定模式。只有发生缺失、过期、降级、阻塞、用户要求说明，或能力状态改变工具选择时，才在入口块中展开简短状态：

```text
【入口：Preflight / Skill 路由】
主分支：实施分支
Harness：snapshot stale；仅确认本任务必需能力
工具策略：Code Index disabled，使用 rg + 文件阅读
```

当 snapshot `fresh` 且不改变工具选择时，入口块只保留分支、Skills 和理由即可，不额外输出泛化的 harness 可用性宣告。不要输出未经当前 compact status 支撑的 Code Index、Unity Editor、Runtime 等能力结论。

需要完整 snapshot JSON 时才运行：

```bash
$CLI harness status --detail full
$CLI harness status --include-snapshot true
```

状态值建议使用：

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
- Code Index disabled/unavailable：不要重试同一语义查询；使用 `rg`、相邻文件和常规源码阅读；如需说明，单独写成工具策略，不混入 harness 可用性宣告。
- Runtime target 缺失：不要推断 Player 行为；列出需要用户启动 Player/Play Mode 或提供 target。
- workflow `agent` / `manual` step：AIBridge CLI 只记录为 `skipped_requires_external_executor`；外部执行完成后用 `workflow import` 导入结构化结果。

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
