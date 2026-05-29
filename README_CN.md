<p align="center">
  <img src="./Images/aibridge-banner.png" alt="AIBridge banner" width="100%">
</p>

# AIBridge

[English](./README.md) | 中文

![Unity 2019.4+](https://img.shields.io/badge/Unity-2019.4%2B-black?style=flat-square&logo=unity)
![Package 1.4.2](https://img.shields.io/badge/Package-1.4.2-5b6cff?style=flat-square)
![MIT License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)
![AI Unity Automation](https://img.shields.io/badge/Workflow-AI%20Unity%20Automation-14b8a6?style=flat-square)

> 设计准则：**简单、易用、稳定**。

AIBridge 现在已经不再是单纯类似 MCP 的连接工具。它更接近一个 AI Unity 开发 harness 框架，把项目本地工作流、AIBridge Skills、CLI 与 Runtime 工具、代码索引、视觉验证、输入模拟、Player 调试以及 AI 工具集成组合成围绕 Unity 项目的实用闭环。

当前版本仍然不够完善，但它刻意保持简单易用：安装 Package，安装 Codex 等 AI 工具集成后，开发者就能直接体验真实的 AI 辅助 Unity 开发。结合 Codex + AIBridge，Unity 开发体验已经形成从设计、开发、编译、测试、运行时验证、截图/日志到自动调试的闭环。

它可以让 AI 定位真实 Unity 资源路径、检查场景和 Prefab、通过 Unity API 编辑对象、执行 Unity 编译、读取 Console 日志、运行批处理流程、执行测试，模拟 UGUI/EventSystem 运行时点击与拖拽，连接已编译 Player 查询状态、日志和截图；当项目安装 HybridCLR 时，还可以在 Editor 动态编译临时运行时代码并发送到目标 Player 执行。

## 为什么选择 AIBridge

很多 Unity 自动化工具停留在持续在线的 socket、MCP 会话或薄命令桥接层。AIBridge 更像是包在连接层外面的 harness：它会把工作流规范和 Skills 安装到 AI 工具中，暴露 Unity 感知的 CLI 与 Runtime 能力，并把命令结果、日志、截图、GIF、代码索引结果和运行时诊断这些验证产物留在项目本地。

AIBridge 的 Editor 自动化使用落盘命令请求和结果文件，Player 调试使用 HTTP Runtime 控制面，因此能更好地应对脚本重编译、域重载、编辑器焦点变化、重启以及设备/Player 会话变化。

| 维度 | AIBridge | MCP / 持续连接型桥接 |
|---|---|---|
| 连接方式 | Editor 文件请求 + HTTP Runtime 目标 | 在线会话或工具服务 |
| 编译周期适应 | 可轮询并跨重载继续；Runtime 目标可重新发现 | 会话可能中断 |
| 部署方式 | 随包 CLI 命令 | 服务端/客户端配置 |
| 多工程识别 | 项目本地自动无感识别；从 Unity 项目根目录执行即可，无需额外映射、注册或手动选择项目 | 其它 MCP 工具已有支持，但通常依赖服务端/工具配置或当前项目选择状态 |
| AI 集成 | 项目本地工作流、Skills、CLI、JSON 输出和工具适配 | 特定协议工具 |
| 任务追踪 | 命令文件、结果、日志、截图、GIF、代码索引结果和运行时诊断 | 当前会话状态 |
| 扩展方式 | Unity 命令、Runtime handlers、CLI Builder、Skills 和推荐 Skill 库 | 工具服务扩展 |
| 只读代码索引 | 不依赖 IDE 会话的 `code_index` daemon，支持符号、定义、引用、实现、调用者和诊断查询 | 通常绑定 IDE/插件会话 |
| 移动端 Player 调试 | LAN/USB HTTP Runtime Bridge 支持状态、日志、截图、性能、handler，以及 HybridCLR 门控的 `code runtime_execute` | 通常需要为具体工具额外实现运行时服务支持 |

## 核心能力

- **Unity 资源和对象检查**：查找资源、读取场景层级、检查组件和 SerializedProperty，并通过 Unity API 执行安全写入。
- **Prefab 和场景自动化**：支持简单 Inspector 字段修改、Prefab Patch dry-run、多步骤批处理和跨域重载后的任务继续。
- **UGUI 运行时输入模拟**：Play Mode 下通过 `input` 命令模拟点击、坐标点击、拖拽和长按，适合验证按钮、背包拖放、运行时面板等基于 EventSystem 的交互。
- **Player Runtime Bridge**：已编译 Player 中的 `AIBridgeRuntime` 可暴露运行时状态、日志、截图、性能采样、项目白名单 handler，以及 HybridCLR 门控的运行时代码执行，适合 Development Build 和移动端调试。
- **只读 Code Index**：启用后，`code_index` 会启动不依赖 IDE 的 daemon，读取 Unity 编译快照并用于符号、定义、引用、实现、调用者和诊断查询；该能力默认关闭，降级为文本搜索时会明确返回 `semantic=false`。
- **Workflow recipes 和运行产物**：`workflow` CLI 可列出、校验、规划、初始化并运行内置 Unity workflow recipe 中的确定性 CLI 步骤，并在 `.aibridge/workflows/runs/` 下写入 run manifest、命令结果、artifact、gate 和 Markdown 报告。
- **Roslyn 临时 C# 执行**：通过受控的 `code execute` 在 Unity Editor 内执行 `.aibridge/code/*.cs` 或 `.csx` 临时脚本，用于复杂一次性资源生成、结构化分析、诊断和 Runtime/Public API 调用。该能力在设置中默认启用，不可信项目或调用方环境中可在设置里关闭。
- **视觉和日志验证**：支持 Game/Scene 视图截图、GIF、Console 日志读取、Unity 编译和测试命令，帮助 AI 闭环确认改动结果。

## 系统要求

- Unity 2019.4 或更高版本。
- .NET 8.0 Runtime，用于随包 CLI。
- Unity 侧命令需要 Unity Editor 正在运行，例如 `compile unity`、`asset`、`scene`、`inspector`、`prefab`、`input`、`screenshot`、`code`、`get_logs`。
- `code_index` 默认关闭；需要语义查询时，请在 `AIBridge > Settings > Code Index` 中启用。它依赖 Editor 生成的 Unity 编译快照，不依赖 `.sln/.csproj`、Visual Studio、Rider 或 Build Tools；缺少快照时可在 AIBridge 设置中执行 Code Index 预热。
- `runtime` 命令需要 Player 或 Play Mode 场景中存在 `AIBridgeRuntime` 组件；Editor Play Mode 在启用 Runtime Bridge 时可自动注入。`code runtime_execute` 还需要安装 HybridCLR 包并启用 Runtime Code Execution。Release Build 默认关闭 Runtime Bridge，需项目显式开启。

## 安装

在 Unity Package Manager 中使用下面的 Git 地址安装：

```text
https://github.com/liyingsong99/AIBridge.git
```

UPM 备用 Git 地址：

```text
https://gitee.com/lijoujou99_admin/AIBridge.git
```

也可以将本仓库克隆到 Unity 项目的 `Packages` 目录下。

## 配置 AI 工作流

1. 在 Unity Editor 中打开 `Tools > AIBridge Settings`。
2. 进入 `Skills Installation` 区域。
3. 勾选正在使用的 AI 工具。
4. 点击 `Install Selected Integrations`。
5. 可选：点击 `Install Unity Project AGENTS.md Template`，在项目根目录创建 `AGENTS.md`。

安装后的 AIBridge Skills 默认写入各已选工具自己的默认 skills 目录，例如 Codex 使用 `.codex/skills/`。也可以在 Skills 安装页签设置自定义目录，但自定义目录可能无法被 AI 工具自动发现。不同 AI 工具只写入各自的最小 RootRule；只有自定义目录需要时才写入插件适配层并引用该 Skill 根目录。RootRule 只包含固定 CLI 路径、常用命令、Skill 根目录和 `aibridge-development-workflow` 入口；完整路由和检查清单由 workflow Skill 维护。高级 Workflow 编排规则会作为 AIBridge Skill 一起安装，只在多 Agent workflow、对抗验证、recipe 或 Runtime 多目标 sweep 任务中加载。命令说明会生成到各 Skill 的 `references/` 目录。

如果项目有需要，也可以在 `Recommended Skill Library / 推荐 Skill 库` 页签刷新默认的 `obra/superpowers` 推荐仓库，并将其中的第三方 Skill 安装到已选工具的 skills 目录。

## CLI 与命令参考

下面的命令示例默认折叠，日常使用时可以按需展开。完整命令说明也会随 Skills 安装生成到对应 `references/` 目录。

<details>
<summary>CLI 基础</summary>

AIBridge 复制 CLI 缓存后，在 Unity 项目根目录执行：

```powershell
$CLI = "./.aibridge/cli/AIBridgeCLI.exe"
```

macOS/Linux 可使用随包平台可执行文件，或按项目配置通过 `dotnet` 运行 DLL。

多数命令格式如下：

```bash
$CLI <command> <action> [options]
```

少数 CLI-only 或辅助命令格式不同：`focus` 没有 action，`dialog` 使用 `status/click/wait`，`multi` 使用 `--cmd` 或 `--stdin`。

</details>

<details>
<summary>常用命令</summary>

### 编辑器、编译、日志、测试

```bash
$CLI focus
$CLI dialog status
$CLI dialog click --choice cancel
$CLI editor log --message "Hello" --logType Warning
$CLI editor get_state
$CLI compile unity
$CLI get_logs --logType Error --count 50
$CLI get_logs --logType Warning --count 50
$CLI get_logs --regex "NullReference|MissingReference"
$CLI test run --mode EditMode
$CLI test status
```

Unity 验证必须使用 `compile unity`。`compile dotnet` 只能作为额外的解决方案构建检查，不能替代 Unity 编译。

### Workflow Recipes

Workflow recipe 是确定性的运行产物模板，不是内置 LLM 调度器。`workflow run-cli` 只执行 CLI、barrier 和 report 步骤；`agent`、`manual` 步骤会被记录下来，留给 Codex、Claude、Cursor 或人工执行。

```bash
$CLI workflow list
$CLI workflow validate --recipe runtime-target-sweep
$CLI workflow plan --recipe runtime-ui-validation --format markdown
$CLI workflow init --recipe runtime-ui-validation
$CLI workflow run-cli --file ".aibridge/workflows/recipes/runtime-target-sweep.aibridge-workflow.json"
$CLI workflow status --run wf_20260529_213000_ab12cd34
$CLI workflow report --run wf_20260529_213000_ab12cd34 --format markdown
$CLI workflow clean --older-than 30d --dry-run true
$CLI workflow clean --older-than 3d --save-settings true --auto-clean true
```

内置 recipes 包括 `unity-change-implementation`、`unity-sharded-review`、`runtime-target-sweep`、`runtime-ui-validation`、`prefab-asset-sweep` 和 `bug-hunter-loop`。

Workflow 清理默认保守：`clean` 默认只 dry-run。只有保存到 `.aibridge/workflows/settings.json` 后才会启用自动清理；之后 `run-cli` 开始前会按设置清理旧 run，并保留失败/阻塞 run 和最新保留数量。

Unity 命令超时且怀疑 Editor 被保存/确认弹窗阻塞时，使用 `dialog status` 检查。未检测到弹窗时，精简 JSON 不返回 `blockedByDialog` 和 `dialogs` 字段；字段不存在即表示无弹窗。macOS 检查和点击弹窗需要 Accessibility 权限。Unity 命令可显式指定超时处理，例如 `--on-dialog cancel` 或 `--on-dialog discard`。

`get_logs` 支持设置面板默认值和可选的正则筛选：

- 日志页签可设置默认最低日志等级和全局正则筛选。
- 如果不传 `--logType`，`get_logs` 会使用日志页签里的默认等级筛选。
- 如果传了 `--regex`，会先按日志内容正则匹配，再返回结果。
- 如果在日志页签里启用了全局正则，而本次没有传 `--regex`，则会自动应用全局正则。

### 模态弹窗处理

Unity 模态弹窗可能会阻塞 Editor 主线程，使普通 Unity 侧命令来不及被 AIBridge 处理。AIBridge 提供 CLI-only 的 `dialog` 辅助命令，可直接检查并点击操作系统层面的弹窗窗口，让 AI 在遇到保存、丢弃、取消、删除、替换和确认提示时能明确恢复流程，而不是盲猜。

```bash
$CLI dialog status
$CLI dialog click --choice discard
$CLI dialog click --button "Don't Save"
$CLI dialog wait --timeout 5000 --click cancel
```

当 Unity 已被模态弹窗阻塞时，普通 Unity 命令会返回检测到的弹窗信息，而不是静默等待超时。返回内容包含可见按钮文本和 `save`、`discard`、`cancel` 等逻辑选项，方便 AI 决定下一步显式点击。无人值守流程可在 Unity 命令上加 `--on-dialog <choice>`，例如：

```bash
$CLI scene load --scenePath "Assets/Scenes/Main.unity" --on-dialog discard
```

Windows 上会通过窗口 API 检测弹窗按钮，并兼容 `&Don't Save` 这类按钮助记符。macOS 检查和点击弹窗需要 Accessibility 权限。

batch 脚本可以声明持续生效的弹窗自动点击规则。声明行执行后，后续步骤遇到弹窗时会自动点击第一个匹配的逻辑选项或可见按钮文本；后续再次声明 `dialog click` 会覆盖前一个策略。调用端需要保持等待；使用 `--no-wait` 后 CLI 进程退出，无法继续代点后续弹窗：

```text
dialog click ok | yes | Save
```

### 资源和场景

```bash
$CLI asset search --mode script --keyword "Player" --format paths
$CLI asset find --filter "t:Prefab" --format paths
$CLI asset get_path --guid "abc123..."
$CLI asset read_text --assetPath "Assets/Configs/GameConfig.asset"

$CLI scene get_hierarchy --depth 3 --includeInactive false
$CLI scene get_active
$CLI scene load --scenePath "Assets/Scenes/Main.unity" --mode single
$CLI scene save
```

### GameObject 和 Transform

```bash
$CLI gameobject create --name "MyCube" --primitiveType Cube
$CLI gameobject find --withComponent "Rigidbody" --maxResults 20
$CLI gameobject set_active --path "Player" --active true

$CLI transform get --path "Player"
$CLI transform set_position --path "Player" --x 0 --y 1 --z 0
$CLI transform look_at --path "Player" --targetPath "Enemy"
$CLI transform look_at --path "Player" --targetInstanceId 12345
$CLI transform set_sibling_index --path "Canvas/Button" --first true
```

### Inspector 和 Prefab

```bash
$CLI inspector get_components --path "Player"
$CLI inspector get_properties --path "Player" --componentName "Transform"
$CLI inspector find_property --path "Player" --componentName "Rigidbody" --keyword "mass"
$CLI inspector set_property --path "Player" --componentName "Rigidbody" --propertyName "mass" --value 10

$CLI prefab get_info --prefabPath "Assets/Prefabs/Player.prefab"
$CLI prefab get_hierarchy --prefabPath "Assets/Prefabs/Player.prefab" --includeComponents true
$CLI prefab patch --prefabPath "Assets/Prefabs/Player.prefab" --ops ".aibridge/patch_ops/player_patch.json" --dryRun true
```

简单 Prefab 字段修改可用 `inspector set_property`，并传入 `assetPath + objectPath + componentName`。多步骤 Prefab 修改使用 `prefab patch --ops <file>`，正式写入前先执行 `--dryRun true`。AIBridge 不支持的 Prefab、Scene、ScriptableObjectTable 或自定义 `.asset` 结构修改，使用 `unity-yaml-editing` 作为直接 YAML 兜底规范。

PowerShell 传复杂 JSON 时建议先构造变量：

```powershell
$values = (@{ 'm_LocalPosition.x' = 0; 'm_LocalPosition.y' = 1 } | ConvertTo-Json -Compress) -replace '"', '\"'
& "./.aibridge/cli/AIBridgeCLI.exe" inspector set_properties --assetPath 'Assets/Prefabs/Player.prefab' --componentName Transform --values $values
```

### Batch 和 Multi

```bash
$CLI batch from_text --text "call editor log 'Hello'\ndelay 1000"
$CLI batch from_file --file ".aibridge/scripts/setup_scene.txt"
$CLI multi --cmd "editor log --message Step1&get_logs --logType Error --count 1"
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

`input` 命令用于 Play Mode 下的 UGUI/EventSystem 自动化验证，需要当前场景存在可用的 `EventSystem`。它可以按层级路径或实例 ID 点击对象，也可以点击屏幕坐标、拖拽对象或执行长按。

```bash
$CLI editor play
$CLI input click --path "Canvas/StartButton"
$CLI input click_at --x 960 --y 540
$CLI input drag --path "Canvas/Item" --toPath "Canvas/Slot" --frames 12
$CLI input long_press --instanceId 12345 --duration-ms 800

$CLI gameview get_resolution
$CLI gameview set_resolution --width 1920 --height 1080
$CLI gameview list_resolutions
$CLI screenshot game
$CLI screenshot scene_view
$CLI screenshot scene_view --width 1920 --height 1080
$CLI screenshot gif --frameCount 50 --fps 20 --scale 0.5
$CLI editor stop
```

Game 视图截图、GIF 捕获和 `input` 命令都需要 Play Mode。Scene 视图截图只需要 Unity Editor 中存在 Scene 视图，可在 Edit Mode 使用。运行时 UI 推荐流程是进入 Play Mode，检查场景层级，执行 `input`，读取 Error 日志，再用截图或 GIF 复核画面。

### 已编译 Player Runtime Bridge

`runtime` 命令用于连接 Player 或 Play Mode 中的 `AIBridgeRuntime`。HTTP transport 是默认 Runtime 控制面，默认入口为 `http://127.0.0.1:27182`；端口被占用时 Runtime Bridge 会在小范围内自动递增，并把实际 URL 写入 heartbeat，方便同项目 CLI 自动解析当前目标。LAN discovery 从 UDP `27183` 开始，也会用同样方式自动递增，因此同一台机器上可同时运行多个 Editor 或已编译 Player，且不会共用一个发现端口。File transport 保留给 Editor/本机兼容调试。使用 File transport 时，已编译 Player 仍可通过启动参数 `--aibridge-runtime-dir <path>` 和 `--aibridge-target-id <id>` 指定目标。

默认情况下，HTTP `runtime list_targets` 会检查项目 heartbeat/cache 并扫描本机自动递增端口范围，`runtime discover` 会广播匹配的 UDP 端口范围。多个本机 Player 同时运行时，先执行 `runtime list_targets`，再传返回的 target id，例如 `runtime status --target AIBridgeDev_12345`；`runtime diagnose --target <id>` 会解析并检查该目标的实际 URL。

可在 `AIBridge/Settings > Runtime` 配置默认开关、HTTP 监听地址/端口、局域网发现、Editor Play Mode 自动注入、Development Build 自动注入、Release Build 允许项、后台保持运行、TargetId、Auth Token、Allowed Actions 和日志缓存。Settings 页可写入 `.aibridge/runtime-config.json`，让 CLI 直接使用项目默认 Runtime 配置。`AIBridge/Players` 面板可查看 File heartbeat 目标、本机 HTTP 入口、局域网发现缓存、状态、场景、平台和常用 CLI 命令。已过期的 File/CACHE 条目会显示 `删除缓存` 按钮，可清理旧 target 目录或 discovery-cache 条目，不影响在线 Player。Editor Play Mode 自动注入默认启用，进入 Play Mode 且场景内没有 `AIBridgeRuntime` 时会创建临时隐藏 Runtime 对象。`后台保持运行` 默认对 Editor Play Mode 和 Development Build 启用，避免 Unity 失焦后 heartbeat 和 runtime 命令停止响应。

```bash
$CLI runtime list_targets
$CLI runtime status --target latest
$CLI runtime discover
$CLI runtime diagnose --target latest
$CLI runtime logs --target latest --logType Error --count 100
$CLI runtime perf --target latest --duration 5s --interval 100ms
$CLI runtime screenshot --target latest
$CLI runtime handlers --target latest
$CLI runtime call --target latest --action qa.open_panel --json "{\"panel\":\"Inventory\"}"
$CLI code runtime_execute --file ".aibridge/code/player_probe.csx" --target latest --timeout 10000
```

远程手机先执行局域网发现：`$CLI runtime discover`，再连接发现到的 target id 或 URL。Android USB 调试可先执行 `adb reverse tcp:27182 tcp:27182`，再通过 `--transport http --url http://127.0.0.1:27182` 连接；`adb` 不作为独立 Runtime transport。项目安装 HybridCLR 后，`code runtime_execute` 可以在 Editor 编译临时运行时安全 DLL，经 Runtime Bridge 发送到手机或已编译 Player，再在目标端通过 `Assembly.Load` 和反射调用入口方法。这个能力很适合移动端一次性诊断，尤其是状态、日志、截图这些通用命令无法直接表达的检查。

Runtime Bridge 不内置游戏内 LLM，也不提供无约束的项目方法反射 RPC。它的内置能力包括状态、日志、截图、性能、handler 列表/调用，以及 HybridCLR 和 Runtime Code Execution 开关允许时由 `code runtime_execute` 使用的显式 `runtime.code.execute` 动作。请把运行时代码执行视为可信调试入口。Release Build 默认关闭，只有项目明确接受安全边界、网络暴露、鉴权 Token 和运行时代码执行策略后，才应开启。

</details>

<details>
<summary>只读 Code Index</summary>

`code_index` 是 CLI-only 的只读语义查询入口，默认关闭；只有项目需要语义查询时，才在 `AIBridge > Settings > Code Index > Enable Code Index` 中启用。它不要求打开 Rider、VS Code、Cursor、Visual Studio、Build Tools，也不要求 Unity 生成 solution。Unity Editor 会把编译快照写入 `.aibridge/code-index/snapshot/`；当前工程本地的 `AIBridgeCodeIndex` daemon 读取该快照，并用 Roslyn `AdhocWorkspace` 执行语义查询。

```bash
$CLI code_index status
$CLI code_index doctor
$CLI code_index warmup
$CLI code_index reset
$CLI code_index symbol --query PlayerController
$CLI code_index definition --file Assets/Scripts/Foo.cs --line 42 --column 17
$CLI code_index references --file Assets/Scripts/Foo.cs --line 42 --column 17
$CLI code_index implementations --type Game.IFoo
$CLI code_index derived --type Game.BasePanel
$CLI code_index callers --file Assets/Scripts/Foo.cs --line 42 --column 17
$CLI code_index diagnostics --file Assets/Scripts/Foo.cs
```

该命令刻意保持只读：不做 rename、重构、自动修复或文件写入。语义 workspace 不可用时，fallback 结果会明确标记 `semantic=false`，`source` 为 `rg-fallback` 或 `text-fallback`。`doctor` 会直接报告快照缺失等问题；最终编译权威仍然是 `compile unity`。

启用 Code Index 后，Unity Editor 可在 `AIBridge > Settings > Code Index` 中生成快照、配置启动后空闲预热、快照自动刷新、文本降级、PackageCache 源码是否纳入索引、忽略程序集/源码路径规则和退出清理策略。预热阶段只加载轻量 snapshot name index；声明位置和全局唯一的索引成员定义可直接由快照返回，Roslyn 语义 workspace 会延迟到更复杂的定义、引用、诊断等语义查询真正需要时再构建。引用查询也会尽量使用 snapshot token index 缩小 Roslyn 候选文件范围。被排除的源码程序集仍会作为 metadata reference 保留，避免工程语义查询缺少包类型。`status` 会返回 `workspaceMode=unity-snapshot`、快照元数据、排除计数，以及 daemon 需要加载新快照时的 `stale=true`。

</details>

<details>
<summary>高级 C# 执行</summary>

### Roslyn 临时 C# 执行

`code execute` 用于受控执行临时 Editor C#。它适合声明式 CLI 命令难以表达的复杂一次性任务，例如生成组合资源、批量诊断、结构化报告、调用项目 Runtime/Public API 或编排多步 UnityEditor API。它不是 `compile unity` 或 `test run` 的替代品。

`Tools > AIBridge Settings > Basic` 中的 `Enable Code Execution` 默认启用；不可信项目或调用方环境中可在设置里关闭。这个总开关同时约束 `code execute` 和 `code runtime_execute`。文件模式只允许 `.aibridge/code/*.cs` 或 `.aibridge/code/*.csx`，复杂脚本优先使用文件模式。代码执行同一时间只允许一个任务；超时后先用 `code status` 查看状态，必要时再用 `code cancel` 释放 AIBridge 等待状态。

```bash
$CLI code execute --file ".aibridge/code/check.csx" --timeout 5000
$CLI code execute --code "Debug.Log(\"hello\"); return 123;"
$CLI code runtime_execute --file ".aibridge/code/player_probe.csx" --target latest --timeout 10000
$CLI code runtime_execute --code "return Application.platform.ToString();" --transport http --url http://127.0.0.1:27182 --timeout 10000
$CLI code status
$CLI code cancel
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
7. 只有需要语义查询时才启用 `code_index`，再在大范围源码编辑前用它做只读语义定位。
8. 只有在声明式命令不足以表达复杂一次性 Editor 或 Player 调试任务时，才启用并使用 `code execute` 或 HybridCLR 支持的 `code runtime_execute`。

## 仓库结构

```text
Editor/        Unity Editor 命令、设置窗口、集成逻辑、Prefab Patch
Runtime/       Runtime 桥接契约和轻量运行时数据
Tools~/       AIBridgeCLI 源码、CodeIndex daemon 源码和随包平台二进制
Templates~/   AI 根规则模板和 Unity 项目 AGENTS.md 模板
Skill~/       AIBridge Skills 和工作流参考
Tests/        Unity EditMode 测试
Images/       README 图片
```

## 许可证

MIT License。详见 [LICENSE](./LICENSE)。

## 贡献

欢迎提交 issue 和 pull request。修改 Unity 侧行为时，请同步更新相关 CLI 示例、Skill reference 和验证说明。
