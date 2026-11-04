# Unity CLI 可选增强测试覆盖

## 执行边界

本轮实现 CLI 诊断与可选程序集、更新测试和文档；未安装官方 CLI/Pipeline，也未升级工程。下表是验收矩阵，不是已全部通过的实机结果。CLI 进程契约、Skill 安装和可选程序集静态检查不能替代 Unity 编译与 Editor 集成验证。

测试沿用包内 `Tests/Editor/AssistantIntegration` 的 NUnit 风格：临时工程、`SetUp`/`TearDown` 隔离、恢复全局设置、明确断言输出与退出码。外部进程测试仅启动 AIBridgeCLI，不启动 Unity；不得依赖开发机恰好安装官方 CLI。没有当前平台 CLI 构建时应明确跳过，不把跳过统计为通过。

已实现的 Editor NUnit 用例：`UnityCliSkillInstallationTests` 现包含 5 个测试用例（Codex、Claude、Cursor 三种目标，以及更新内容/保留用户与生成文件、无变化时保持时间戳），验证主 Skill 安装会复制可选诊断 reference、入口存在、内容与源文件一致且不复制 `.meta`。下表其余条目为测试设计或独立 CLI 验证范围，不表示本文件已提供对应 NUnit 用例。

## 第一阶段：诊断与能力检测

| 场景 | 关键断言 | 验证层 |
|---|---|---|
| 无配置 | 默认为 `native`，既有命令路由不变 | CLI 契约 |
| 显式 native | 官方增强不自动启用；原生仍可用 | CLI 契约 + 后续 Editor |
| auto 与 unity-cli | 策略字段准确；仅影响本轮诊断，不切换业务路由 | CLI 契约 |
| 配置 JSON 损坏/未知 backend | 明确失败/诊断，不能被当作有效增强配置 | CLI 契约 |
| 显式不存在 executablePath | 不调用 PATH 中其他可执行文件、不下载工具，返回缺失原因 | CLI 契约 |
| 路径含空格/中文 | 作为单个进程参数处理，工程身份不变 | 进程 fixture |
| version/help/Pipeline help | 仅构造允许的只读官方参数，不支持任意转发 | 进程 fixture |
| 未知 action/不支持参数 | 在启动官方进程前拒绝，不注入额外参数；`--topic` 不在允许列表 | CLI 契约 |
| 子进程非零退出/空输出/非法结果 | 不误报成功，保留诊断分类 | 进程 fixture |
| 超时与大 stdout/stderr | 有界等待/读取；不重试、不切后端；错误清晰 | 进程 fixture |
| CLI 存在而 Pipeline 缺失 | 区分 CLI 可用与 Pipeline 能力未确认 | CLI + 后续 Editor |
| 核心包无 Pipeline | 不引用强依赖程序集；原生低版本可编译 | 静态检查 + Unity 编译 |
| Skill 安装 | 安装按需 reference，主 Skill 提供入口，不复制 `.meta` | Editor NUnit |
| 双语说明 | 配置键、动作、范围及回退限制一致 | 文档契约 |

进程 fixture 应使用受控的可执行测试替身或可注入 runner，记录实际 argv 和启动次数，输出固定退出码/stdout/stderr。不要启动真实官方命令制造超时，也不要通过源码字符串匹配声称执行语义已验证。

## 第二阶段：真实 Editor 兼容矩阵

每次记录 Editor 完整版本、CLI 版本、Pipeline 包版本、操作、目标工程、结果和证据路径。至少包含：

| 环境 | 必须验证 |
|---|---|
| Unity 2019.4，无 Pipeline | 可选程序集被正确隔离；`compile unity` 与原生基本操作仍正常 |
| 当前 Unity 6000.0，无 Pipeline | 核心编译通过，报告可选包缺失，不影响默认通道 |
| Unity 6000.x，受测 Pipeline | 可选程序集编译、运行时能力识别、命令可见性与结果格式 |
| Unity 6000.6 | 新/弃用 API 兼容，不依赖旧对象查询签名 |
| 两个并存工程 | 只针对请求工程，不能误选另一实例 |
| 冷导入、编译中、模态弹窗 | 可达性与 ready 分开，不误报可执行 |
| reload / Editor 重启 | 会话与能力失效处理正确，不复用旧身份或 token |

使用项目规定的 `$CLI compile unity` 完成 Unity 编译验证，随后读取 Error 日志。独立 CLI 的 `dotnet build` 可单独记录，不能抵扣上述矩阵。未安装的版本明确标为“未验证”。

## 后续执行后端接入的发布门槛

下列用例不代表当前已发布能力；只有接入对应操作时才启动验收：

- 编译：成功、故意错误、缺失错误计数、嵌套失败、reload 丢失终态；错误绝不归一化为成功
- 测试：EditMode/PlayMode、过滤无命中、Explicit、断言失败与运行失败区分、取消请求与确认取消区分
- 脚本：执行开关关闭、路径越界、超时后仍运行、结果丢失；所有后端遵守相同授权与路径约束
- 恢复：同一请求仅提交一次，超时后查询原任务；任务丢失返回 unknown，不自动重放或切换后端
- 信息保护：错误输出、共享 artifact 和 descriptor 处理不泄露 bearer token 或认证信息
- Runtime：原有移动/LAN 目标不被 loopback 官方目标替代；Method Reload 独立验证适用限制

## Editor 安装菜单验证（2026-11-04）

`UnityCliPipelineInstallerTests` 30/30 通过，覆盖版本门槛、兼容版本选择、已有包判断、重入阻止、准确版本核验。当前 Unity 6000.0.51f1 的实际 UPM Search 返回 `latestCompatible=0.10.0-exp.1`；调用 `AIBridge/Install Unity CLI Dependencies` 后确认保留现有 Pipeline。Unity 编译 0 errors/0 warnings，Error 日志为空，CLI 项目构建通过。

这些单测为纯决策测试，不能替代首次下载或网络/域重载异常测试。当前工程已安装包，未卸载依赖制造测试条件；首次安装、域重载恢复及 Unity 2019.4/6000.6 实机兼容仍未验证。

## 本轮交接记录

AIBridgeCLI `dotnet build --no-restore` 通过（0 警告、0 错误）。`unity_cli help` 成功；用 `dotnet.exe` 作为显式 CLI 路径验证返回 `NOT_UNITY_CLI`，`--format text` 返回摘要而非 JSON。Unity `$CLI compile unity` 通过（0 errors / 0 warnings），`get_logs --logType Error` 为空。Editor NUnit 启动 174 项后发生 reload，AIBridge 丢失 run 状态，未取得可确认结果，不能记为测试通过。2026-11-04 后续实装验证：CLI 1.0.0-beta.16 / Pipeline 0.10.0-exp.1 / Windows Unity 6000.0.51f1。官方当前工程 editor status 返回 ready；AIBridge pipeline capabilities 成功；可选适配程序集加载、宏启用，GetStatus 返回 compiled/editorSupported/serverReady/IsAvailable 均 true。最终 compile unity 0 errors / 0 warnings，Error 日志为空。修正帮助别名 pipeline|pipe、asmdef versionDefines 使用包存在检测、descriptor evalToken 字段。UnityCliSkillInstallationTests 限定组运行 5/5 通过，已验证主 Skill reference 同步、跨助手目标、更新保留用户/生成文件和无变化时间戳稳定；异常场景和其他版本兼容矩阵仍未验证。
