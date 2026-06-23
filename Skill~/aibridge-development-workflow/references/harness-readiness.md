# Harness Preflight gate

## 目标

Harness 判定是 workflow 的 Preflight gate，不是业务分支内的固定步骤。默认走 compact-first：先使用 RootRule 能力摘要或 `$CLI harness status` 默认输出；snapshot `fresh` 且不影响当前工具选择时，不读取完整 snapshot、不重复探测，也不额外输出 harness 状态。

## Compact 入口

1. 先使用 RootRule 能力摘要；需要确认 freshness 时执行 `$CLI harness status`。
2. 状态为 `fresh` 时，按 compact summary 分流，继续读取当前分支文档。
3. 状态为 `missing`、`stale`、`invalid`，或任务需要未确认能力时，才读取 `harness-readiness-detail.md` 并只补测本任务必需能力。
4. 如果 harness 状态改变工具选择，单独写一行工具策略；不要把搜索收窄、文件读取、证据定位等执行进度混入 harness 状态。

快照由 Unity Editor/SkillInstaller 自动生成，只包含项目侧可确认能力。sub-agent、shell、sandbox、网络等 harness 原生权限必须视为 `unknown`，除非当前 harness 明确提供。

## 输出规则

只有发生缺失、过期、降级、阻塞、用户要求说明，或能力状态改变工具选择时，才在入口块简短展开：

```text
【入口：Preflight / Skill 路由】
主分支：实施分支
Harness：snapshot stale；仅确认本任务必需能力
工具策略：Code Index disabled，使用 text_index/rg + 文件阅读
```

snapshot `fresh` 且不改变工具选择时，入口块只保留分支、Skills 和理由即可。不要输出未经当前 compact status 支撑的 Code Index、Unity Editor、Runtime 等能力结论。

## 不变量

- 过期 snapshot 只能作为待确认线索，不能写成当前事实。
- 不把静态检查、`dotnet build` 或推断说成当前能力或 Unity 验证通过；`compile dotnet` 只能作为额外检查，不能替代 `$CLI compile unity`。
- `agent` / `manual` step 需要当前 AI harness、外部 executor 或人工完成；AIBridge CLI 只记录、导出和导入结构化结果。
- 大日志、截图、GIF、性能采样和完整 JSON 结果保存为 artifact，主回复只引用路径或 id。

需要详细探测表、降级处理、续跑或证据 schema 时，读取 `harness-readiness-detail.md`。
