# Pipeline 编译观察与受限测试

默认 `compile unity` 和 `test run/status` 继续使用 AIBridge 原生后端。以下诊断命令只查询当前 Editor 的 Pipeline 0.10.0-exp.1；下方另有显式同步测试入口。所有入口均不经官方 CLI 子进程，也不自动回退 native。

```powershell
$CLI = './.aibridge/cli/AIBridgeCLI.exe'
& $CLI compile status --backend pipeline --timeout 10000
& $CLI test list --backend pipeline --mode EditMode --timeout 10000
```

要求当前 Unity 6 Editor 与注册的 Pipeline 包、项目/PID/Unity 版本及服务 descriptor 匹配。适配器捕获固定 loopback 地址和 bearer 身份，不接受调用方地址/命令，不跟随 redirect、代理或重试。查询前后目标变化、超时、reload、未知字段或损坏响应都不能成为成功证据。只读查询可以由调用方稍后显式重查；不恢复或认证任何旧操作。

## 编译状态

命令使用上游精确 `script recompile_status`，返回 `status=observed` 表示响应通过协议校验，`operationCorrelation=false` 表示它没有与本次调用关联的编译操作 ID。`compileStatus` 可为 idle、triggered、compiling、completed、up_to_date。

- `failed`、`compilationFailed` 是上游分别采样的标志；保留两者，不用一个覆盖另一个。
- `errors` 是观察到的错误文本，`observedDiagnosticCount` 只是该数组长度。
- `errorCount`、`warningCount`、`duration` 明确为 null：上游没有提供完整计数、警告或本次编译耗时。
- `idle`、`up_to_date` 或 `completed/failed=false` 都不能证明本次请求触发并完成编译。全局状态可被自动编译或其他调用覆盖，底层文件损坏甚至可能被上游归为 idle。
- 修复后 reload 前可出现 `completed/failed=false/compilationFailed=true`，因此该组合保留为观察结果。

需要验证编译结果请使用默认 `$CLI compile unity`。显式 `compile unity --backend pipeline` 仍拒绝；上游 `script recompile` 仅 Refresh，另有下列受限触发入口，不复用原生生命周期判据。

```powershell
& $CLI compile start --backend pipeline --confirm true --timeout 10000
& $CLI compile status --backend pipeline --requestId '<original-request-id>'
& $CLI compile release --backend pipeline --requestId '<original-request-id>' --confirm true
```

start 固定 focus=false；原子意图先写 `.aibridge/pipeline-compile-requests/`，绑定当前身份后仅发一次 POST。同 ID 只读既有记录，不重放。合法应答记为 acknowledged，acknowledgement 保留 up_to_date/compiling/failed；外层 success/退出0只说明触发应答被验证，`compilationSuccess/errorCount/warningCount/duration` 始终 null，operationCorrelation=false。所有应答均保持 busy，不能用上游全局槽自动解除门或确认本次完成。

start 的1000–60000ms只限制触发应答等待。reload、超时、HTTP/身份/IO不确定均 unknown并保持busy；后续原请求本地status不再调用上游。release仅精确acknowledged/unknown请求及confirm=true，状态变unknown/released=true/busy=false，不停止编译、不确认完成。真实Editor活动探针和Build/code/nativecapture/test双向门保持；释放后下次运行仍要满足实际空闲。

## 测试目录

`test list --backend pipeline` 只支持 EditMode。严格验证内层 Mode/Count/Tests 与每项 FullName/Mode/Assembly/Categories/Explicit，输出 `mode/count/tests`；Assembly 的显式 null 保留，目录条目保持上游 PascalCase 字段。

`listingIsExecution=false`：列出测试不代表执行通过，也不代表实际可执行数量。上游 Explicit fixture 过滤与目录中的 Explicit 标志可能不同。

目录不接受过滤参数。PlayMode、all、异步提交、外来 run ID 与远端取消都不开放。

## 受限同步测试运行

```powershell
& $CLI test run --backend pipeline --pipeline-filter 'MyNamespace.MyFixture' --filter-type testName --timeout 120000
& $CLI test status --backend pipeline --requestId '<original-request-id>'
& $CLI test release --backend pipeline --requestId '<unknown-request-id>' --confirm true
```

仅 EditMode，固定 async-tests=false、job=false、include-explicit=false。Pipeline testName/assembly 是大小写无关子串，category 是大小写无关全等；使用独立 `pipeline-filter/filter-type`，拒绝原生 test-name/group-name/assembly-name 及 JSON/异步开关。省略过滤会选择该模式全部非 Explicit 测试，不能将目录数量当作实际执行数量。

执行前先原子保存单请求意图到 `.aibridge/pipeline-test-runs/`，然后只提交一次 POST。CLI 后续只查询原始 requestId 的本地日志，不读取无归属的上游全局 test status，不自动重新提交。同 ID 不重放。UTC 时间、Summary/Results 每类计数、持续时间和响应 schema 都通过验证才接纳终态；`passed` 才是业务成功，failed、no_tests、skipped（全部跳过）和 inconclusive 都返回非零退出码。status 的外层 success 只表示查询成功，要看记录 success/status/executionEnded/busy。

真实 UTF `IsRunActive()` 与 AIBridge native 活跃/队列共同检查。探针不可用/异常时拒绝提交。AIBridge native 测试启动和出队也检查 Pipeline busy；Build、代码与原生 Profiler/Memory 写入口同受影响。该门只约束本插件，不能阻止其他插件或用户界面并发操作。

run timeout 为 1000–600000ms，默认 120000，只限制本地等待；传输超时、身份改变、reload 或不确定响应均保留 unknown/busy，禁止自动重放。显式 release 仅释放精确 unknown 请求的本地占位，不停止远端测试、不回滚副作用；迟到结果不能覆盖后续操作，后续运行仍须通过真实 UTF 空闲检查。remote cancel、PlayMode、异步单槽归属与跨 reload 重新附着均不支持。

## 结果边界

外层 success 只表示诊断查询通过验证。非 200、未知/缺失/重复键、错误大小写、类型和计数矛盾、非有限数值、超出有界响应等返回 failure/unknown，不用默认值补成空目录或零错误。凭据只留在捕获连接内存，不写结果；解析后对键和值脱敏，并拒绝脱敏键碰撞。

超时接受 1000–60000ms。此预算限制一次诊断观察，不向 Unity 发取消。主线程重载或服务变化时可能无法收到当前查询结果，应保留 unknown。真实验收和定向测试证据记录于项目 `.aibridge/plan/pipeline-compile-test-implementation.md`；触发只认证应答；同步测试及触发的真实验收范围以实施底稿记录为准。
