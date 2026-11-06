# AIBridge 命令示例与进阶用法

[返回 README](../README_CN.md) | [English](CommandExamples.en.md)

以下为详细用法。初次安装请从 README 开始；具体参数以当前 CLI 的 --help 和安装后的 Skill references 为准。

## CLI 与命令参考

下面的命令示例默认折叠，日常使用时可以按需展开。完整命令说明也会随 Skills 安装生成到对应 `references/` 目录。

<details>
<summary>CLI 基础</summary>

AIBridge 复制 CLI 缓存后，在 Unity 项目根目录执行：

```powershell
& $CLI = "./.aibridge/cli/AIBridgeCLI.exe"
```

生成的 RootRule 会显式写明这个路径，并包含简洁的 PowerShell `$CLI` 用法。

macOS/Linux 可使用随包平台可执行文件，或按项目配置通过 `dotnet` 运行 DLL。

多数命令格式如下：

```powershell
& $CLI <command> <action> [options]
```

少数 CLI-only 或辅助命令格式不同：`focus` 和 `menu_item` 没有 action，`dialog` 使用 `status/click/wait`，`multi` 使用 `--cmd` 或 `--stdin`。

</details>

<details>
<summary>常用命令</summary>

### 编辑器、编译、日志、测试

```powershell
& $CLI focus
& $CLI dialog status
& $CLI dialog click --choice cancel
& $CLI editor log --message "Hello" --logType Warning
& $CLI editor get_state
& $CLI compile unity
& $CLI get_logs --logType Error --count 50
& $CLI get_logs --logType Warning --count 50
& $CLI get_logs --regex "NullReference|MissingReference"
& $CLI test run --mode EditMode
& $CLI test status
```

`focus` 通过项目外的 Editor 实例心跳定位当前工程的 Unity 窗口（Windows 默认 `%LOCALAPPDATA%/AIBridge/instances/<projectKey>/editor-instance.json`，可用 `AIBRIDGE_STATE_DIR` 覆盖根目录）。外部文件不存在时仍兼容读取遗留的 `.aibridge/editor-instance.json`。命令交换、结果、截图、workflow 等项目产物仍保留在 `.aibridge/`。

`test run` 必须在 Editor 处于 Edit Mode 时启动。如果 Unity 已经处于 Play Mode，命令会直接失败，并明确提示先退出 Play Mode 再重试。

Unity 验证必须使用 `compile unity`。`compile dotnet` 只能作为额外的解决方案构建检查，不能替代 Unity 编译。

`compile dotnet` 会用有界读取器持续排空子进程输出。原始输出最多保留 200,000 字符，诊断单行最多 32,768 字符，错误和警告各最多返回 1,000 条；结果仍会提供完整总数、过滤数、省略数和截断状态。

### Selection、菜单项和 Profiler

```powershell
& $CLI harness status
& $CLI selection get --includeComponents true
& $CLI selection set --path "Player"
& $CLI selection clear
& $CLI menu_item --menuPath "GameObject/Create Empty"
& $CLI profiler start
& $CLI profiler get_status
& $CLI profiler capture_frame
& $CLI profiler save_data --path ".aibridge/profiler/latest.json"
& $CLI profiler stop
```

`harness status` 仅用于可选诊断（项目侧能力以 Root Rule / workflow 规则为准），`selection` 用于管理 Unity 当前选择对象，`menu_item` 用于调用 Unity 菜单路径，`profiler` 用于 Editor 诊断和快照采集。

### Workflow Recipes

Workflow recipe 是确定性的运行产物模板。`workflow run-cli` 只执行 CLI、barrier 和 report 步骤；`agent`、`manual` 步骤会被记录下来，留给 Codex、Claude、Cursor 或人工执行。

```powershell
& $CLI workflow list
& $CLI workflow validate --recipe runtime-target-sweep
& $CLI workflow plan --recipe runtime-debug-investigation --format markdown
& $CLI workflow plan --recipe runtime-ui-validation --format markdown
& $CLI workflow init --recipe runtime-ui-validation
& $CLI workflow begin --recipe unity-change-implementation
& $CLI get_logs --logType Error --count 50 --workflow-run wf_20260529_213000_ab12cd34
& $CLI runtime screenshot --target latest --workflow-run wf_20260529_213000_ab12cd34
& $CLI workflow import --run wf_20260529_213000_ab12cd34 --step adversarial-verify --schema Verdict --file verdicts.json
& $CLI workflow export --recipe runtime-ui-validation --target codex-task-pack --output .aibridge/workflows/exports
& $CLI workflow run-cli --file ".aibridge/workflows/recipes/runtime-target-sweep.aibridge-workflow.json" --inputs ".aibridge/workflows/inputs.json"
& $CLI workflow run-cli --recipe performance-hotspot-investigation --inputs ".aibridge/workflows/perf-inputs.json" --timeout 30000
& $CLI workflow run-cli --recipe unity-sharded-review --allow-partial true
& $CLI workflow run-cli --recipe unity-sharded-review --resume wf_20260529_213000_ab12cd34 --rerun failed
& $CLI workflow status --run wf_20260529_213000_ab12cd34
& $CLI workflow report --run wf_20260529_213000_ab12cd34 --format markdown
& $CLI workflow finish --run wf_20260529_213000_ab12cd34 --status passed
& $CLI workflow clean --older-than 30d --dry-run true
& $CLI workflow clean --older-than 3d --save-settings true --auto-clean true
```

内置 recipes 包括 `bug-hunter-loop`、`harness-readiness-check`、`performance-hotspot-investigation`、`prefab-asset-sweep`、`runtime-debug-investigation`、`runtime-target-sweep`、`runtime-ui-validation`、`unity-change-implementation` 和 `unity-sharded-review`。

`performance-hotspot-investigation` 是常规性能热点一键报告。它会采集 Editor Profiler 快照、Runtime 状态/日志/截图证据和有边界的 `runtime perf` 采样，并在 Markdown 报告中整理 FPS、帧耗时、hitch、内存、GC、渲染、warning 和 unsupported 摘要。默认采样 15 秒，运行时应传入类似 `--timeout 30000` 的 workflow 超时。

`runtime-debug-investigation` 用于排查 Runtime、Player、Play Mode、UI、日志或性能症状。它优先检查证据是否完整，不把 Runtime 错误本身当作 workflow 失败条件；确认根因并需要修复时，再交接到实施工作流。

`workflow begin` 会创建 active run；普通命令可通过 `--workflow-run`、`AIBRIDGE_WORKFLOW_RUN_ID` 或 active run 指针归档证据。`workflow status` 和 `workflow report` 必须显式传 `--run`；需要 active run 时先读取 `.aibridge/workflows/active-run.json` 里的 run id。`workflow run-cli --resume <runId>` 会继续已有 run，但仍必须带 `--recipe` 或 `--file`，让 CLI 加载 recipe 定义。`--inputs` 优先传 JSON 文件路径；PowerShell inline JSON 很容易被 shell quoting 破坏。`workflow import` 保存 `Verdict` 等结构化外部结果，`externalVerdict` gate 只基于导入 artifact 通过。`workflow export` 生成外部工具交接包。`partial` workflow 状态默认不算 CLI 成功，只有显式传入 `--allow-partial true` 才按成功返回。`workflow status`、`workflow run-cli`、`workflow finish` 和 JSON `workflow report` 默认都是 compact；只有需要完整 manifest JSON 时才用 `--detail full`。精简输出保留 `terminalState`、`terminalReason`、`runDirectory`、`manifestPath`、`reportPath`、`artifactIds`、gate 摘要和外部缺口，`stepGaps`、`evidenceFreshness`、`failedCommands` 只在 full detail 中展开。

嵌套 `workflow run-cli` 的 stdout/stderr 每个流最多保留 1,000,000 字符。命令结果会返回 `stdoutCharsRead`、`stderrCharsRead`、`stdoutTruncated` 和 `stderrTruncated`；一旦发生截断，该步骤直接失败，避免把不完整 JSON 当成有效证据。

Workflow 清理是显式维护命令：`workflow clean` 默认只 dry-run，适合用户明确要检查或修剪 workflow 产物时使用。常规过期 run 目录由 AIBridge Settings > Cache/缓存清理策略负责；active run 会保留，旧 failed/blocked run 超过保留窗口后不再永久豁免。

Unity 命令超时且怀疑 Editor 被保存/确认弹窗阻塞时，使用 `dialog status` 检查。未检测到弹窗时，精简 JSON 不返回 `blockedByDialog` 和 `dialogs` 字段；字段不存在即表示无弹窗。macOS 检查和点击弹窗需要 Accessibility 权限。Unity 命令可显式指定超时处理，例如 `--on-dialog cancel` 或 `--on-dialog discard`。

`get_logs` 支持设置面板默认值和可选的正则筛选：

- 日志页签可设置默认最低日志等级和全局正则筛选。
- 如果不传 `--logType`，`get_logs` 会使用日志页签里的默认等级筛选。
- 如果传了 `--regex`，会先按日志内容正则匹配，再返回结果。
- 如果在日志页签里启用了全局正则，而本次没有传 `--regex`，则会自动应用全局正则。

### 模态弹窗处理

Unity 模态弹窗可能会阻塞 Editor 主线程，使普通 Unity 侧命令来不及被 AIBridge 处理。AIBridge 提供 CLI-only 的 `dialog` 辅助命令，可直接检查并点击操作系统层面的弹窗窗口，让 AI 在遇到保存、丢弃、取消、删除、替换和确认提示时能明确恢复流程，而不是盲猜。

```powershell
& $CLI dialog status
& $CLI dialog click --choice discard
& $CLI dialog click --button "Don't Save"
& $CLI dialog wait --timeout 5000 --click cancel
```

当 Unity 已被模态弹窗阻塞时，普通 Unity 命令会返回检测到的弹窗信息，而不是静默等待超时。每个启用按钮都会返回精确可见文本；只有能稳定映射时才返回 `choices` 逻辑选项，例如 `save`、`discard`、`cancel`。如果按钮没有逻辑选项，应使用精确 `--button` 文本点击；如果按钮被禁用，或多个弹窗/按钮同时匹配同一逻辑选项，AIBridge 会明确返回错误而不是猜测点击。无人值守流程可在 Unity 命令上加 `--on-dialog <choice>`，例如：

```powershell
& $CLI scene load --scenePath "Assets/Scenes/Main.unity" --on-dialog discard
```

Windows 上会通过窗口 API 检测弹窗按钮，并兼容 `&Don't Save` 这类按钮助记符。macOS 检查和点击弹窗需要 Accessibility 权限。

batch 脚本可以声明持续生效的弹窗自动点击规则。声明行执行后，后续步骤遇到弹窗时会自动点击第一个匹配的逻辑选项或可见按钮文本；后续再次声明 `dialog click` 会覆盖前一个策略。调用端需要保持等待；使用 `--no-wait` 后 CLI 进程退出，无法继续代点后续弹窗：

```text
dialog click ok | yes | Save
```

### 资源和场景

```powershell
& $CLI asset search "Player" --mode script --format paths
& $CLI asset search --mode script --keyword "Player" --format paths
& $CLI asset find --filter "t:Prefab" --format paths
& $CLI asset get_path --guid "abc123..."
& $CLI asset read_text --assetPath "Assets/Configs/GameConfig.asset"

& $CLI scene get_hierarchy --depth 3 --includeInactive false
& $CLI scene get_active
& $CLI scene load --scenePath "Assets/Scenes/Main.unity" --mode single
& $CLI scene save
```

### GameObject 和 Transform

```powershell
& $CLI gameobject create --name "MyCube" --primitiveType Cube
& $CLI gameobject find --withComponent "Rigidbody" --maxResults 20
& $CLI gameobject set_active --path "Player" --active true

& $CLI transform get --path "Player"
& $CLI transform set_position --path "Player" --x 0 --y 1 --z 0
& $CLI transform look_at --path "Player" --targetPath "Enemy"
& $CLI transform look_at --path "Player" --targetInstanceId 12345
& $CLI transform set_sibling_index --path "Canvas/Button" --first true
```

### Inspector 和 Prefab

```powershell
& $CLI inspector get_components --path "Player"
& $CLI inspector get_properties --path "Player" --componentName "Transform"
& $CLI inspector find_property --path "Player" --componentName "Rigidbody" --keyword "mass"
& $CLI inspector set_property --path "Player" --componentName "Rigidbody" --propertyName "mass" --value 10

& $CLI prefab get_info --prefabPath "Assets/Prefabs/Player.prefab"
& $CLI prefab get_hierarchy --prefabPath "Assets/Prefabs/Player.prefab" --includeComponents true
& $CLI prefab patch --prefabPath "Assets/Prefabs/Player.prefab" --ops ".aibridge/patch_ops/player_patch.json" --dryRun true
```

简单 Prefab 字段修改可用 `inspector set_property`，并传入 `assetPath + objectPath + componentName`。多步骤 Prefab 修改使用 `prefab patch --ops <file>`，正式写入前先执行 `--dryRun true`。AIBridge 不支持的 Prefab、Scene、ScriptableObjectTable 或自定义 `.asset` 结构修改，使用 `unity-yaml-editing` 作为直接 YAML 兜底规范。

PowerShell 传复杂 JSON 时建议先构造变量：

```powershell
$values = (@{ 'm_LocalPosition.x' = 0; 'm_LocalPosition.y' = 1 } | ConvertTo-Json -Compress) -replace '"', '\"'
& "./.aibridge/cli/AIBridgeCLI.exe" inspector set_properties --assetPath 'Assets/Prefabs/Player.prefab' --componentName Transform --values $values
```

### Batch 和 Multi

```powershell
& $CLI batch from_text --text "call editor log 'Hello'`ndelay 1000"
& $CLI batch from_file --file ".aibridge/scripts/setup_scene.txt"
& $CLI multi --cmd "editor log --message Step1&get_logs --logType Error --count 1"
```

长脚本或复杂 JSON 命令建议使用 `multi --stdin`：

```powershell
$script = @'
editor log --message "Start"
dialog click ok | yes | Save
delay 1000
get_logs --logType Error --count 1
'@
$script | & "./.aibridge/cli/AIBridgeCLI.exe" multi --stdin
```

</details>

<details>
<summary>运行时调试</summary>

### 运行时输入和视觉验证

`input` 命令用于 Play Mode 下的 UGUI/EventSystem 自动化验证，需要当前场景存在可用的 `EventSystem`。它可以按层级路径或实例 ID 点击对象，也可以点击屏幕坐标、归一化屏幕坐标、拖拽对象或执行长按。像素坐标和归一化坐标都统一使用 Unity 屏幕坐标系，原点固定为左下角；`click_pct` 的 `x`、`y` 取值范围为 `0` 到 `1`。

```powershell
& $CLI editor play
& $CLI input click --path "Canvas/StartButton"
& $CLI input click_at --x 960 --y 540
& $CLI input click_pct --x 0.5 --y 0.5
& $CLI input drag --path "Canvas/Item" --toPath "Canvas/Slot" --frames 12
& $CLI input long_press --instanceId 12345 --duration-ms 800

& $CLI gameview get_resolution
& $CLI gameview set_resolution --width 1920 --height 1080
& $CLI gameview list_resolutions
& $CLI screenshot game
& $CLI screenshot scene_view
& $CLI screenshot scene_view --width 1920 --height 1080
& $CLI screenshot editor_window --target editor
& $CLI screenshot editor_window --target active
& $CLI screenshot editor_window --windowType MyNamespace.MyCustomEditorWindow
& $CLI screenshot gif --frameCount 50 --fps 20 --scale 0.5
& $CLI editor stop
```

Game 视图截图、GIF 捕获和 `input` 命令都需要 Play Mode。Scene 视图和 Editor 窗口截图可在 Edit Mode 使用。`editor_window` 在 Windows 10 1803+ 或 macOS 12.3+ 上只捕获 Unity 所属的原生窗口像素，不截取桌面；可用 `--windowType`、`--title` 或 `--instanceId` 定位内置和自定义 EditorWindow。运行时 UI 推荐流程是进入 Play Mode，检查场景层级，执行 `input`，读取 Error 日志，再用截图或 GIF 复核画面。

### 已编译 Player Runtime Bridge

`runtime` 命令用于连接 Player 或 Play Mode 中的 `AIBridgeRuntime`。HTTP transport 是默认 Runtime 控制面，默认入口为 `http://127.0.0.1:27182`；端口被占用时 Runtime Bridge 会在小范围内自动递增，并把实际 URL 写入 heartbeat，方便同项目 CLI 自动解析当前目标。LAN discovery 从 UDP `27183` 开始，也会用同样方式自动递增，因此同一台机器上可同时运行多个 Editor 或已编译 Player，且不会共用一个发现端口。File transport 保留为本机兼容回退路径，但 Runtime 只会在 HTTP transport 未运行时轮询文件命令目录，HTTP command 结果会通过内存 pending 通道返回，不默认写入 file transport 的 `results` 目录。使用 File transport 时，已编译 Player 仍可通过启动参数 `--aibridge-runtime-dir <path>` 和 `--aibridge-target-id <id>` 指定目标。

默认情况下，HTTP `runtime list_targets` 使用 quick 路径：检查项目 heartbeat/cache 和已配置或显式传入的 URL，但不扫描本机自动递增端口范围。quick HTTP 健康检查使用短时有界重试；自动发现会遵循配置的 `discovery.udpPort`；并发 CLI 或 `AIBridge/Players` 发现请求会串行化缓存更新、合并仍新鲜的目标并原子发布缓存，使远程或手机 Player 的 `--target latest` 解析保持稳定。HTTP Runtime 最多接收 128 个排队 HTTP 命令；重复的活动/近期 command id 会以 `409` 拒绝，队列满会以 `429` 拒绝，health/status 会返回当前队列深度。需要本机端口扫描时执行 `runtime list_targets --probe true`，需要局域网 UDP 发现时执行 `runtime discover`。多个本机 Player 同时运行时，先执行 `runtime list_targets`，再传返回的 target id，例如 `runtime status --target AIBridgeDev_12345`；`runtime diagnose --target <id>` 会解析并深度检查该目标的实际 URL。

可在 `AIBridge/Settings > Runtime` 配置默认开关、HTTP 监听地址/端口、局域网发现、Editor Play Mode 自动注入、Development Build 自动注入、Release Build 允许项、后台保持运行、TargetId、Auth Token、Allowed Actions、heartbeat 间隔和日志缓存。Runtime heartbeat 默认 5 秒。Settings 页可写入 `.aibridge/runtime-config.json`，让 CLI 直接使用项目默认 Runtime 配置。`AIBridge/Players` 面板可查看 File heartbeat 目标、本机 HTTP 入口、局域网发现缓存、状态、场景、平台和常用 CLI 命令。已过期的 File/CACHE 条目会显示 `删除缓存` 按钮，可清理旧 target 目录或 discovery-cache 条目，不影响在线 Player。Editor Play Mode 自动注入默认启用，进入 Play Mode 且场景内没有 `AIBridgeRuntime` 时会创建临时隐藏 Runtime 对象。`后台保持运行` 默认对 Editor Play Mode 和 Development Build 启用，避免 Unity 失焦后 heartbeat 和 runtime 命令停止响应。UI 自动化建议先用 `runtime.ui.snapshot`，按钮条目会带屏幕坐标和屏幕矩形，后续就能直接点固定像素位置。snapshot/find 默认不做逐按钮 raycast；需要遮挡诊断时传入 `includeRaycastDetails=true`。

```powershell
& $CLI runtime list_targets
& $CLI runtime list_targets --probe true
& $CLI runtime status --target latest
& $CLI runtime discover
& $CLI runtime diagnose --target latest
& $CLI runtime logs --target latest --logType Error --count 100
& $CLI runtime perf --target latest --duration 5s --interval 100ms
& $CLI runtime screenshot --target latest
& $CLI runtime handlers --target latest
& $CLI runtime call --target latest --action qa.open_panel --json '{"panel":"Inventory"}'
& $CLI code runtime_execute --file ".aibridge/code/player_probe.csx" --target latest --timeout 10000
```

远程手机先执行局域网发现：`$CLI runtime discover`，再连接发现到的 target id 或 URL。Android USB 调试可先执行 `adb reverse tcp:27182 tcp:27182`，再通过 `--transport http --url http://127.0.0.1:27182` 连接；`adb` 不作为独立 Runtime transport。项目安装 HybridCLR 后，`code runtime_execute` 可以在 Editor 编译临时运行时安全 DLL，经 Runtime Bridge 发送到手机或已编译 Player，再在目标端通过 `Assembly.Load` 和反射调用入口方法。这个能力很适合移动端一次性诊断，尤其是状态、日志、截图这些通用命令无法直接表达的检查。

Runtime Bridge 不内置游戏内 LLM，也不提供无约束的项目方法反射 RPC。它的内置能力包括状态、日志、截图、性能、handler 列表/调用，以及 HybridCLR 和 Runtime Code Execution 开关允许时由 `code runtime_execute` 使用的显式 `runtime.code.execute` 动作。请把运行时代码执行视为可信调试入口。Release Build 默认关闭，只有项目明确接受安全边界、网络暴露、鉴权 Token 和运行时代码执行策略后，才应开启。

</details>

<details>
<summary>只读 Code Index</summary>

`code_index` 是 CLI-only 的只读轻量查询入口，默认关闭；只有项目需要快速做 C# 声明名定位时，才在 `AIBridge > Settings > Code Index > Enable Code Index` 中启用。它不要求打开 Rider、VS Code、Cursor、Visual Studio、Build Tools，也不要求 Unity 生成 solution。Unity Editor 会把编译快照写入 `.aibridge/code-index/snapshot/`；当前工程本地的 `AIBridgeCodeIndex` daemon 读取该快照，并直接消费 snapshot name index 做轻量查询。

```powershell
& $CLI code_index symbol --query PlayerController
& $CLI code_index definition --query PlayerController
```

该命令刻意保持只读：不做 rename、重构、自动修复或文件写入。`symbol` 只返回声明候选，`definition` 只按名称返回最佳声明位置。`--query` 应使用 C# 声明标识符（或其子串），不要用自然语言、中文业务名或注释文案。公开查询响应默认精简为命中列表与必要错误字段；快照/队列诊断请用 `status` / `doctor`。查到路径后，由 AI 继续直接读取 `.cs` 文件做真实分析。daemon 生命周期、快照构建、预热和恢复都属于内部实现细节，不再作为公开 AI CLI 契约的一部分。最终编译权威仍然是 `compile unity`。

启用 Code Index 后，Unity Editor 可在 `AIBridge > Settings > Code Index` 中生成快照、配置启动后空闲预热、快照自动刷新、PackageCache 源码是否纳入索引、忽略程序集/源码路径规则和退出清理策略。公开使用面继续保持极窄：只通过 `symbol` / `definition` 做声明名定位，daemon 启动、恢复和快照刷新都交给内部生命周期处理。

以下能力已明确从公开 `code_index` 能力面移除，因为它们在大型工程里通常过慢、噪音太大，或可靠性不够稳定：

- `references`
- `callers`
- `implementations`
- `derived`
- `diagnostics --all`
- 首轮完整 Roslyn workspace 语义预热 / 全工程语义建表
- 单队列 daemon 上的 `batch` 语义查询

</details>

<details>
<summary>高级 C# 执行</summary>

### Roslyn 临时 C# 执行

原生编译器优先使用 Unity 随附的 Roslyn：兼容旧版 `Tools/Roslyn`、`DotNetSdkRoslyn`，并按 SDK 版本探测新版 `DotNetSdk/sdk/<version>/Roslyn/bincore/csc.dll`；无需另装系统 SDK。探测/启动失败仍返回编译诊断，不自动执行源码或切换后端。

`code execute` 用于执行临时 Editor C#。它适合声明式 CLI 命令难以表达的复杂一次性任务，例如生成组合资源、批量诊断、结构化报告、调用项目 Runtime/Public API 或编排多步 UnityEditor API。它不是 `compile unity` 或 `test run` 的替代品。

`AIBridge/Settings > Basic` 中的 `Enable Code Execution` 同时约束 `code execute` 和 `code runtime_execute`。不可信项目或调用方环境中可在设置里关闭。文件模式只允许 `.aibridge/code/*.cs` 或 `.aibridge/code/*.csx`，复杂脚本优先使用文件模式。代码执行同一时间只允许一个任务；超时后先用 `code status` 查看状态，必要时再用 `code cancel` 释放 AIBridge 等待状态。

```powershell
& $CLI code execute --file ".aibridge/code/check.csx" --timeout 5000
& $CLI code execute --code 'Debug.Log("hello"); return 123;'
& $CLI code runtime_execute --file ".aibridge/code/player_probe.csx" --target latest --timeout 10000
& $CLI code runtime_execute --code "return Application.platform.ToString();" --transport http --url http://127.0.0.1:27182 --timeout 10000
& $CLI code status
& $CLI code cancel
```

`code runtime_execute` 是 `code execute` 的 Player 侧配套能力。它在 Editor 中编译运行时安全 DLL，经 Runtime Bridge 下发到目标 Player，并在目标端加载和执行。该能力只有安装 `com.code-philosophy.hybridclr`、全局代码执行开关已确认启用，且 `AIBridge/Settings > Runtime` 中保持 Runtime Code Execution 启用时才可用；只应在可信调试构建和明确的移动端/Player 诊断中使用。

</details>

## 推荐 AI 工作流程

1. 先解析真实 Unity 资源路径或对象路径。
2. 检查当前场景、Prefab、组件或 SerializedProperty 状态。
3. 通过 Unity 感知命令或源码编辑执行最小安全改动。
4. 执行 `compile unity`。
5. 读取 `get_logs --logType Error`。
6. 涉及运行时 UI 时，进入 Play Mode 后用 `input`、日志和截图/GIF 验证交互。
7. 只有需要快速做 C# 声明名定位时才启用 `code_index`，并仅使用 `symbol` / `definition`；拿到路径后直接读 `.cs` 文件，不把引用/调用者/继承分析继续压在 Code Index 上。
8. 只有在声明式命令不足以表达复杂一次性 Editor 或 Player 调试任务时，才启用并使用 `code execute` 或 HybridCLR 支持的 `code runtime_execute`。
