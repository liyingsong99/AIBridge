# 调试诊断工作流

## 适用范围

排查问题、追踪运行时信息、分析日志、复现异常、定位根因时使用。默认目标是诊断结论，不是改代码。原则与门控见 `branches/debug.md` 与 `debug-investigation-checklist.md`

## 阶段

### 1. 问题定界

记录症状、期望/实际、环境（Editor/Play Mode/Player/平台/target）、复现步骤、是否要求修复

### 2. 基线证据

```bash
$CLI compile unity
$CLI get_logs --logType Error --count 100
$CLI get_logs --logType Warning --count 100
$CLI get_logs --regex "<关键字|异常名|对象名>"
```

编译失败时优先处理编译错误，不基于过期 Runtime 状态推断

### 3. Runtime 追踪

```bash
$CLI runtime list_targets
$CLI runtime list_targets --probe true
$CLI runtime status --target latest
$CLI runtime diagnose --target latest
$CLI runtime logs --target latest --logType Error --count 100
$CLI runtime screenshot --target latest
$CLI runtime perf --target latest --duration 5s --interval 100ms
$CLI runtime handlers --target latest
$CLI runtime call --target latest --action <handler> --json "<json>"
```

Profiler 见 `profiler-debugging.md`。`list_targets` 默认 quick；需端口扫描才加 `--probe true`。多目标优先 `runtime-target-sweep` / `runtime-debug-investigation` recipe

### 4. 复现与交互

```bash
$CLI input click --path "<Canvas/Button>"
$CLI input click_pct --x 0.5 --y 0.5
$CLI screenshot game
$CLI screenshot gif
```

动作串行。截图/GIF 只证明可见状态

### 5. 代码和资源关联

- C#：Root Rule/preferences 启用 Code Index 时用 `aibridge-code-index`，否则直接读 `.cs`
- 非 C#：直接读文件或命令结果；Prefab/Scene 结构只读优先

### 6. 候选根因验证

```text
候选根因：
1. 状态：confirmed/refuted/uncertain
   证据：命令、日志、截图 artifact、代码位置或 Runtime target
   说明：...
   剩余风险：...
```

### 7. 结论与交接

含根因状态、证据列表、复现路径、未验证项；需修复时给出范围/风险/验证命令并交接实施分支
