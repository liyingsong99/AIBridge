<p align="center">
  <img src="./Images/aibridge-banner.png" alt="AIBridge banner" width="100%">
</p>

# AIBridge

[English](./README.md) | 中文

![Unity 2019.4+](https://img.shields.io/badge/Unity-2019.4%2B-black?style=flat-square&logo=unity)
![Package 1.4.14](https://img.shields.io/badge/Package-1.4.14-5b6cff?style=flat-square)
![MIT License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)
![AI Unity Automation](https://img.shields.io/badge/Workflow-AI%20Unity%20Automation-14b8a6?style=flat-square)

> 设计准则：**简单、易用、稳定**。

AIBridge 是面向 Unity 项目的本地 AI 开发 harness。它把项目规则、AIBridge Skills、CLI、Runtime Bridge、代码索引、视觉验证、输入模拟、Player 调试和 workflow 产物组织成一套可被 Codex、Claude、Cursor 等 AI 工具使用的 Unity 工作闭环。

安装 Package 并安装 AI 工具集成后，AI 不再只能读写脚本文本。它可以定位真实 Unity 资源路径、检查场景和 Prefab、通过 Unity API 修改对象、执行 Unity 编译和测试、读取 Console 日志、模拟 Play Mode UI 输入、采集截图/GIF、连接已编译 Player，并把每次验证证据留在项目本地。

如果项目安装了 HybridCLR，AIBridge 还可以在 Editor 中编译受控的临时运行时代码，并通过 Runtime Bridge 下发到目标 Player 执行，用于移动端和已编译 Player 的一次性诊断。

## 你可以用 AIBridge 做什么

| 场景 | 你可以这样要求 AI | AIBridge 提供的能力 | 产物 |
|---|---|---|---|
| Unity 改动闭环 | “帮我改这个 Editor 面板，并验证没有编译错误。” | 读取项目规则、定位代码、执行 `compile unity`、读取 Console | 编译结果、Error 日志 |
| Prefab / 场景检查 | “检查所有按钮 Prefab 是否缺少点击音效，先给 dry-run。” | 搜索资源、读取 Prefab 层级、Inspector 字段、Prefab Patch dry-run | patch proposal、dry-run 报告 |
| Play Mode UI 验证 | “进入 Play Mode 点击背包按钮，确认面板打开。” | `input` 点击/拖拽/长按、Game 视图截图、日志检查 | 截图/GIF、Error 日志 |
| Player / 手机端调试 | “Android 包里登录按钮没反应，帮我收集证据。” | Runtime discover/status/logs/screenshot/perf、UI snapshot/find/click/key、handler 调用 | target id、日志、截图、性能数据 |
| 语义代码理解 | “找出 InventoryItem 的定义、引用和调用者。” | 只读 `code_index`，不可用时明确降级文本搜索 | definition/reference/caller 结果 |
| 多步骤 workflow | “分片审查 Runtime Bridge 风险，再做对抗验证。” | workflow recipe、artifact、gate、external verdict、report | Finding、Verdict、Markdown 报告 |
| 项目专属拓展 | “暴露一个查询背包状态的运行时调试入口。” | Runtime handler、CLI command、Skill、workflow recipe | 项目专属 AI harness 能力 |

## 典型场景案例

### 1. AI 辅助 Unity 开发闭环

让 AI 先读取项目规则和相关代码，再通过 Unity 感知命令确认资源、场景或 Prefab 状态。改动完成后，AI 应执行 `compile unity`、读取 `get_logs --logType Error`，必要时补充截图、测试或 Runtime 证据。

适合：Editor 工具修改、Runtime 脚本修复、Prefab 字段调整、一次性资源生成、配置迁移。

### 2. Play Mode UI 自动化验证

让 AI 进入 Play Mode，使用 `input` 对 UGUI/EventSystem 界面执行点击、拖拽、长按，再用日志和截图确认结果。对 Player 目标，先用 `runtime.ui.snapshot` 找按钮和屏幕坐标，再用 `runtime.ui.click`、`runtime.ui.raycast` 或 `runtime.input.key` 做后续操作。它适合验证按钮、背包拖放、运行时面板、弹窗流程和基础交互回归。

适合：UI bug 复现、交互回归、Game 视图截图核验、简单视觉证据采集。

### 3. 已编译 Player 和移动端诊断

在 Development Build 或启用 Runtime Bridge 的目标中，AI 可以发现 Player、读取状态、拉取日志、截图、性能采样，并调用项目白名单 handler。安装 HybridCLR 且显式启用运行时代码执行时，还能下发临时诊断代码到目标 Player。

适合：Android/iOS/Windows Player 问题定位、真机状态采集、Release 前调试构建验证、移动端一次性探针。

### 4. 多 Agent / 长任务工作流

当任务需要分片审查、对抗验证、多目标 Runtime sweep 或跨轮次恢复时，使用 workflow recipe。AIBridge 负责 recipe、运行记录、artifact、gate、报告和外部结果导入；`agent` 和 `manual` 步骤仍由 Codex、Claude、Cursor 或人工执行。

适合：大范围只读审查、Runtime 多目标验证、bug-hunter loop、Prefab asset sweep、跨工具任务交接。

## 核心能力

- **Unity 资源和对象检查**：查找资源、读取场景层级、检查组件和 SerializedProperty，并通过 Unity API 执行安全写入。
- **Prefab 和场景自动化**：支持简单 Inspector 字段修改、Prefab Patch dry-run、多步骤批处理和跨域重载后的任务继续。
- **UGUI 运行时输入模拟**：Play Mode 下通过 `input` 命令模拟点击、Unity 屏幕坐标点击、Unity 归一化屏幕坐标点击、拖拽和长按，适合验证按钮、背包拖放、运行时面板等基于 EventSystem 的交互。
- **Player Runtime Bridge**：已编译 Player 中的 `AIBridgeRuntime` 可暴露运行时状态、日志、截图、性能采样、UI snapshot/find/raycast/click、语义化按键输入、项目白名单 handler，以及 HybridCLR 门控的运行时代码执行，适合 Development Build 和移动端调试。
- **只读 Code Index**：启用后，`code_index` 会启动不依赖 IDE 的 daemon，读取 Unity 编译快照并用于符号、定义、引用、实现、调用者和诊断查询；该能力默认关闭，降级为文本搜索时会明确返回 `semantic=false`。
- **Workflow recipes 和运行产物**：`workflow` CLI 可列出、校验、规划、初始化并运行内置 Unity workflow recipe 中的确定性 CLI 步骤，并在 `.aibridge/workflows/runs/` 下写入 run manifest、命令结果、artifact、gate 和 Markdown 报告。
- **Roslyn 临时 C# 执行**：通过受控的 `code execute` 在 Unity Editor 内执行 `.aibridge/code/*.cs` 或 `.csx` 临时脚本，用于复杂一次性资源生成、结构化分析、诊断和 Runtime/Public API 调用。该能力在设置中默认启用，不可信项目或调用方环境中可在设置里关闭。
- **视觉和日志验证**：支持 Game/Scene 视图截图、GIF、Console 日志读取、Unity 编译和测试命令，帮助 AI 闭环确认改动结果。

## AIBridge 在 Unity AI Harness 生态中的位置

Unity 的真实状态不只在文本文件里，还包括 Editor 状态、场景层级、Prefab 序列化对象、Inspector 属性、Play Mode、Player、Console、截图和运行时对象。AIBridge 的不可替代作用，是把这些 Unity 专属状态变成 AI 可以查询、修改、验证和留痕的本地数据面。

它不替代 Codex、Claude、Cursor，也不替代 MCP。AI 工具负责推理、编辑和调度；MCP 可以作为通用工具连接协议；AIBridge 则提供 Unity-specific 的 harness 层：项目规则、Unity 感知 CLI、Runtime Bridge、Code Index、workflow recipe、artifact、gate 和报告。

| 维度 | AIBridge 负责 | 普通 MCP / 持续连接桥通常负责 |
|---|---|---|
| Unity 状态 | 场景、Prefab、Inspector、Console、Play Mode、Player 证据 | 暴露工具调用入口 |
| 编译和域重载 | 文件请求和结果可跨重载轮询，Runtime 目标可重新发现 | 在线会话可能中断 |
| 多工程 | 从 Unity 项目根目录自动使用项目本地 CLI 和规则 | 依赖服务端或工具映射 |
| 证据留存 | 命令结果、日志、截图、GIF、Code Index、Runtime 诊断、workflow report | 多数保留在会话或工具返回里 |
| 拓展方式 | Unity 命令、Runtime handler、CLI Builder、Skill、recipe | 工具服务或协议适配 |

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

如果想尝试当前开发分支，可以显式安装 `dev` 分支：

```text
https://github.com/liyingsong99/AIBridge.git#dev
```

UPM 备用 Git 地址：

```text
https://gitee.com/lijoujou99_admin/AIBridge.git
```

也可以将本仓库克隆到 Unity 项目的 `Packages` 目录下。

## 配置 AI 工作流

1. 在 Unity Editor 中打开 `AIBridge/Workflows`。
2. 进入 `Skills` 页签。
3. 勾选正在使用的 AI 工具。
4. 点击 `Install Selected Integrations`。
5. 可选：点击 `Install Unity Project AGENTS.md Template`，在项目根目录创建 `AGENTS.md`。

安装后的 AIBridge Skills 默认写入各已选工具自己的默认 skills 目录，例如 Codex 使用 `.codex/skills/`。也可以在 `Workflows > Skills` 页签设置自定义目录，但自定义目录可能无法被 AI 工具自动发现。不同 AI 工具只写入各自的最小 RootRule；只有自定义目录需要时才写入插件适配层并引用该 Skill 根目录。RootRule 包含固定 CLI 路径、常用命令、host 工具 `exec` 路由、Skill 根目录和 `aibridge-development-workflow` 入口；多分支路由和针对性检查清单由 workflow Skill 维护。高级 Workflow 编排规则会作为 AIBridge Skill 一起安装，只在多 Agent workflow、对抗验证、recipe、Runtime 调试诊断或 Runtime 多目标 sweep 任务中加载。命令说明会生成到各 Skill 的 `references/` 目录。

如果项目有需要，也可以在 `Workflows > 推荐库` 页签刷新默认的 `obra/superpowers` 推荐仓库，并将其中的第三方 Skill 安装到已选工具的 skills 目录。

`Workflows > Workflow 选项` 会保存项目级工作流偏好。应用这些选项时，会刷新已安装的 `aibridge-development-workflow` Skill 下的生成文件，包括 `references/project-workflow-preferences.md` 和根据分支开关生成的分支选择规则。

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
$CLI workflow plan --recipe runtime-debug-investigation --format markdown
$CLI workflow plan --recipe runtime-ui-validation --format markdown
$CLI workflow init --recipe runtime-ui-validation
$CLI workflow begin --recipe unity-change-implementation
$CLI get_logs --logType Error --count 50 --workflow-run wf_20260529_213000_ab12cd34
$CLI runtime screenshot --target latest --workflow-run wf_20260529_213000_ab12cd34
$CLI workflow import --run wf_20260529_213000_ab12cd34 --step adversarial-verify --schema Verdict --file verdicts.json
$CLI workflow export --recipe runtime-ui-validation --target codex-task-pack --output .aibridge/workflows/exports
$CLI workflow run-cli --file ".aibridge/workflows/recipes/runtime-target-sweep.aibridge-workflow.json" --inputs ".aibridge/workflows/inputs.json"
$CLI workflow run-cli --recipe unity-sharded-review --allow-partial true
$CLI workflow run-cli --recipe unity-sharded-review --resume wf_20260529_213000_ab12cd34 --rerun failed
$CLI workflow status --run wf_20260529_213000_ab12cd34
$CLI workflow report --run wf_20260529_213000_ab12cd34 --format markdown
$CLI workflow finish --run wf_20260529_213000_ab12cd34 --status passed
$CLI workflow clean --older-than 30d --dry-run true
$CLI workflow clean --older-than 3d --save-settings true --auto-clean true
```

内置 recipes 包括 `unity-change-implementation`、`unity-sharded-review`、`runtime-target-sweep`、`runtime-debug-investigation`、`runtime-ui-validation`、`prefab-asset-sweep` 和 `bug-hunter-loop`。

`runtime-debug-investigation` 用于排查 Runtime、Player、Play Mode、UI、日志或性能症状。它优先检查证据是否完整，不把 Runtime 错误本身当作 workflow 失败条件；确认根因并需要修复时，再交接到实施工作流。

`workflow begin` 会创建 active run；普通命令可通过 `--workflow-run`、`AIBRIDGE_WORKFLOW_RUN_ID` 或 active run 指针归档证据。`workflow status` 和 `workflow report` 必须显式传 `--run`；需要 active run 时先读取 `.aibridge/workflows/active-run.json` 里的 run id。`workflow run-cli --resume <runId>` 会继续已有 run，但仍必须带 `--recipe` 或 `--file`，让 CLI 加载 recipe 定义。`--inputs` 优先传 JSON 文件路径；PowerShell inline JSON 很容易被 shell quoting 破坏。`workflow import` 保存 `Verdict` 等结构化外部结果，`externalVerdict` gate 只基于导入 artifact 通过。`workflow export` 生成外部工具交接包，它只是导出器，不是内置 LLM runtime。`partial` workflow 状态默认不算 CLI 成功，只有显式传入 `--allow-partial true` 才按成功返回。

Workflow 清理默认保守：`clean` 默认只 dry-run。只有保存到 `.aibridge/workflows/settings.json` 后才会启用自动清理；之后 `run-cli` 开始前会按设置清理旧 run，并保留失败/阻塞 run、active run 和最新保留数量。

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

### 外部 Exec

`exec` 用于无 shell 执行 `rg`、`git`、`dotnet`、`python` 或 `node` 等外部工具。请求通过 stdin 或请求文件传入 JSON，参数保持数组形式，不再拼接 PowerShell 字符串。

```powershell
$request = @'
{
  "command": "rg",
  "args": ["-n"],
  "queries": ["ProcessStartInfo", "ArgumentList"],
  "globs": ["*.cs"],
  "paths": ["Packages/cn.lys.aibridge/Tools~/AIBridgeCLI"]
}
'@
$request | & "./.aibridge/cli/AIBridgeCLI.exe" exec run --stdin
```

多条独立命令使用 `jobs` 批量请求。`rg` 和 `search` 请求会把退出码 `1` 视为成功的无匹配结果。

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

`input` 命令用于 Play Mode 下的 UGUI/EventSystem 自动化验证，需要当前场景存在可用的 `EventSystem`。它可以按层级路径或实例 ID 点击对象，也可以点击屏幕坐标、归一化屏幕坐标、拖拽对象或执行长按。像素坐标和归一化坐标都统一使用 Unity 屏幕坐标系，原点固定为左下角；`click_pct` 的 `x`、`y` 取值范围为 `0` 到 `1`。

```bash
$CLI editor play
$CLI input click --path "Canvas/StartButton"
$CLI input click_at --x 960 --y 540
$CLI input click_pct --x 0.5 --y 0.5
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

默认情况下，HTTP `runtime list_targets` 使用 quick 路径：检查项目 heartbeat/cache 和已配置或显式传入的 URL，但不扫描本机自动递增端口范围。需要本机端口扫描时执行 `runtime list_targets --probe true`，需要局域网 UDP 发现时执行 `runtime discover`。多个本机 Player 同时运行时，先执行 `runtime list_targets`，再传返回的 target id，例如 `runtime status --target AIBridgeDev_12345`；`runtime diagnose --target <id>` 会解析并深度检查该目标的实际 URL。

可在 `AIBridge/Settings > Runtime` 配置默认开关、HTTP 监听地址/端口、局域网发现、Editor Play Mode 自动注入、Development Build 自动注入、Release Build 允许项、后台保持运行、TargetId、Auth Token、Allowed Actions 和日志缓存。Settings 页可写入 `.aibridge/runtime-config.json`，让 CLI 直接使用项目默认 Runtime 配置。`AIBridge/Players` 面板可查看 File heartbeat 目标、本机 HTTP 入口、局域网发现缓存、状态、场景、平台和常用 CLI 命令。已过期的 File/CACHE 条目会显示 `删除缓存` 按钮，可清理旧 target 目录或 discovery-cache 条目，不影响在线 Player。Editor Play Mode 自动注入默认启用，进入 Play Mode 且场景内没有 `AIBridgeRuntime` 时会创建临时隐藏 Runtime 对象。`后台保持运行` 默认对 Editor Play Mode 和 Development Build 启用，避免 Unity 失焦后 heartbeat 和 runtime 命令停止响应。UI 自动化建议先用 `runtime.ui.snapshot`，按钮条目会带屏幕坐标和屏幕矩形，后续就能直接点固定像素位置。

```bash
$CLI runtime list_targets
$CLI runtime list_targets --probe true
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
Doc~/          包级功能文档和功能规格说明
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
