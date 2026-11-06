# Unity CLI 可选增强

[English](UnityCliIntegration.en.md) | 简体中文

## 用途与兼容范围

Unity CLI 增强面向需要官方环境诊断、命令发现及项目自定义 Pipeline 命令的用户。AIBridge 保留项目本地统一入口与原生通道；不安装官方 CLI 或 `com.unity.pipeline` 仍可使用原有能力。

核心包兼容 Unity 2019.4+。官方 Pipeline 的实时 Editor 控制要求 Unity 6.0 LTS 或更新版本及兼容的 `com.unity.pipeline`；独立 CLI 的环境管理与实时控制应分别检测。满足 Editor 版本门槛不代表每个命令都可用，具体能力还取决于 CLI/Pipeline 版本、目标实例、命令参数和返回结构。

官方 CLI 与 Pipeline 仍属实验性能力。诊断不会自动下载安装、升级 Unity、安装 Pipeline、切换账户或更换项目依赖。安装和升级由用户明确决定。

## 从 Editor 安装项目依赖

点击 `AIBridge/Install Unity CLI Dependencies`，通过 Unity Package Manager 安装 `com.unity.pipeline`。该菜单安装的是 Unity CLI 的项目依赖；独立 `unity` 可执行程序仍需按官方指南安装。

- Unity 6.0 LTS+：查询 UPM 返回的当前 Editor 最新兼容版本，并使用精确版本安装；兼容版本可能为实验版。没有兼容版本时停止并说明原因。
- 已安装 Pipeline：保留当前版本，不自动升级或降级。低于 Unity 6 时说明不支持，AIBridge 原生能力仍可用。
- 安装异步执行并防止重复提交；包安装引起域重载后恢复检查实际版本。未知结果不会触发自动重装。
- 包安装成功不代表官方 CLI 已安装或 Pipeline 服务已就绪；等待导入/编译后，通过 `unity command --project-path . --format json editor status` 验证连接。

## 配置与调用

在项目根目录的 `.aibridge/unity-cli.json` 配置可选增强：

```json
{
  "schemaVersion": 1,
  "backend": "native",
  "executablePath": "",
  "probeTimeoutMs": 5000
}
```

`backend` 接受 `native`、`auto`、`unity-cli`。默认 `native` 保留原生通道；本轮 `auto` 仅参与诊断与能力报告，所有既有业务命令保持原生路由。`executablePath` 指定官方 CLI 的本机路径；`probeTimeoutMs` 限制诊断探测等待时间，不代表官方任务已停止。错误配置应报告具体原因，不能被当作已启用或成功。

从项目根目录调用项目本地 AIBridge CLI：

```powershell
$CLI = "./.aibridge/cli/AIBridgeCLI.exe"
& $CLI unity_cli version --raw
& $CLI unity_cli capabilities --raw
& $CLI unity_cli doctor --raw
& $CLI pipeline capabilities --raw
& $CLI unity_cli help
```

`version` 调用官方 `unity --version`。`doctor` / `capabilities` 执行只读的版本、官方帮助和 Pipeline 帮助探测，并返回结构化诊断；只有版本输出确认自身为 Unity CLI 且所需能力成功探测时，根 `available` 才为 `true`。`pipeline capabilities` 检查官方 `unity pipeline --help` 的可用性；它不证明 Editor 实例就绪或业务命令 schema 兼容。`unity_cli help` 是本地 AIBridge 命令帮助，不启动官方进程。

不提供任意命令转发。`code execute --dry-run true` 默认使用原生编译器；显式 `code execute --backend pipeline` 使用可选且版本受限的 Pipeline eval。执行必须同时给出 `--dry-run false --confirm true`；未显式确认时在提交前拒绝。调用沿用 Editor 双重授权、`.aibridge/code` 来源限制、审计、single-flight 与持久化状态；`.csx` 作为原始 code 传输，internals/auto-import 关闭。超时或未知结果不得重放或切换后端，取消只结束本地等待。受限 `code run` 已接入 Pipeline `script run`；hotpatch 与通用 Pipeline 命令转发仍未开放。

## 受控 Pipeline eval

```powershell
& $CLI code execute --backend pipeline --dry-run true --file .aibridge/code/check.csx --timeout 15000
& $CLI code execute --backend pipeline --dry-run false --confirm true --code 'throw new System.InvalidOperationException("controlled test")' --timeout 15000
& $CLI code status --requestId <原请求ID>
& $CLI code cancel --requestId <原请求ID>
```

Pipeline 缺省 `dry-run=true`，native 缺省 `dry-run=false`。Pipeline 执行需要 `--dry-run false --confirm true`；两项只接受布尔值，命令路由与可选适配器入口均复核授权和单次确认。

运行时版本门禁仅使用 UPM 已注册/加载包的 `version` 与 `resolvedPath`；不得用 `Library/PackageCache` 中任意残留目录授权请求。现仅支持 0.10.0-exp.1 的已核实协议。`.cs`/`.csx` 均先在 AIBridge 受限目录内读取为源码快照，再以内联 code 传送，保留原始 using、注释与行号；Pipeline 按 Roslyn script 语义编译，与原生包装方法体的语义不同。调用按请求指定 compile-only 或确认执行，并关闭 internals 与 auto-import；compile-only 不加载/执行程序集。HTTP 请求固定 loopback、PID/路径/版本/启动身份与 token，禁止重定向和重试。

返回 `backend=pipeline`、`operationId/requestId`、源码 SHA-256 与目标身份、`status`、`httpStatus`、执行结果/输出和诊断；执行成功为 `executed`，编译或运行异常为终态失败，`TIMEOUT`/传输不确定仍是 `unknown`。`id` 映射为 `code`，诊断行列统一为 1 基，缺失坐标保留未知。compile-only 成功须同时验证外层、内层 success 和此版本回执；不把 HTTP 200 当成成功。状态保存于 `.aibridge/code-operations/`，恢复时保留 `resultValue` 的对象/数组/显式 null 及 `executionTimeMs`。`output` 仅透传上游字段；当前已核实版本在执行成功时返回 null，不承诺捕获 Console/Debug 日志。双层错误码或 HTTP 语义矛盾、字段类型错误、重复 JSON 键、过深嵌套均保持 `unknown`。脱敏覆盖解码后的键和值，键碰撞拒绝结果。记录不存源码或 token；若用户代码主动把源码返回为结果，结果字段仍会包含该用户输出。

超时仅结束等待，操作继续占用 single-flight；迟到结果可通过 `code status --requestId` 查询，不覆盖已返回的超时结果。`code cancel` 仅取消等待，不确认远端停止；Task 结束前不释放占位。transport unknown 或 reload/restart 后保留 `unknown` 与本地占位，需显式 `code cancel` 才释放；该动作仍不证明远端已停止。禁止自动重放相同请求 ID 或切换后端重试。状态写入要求原子替换，失败保留旧记录；`latest` 丢失时校验单请求日志并恢复 busy 占位，损坏或矛盾状态不当作 idle。

## 受限 Pipeline run

```powershell
& $CLI code run --file .aibridge/code/task.cs --entry Tools.Task.Run --args '[42,"label"]' --timeout 15000
& $CLI code run --file .aibridge/code/task.cs --entry Tools.Task.Run --args '[42,"label"]' --dry-run false --confirm true --timeout 15000
& $CLI code status --requestId <原请求ID>
```

仅支持 Pipeline `0.10.0-exp.1`、固定 `ephemeral`；不接受 inline code、`.csx`、子目录、链接/重解析路径、自动入口、hotpatch、references/defines 或任意命令。源码必须为 `.aibridge/code` 直属 `.cs` 完整类型文件（不经过 eval 的语句包装），上限 512 KiB。`entry` 必须明确 `Type.Method` / `Namespace.Type.Method` 静态入口；建议用完整类型名并避免重载。`args` 是单个 JSON 数组，最多 32768 字符/16 层（含包装），对象 null 保留；数组参数需嵌套数组。入口解析和参数转换使用上游语义，不保证 ValueTask/Awaitable 自动等待。

缺省 compile-only，**只证明编译，不验证 entry 存在或 args 与方法签名匹配**。实际执行仍须双重项目授权和 `--dry-run false --confirm true`；每次实际加载的程序集不可单独卸载。run 独立验证双层回包：即使 HTTP 200/外层 success=true，内层也可能是编译失败、入口/参数拒绝或运行异常。内部 `Timeout`、HTTP 504、未知 Failure/矛盾字段均保持 unknown，不重放。

源码捕获一次后写至 `.aibridge/tmp/pipeline-run/<随机ID>.cs`，使用原路径 `#line`，Windows 下持有只读共享锁并复核正文；上游仅读取内部快照，不再读用户可变源文件。明确终态清理本次快照；unknown 保留临时源码和当前进程句柄，显式 cancel 只释放操作占位。reload/进程退出会失去文件锁，不能据此保证排队远端已停止或后续文件未变；应保留 unknown 并人工核实。此路径门禁不是 C# 安全沙箱。操作日志只保存原来源、源码/args SHA-256、entry/目标与结果，不另存 args 或源码正文。

## 显式 detached run 作业

```powershell
& $CLI code run --file .aibridge/code/task.cs --entry Tools.Task.Run --args '[42]' --dry-run false --job true --confirm true --timeout 5000
& $CLI code status --requestId <原请求ID>
& $CLI code job_list --limit 20
& $CLI code job_wait --requestId <原请求ID> --timeout 10000
& $CLI code job_cancel --requestId <原请求ID> --confirm true
```

`code run` 缺省 `job=false`，保持既有执行超时；显式 `--job true` 使用固定 detached 提交，**上游执行没有超时**，此时 `timeout` 只限制本地等待（1000–60000 ms）。包括 compile-only 在内，每次 detached 提交均须 `--confirm true`；实际执行另须 `--dry-run false`。`code execute` 不接受 job 参数。

提交意图先原子落盘，取得 `remoteJobId` 后绑定原请求；源码、参数正文及 token 不写作业日志。固定首次捕获的 PID/port/project/version/startedAt、内存 token 与适配器世代，禁止外来 ID 控制、重定向、代理、POST 重试及后端回退。每次响应严格解析和脱敏；只有正确 job envelope 与独立 run 业务 payload 都通过时才确认业务终态。`jobState=completed` 可对应 `status=failed`；裸 job 无执行耗时，`executionTimeMs` 保持未知而非 0。

原 CLI 等待超时后，Editor 仍每 500 ms 串行观察该作业；迟到终态更新原持久记录，不覆盖已交付回执。`code status` 返回最近观察/持久记录，`job_list` 只列本地登记（最多扫描4096文件，不枚举远端），`job_wait` 有界观察原记录，不发执行或取消请求。`code cancel` 仍只释放本地等待，待已在途 IO 完成后释放占位，并继续保留未知快照只读锁；之后该作业不再自动轮询。

`job_cancel` 是独立的逐次确认动作，仅控制当前 Editor 世代仍活动且已绑定的原请求；返回 `requestAccepted`、`executionEnded`、`notStarted`、`sideEffectsUnknown`，HTTP200 或 cancellationRequested 不等于执行已停止。合作取消可能在全部副作用之后才成为 canceled，不代表回滚，后台子任务也可能继续。已终态的记录不再发送取消 POST。

404/401/断连/身份变化/坏响应均保留 unknown 和 single-flight，不重放。状态写失败保留占位，不重复 POST。job 仅在上游内存保留（终态最多100条/1小时）；reload/restart 后不会重建会话，原 ID 只供审计，恢复 unknown 必须显式本地 cancel；token 相同也不能证明作业连续。无 jobId 的普通 eval/run unknown 不可用 job 查询恢复。源码锁仍有 reload/进程退出边界，见上节。

## 能力检测

按任务需要读取诊断和相关命令，避免在每次 AIBridge 操作前做全量扫描。

| 检查层 | 需要确认的事实 | 失败时的处理 |
|---|---|---|
| 集成设置 | 当前是否启用可选增强、可执行文件配置是否有效 | 保留原生 AIBridge；不要静默启用 |
| CLI | 实际可执行路径、版本、进程可启动 | 报告缺失或启动失败，不下载安装 |
| 工程 | 规范化项目路径、Editor 版本、Pipeline 包版本 | 不把其他工程的可用状态用于当前工程 |
| 实例 | 目标工程、实例身份、连接和就绪状态 | 连接成功不等于可执行；报告未就绪或身份不明 |
| 命令 | 目标命令存在、参数 schema 和结果语义符合当前操作 | 未验证的映射不自动使用 |

CLI 命令清单与运行中 Editor 的命令清单不同。发现某个命令仅说明它存在，不代表 AIBridge 已支持其全部参数或完成结果归一化。仅加载当前任务所需命令的 schema，不将完整官方目录写入 RootRule。

主 Skill 的 `references/unity-cli-reference.md` 会在选择助手集成时同步到目标 Skill 根目录；安装器跳过 `.meta`，更新参考文件时保留生成的命令参考和用户文件。带生成标记的包内命令参考快照不参与模板复制，由当前 Editor 按版本生成；内容不变时重装保持文件时间戳，不因对象 ID 参数名差异反复覆写。

Pipeline 提交前先校验本地持久状态，再探测可选 adapter；缺少 Pipeline 包不能掩盖损坏日志或未解决的 busy 占位。重启恢复允许向原请求写入失败回执，但不重新提交原任务；不能把 result 文件存在当作任务已重放。

## 回退与结果边界

- 原生 AIBridge 是兼容路径；可选增强未安装、关闭或能力不满足时，原生功能继续可用。
- 显式要求官方能力时，返回缺失原因；不要用原生操作伪装成官方操作成功。
- 一次操作开始后固定目标和后端。请求已发送、超时、连接丢失或结果未知时，不自动切换后端或重放写入；先查询原任务状态。
- 进程退出码为 0、HTTP 200 或外层 envelope 成功均不足以证明业务成功。嵌套失败、缺少终态、缺少必要计数应保留失败或未知语义。
- 取消请求不等于确认停止；官方脚本调用超时后代码可能继续运行。不能用“超时”推断“未执行”。
- 编译验证仍使用 `$CLI compile unity`。`compile dotnet` 只是附加检查，不是 Unity 编译或回退验证。
- 官方增强不能绕过 AIBridge 脚本开关、路径限制、目标授权与记录要求。

## 本轮范围与后续能力

本轮优先只读诊断与命令发现，以及隔离的可选 Pipeline 适配。Editor 适配程序集通过 `versionDefines` 和 `defineConstraints` 的 `AIBRIDGE_HAS_UNITY_PIPELINE` 宏隔离；未装包时不编译该适配程序集。运行时分别检测包版本、Editor 支持与 descriptor 身份（PID、项目路径、Unity 版本）；`serverReady` 只有在后台 bearer-auth `/api/status` 返回 ready 时才为 true；当前 Editor 身份另由 descriptor 的 PID、项目路径和 Unity 版本校验。探测不阻塞 Editor 主线程，也不暴露 token。宏或 descriptor 均不证明任意 Pipeline API 已验证。不要据此推断 `compile unity`、`code execute`、`test run` 或 Runtime 已自动切换至官方后端。

脚本执行、编译、PlayMode 测试、构建、Method Reload 和通用命令转发必须分别验证后发布。AIBridge 原生 `test run` 仍要求从 Edit Mode 启动；官方支持 PlayMode 测试不等于 AIBridge 此入口已经支持。官方本机 Player 通道也不替代 AIBridge 的移动设备/LAN Runtime Bridge。

## 诊断与信息保护

常见缺失应区分为：集成关闭、CLI 不存在、CLI 进程失败、包缺失或版本不匹配、Editor 不可达、Editor 未就绪、命令/schema 不支持。网络登录、Editor 授权与本地实例连通是不同条件，不能将云登录当作每次本地操作的前提。

保存诊断时可以记录版本、命令、规范化工程路径、退出码和脱敏错误。Pipeline bearer token、认证信息与 descriptor 原文不应写入共享日志、报告或 workflow artifact。直接 HTTP 接入必须另行承担 descriptor 更新、loopback 地址、身份及 token 验证，不属于普通用户配置步骤。

## 验证状态

2026-11-04 在 Windows / Unity 6000.0.51f1 实装 Unity CLI 1.0.0-beta.16 和 Pipeline 0.10.0-exp.1，官方 `command --project-path <工程> editor status` 返回 ready，AIBridge CLI 能力探测通过。真实安装修正了 `pipeline|pipe` 帮助别名识别、asmdef 包存在检测（空 versionDefines expression）和 descriptor 的 `evalToken` 字段。宏不代表任意版本 API 兼容，descriptor 存在不单独证明服务可达。测试范围见 [测试覆盖](UnityCliTesting.md)；其他 Unity 版本及业务命令委托仍未验证。
