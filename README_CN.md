<p align="center">
  <img src="./Images/aibridge-banner.png" alt="AIBridge banner" width="100%">
</p>

# AIBridge

[English](./README.md) | 中文

![Unity 2019.4+](https://img.shields.io/badge/Unity-2019.4%2B-black?style=flat-square&logo=unity)
![Package 1.3.6](https://img.shields.io/badge/Package-1.3.6-5b6cff?style=flat-square)
![MIT License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)
![AI Unity Automation](https://img.shields.io/badge/Workflow-AI%20Unity%20Automation-14b8a6?style=flat-square)

AIBridge 是一个 Unity Package，用于在 AI 编码助手和 Unity Editor 之间建立稳定的 CLI 桥接。它可以让 AI 定位真实 Unity 资源路径、检查场景和 Prefab、通过 Unity API 编辑对象、执行 Unity 编译、读取 Console 日志、运行批处理流程、执行测试，模拟 UGUI/EventSystem 运行时点击与拖拽，并用截图或 GIF 做视觉验证。

它面向 AI 真实参与 Unity 项目开发的场景，而不是只生成代码建议。

## 为什么选择 AIBridge

很多 Unity 自动化工具依赖持续在线的 socket 或 MCP 会话。AIBridge 使用落盘命令请求和结果文件，因此能更好地应对脚本重编译、域重载、编辑器焦点变化和重启。

| 维度 | AIBridge | 持续连接型桥接 |
|---|---|---|
| 连接方式 | 文件请求和结果 | 在线会话 |
| 编译周期适应 | 可轮询并跨重载继续 | 会话可能中断 |
| 部署方式 | 随包 CLI 命令 | 服务端/客户端配置 |
| AI 集成 | CLI + JSON 输出 | 特定协议工具 |
| 任务追踪 | 命令文件、结果、日志、截图 | 当前会话状态 |
| 扩展方式 | Unity 命令 + CLI Builder | 工具服务扩展 |

## 核心能力

- **Unity 资源和对象检查**：查找资源、读取场景层级、检查组件和 SerializedProperty，并通过 Unity API 执行安全写入。
- **Prefab 和场景自动化**：支持简单 Inspector 字段修改、Prefab Patch dry-run、多步骤批处理和跨域重载后的任务继续。
- **UGUI 运行时输入模拟**：Play Mode 下通过 `input` 命令模拟点击、坐标点击、拖拽和长按，适合验证按钮、背包拖放、运行时面板等基于 EventSystem 的交互。
- **Roslyn 临时 C# 执行**：通过受控的 `code execute` 在 Unity Editor 内执行 `.aibridge/code/*.cs` 或 `.csx` 临时脚本，用于复杂一次性资源生成、结构化分析、诊断和 Runtime/Public API 调用。该能力在设置中默认启用，不可信项目或调用方环境中可在设置里关闭。
- **视觉和日志验证**：支持 Game 视图截图、GIF、Console 日志读取、Unity 编译和测试命令，帮助 AI 闭环确认改动结果。

## 系统要求

- Unity 2019.4 或更高版本。
- .NET 8.0 Runtime，用于随包 CLI。
- Unity 侧命令需要 Unity Editor 正在运行，例如 `compile unity`、`asset`、`scene`、`inspector`、`prefab`、`input`、`screenshot`、`code`、`get_logs`。

## 安装

在 Unity Package Manager 中使用下面的 Git 地址安装：

```text
https://github.com/liyingsong99/AIBridge.git
```

也可以将本仓库克隆到 Unity 项目的 `Packages` 目录下。

## 配置 AI 工作流

1. 在 Unity Editor 中打开 `Tools > AIBridge Settings`。
2. 进入 `Skills Installation` 区域。
3. 勾选正在使用的 AI 工具。
4. 点击 `Install Selected Integrations`。
5. 可选：点击 `Install Unity Project AGENTS.md Template`，在项目根目录创建 `AGENTS.md`。

安装后的 AIBridge Skills 默认写入各已选工具自己的默认 skills 目录，例如 Codex 使用 `.codex/skills/`。也可以在 Skills 安装页签设置自定义目录，但自定义目录可能无法被 AI 工具自动发现。不同 AI 工具只写入各自的最小 RootRule；只有自定义目录需要时才写入插件适配层并引用该 Skill 根目录。RootRule 只包含固定 CLI 路径、常用命令、Skill 根目录和 `aibridge-development-workflow` 入口；完整路由和检查清单由 workflow Skill 维护。命令说明会生成到各 Skill 的 `references/` 目录。

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
$CLI screenshot gif --frameCount 50 --fps 20 --scale 0.5
$CLI editor stop
```

Game 视图截图、GIF 捕获和 `input` 命令都需要 Play Mode。推荐流程是进入 Play Mode，检查场景层级，执行 `input`，读取 Error 日志，再用截图或 GIF 复核画面。

### Roslyn 临时 C# 执行

`code execute` 用于受控执行临时 Editor C#。它适合声明式 CLI 命令难以表达的复杂一次性任务，例如生成组合资源、批量诊断、结构化报告、调用项目 Runtime/Public API 或编排多步 UnityEditor API。它不是 `compile unity` 或 `test run` 的替代品。

`Tools > AIBridge Settings > Basic` 中的 `Enable Code Execution` 默认启用；不可信项目或调用方环境中可在设置里关闭。文件模式只允许 `.aibridge/code/*.cs` 或 `.aibridge/code/*.csx`，复杂脚本优先使用文件模式。`code execute` 同一时间只允许一个任务；超时后先用 `code status` 查看状态，必要时再用 `code cancel` 释放 AIBridge 等待状态。

```bash
$CLI code execute --file ".aibridge/code/check.csx" --timeout 5000
$CLI code execute --code "Debug.Log(\"hello\"); return 123;"
$CLI code status
$CLI code cancel
```

</details>

## 推荐 AI 工作流程

1. 先解析真实 Unity 资源路径或对象路径。
2. 检查当前场景、Prefab、组件或 SerializedProperty 状态。
3. 通过 Unity 感知命令或源码编辑执行最小安全改动。
4. 执行 `compile unity`。
5. 读取 `get_logs --logType Error`。
6. 涉及运行时 UI 时，进入 Play Mode 后用 `input`、日志和截图/GIF 验证交互。
7. 只有在声明式命令不足以表达复杂一次性 Editor 任务时，才启用并使用 `code execute`。

## 仓库结构

```text
Editor/        Unity Editor 命令、设置窗口、集成逻辑、Prefab Patch
Runtime/       Runtime 桥接契约和轻量运行时数据
Tools~/       AIBridgeCLI 源码和随包平台二进制
Templates~/   AI 根规则模板和 Unity 项目 AGENTS.md 模板
Skill~/       AIBridge Skills 和工作流参考
Tests/        Unity EditMode 测试
Images/       README 图片
```

## 许可证

MIT License。详见 [LICENSE](./LICENSE)。

## 贡献

欢迎提交 issue 和 pull request。修改 Unity 侧行为时，请同步更新相关 CLI 示例、Skill reference 和验证说明。
