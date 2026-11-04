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

本轮不提供任意命令转发。`--unity-cli-path` / `--backend` 可覆盖配置，环境变量为 `AIBRIDGE_UNITY_CLI_PATH` / `AIBRIDGE_UNITY_CLI_BACKEND`。`--config` 指定配置文件，`--timeout` 为单次只读探测毫秒数（1–60000）。机器输出默认为 JSON；缺失 CLI 返回 `CLI_NOT_FOUND`，损坏配置或不支持参数返回 `INVALID_CONFIGURATION`，均不能当作增强可用。

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

主 Skill 的 `references/unity-cli-reference.md` 会在选择助手集成时同步到目标 Skill 根目录；安装器跳过 `.meta`，更新参考文件时保留生成的命令参考和用户文件。

## 回退与结果边界

- 原生 AIBridge 是兼容路径；可选增强未安装、关闭或能力不满足时，原生功能继续可用。
- 显式要求官方能力时，返回缺失原因；不要用原生操作伪装成官方操作成功。
- 一次操作开始后固定目标和后端。请求已发送、超时、连接丢失或结果未知时，不自动切换后端或重放写入；先查询原任务状态。
- 进程退出码为 0、HTTP 200 或外层 envelope 成功均不足以证明业务成功。嵌套失败、缺少终态、缺少必要计数应保留失败或未知语义。
- 取消请求不等于确认停止；官方脚本调用超时后代码可能继续运行。不能用“超时”推断“未执行”。
- 编译验证仍使用 `$CLI compile unity`。`compile dotnet` 只是附加检查，不是 Unity 编译或回退验证。
- 官方增强不能绕过 AIBridge 脚本开关、路径限制、目标授权与记录要求。

## 本轮范围与后续能力

本轮优先只读诊断与命令发现，以及隔离的可选 Pipeline 适配。Editor 适配程序集通过 `versionDefines` 和 `defineConstraints` 的 `AIBRIDGE_HAS_UNITY_PIPELINE` 宏隔离；未装包时不编译该适配程序集。运行时仍分别检测包版本、Editor 支持、descriptor 与服务状态；宏存在不证明服务可用，也不代表未来 Pipeline API 已验证。不要据此推断 `compile unity`、`code execute`、`test run` 或 Runtime 已自动切换至官方后端。

脚本执行、编译、PlayMode 测试、构建、Method Reload 和通用命令转发必须分别验证后发布。AIBridge 原生 `test run` 仍要求从 Edit Mode 启动；官方支持 PlayMode 测试不等于 AIBridge 此入口已经支持。官方本机 Player 通道也不替代 AIBridge 的移动设备/LAN Runtime Bridge。

## 诊断与信息保护

常见缺失应区分为：集成关闭、CLI 不存在、CLI 进程失败、包缺失或版本不匹配、Editor 不可达、Editor 未就绪、命令/schema 不支持。网络登录、Editor 授权与本地实例连通是不同条件，不能将云登录当作每次本地操作的前提。

保存诊断时可以记录版本、命令、规范化工程路径、退出码和脱敏错误。Pipeline bearer token、认证信息与 descriptor 原文不应写入共享日志、报告或 workflow artifact。直接 HTTP 接入必须另行承担 descriptor 更新、loopback 地址、身份及 token 验证，不属于普通用户配置步骤。

## 验证状态

2026-11-04 在 Windows / Unity 6000.0.51f1 实装 Unity CLI 1.0.0-beta.16 和 Pipeline 0.10.0-exp.1，官方 `command --project-path <工程> editor status` 返回 ready，AIBridge CLI 能力探测通过。真实安装修正了 `pipeline|pipe` 帮助别名识别、asmdef 包存在检测（空 versionDefines expression）和 descriptor 的 `evalToken` 字段。宏不代表任意版本 API 兼容，descriptor 存在不单独证明服务可达。测试范围见 [测试覆盖](UnityCliTesting.md)；其他 Unity 版本及业务命令委托仍未验证。
