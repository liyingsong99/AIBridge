<p align="center">
  <img src="./Images/aibridge-banner.png" alt="AIBridge banner" width="100%">
</p>

# AIBridge

[English](./README.md) | 中文

![Unity 2019.4+](https://img.shields.io/badge/Unity-2019.4%2B-black?style=flat-square&logo=unity)
![Package 1.3.0](https://img.shields.io/badge/Package-1.3.0-5b6cff?style=flat-square)
![MIT License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)
![AI Unity Automation](https://img.shields.io/badge/Workflow-AI%20Unity%20Automation-14b8a6?style=flat-square)

AIBridge 是一个 Unity Package，用于在 AI 编码助手和 Unity Editor 之间建立稳定的 CLI 桥接。它可以让 AI 定位真实 Unity 资源路径、检查场景和 Prefab、通过 Unity API 编辑对象、执行 Unity 编译、读取 Console 日志、运行批处理流程、执行测试，并用截图或 GIF 做视觉验证。

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

## 系统要求

- Unity 2019.4 或更高版本。
- .NET 8.0 Runtime，用于随包 CLI。
- Unity 侧命令需要 Unity Editor 正在运行，例如 `compile unity`、`asset`、`scene`、`inspector`、`prefab`、`screenshot`、`get_logs`。

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

安装后的集成会包含固定 CLI 路径、Skill 路由规则、Unity 编译检查、Console 诊断、C# 兼容规则和工作流检查清单。命令说明会生成到各 Skill 的 `references/` 目录。

## CLI 基础

AIBridge 复制 CLI 缓存后，在 Unity 项目根目录执行：

```powershell
$CLI = "./.aibridge/cli/AIBridgeCLI.exe"
```

macOS/Linux 可使用随包平台可执行文件，或按项目配置通过 `dotnet` 运行 DLL。

多数命令格式如下：

```bash
$CLI <command> <action> [options]
```

少数 CLI-only 或辅助命令格式不同：`focus` 没有 action，`multi` 使用 `--cmd` 或 `--stdin`。

## 常用命令

### 编辑器、编译、日志、测试

```bash
$CLI focus
$CLI editor log --message "Hello" --logType Warning
$CLI editor get_state
$CLI compile unity
$CLI get_logs --logType Error --count 50
$CLI get_logs --logType Warning --count 50
$CLI test run --mode EditMode
$CLI test status
```

Unity 验证必须使用 `compile unity`。`compile dotnet` 只能作为额外的解决方案构建检查，不能替代 Unity 编译。

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

简单 Prefab 字段修改可用 `inspector set_property`，并传入 `assetPath + objectPath + componentName`。多步骤 Prefab 修改使用 `prefab patch --ops <file>`，正式写入前先执行 `--dryRun true`。

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
delay 1000
get_logs --logType Error --count 1
'@
$script | & "./.aibridge/cli/AIBridgeCLI.exe" multi --stdin
```

### 视觉验证

```bash
$CLI gameview get_resolution
$CLI gameview set_resolution --width 1920 --height 1080
$CLI gameview list_resolutions
$CLI screenshot game
$CLI screenshot gif --frameCount 50 --fps 20 --scale 0.5
```

Game 视图截图和 GIF 捕获需要 Play Mode。

## 推荐 AI 工作流程

1. 先解析真实 Unity 资源路径或对象路径。
2. 检查当前场景、Prefab、组件或 SerializedProperty 状态。
3. 通过 Unity 感知命令或源码编辑执行最小安全改动。
4. 执行 `compile unity`。
5. 读取 `get_logs --logType Error`。
6. 涉及画面效果时，用截图或 GIF 验证。

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
