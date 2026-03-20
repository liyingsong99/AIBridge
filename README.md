# AI Bridge

English | [中文](./README_CN.md)

File-based communication framework between AI Code assistants and Unity Editor.

## Features

- **GameObject** - Create, destroy, find, rename, duplicate, toggle active
- **Transform** - Position, rotation, scale, parent hierarchy, look at
- **Component/Inspector** - Get/set properties, add/remove components
- **Scene** - Load, save, get hierarchy, create new
- **Prefab** - Instantiate, save, unpack, apply overrides
- **Asset** - Search, import, refresh, find by filter
- **Text Asset Read (Fallback)** - Read scripts, YAML/text assets, and config-like files by Unity path when host-native file reads are unavailable
- **Editor Control** - Compile, undo/redo, play mode, focus window
- **Screenshot & GIF** - Capture game view, record animated GIFs
- **Batch Commands** - Execute multiple commands efficiently
- **Runtime Extension** - Custom handlers for Play mode

## Why AI Bridge? (vs Unity MCP)

| Feature | AI Bridge | Unity MCP |
|---------|-----------|-----------|
| Communication | File-based | WebSocket |
| During Unity Compile | **Works normally** | Connection lost |
| Port Conflicts | None | May cause reconnection failure |
| Multi-Project Support | **Yes** | No |
| Stability | **High** | Affected by compile/restart |
| Context Usage | **Low** | Higher |
| Extensibility | Simple interface | Requires MCP protocol knowledge |

**The Problem with MCP**: Unity MCP uses persistent WebSocket connections. When Unity recompiles (which happens frequently during development), the connection breaks. Port conflicts can also prevent reconnection, leading to a frustrating experience.

**AI Bridge Solution**: By using file-based communication, AI Bridge completely avoids these issues. Commands are written as JSON files and results are read back - simple, stable, and reliable regardless of Unity's state.

## Overview

AI Bridge enables AI coding assistants (like Claude, GPT, etc.) to communicate with Unity Editor through a simple file-based protocol. This allows AI to:

- Create and manipulate GameObjects
- Modify Transforms and Components
- Load and save Scenes
- Capture screenshots and GIF recordings
- Execute menu items
- And much more...

## Installation

### Via Unity Package Manager

1. Open Unity Package Manager (Window > Package Manager)
2. Click "+" > "Add package from git URL"
3. Enter: `https://github.com/liyingsong99/AIBridge.git`

### Manual Installation

1. Download or clone this repository
2. Copy the entire folder to your Unity project's `Packages` folder

## Requirements

- Unity 2019.4 or later
- .NET 6.0 Runtime (for CLI tool)

## Package Structure

```
cn.lys.aibridge/
├── package.json
├── README.md
├── Editor/
│   ├── cn.lys.aibridge.Editor.asmdef
│   ├── Core/
│   │   ├── AIBridge.cs              # Main entry point
│   │   ├── CommandWatcher.cs        # File watcher for commands
│   │   └── CommandQueue.cs          # Command processing queue
│   ├── Commands/
│   │   ├── ICommand.cs              # Command interface
│   │   ├── CommandRegistry.cs       # Command registration
│   │   └── ...                      # Various command implementations
│   ├── Models/
│   │   ├── CommandRequest.cs        # Request model
│   │   └── CommandResult.cs         # Result model
│   └── Utils/
│       ├── AIBridgeLogger.cs        # Logging utility
│       └── ComponentTypeResolver.cs  # Component type resolution
├── Runtime/
│   ├── cn.lys.aibridge.Runtime.asmdef
│   ├── AIBridgeRuntime.cs           # MonoBehaviour singleton for runtime
│   ├── AIBridgeRuntimeData.cs       # Runtime data classes
│   └── IAIBridgeHandler.cs          # Extension interface
└── Tools~/
    ├── CLI/
    │   └── AIBridgeCLI.exe          # Command line tool
    ├── AIBridgeCLI/                 # CLI source code
    └── Exchange/
        ├── commands/                # Command files written here
        ├── results/                 # Result files returned here
        └── screenshots/             # Screenshots saved here
```

## Usage

### Editor Mode

AI Bridge automatically starts when Unity Editor opens. Commands are processed from `AIBridgeCache/commands/`.

#### Menu Items
- `AIBridge/Process Commands Now` - Process pending commands immediately
- `AIBridge/Toggle Auto-Processing` - Enable/disable automatic command processing

### CLI Tool

The CLI tool is copied to `./AIBridgeCache/CLI/AIBridgeCLI.exe`. Run the examples below from the Unity project root.

```bash
# Show help
./AIBridgeCache/CLI/AIBridgeCLI.exe --help

# Send a log message
./AIBridgeCache/CLI/AIBridgeCLI.exe editor log --message "Hello from AI!"

# Create a GameObject
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject create --name "MyCube" --primitiveType Cube

# Set transform position
./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_position --path "MyCube" --x 1 --y 2 --z 3

# Get scene hierarchy
./AIBridgeCache/CLI/AIBridgeCLI.exe scene get_hierarchy

# Get prefab hierarchy
./AIBridgeCache/CLI/AIBridgeCLI.exe prefab get_hierarchy --prefabPath "Assets/Prefabs/Player.prefab"

# Search through Unity index to resolve canonical asset paths for AI workflows
./AIBridgeCache/CLI/AIBridgeCLI.exe asset search --mode script --keyword "Player" --format paths --raw

# Optional fallback: read text through AIBridge only when native file reads are unavailable
./AIBridgeCache/CLI/AIBridgeCLI.exe asset read_text --assetPath "Assets/Scripts/Player.cs" --startLine 1 --maxLines 120 --raw

# Capture screenshot
./AIBridgeCache/CLI/AIBridgeCLI.exe screenshot game

# Record GIF
./AIBridgeCache/CLI/AIBridgeCLI.exe screenshot gif --frameCount 60 --fps 20

# Record GIF with delayed start
./AIBridgeCache/CLI/AIBridgeCLI.exe screenshot gif --frameCount 60 --fps 20 --startDelay 0.5
```

### Available Commands

| Command | Description |
|---------|-------------|
| `editor` | Editor operations (log, undo, redo, play mode, etc.) |
| `compile` | Compilation operations (unity, dotnet) |
| `gameobject` | GameObject operations (create, destroy, find, etc.) |
| `transform` | Transform operations (position, rotation, scale, parent) |
| `inspector` | Component/Inspector operations |
| `selection` | Selection operations |
| `scene` | Scene operations (load, save, hierarchy) |
| `prefab` | Prefab operations (instantiate, inspect, save, unpack) |
| `asset` | AssetDatabase operations, including indexed lookup, canonical path resolution, metadata checks, and fallback text reads |
| `menu_item` | Invoke Unity menu items |
| `get_logs` | Get Unity console logs |
| `batch` | Execute multiple commands |
| `screenshot` | Capture screenshots and GIF recordings |
| `focus` | Bring Unity Editor to foreground (CLI-only) |

### Runtime Extension

For runtime (Play mode) support, add `AIBridgeRuntime` component to your scene:

```csharp
// Option 1: Add via code
if (AIBridgeRuntime.Instance == null)
{
    var go = new GameObject("AIBridgeRuntime");
    go.AddComponent<AIBridgeRuntime>();
}

// Option 2: Add via Inspector
// Create empty GameObject and add AIBridgeRuntime component
```

#### Implementing Custom Handlers

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
                // Handle the command
                return AIBridgeRuntimeCommandResult.FromSuccess(command.Id, new { result = "success" });

            case "another_action":
                // Handle another command
                return AIBridgeRuntimeCommandResult.FromSuccess(command.Id);

            default:
                return null; // Not handled
        }
    }
}

// Register the handler
AIBridgeRuntime.Instance.RegisterHandler(new MyCustomHandler());
```

## Recommended AI Query Workflow

For large Unity projects, prefer AIBridge asset queries before generic filesystem search:

1. Use `asset search` / `asset find` with `format=paths` to discover canonical Unity asset paths with minimal token usage.
2. Use `asset get_path` only when you start from a GUID, and `asset load` only when you want quick metadata confirmation.
3. Once the path is known, prefer your AI assistant's native file-read tool for text-based assets such as `.cs`, `.shader`, `.json`, `.asset`, `.prefab`, `.unity`, `.mat`, `.meta`, and similar files under the project root.
4. Use `asset read_text` only as a fallback when native reads are unavailable or when you specifically want a Unity-side line window.
5. Fall back to generic repo search only if the target cannot be resolved through AIBridge.

`format=full` (default) keeps `data.assets` as an array of asset objects. `format=paths` changes `data.assets` to an array of Unity asset path strings, which is usually the better fit for AI-driven file discovery.

## Command Protocol

Commands are JSON files placed in `AIBridgeCache/commands/`:

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

Results are returned in `AIBridgeCache/results/`:

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

## License

MIT License

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
