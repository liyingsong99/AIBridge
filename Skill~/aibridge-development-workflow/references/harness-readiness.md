# Harness Preflight gate

## 目标

Harness 判定是 Preflight gate，不是业务分支固定步骤。项目侧能力以 Root Rule 与 `project-workflow-preferences.md` 为准；勿用 `$CLI harness status` 做 enablement/freshness 预检

## Compact 入口

1. 读 Root Rule 能力块与 preferences（若存在），按声明决定工具策略
2. 仅当任务需要 Unity/Runtime/dialog 等运行时能力时，用对应业务命令探测；失败再读 `harness-readiness-detail.md`
3. 能力改变工具选择时，单独写一行工具策略，不混入执行进度

`harness status` / `capabilities.json` 仅用于显式诊断，不是默认 Preflight。sub-agent/shell/sandbox/网络等 harness 原生权限视为 `unknown`，除非当前 harness 明确提供

## 输出规则

仅在降级、阻塞、用户要求说明，或能力改变工具选择时简短展开：

```text
【实施分支】
Skills：aibridge-development-workflow
已加载规范：implementation.md
输出目标：改动当前工作树并验证
工具策略：Code Index disabled，使用宿主文件阅读与 AIBridge 常规命令
```

Root Rule / preferences 已声明的能力不要再输出 Preflight 宣告

## 不变量

- 不把静态检查、`dotnet build` 或推断说成 Unity 验证通过；`compile dotnet` 不能替代 `$CLI compile unity`
- `agent` / `manual` step 需外部 executor 或人工；AIBridge CLI 只记录/导出/导入结构化结果
- 大日志、截图、GIF、性能采样和完整 JSON 保存为 artifact，主回复只引用路径或 id

需要探测矩阵、降级、续跑或证据 schema 时，读取 `harness-readiness-detail.md`
