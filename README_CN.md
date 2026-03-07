# AI Bridge

[English](./README.md) | 中文

AI 编码助手与 Unity Editor 之间的文件通信框架。

## 功能特性

- **GameObject** - 创建、删除、查找、重命名、复制、切换激活状态
- **Transform** - 位置、旋转、缩放、父子层级、LookAt
- **Component/Inspector** - 获取/设置属性、添加/移除组件
- **Scene** - 加载、保存、获取层级、创建新场景
- **Prefab** - 实例化、保存、解包、应用覆盖
- **Asset** - 搜索、导入、刷新、按过滤器查找
- **编辑器控制** - 编译、撤销/重做、播放模式、聚焦窗口
- **截图 & GIF** - 捕获游戏视图、录制动画 GIF
- **批量命令** - 高效执行多个命令
- **运行时扩展** - Play 模式自定义处理器

## 为什么选择 AI Bridge？（对比 Unity MCP）

| 特性 | AI Bridge | Unity MCP |
|------|-----------|-----------|
| 通信方式 | 文件通信 | WebSocket 长连接 |
| Unity 编译时 | **正常工作** | 连接断开 |
| 端口冲突 | 无 | 可能导致重连失败 |
| 多工程支持 | **支持** | 不支持 |
| 稳定性 | **高** | 受编译/重启影响 |
| 上下文消耗 | **低** | 较高 |
| 扩展性 | 简单接口 | 需了解 MCP 协议 |

**MCP 的问题**：Unity MCP 使用 WebSocket 长连接。当 Unity 重新编译时（开发过程中频繁发生），连接会断开。端口冲突还可能导致无法重连，使用体验较差。

**AI Bridge 方案**：通过文件通信，AI Bridge 从根源上完美解决了这些问题。命令以 JSON 文件写入，结果以文件读取——简单、稳定、可靠，不受 Unity 状态影响。

## 概述

AI Bridge 使 AI 编码助手（如 Claude、GPT 等）能够通过简单的基于文件的协议与 Unity Editor 进行通信。这使得 AI 能够：

- 创建和操作 GameObject
- 修改 Transform 和 Component
- 加载和保存场景
- 捕获截图和 GIF 录制
- 执行菜单项
- 以及更多功能...

## 安装

### 通过 Unity Package Manager

1. 打开 Unity Package Manager（Window > Package Manager）
2. 点击 "+" > "Add package from git URL"
3. 输入：`https://github.com/liyingsong99/AIBridge.git`

### 手动安装

1. 下载或克隆此仓库
2. 将整个文件夹复制到 Unity 项目的 `Packages` 目录

## 系统要求

- Unity 2021.3 或更高版本
- .NET 6.0 Runtime（用于 CLI 工具）
- Newtonsoft.Json (com.unity.nuget.newtonsoft-json)

## 包结构

```
cn.lys.aibridge/
├── package.json
├── README.md
├── README_CN.md
├── Editor/
│   ├── cn.lys.aibridge.Editor.asmdef
│   ├── Core/
│   │   ├── AIBridge.cs              # 主入口点
│   │   ├── CommandWatcher.cs        # 命令文件监视器
│   │   └── CommandQueue.cs          # 命令处理队列
│   ├── Commands/
│   │   ├── ICommand.cs              # 命令接口
│   │   ├── CommandRegistry.cs       # 命令注册表
│   │   └── ...                      # 各种命令实现
│   ├── Models/
│   │   ├── CommandRequest.cs        # 请求模型
│   │   └── CommandResult.cs         # 结果模型
│   └── Utils/
│       ├── AIBridgeLogger.cs        # 日志工具
│       └── ComponentTypeResolver.cs  # 组件类型解析器
├── Runtime/
│   ├── cn.lys.aibridge.Runtime.asmdef
│   ├── AIBridgeRuntime.cs           # 运行时单例 MonoBehaviour
│   ├── AIBridgeRuntimeData.cs       # 运行时数据类
│   └── IAIBridgeHandler.cs          # 扩展接口
└── Tools~/
    ├── CLI/
    │   └── AIBridgeCLI.exe          # 命令行工具
    ├── AIBridgeCLI/                 # CLI 源代码
    └── Exchange/
        ├── commands/                # 命令文件写入此处
        ├── results/                 # 结果文件返回此处
        └── screenshots/             # 截图保存此处
```

## 使用方法

### 编辑器模式

AI Bridge 在 Unity Editor 打开时自动启动。命令从 `AIBridgeCache/commands/` 目录处理。

#### 菜单项
- `AIBridge/Process Commands Now` - 立即处理待处理的命令
- `AIBridge/Toggle Auto-Processing` - 启用/禁用自动命令处理

### CLI 工具

CLI 工具（`AIBridgeCLI.exe`）提供命令行接口用于发送命令。

```bash
# 显示帮助
AIBridgeCLI --help

# 发送日志消息
AIBridgeCLI editor log --message "Hello from AI!"

# 创建 GameObject
AIBridgeCLI gameobject create --name "MyCube" --primitiveType Cube

# 设置 Transform 位置
AIBridgeCLI transform set_position --path "MyCube" --x 1 --y 2 --z 3

# 获取场景层级
AIBridgeCLI scene get_hierarchy

# 捕获截图
AIBridgeCLI screenshot game

# 录制 GIF
AIBridgeCLI screenshot gif --frameCount 60 --fps 20

# 延迟开始录制 GIF
AIBridgeCLI screenshot gif --frameCount 60 --fps 20 --startDelay 0.5
```

### 可用命令

| 命令 | 描述 |
|------|------|
| `editor` | 编辑器操作（日志、撤销、重做、播放模式等） |
| `compile` | 编译操作（unity、dotnet） |
| `gameobject` | GameObject 操作（创建、销毁、查找等） |
| `transform` | Transform 操作（位置、旋转、缩放、父级） |
| `inspector` | Component/Inspector 操作 |
| `selection` | 选择操作 |
| `scene` | 场景操作（加载、保存、层级） |
| `prefab` | 预制体操作（实例化、保存、解包） |
| `asset` | AssetDatabase 操作 |
| `menu_item` | 调用 Unity 菜单项 |
| `get_logs` | 获取 Unity 控制台日志 |
| `batch` | 执行多个命令 |
| `screenshot` | 捕获截图和 GIF 录制 |
| `focus` | 将 Unity Editor 置于前台（仅 CLI） |

### 运行时扩展

若需运行时（Play 模式）支持，在场景中添加 `AIBridgeRuntime` 组件：

```csharp
// 方式 1：通过代码添加
if (AIBridgeRuntime.Instance == null)
{
    var go = new GameObject("AIBridgeRuntime");
    go.AddComponent<AIBridgeRuntime>();
}

// 方式 2：通过 Inspector 添加
// 创建空 GameObject 并添加 AIBridgeRuntime 组件
```

#### 实现自定义处理器

```csharp
using AIBridge.Runtime;

public class MyCustomHandler : IAIBridgeHandler
{
    public string[] SupportedActions => new[] { "my_action", "another_action" };

    public AIBridgeRuntimeCommandResult HandleCommand(AIBridgeRuntimeCommand command)
    {
        switch (command.Action)
        {
            case "my_action":
                // 处理命令
                return AIBridgeRuntimeCommandResult.FromSuccess(command.Id, new { result = "success" });

            case "another_action":
                // 处理另一个命令
                return AIBridgeRuntimeCommandResult.FromSuccess(command.Id);

            default:
                return null; // 未处理
        }
    }
}

// 注册处理器
AIBridgeRuntime.Instance.RegisterHandler(new MyCustomHandler());
```

## 命令协议

命令是放置在 `AIBridgeCache/commands/` 中的 JSON 文件：

```json
{
    "id": "cmd_123456789",
    "type": "gameobject",
    "params": {
        "action": "create",
        "name": "MyCube",
        "primitiveType": "Cube"
    }
}
```

结果返回在 `AIBridgeCache/results/` 中：

```json
{
    "id": "cmd_123456789",
    "success": true,
    "data": {
        "name": "MyCube",
        "instanceId": 12345,
        "path": "MyCube"
    },
    "executionTime": 15
}
```

## 许可证

MIT License

## 贡献

欢迎贡献！请随时提交 Pull Request。
