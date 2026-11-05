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

## 本轮受控 Pipeline eval 与安全协议验证（2026-11-05）

`CodeCommandTests|CodePipelineGateTests|PipelineCodeProtocolTests|CodePipelineOperationTests|PipelineReadinessCacheTests` 精确限定组 140/140 通过，包含双层回执/HTTP 矛盾保持 unknown、执行异常终态、诊断类型/坐标、适配器直调确认门禁、解码后 token 键值递归脱敏及碰撞失败、重复 JSON 键/嵌套上限、普通/迟到结果恢复、对象显式 null、超时/取消/reload、缺失 latest 的单日志恢复、损坏/矛盾状态 fail-closed 与 runtime_execute 参数拒绝。`compile unity` 0 errors/0 warnings；随后真实 Pipeline compile-only eval 成功返回 `validated`、HTTP 200、`mayStillBeRunning=false`，显式确认执行的受控异常返回 `execution_failed`/`RUNTIME_EXCEPTION`/HTTP 400 并可由 `code status` 读回；未确认执行在 HTTP 提交前拒绝。真实成功执行返回嵌套对象/数组/显式 null；另一次 `code status` 保留相同回值和 executionTimeMs。真实延时执行触发 HTTP 504/unknown 后，新请求被 single-flight 拒绝；显式 cancel 仅释放本地占位，remoteCancellationConfirmed=false。上游 timeout 无迟到终态接口，本次不声称确认远端停止；迟到 Task 结果恢复由单测覆盖。`output` 在已核实上游版本为空，不承诺捕获 Console/Debug 日志。真实 `.csx` 缺失符号诊断映射到源码第 4 行、第 13 列；这是 run 接入前 eval 切片结果；当前 run 验证见下，hotpatch 与 P2/P3 尚未实现。

## 受限 code run 验证（2026-11-05）

八组精确测试（上述五组加 `PipelineRunSourceTests|PipelineRunProtocolTests|UnityCliSkillInstallationTests`）共 290 项：289 通过、0 失败、1 跳过（runId `cmd_1793849018533_48c1f334e3f24a`）。跳过项是宿主 Mono profile 未提供符号链接创建 API 的真实 reparse 用例，不记为通过；另以 PowerShell 创建独立 Windows junction，真实调用 ValidatePath 已拒绝重解析祖先（用毕清理）；直属/扩展名/越界/大小、快照锁与正文固定、args 格式/深度/数字边界、布尔确认/授权/白名单、run 独立协议与超时/错误矩阵均已执行。Unity 6000.0.51f1 编译 0 errors/0 warnings，CLI 构建 0 errors/0 warnings。

真实 Pipeline 0.10.0-exp.1：缺省 run `validated`；确认执行 `[42,"probe"]` 返回对象、数组、显式 null，后续 status 保留结果与耗时。入口缺失与 args 不匹配为 `rejected`，同步受控异常 `execution_failed`，编译失败 `compile_failed`，均在 HTTP200 外层success时正确解释内层。诊断映射原始 `.cs` 第5行/第45列，堆栈映射原文件第19行。异步 delay 超过预算返回 HTTP504/unknown，新请求被single-flight拒绝，显式cancel仅释放本地占位。内部 HTTP200 `Timeout` 保unknown由 fixture覆盖，不声称远端取消。真实 Windows unknown 后显式cancel仍拒绝ReadWrite打开内部快照；未知快照保留源码和当前进程只读锁。reload/进程退出丢锁的残余边界已写文档，跨系统锁/断电/网络注入及 Unity2019.4/6000.6 未验证。

## 内部 generic job 观察协议验证（2026-11-05）

新增 `PipelineJobProtocolTests`，结合前述八组精确回归共343项：342通过/0失败/1跳过（原run符号链接API用例），runId `cmd_1793850617619_97e91e78d0e949`。覆盖固定verbose提交/裸status、13键与显式null、ID/command/UTC时序/阶段、progress/warnings、completed+run内层失败不被当业务成功、取消副作用未知、404/401/坏JSON/重复键/大小深度及内部适配器脱敏/碰撞/输入门禁。Unity compile 0 errors/0 warnings，Error日志为空。内部 `ObserveJobAsync` 真实查询不存在job返回httpStatus404、valid=false/status=unknown/executionEnded=false/sideEffectsUnknown=true；未开放CLI/提交/远端取消，不声称detached生命周期实测已完成。

## 显式 detached run 生命周期验收（2026-11-05）

十二组精确回归（上述九组加 `PipelineJobOperationTests|PipelineJobResultTests|PipelineRunJobSessionTests`）：417项416通过/0失败/1跳过，runId `cmd_1793853377995_69661cb163c34f`。唯一跳过仍是宿主符号链接创建API不可用。Unity编译0错误/0警告，Error日志为空；CLI build/publish通过并同步包内与项目单文件exe。覆盖一次性提交、当前世代固定身份变化、args嵌套null、confirm/类型/外来jobId门禁、严格业务终态、迟到结果、串行GET/取消、本地取消在途IO、存储写失败不重复POST且不丢原回执、旧同步回执同型修复、恢复unknown及本地cancel状态归一化。local list只读本地登记，坏记录计数/incomplete；bounded wait不发执行或取消。

真实Windows Unity6000.0.51f1 / Pipeline0.10.0-exp.1：detached compile-only为validated；Delay2500ms、本地timeout1000ms后原requestId终态executed并保留nested null；受控入口异常jobState=completed但业务status=failed；Delay8000ms远端cancel先requestAccepted=true/executionEnded=false，后canceled且副作用未知；local cancel后内部快照ReadWrite打开被拒；Delay10000ms期间compile触发domain reload，恢复unknown/busy=true而非重放，显式本地cancel后busy=false。真实job_list输出5项/total6及invalidRecords=1（旧格式记录原样保留）；job_wait读回原终态值。

未验证：排队未启动取消、跨进程重启、1小时retention实测、断电、跨操作系统文件锁及Unity2019.4/6000.6。fixture不代替这些实机矩阵；无上游执行超时、合作取消非回滚、reload丢锁边界见集成文档。

## P2/P3 集成与兼容复验（2026-11-05）

当前主工程 Unity6000.0.51f1 精确集成组904项：899通过、0失败、5跳过，runId `cmd_1793863274630_aed4f93be33e47`；编译触发最后防护补丁定向97项96通过、0失败、1跳过，runId `cmd_1793863800071_813eb024bbb842`。Unity编译0错误/0警告，CLI构建与契约测试通过，Error日志为空。跳过不计通过。

2019.4.40f1c1、6000.0.51f1、6000.6.0a7核心编译通过；6000.6最终定向489项444通过、0失败、45跳过，runId `cmd_1793863355631_5fb689e2f77a40`。隔离空工程缺saved scene及可选Pipeline adapter；2019.4未发现有效测试程序集，0项不计测试通过。上述编译证据不代表跨版本Pipeline实际执行或原生文件互读。

P2真实证据：当前Editor Profiler `.data` 3,343,124bytes保存/加载成功，内置Memory `.snap` 1,559,242,224bytes捕获完成；Build真实失败Report正确返回失败，本机缺IL2CPP C++工具链/Windows SDK，未验证成功Player产物。Memory分析load仍明确unsupported。

P3真实证据：Pipeline受控测试1/1通过，零匹配返回no_tests/非零退出，1000ms超时unknown保持busy，阻止新测试/native编译，原ID本地release不声称停止。编译触发 `cmd_1793863036566_f7fb6886374449` 收到compiling应答后reload恢复unknown/busy，互斥与本地release通过；后续共享传输测试1/1通过。所有compile acknowledgement仅确认Refresh应答，compilationSuccess/counts/duration保持null，不能认证本次编译结果。完整边界见 `PipelineDiagnostics.md`、`NativeBuild.md`、`NativeProfiling.md` 和项目实施底稿。

## 其它既有验证记录

AIBridgeCLI `dotnet build --no-restore` 通过（0 警告、0 错误）。`unity_cli help` 成功；用 `dotnet.exe` 作为显式 CLI 路径验证返回 `NOT_UNITY_CLI`，`--format text` 返回摘要而非 JSON。Unity `$CLI compile unity` 通过（0 errors / 0 warnings），`get_logs --logType Error` 为空。Editor NUnit 启动 174 项后发生 reload，AIBridge 丢失 run 状态，未取得可确认结果，不能记为测试通过。2026-11-04 后续实装验证：CLI 1.0.0-beta.16 / Pipeline 0.10.0-exp.1 / Windows Unity 6000.0.51f1。官方当前工程 editor status 曾返回 ready；旧版适配器曾把 descriptor 存在误报为 `serverReady=true`，该历史结果不代表 authenticated connectivity，已修正语义。当前适配器校验 PID/项目路径/Unity 版本，并以后台 bearer-auth `/api/status` 探测后才置 `serverReady`。修正帮助别名 pipeline|pipe、asmdef versionDefines 使用包存在检测、descriptor evalToken 字段。UnityCliSkillInstallationTests 限定组运行 5/5 通过，已验证主 Skill reference 同步、跨助手目标、更新保留用户/生成文件和无变化时间戳稳定；异常场景和其他版本兼容矩阵仍未验证。
