---
description: "AI Bridge Unity integration - File-based communication framework for AI to control Unity Editor. Send commands via JSON files, manipulate GameObjects, Transforms, Components, Scenes, Prefabs, and more. Supports multi-command execution and runtime extension."
---

# AI Bridge Unity Skill

## When to Use This Skill

Activate this skill when you need to:

- Manipulate Unity Editor (create/modify/delete GameObjects)
- Get or set Transform properties (position/rotation/scale)
- Manage scene hierarchy or load/save scenes
- Instantiate or modify prefabs
- Read/write component properties
- Control editor state (undo/redo/compile/play mode)
- Query Unity console logs or selection state
- Output logs to Unity console
- **Bring Unity Editor window to foreground** (triggers auto-refresh/compile)
- **Capture screenshots or record animated GIFs** (requires Play Mode)
- **Execute multiple commands efficiently** (use `multi` command)

---

## AIBridgeCLI - Recommended Method

**IMPORTANT**: Always use `AIBridgeCLI.exe` to send commands. This avoids UTF-8 encoding issues and provides a cleaner interface.

### CLI Location

```
AIBridgeCache/CLI/AIBridgeCLI.exe
```

> **Note**: The CLI is automatically copied to `AIBridgeCache/CLI/` when the package is installed. This provides a stable, fixed path regardless of how the package was installed (local, git, or registry).

### Cache Directory

Commands and results are stored in `AIBridgeCache/` under the Unity project root:

```
{Unity Project Root}/
├── AIBridgeCache/
│   ├── commands/      # Command JSON files
│   ├── results/       # Result JSON files
│   └── screenshots/   # Screenshots and GIFs
```

### Basic Usage

```bash
# Format
AIBridgeCLI.exe <command-type> <action> [options]

# Examples
AIBridgeCLI.exe editor log --message "Hello World"
AIBridgeCLI.exe gameobject create --name "MyCube" --primitiveType Cube
AIBridgeCLI.exe transform set_position --path "Player" --x 1 --y 2 --z 3
```

### Global Options

| Option | Description |
|--------|-------------|
| `--timeout <ms>` | Timeout in milliseconds (default: 5000) |
| `--no-wait` | Don't wait for result, return command ID immediately |
| `--raw` | Output raw JSON (single line, for AI parsing) |
| `--quiet` | Quiet mode, minimal output |
| `--json <json>` | Pass complex parameters as JSON string |
| `--stdin` | Read parameters from stdin (JSON format) |
| `--help` | Show help |

**AI Usage:** Always add `--raw` for JSON output.

---

## Command Reference

### 0. `focus` - Bring Unity to Foreground (CLI-only)

**IMPORTANT**: This is a CLI-only command that does NOT require Unity to process it. It uses Windows API to bring the Unity Editor window to the foreground, which triggers automatic asset refresh and code compilation.

```bash
# Bring Unity Editor window to foreground
AIBridgeCLI.exe focus

# With raw JSON output
AIBridgeCLI.exe focus --raw
# Output: {"Success":true,"ProcessId":1234,"WindowTitle":"MyProject - Unity 6000.0.51f1","Error":null}
```

**Use Cases:**

- After modifying code files, use `focus` to trigger Unity's automatic recompilation
- After adding/modifying assets, use `focus` to trigger AssetDatabase refresh
- Useful in automation scripts to ensure Unity processes pending changes

**Notes:**

- Works only on Windows (uses Windows API)
- Does not require Unity to be responsive (direct window manipulation)
- Returns process ID and window title on success

### 1. `editor` - Editor Control

```bash
# Log to Unity console
AIBridgeCLI.exe editor log --message "Hello World"
AIBridgeCLI.exe editor log --message "Warning!" --logType Warning
AIBridgeCLI.exe editor log --message "Error!" --logType Error

# Undo/Redo
AIBridgeCLI.exe editor undo
AIBridgeCLI.exe editor undo --count 3
AIBridgeCLI.exe editor redo

# Compile and Refresh (simple, use `compile` command for full features)
AIBridgeCLI.exe editor compile
AIBridgeCLI.exe editor refresh
AIBridgeCLI.exe editor refresh --forceUpdate true

# Play Mode
AIBridgeCLI.exe editor play
AIBridgeCLI.exe editor stop
AIBridgeCLI.exe editor pause

# Get Editor State
AIBridgeCLI.exe editor get_state
```

### 1.1 `compile` - Compilation Operations (Recommended for AI)

**IMPORTANT for AI**: Use `compile unity` (recommended) or `compile dotnet` (fallback) to verify code changes compile successfully.

```bash
# Recommended: Unity internal compilation (requires Unity Editor running)
AIBridgeCLI.exe compile unity --raw

# Fallback: External dotnet build (when Unity is not running)
AIBridgeCLI.exe compile dotnet --raw
```

**Workflow for AI after modifying code:**

```bash
# Step 1: Try Unity compile first (recommended)
AIBridgeCLI.exe compile unity --raw

# If timeout (Unity not running), fallback to dotnet
AIBridgeCLI.exe compile dotnet --raw

# Output (success): {"success":true,"status":"success","duration":5.2,"errorCount":0,"warningCount":3,...}
# Output (failed):  {"success":false,"status":"failed","errorCount":3,"errors":[{"file":"...","line":10,"code":"CS0103","message":"..."}],...}
```

**Unity compile response fields:**

| Field | Description |
|-------|-------------|
| `success` | Whether build succeeded |
| `status` | "success", "failed", "idle", or "timeout" |
| `duration` | Build duration in seconds |
| `errorCount` | Number of errors |
| `warningCount` | Number of warnings |
| `errors` | Array of error details (file, line, column, code, message) |
| `warnings` | Array of warning details (limited to 20) |

**Unity compile parameters:**

| Parameter | Description | Default |
|-----------|-------------|---------|
| `--timeout` | Total compilation timeout in ms | `120000` |
| `--poll-interval` | Status polling interval in ms | `500` |

**Dotnet compile parameters (fallback):**

| Parameter | Description | Default |
|-----------|-------------|---------|
| `--solution` | Solution file path | `ET.sln` |
| `--configuration` | Build configuration | `Debug` |
| `--verbosity` | MSBuild verbosity | `minimal` |
| `--timeout` | Timeout in ms | `300000` |
| `--no-filter` | Disable error filtering | `false` |
| `--exclude` | Custom exclude paths (comma separated) | - |

**NOTE**:

- `compile unity` requires Unity Editor to be running, automatically polls for completion
- `compile dotnet` runs independently without Unity, has intelligent error filtering
- Use `compile start` and `compile status` for low-level manual compilation control

### 2. `gameobject` - GameObject Operations

```bash
# Create
AIBridgeCLI.exe gameobject create --name "MyCube" --primitiveType Cube
AIBridgeCLI.exe gameobject create --name "Child" --parentPath "Parent"

# Destroy
AIBridgeCLI.exe gameobject destroy --path "MyCube"
AIBridgeCLI.exe gameobject destroy --instanceId 12345

# Find
AIBridgeCLI.exe gameobject find --name "Player"
AIBridgeCLI.exe gameobject find --tag "Enemy" --maxResults 10
AIBridgeCLI.exe gameobject find --withComponent "BoxCollider"

# Set Active
AIBridgeCLI.exe gameobject set_active --path "Player" --active false
AIBridgeCLI.exe gameobject set_active --path "Player" --toggle true

# Rename
AIBridgeCLI.exe gameobject rename --path "OldName" --newName "NewName"

# Duplicate
AIBridgeCLI.exe gameobject duplicate --path "Original"

# Get Info
AIBridgeCLI.exe gameobject get_info --path "Player"
```

### 3. `transform` - Transform Operations

```bash
# Get Transform
AIBridgeCLI.exe transform get --path "Player"

# Set Position
AIBridgeCLI.exe transform set_position --path "Player" --x 0 --y 1 --z 0
AIBridgeCLI.exe transform set_position --path "Player" --x 0 --y 1 --z 0 --local true

# Set Rotation
AIBridgeCLI.exe transform set_rotation --path "Player" --x 0 --y 90 --z 0

# Set Scale
AIBridgeCLI.exe transform set_scale --path "Player" --x 2 --y 2 --z 2
AIBridgeCLI.exe transform set_scale --path "Player" --uniform 2

# Set Parent
AIBridgeCLI.exe transform set_parent --path "Child" --parentPath "Parent"

# Look At
AIBridgeCLI.exe transform look_at --path "Player" --targetPath "Enemy"
AIBridgeCLI.exe transform look_at --path "Player" --targetX 0 --targetY 0 --targetZ 10

# Reset
AIBridgeCLI.exe transform reset --path "Player"

# Sibling Index
AIBridgeCLI.exe transform set_sibling_index --path "Child" --index 0
AIBridgeCLI.exe transform set_sibling_index --path "Child" --first true
```

### 4. `inspector` - Component Operations

```bash
# Get Components
AIBridgeCLI.exe inspector get_components --path "Player"

# Get Properties
AIBridgeCLI.exe inspector get_properties --path "Player" --componentName "Transform"

# Set Property
AIBridgeCLI.exe inspector set_property --path "Player" --componentName "Rigidbody" --propertyName "mass" --value 10

# Add Component
AIBridgeCLI.exe inspector add_component --path "Player" --typeName "Rigidbody"
AIBridgeCLI.exe inspector add_component --path "Player" --typeName "BoxCollider"

# Remove Component
AIBridgeCLI.exe inspector remove_component --path "Player" --componentName "Rigidbody"
```

### 5. `selection` - Selection Operations

```bash
# Get Selection
AIBridgeCLI.exe selection get
AIBridgeCLI.exe selection get --includeComponents true

# Set Selection
AIBridgeCLI.exe selection set --path "Player"
AIBridgeCLI.exe selection set --assetPath "Assets/Prefabs/Player.prefab"

# Clear
AIBridgeCLI.exe selection clear

# Add/Remove
AIBridgeCLI.exe selection add --path "Enemy1"
AIBridgeCLI.exe selection remove --path "Enemy1"
```

### 6. `scene` - Scene Operations

```bash
# Load Scene
AIBridgeCLI.exe scene load --scenePath "Assets/Scenes/Main.unity"
AIBridgeCLI.exe scene load --scenePath "Assets/Scenes/UI.unity" --mode additive

# Save Scene
AIBridgeCLI.exe scene save
AIBridgeCLI.exe scene save --saveAs "Assets/Scenes/NewScene.unity"

# Get Hierarchy
AIBridgeCLI.exe scene get_hierarchy
AIBridgeCLI.exe scene get_hierarchy --depth 3 --includeInactive false

# Get Active Scene
AIBridgeCLI.exe scene get_active

# New Scene
AIBridgeCLI.exe scene new
AIBridgeCLI.exe scene new --setup empty
```

### 7. `prefab` - Prefab Operations

```bash
# Instantiate
AIBridgeCLI.exe prefab instantiate --prefabPath "Assets/Prefabs/Player.prefab"
AIBridgeCLI.exe prefab instantiate --prefabPath "Assets/Prefabs/Enemy.prefab" --posX 5 --posY 0 --posZ 0

# Save as Prefab
AIBridgeCLI.exe prefab save --gameObjectPath "Player" --savePath "Assets/Prefabs/Player.prefab"

# Unpack
AIBridgeCLI.exe prefab unpack --gameObjectPath "Player(Clone)"
AIBridgeCLI.exe prefab unpack --gameObjectPath "Player(Clone)" --completely true

# Get Info
AIBridgeCLI.exe prefab get_info --prefabPath "Assets/Prefabs/Player.prefab"

# Apply Overrides
AIBridgeCLI.exe prefab apply --gameObjectPath "Player(Clone)"
```

### 8. `asset` - AssetDatabase Operations

```bash
# Search Assets (recommended, simplified search)
AIBridgeCLI.exe asset search --mode script --keyword "Player" --raw    # Search scripts
AIBridgeCLI.exe asset search --mode prefab --keyword "UI" --raw        # Search prefabs
AIBridgeCLI.exe asset search --mode all --keyword "Config" --raw       # Search all assets
AIBridgeCLI.exe asset search --filter "t:ScriptableObject" --raw       # Custom filter

# Preset modes: all, prefab, scene, script, texture, material, audio, animation, shader, font, model, so

# Find Assets (precise control)
AIBridgeCLI.exe asset find --filter "t:Prefab"
AIBridgeCLI.exe asset find --filter "t:Texture2D" --searchInFolders "Assets/Textures" --maxResults 50

# Import / Refresh
AIBridgeCLI.exe asset import --assetPath "Assets/Textures/icon.png"
AIBridgeCLI.exe asset refresh

# Get Path from GUID / Load Asset Info
AIBridgeCLI.exe asset get_path --guid "abc123..."
AIBridgeCLI.exe asset load --assetPath "Assets/Prefabs/Player.prefab"
```

### 9. `menu_item` - Invoke Menu Item

```bash
AIBridgeCLI.exe menu_item --menuPath "GameObject/Create Empty"
AIBridgeCLI.exe menu_item --menuPath "Assets/Create/Folder"
```

### 10. `get_logs` - Get Console Logs

```bash
AIBridgeCLI.exe get_logs
AIBridgeCLI.exe get_logs --count 100
AIBridgeCLI.exe get_logs --logType Error
AIBridgeCLI.exe get_logs --logType Warning --count 20
```

### 11. `screenshot` - Screenshot & GIF Recording (Play Mode)

**Requires Play mode.** Files saved to `AIBridgeCache/screenshots/`.

#### Static Screenshot

```bash
# Capture Game view screenshot (JPG format)
AIBridgeCLI.exe screenshot game --raw
```

**Response:**

```json
{"success":true,"data":{"action":"game","imagePath":"...screenshots/game_xxx.jpg","width":1920,"height":1080,"filename":"game_xxx.jpg","timestamp":"2025-01-19T12:00:00"}}
```

#### Animated GIF Recording

```bash
# Record GIF (required: frameCount)
AIBridgeCLI.exe screenshot gif --frameCount 50 --raw

# With custom parameters
AIBridgeCLI.exe screenshot gif --frameCount 100 --fps 25 --scale 0.5 --colorCount 128 --raw
```

**Parameters:**

| Parameter | Range | Default | Description |
|-----------|-------|---------|-------------|
| `--frameCount` | 1-200 | Required | Number of frames to capture |
| `--fps` | 10-30 | 25 | Frames per second |
| `--scale` | 0.25-1.0 | 0.5 | Resolution scale factor |
| `--colorCount` | 64-256 | 128 | GIF palette color count |

**Response:**

```json
{"success":true,"data":{"action":"gif","gifPath":"...screenshots/gif_xxx.gif","filename":"gif_xxx.gif","frameCount":50,"width":480,"height":270,"duration":2.0,"fileSize":512000,"timestamp":"2025-01-19T12:00:00"}}
```

**Estimated File Sizes:**

| Frames | Duration | Resolution | Size |
|--------|----------|------------|------|
| 25 | 1s | 480x270 | 200KB - 800KB |
| 50 | 2s | 480x270 | 400KB - 1.5MB |
| 100 | 4s | 480x270 | 800KB - 3MB |
| 200 | 8s | 480x270 | 1.5MB - 6MB |

### 12. `batch` - Batch Commands

```bash
# Execute multiple commands from JSON
AIBridgeCLI.exe batch execute --commands "[{\"type\":\"editor\",\"params\":{\"action\":\"log\",\"message\":\"Step 1\"}},{\"type\":\"editor\",\"params\":{\"action\":\"log\",\"message\":\"Step 2\"}}]"

# Execute from file
AIBridgeCLI.exe batch from_file --file "commands.json"
```

### 13. `multi` - Execute Multiple Commands (RECOMMENDED)

Execute multiple commands in one CLI call (more efficient than multiple calls).

```bash
# Commands separated by &
AIBridgeCLI.exe multi --cmd 'editor log --message Step1&gameobject create --name Cube --primitiveType Cube' --raw
```

| Option | Description |
|--------|-------------|
| `--cmd <commands>` | Commands separated by `&` |
| `--stdin` | Read from stdin (one per line) |

---

## Runtime Extension

AIBridge provides a `AIBridgeRuntime` MonoBehaviour component for extending functionality during Play Mode.

### Setup

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

### Implementing Custom Handlers

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

### Async Handlers

For long-running operations, implement `IAIBridgeAsyncHandler`:

```csharp
public class MyAsyncHandler : IAIBridgeAsyncHandler
{
    public string[] SupportedActions => new[] { "long_operation" };

    public bool HandleCommandAsync(AIBridgeRuntimeCommand command, Action<AIBridgeRuntimeCommandResult> callback)
    {
        // Start async operation
        StartCoroutine(DoLongOperation(command, callback));
        return true; // Indicate we're handling it
    }

    private IEnumerator DoLongOperation(AIBridgeRuntimeCommand command, Action<AIBridgeRuntimeCommandResult> callback)
    {
        yield return new WaitForSeconds(5);
        callback(AIBridgeRuntimeCommandResult.FromSuccess(command.Id, new { done = true }));
    }
}

// Register async handler
AIBridgeRuntime.Instance.RegisterAsyncHandler(new MyAsyncHandler());
```

---

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

---

**Skill Version**: 1.0
**Package**: cn.lys.aibridge
