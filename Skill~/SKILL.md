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

## Search & Query Priority for Unity Projects

When you need to locate files or inspect Unity-managed assets **inside a Unity project**, prefer AIBridge before generic repo search tools.

**Use AIBridge first for:**

- Asset/resource lookup in large Unity projects
- Finding scripts, prefabs, scenes, materials, textures, ScriptableObjects
- Resolving the canonical Unity asset path before opening a file
- Checking Unity-side asset metadata before opening a file with your host AI's native read tool

**Recommended workflow:**

1. Use `asset search` or `asset find` with `format=paths` to locate candidate files through Unity's AssetDatabase index with minimal response payload
2. Use `asset get_path` only when you only have a GUID
3. Use `asset load` only when you want to confirm asset metadata before opening it
4. Once the canonical path is known, use your host AI's native file-read tool to inspect file contents
5. Use `asset read_text` only as a fallback when native reads are unavailable or when a Unity-side line window is specifically needed
6. Only fall back to generic `grep` / filesystem search when AIBridge cannot cover the target

**Why:** Unity's AssetDatabase index is usually faster and more accurate than generic file search for large Unity projects, especially for assets that AI may not find reliably with repo search alone.

---

## AIBridgeCLI - Recommended Method

**IMPORTANT**: On Windows, run `./AIBridgeCache/CLI/AIBridgeCLI.exe` from the Unity project root when sending commands. This avoids PATH resolution issues, preserves UTF-8 output, and provides a cleaner interface.

### CLI Location

```
./AIBridgeCache/CLI/AIBridgeCLI.exe
```

> **Note**: The CLI is automatically copied to `AIBridgeCache/CLI/` when the package is installed. Run commands from the Unity project root so `./AIBridgeCache/CLI/AIBridgeCLI.exe` resolves consistently regardless of how the package was installed (local, git, or registry).

### Cross-Platform Support

**Windows:**
```bash
./AIBridgeCache/CLI/AIBridgeCLI.exe <command> <action> [options]
```

**macOS / Linux:**
```bash
# Requires .NET Runtime installed
dotnet ./AIBridgeCache/CLI/AIBridgeCLI.dll <command> <action> [options]

# Example
dotnet ./AIBridgeCache/CLI/AIBridgeCLI.dll get_logs --logType Error
```

> **Note**: The CLI is built as a .NET assembly, so it can run on macOS/Linux via `dotnet` command from the Unity project root. Install .NET Runtime from https://dotnet.microsoft.com/download if not already installed.

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
./AIBridgeCache/CLI/AIBridgeCLI.exe <command-type> <action> [options]

# Examples
./AIBridgeCache/CLI/AIBridgeCLI.exe editor log --message "Hello World"
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject create --name "MyCube" --primitiveType Cube
./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_position --path "Player" --x 1 --y 2 --z 3
```

### PowerShell Execution

When invoking from PowerShell, use the `&` call operator:

```powershell
& "./AIBridgeCache/CLI/AIBridgeCLI.exe" <command> <action> [options]
```

### Global Options

| Option | Description |
|--------|-------------|
| `--timeout <ms>` | Timeout in milliseconds (default: 5000) |
| `--no-wait` | Don't wait for result, return command ID immediately |
| `--raw` | Output compact raw JSON (default) |
| `--pretty` | Output human-readable formatted text |
| `--quiet` | Quiet mode, minimal output |
| `--json <json>` | Pass complex parameters as JSON string |
| `--stdin` | Read parameters from stdin (JSON format) |
| `--help` | Show help |

**AI Usage:** AIBridgeCLI outputs compact JSON by default, which is preferred for AI parsing. Use `--pretty` only when you want human-readable output. Prefer `asset search` / `asset find --format paths` for canonical Unity paths, use `asset get_path` only when starting from a GUID, and read file contents with the host AI's native file-read tool before falling back to `asset read_text`.

All Windows examples below assume you run them from the Unity project root.

---

## Command Reference

### 0. `focus` - Bring Unity to Foreground (CLI-only)

**IMPORTANT**: This is a CLI-only command that does NOT require Unity to process it. It uses Windows API to bring the Unity Editor window to the foreground, which triggers automatic asset refresh and code compilation.

```bash
# Bring Unity Editor window to foreground
./AIBridgeCache/CLI/AIBridgeCLI.exe focus

# Default JSON output
./AIBridgeCache/CLI/AIBridgeCLI.exe focus
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
./AIBridgeCache/CLI/AIBridgeCLI.exe editor log --message "Hello World"
./AIBridgeCache/CLI/AIBridgeCLI.exe editor log --message "Warning!" --logType Warning
./AIBridgeCache/CLI/AIBridgeCLI.exe editor log --message "Error!" --logType Error

# Undo/Redo
./AIBridgeCache/CLI/AIBridgeCLI.exe editor undo
./AIBridgeCache/CLI/AIBridgeCLI.exe editor undo --count 3
./AIBridgeCache/CLI/AIBridgeCLI.exe editor redo

# Compile and Refresh (simple, use `compile` command for full features)
./AIBridgeCache/CLI/AIBridgeCLI.exe editor compile
./AIBridgeCache/CLI/AIBridgeCLI.exe editor refresh
./AIBridgeCache/CLI/AIBridgeCLI.exe editor refresh --forceUpdate true

# Play Mode
./AIBridgeCache/CLI/AIBridgeCLI.exe editor play
./AIBridgeCache/CLI/AIBridgeCLI.exe editor stop
./AIBridgeCache/CLI/AIBridgeCLI.exe editor pause

# Get Editor State
./AIBridgeCache/CLI/AIBridgeCLI.exe editor get_state
```

### 1.1 `compile` - Compilation Operations (Recommended for AI)

**IMPORTANT for AI**: Use `compile unity` first to verify Unity script compilation. Use `compile dotnet` only when you explicitly want to validate the generated solution build.

```bash
# Recommended: Unity internal compilation (requires Unity Editor running)
./AIBridgeCache/CLI/AIBridgeCLI.exe compile unity

# Optional: External dotnet solution build
./AIBridgeCache/CLI/AIBridgeCLI.exe compile dotnet

# Optional: Explicitly choose a solution when multiple exist
./AIBridgeCache/CLI/AIBridgeCLI.exe compile dotnet --solution MyGame.sln
```

**Workflow for AI after modifying code:**

```bash
# Step 1: Try Unity compile first (recommended)
./AIBridgeCache/CLI/AIBridgeCLI.exe compile unity

# Optional: Run dotnet solution build as a separate compatibility check
./AIBridgeCache/CLI/AIBridgeCLI.exe compile dotnet

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
| `--transport-timeout` | Single command round-trip timeout in ms | `min(30000, timeout)` |

**Dotnet compile parameters (explicit solution build check):**

| Parameter | Description | Default |
|-----------|-------------|---------|
| `--solution` | Solution file path. If omitted, auto-detect from project root; if ambiguous, pass explicitly | auto-detect |
| `--configuration` | Build configuration | `Debug` |
| `--verbosity` | MSBuild verbosity | `minimal` |
| `--timeout` | Timeout in ms | `300000` |
| `--no-filter` | Disable error filtering | `false` |
| `--exclude` | Custom exclude paths (comma separated) | - |

**NOTE**:

- `compile unity` requires Unity Editor to be running, automatically polls for completion
- If Unity is already compiling or temporarily busy, `compile unity` will attach to the current compilation and keep polling until a final result or the outer timeout is reached
- `compile unity` does not fall back to `dotnet build`; Unity compile and solution build are intentionally separate checks
- `--timeout` controls the full compile wait window, while `--transport-timeout` controls each CLI↔Unity communication attempt
- `compile dotnet` runs independently without Unity, auto-detects a single root-level `.sln` or `.slnx` when `--solution` is omitted, and has intelligent error filtering
- Use `compile start` and `compile status` for low-level manual compilation control

### 2. `gameobject` - GameObject Operations

```bash
# Create
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject create --name "MyCube" --primitiveType Cube
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject create --name "Child" --parentPath "Parent"

# Destroy
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject destroy --path "MyCube"
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject destroy --instanceId 12345

# Find
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject find --name "Player"
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject find --tag "Enemy" --maxResults 10
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject find --withComponent "BoxCollider"

# Set Active
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject set_active --path "Player" --active false
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject set_active --path "Player" --toggle true

# Rename
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject rename --path "OldName" --newName "NewName"

# Duplicate
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject duplicate --path "Original"

# Get Info
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject get_info --path "Player"
```

### 3. `transform` - Transform Operations

```bash
# Get Transform
./AIBridgeCache/CLI/AIBridgeCLI.exe transform get --path "Player"

# Set Position
./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_position --path "Player" --x 0 --y 1 --z 0
./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_position --path "Player" --x 0 --y 1 --z 0 --local true

# Set Rotation
./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_rotation --path "Player" --x 0 --y 90 --z 0

# Set Scale
./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_scale --path "Player" --x 2 --y 2 --z 2
./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_scale --path "Player" --uniform 2

# Set Parent
./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_parent --path "Child" --parentPath "Parent"

# Look At
./AIBridgeCache/CLI/AIBridgeCLI.exe transform look_at --path "Player" --targetPath "Enemy"
./AIBridgeCache/CLI/AIBridgeCLI.exe transform look_at --path "Player" --targetX 0 --targetY 0 --targetZ 10

# Reset
./AIBridgeCache/CLI/AIBridgeCLI.exe transform reset --path "Player"

# Sibling Index
./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_sibling_index --path "Child" --index 0
./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_sibling_index --path "Child" --first true
```

### 4. `inspector` - Component Operations

```bash
# Get Components
./AIBridgeCache/CLI/AIBridgeCLI.exe inspector get_components --path "Player"

# Get Properties
./AIBridgeCache/CLI/AIBridgeCLI.exe inspector get_properties --path "Player" --componentName "Transform"

# Set Property
./AIBridgeCache/CLI/AIBridgeCLI.exe inspector set_property --path "Player" --componentName "Rigidbody" --propertyName "mass" --value 10

# Add Component
./AIBridgeCache/CLI/AIBridgeCLI.exe inspector add_component --path "Player" --typeName "Rigidbody"
./AIBridgeCache/CLI/AIBridgeCLI.exe inspector add_component --path "Player" --typeName "BoxCollider"

# Remove Component
./AIBridgeCache/CLI/AIBridgeCLI.exe inspector remove_component --path "Player" --componentName "Rigidbody"
```

### 5. `selection` - Selection Operations

```bash
# Get Selection
./AIBridgeCache/CLI/AIBridgeCLI.exe selection get
./AIBridgeCache/CLI/AIBridgeCLI.exe selection get --includeComponents true

# Set Selection
./AIBridgeCache/CLI/AIBridgeCLI.exe selection set --path "Player"
./AIBridgeCache/CLI/AIBridgeCLI.exe selection set --assetPath "Assets/Prefabs/Player.prefab"

# Clear
./AIBridgeCache/CLI/AIBridgeCLI.exe selection clear

# Add/Remove
./AIBridgeCache/CLI/AIBridgeCLI.exe selection add --path "Enemy1"
./AIBridgeCache/CLI/AIBridgeCLI.exe selection remove --path "Enemy1"
```

### 6. `scene` - Scene Operations

```bash
# Load Scene
./AIBridgeCache/CLI/AIBridgeCLI.exe scene load --scenePath "Assets/Scenes/Main.unity"
./AIBridgeCache/CLI/AIBridgeCLI.exe scene load --scenePath "Assets/Scenes/UI.unity" --mode additive

# Save Scene
./AIBridgeCache/CLI/AIBridgeCLI.exe scene save
./AIBridgeCache/CLI/AIBridgeCLI.exe scene save --saveAs "Assets/Scenes/NewScene.unity"

# Get Hierarchy
./AIBridgeCache/CLI/AIBridgeCLI.exe scene get_hierarchy
./AIBridgeCache/CLI/AIBridgeCLI.exe scene get_hierarchy --depth 3 --includeInactive false

# Get Active Scene
./AIBridgeCache/CLI/AIBridgeCLI.exe scene get_active

# New Scene
./AIBridgeCache/CLI/AIBridgeCLI.exe scene new
./AIBridgeCache/CLI/AIBridgeCLI.exe scene new --setup empty
```

### 7. `prefab` - Prefab Operations

```bash
# Instantiate
./AIBridgeCache/CLI/AIBridgeCLI.exe prefab instantiate --prefabPath "Assets/Prefabs/Player.prefab"
./AIBridgeCache/CLI/AIBridgeCLI.exe prefab instantiate --prefabPath "Assets/Prefabs/Enemy.prefab" --posX 5 --posY 0 --posZ 0

# Save as Prefab
./AIBridgeCache/CLI/AIBridgeCLI.exe prefab save --gameObjectPath "Player" --savePath "Assets/Prefabs/Player.prefab"

# Unpack
./AIBridgeCache/CLI/AIBridgeCLI.exe prefab unpack --gameObjectPath "Player(Clone)"
./AIBridgeCache/CLI/AIBridgeCLI.exe prefab unpack --gameObjectPath "Player(Clone)" --completely true

# Get Info
./AIBridgeCache/CLI/AIBridgeCLI.exe prefab get_info --prefabPath "Assets/Prefabs/Player.prefab"

# Get Hierarchy
./AIBridgeCache/CLI/AIBridgeCLI.exe prefab get_hierarchy --prefabPath "Assets/Prefabs/Player.prefab"
./AIBridgeCache/CLI/AIBridgeCLI.exe prefab get_hierarchy --prefabPath "Assets/Prefabs/UI/MainPanel.prefab" --depth 4 --includeInactive false

# Apply Overrides
./AIBridgeCache/CLI/AIBridgeCLI.exe prefab apply --gameObjectPath "Player(Clone)"
```

### 8. `asset` - AssetDatabase Operations

```bash
# Search Assets (recommended for canonical Unity paths)
./AIBridgeCache/CLI/AIBridgeCLI.exe asset search --mode script --keyword "Player" --format paths    # Search scripts
./AIBridgeCache/CLI/AIBridgeCLI.exe asset search --mode prefab --keyword "UI" --format paths        # Search prefabs
./AIBridgeCache/CLI/AIBridgeCLI.exe asset search --mode all --keyword "Config" --format paths       # Search all assets
./AIBridgeCache/CLI/AIBridgeCLI.exe asset search --filter "t:ScriptableObject" --format paths       # Custom filter

# Preset modes: all, prefab, scene, script, texture, material, audio, animation, shader, font, model, so

# Find Assets (precise control)
./AIBridgeCache/CLI/AIBridgeCLI.exe asset find --filter "t:Prefab" --format paths
./AIBridgeCache/CLI/AIBridgeCLI.exe asset find --filter "t:Texture2D" --format paths --searchInFolders "Assets/Textures" --maxResults 50

# Import / Refresh
./AIBridgeCache/CLI/AIBridgeCLI.exe asset import --assetPath "Assets/Textures/icon.png"
./AIBridgeCache/CLI/AIBridgeCLI.exe asset refresh

# Get Path from GUID / Load Asset Info (metadata only)
./AIBridgeCache/CLI/AIBridgeCLI.exe asset get_path --guid "abc123..."
./AIBridgeCache/CLI/AIBridgeCLI.exe asset load --assetPath "Assets/Prefabs/Player.prefab"

# Fallback: read text only when native file reads are unavailable
./AIBridgeCache/CLI/AIBridgeCLI.exe asset read_text --assetPath "Assets/Scripts/Player.cs" --startLine 1 --maxLines 120
./AIBridgeCache/CLI/AIBridgeCLI.exe asset read_text --assetPath "Assets/Configs/GameConfig.asset" --startLine 1 --maxLines 80
./AIBridgeCache/CLI/AIBridgeCLI.exe asset read_text --assetPath "Assets/Scenes/Main.unity" --startLine 1 --maxLines 200 --maxChars 12000
```

**AI priority note:** For Unity-internal file discovery, use `asset search` / `asset find` with `format=paths` before generic repository search. Use `asset get_path` only when starting from a GUID, and `asset load` only when metadata confirmation is needed. Once the path is known, prefer your host AI's native file-read tool for contents. Use `asset read_text` only as a fallback when native reads are unavailable or when a Unity-side line window is specifically needed.

`format=full` (default) returns asset objects in `data.assets`. `format=paths` returns Unity asset path strings in `data.assets`, which is usually the most efficient shape for AI-driven lookup.

### 9. `menu_item` - Invoke Menu Item

```bash
./AIBridgeCache/CLI/AIBridgeCLI.exe menu_item --menuPath "GameObject/Create Empty"
./AIBridgeCache/CLI/AIBridgeCLI.exe menu_item --menuPath "Assets/Create/Folder"
```

### 10. `get_logs` - Get Console Logs

```bash
./AIBridgeCache/CLI/AIBridgeCLI.exe get_logs
./AIBridgeCache/CLI/AIBridgeCLI.exe get_logs --count 100
./AIBridgeCache/CLI/AIBridgeCLI.exe get_logs --logType Error
./AIBridgeCache/CLI/AIBridgeCLI.exe get_logs --logType Warning --count 20
```

### 11. `screenshot` - Screenshot & GIF Recording (Play Mode)

**Requires Play mode.** Files saved to `AIBridgeCache/screenshots/`.

#### Static Screenshot

```bash
# Capture Game view screenshot (JPG format)
./AIBridgeCache/CLI/AIBridgeCLI.exe screenshot game
```

**Response:**

```json
{"success":true,"data":{"action":"game","imagePath":"...screenshots/game_xxx.jpg","width":1920,"height":1080,"filename":"game_xxx.jpg","timestamp":"2025-01-19T12:00:00"}}
```

#### Animated GIF Recording

```bash
# Record GIF (required: frameCount)
./AIBridgeCache/CLI/AIBridgeCLI.exe screenshot gif --frameCount 50

# With custom parameters
./AIBridgeCache/CLI/AIBridgeCLI.exe screenshot gif --frameCount 100 --fps 25 --scale 0.5 --colorCount 128

# With delayed start (useful for manual capture timing)
./AIBridgeCache/CLI/AIBridgeCLI.exe screenshot gif --frameCount 100 --fps 25 --startDelay 0.5
```

**Parameters:**

| Parameter | Range | Default | Description |
|-----------|-------|---------|-------------|
| `--frameCount` | 1-200 | Required | Number of frames to capture |
| `--fps` | 10-30 | 25 | Frames per second |
| `--scale` | 0.25-1.0 | 0.5 | Resolution scale factor |
| `--colorCount` | 64-256 | 128 | GIF palette color count |
| `--startDelay` | 0-5 seconds | 0 | Delay before capture starts |

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
./AIBridgeCache/CLI/AIBridgeCLI.exe batch execute --commands "[{\"type\":\"editor\",\"params\":{\"action\":\"log\",\"message\":\"Step 1\"}},{\"type\":\"editor\",\"params\":{\"action\":\"log\",\"message\":\"Step 2\"}}]"

# Execute from file
./AIBridgeCache/CLI/AIBridgeCLI.exe batch from_file --file "commands.json"
```

### 13. `multi` - Execute Multiple Commands (RECOMMENDED)

Execute multiple commands in one CLI call (more efficient than multiple calls).

```bash
# Commands separated by &
./AIBridgeCache/CLI/AIBridgeCLI.exe multi --cmd 'editor log --message Step1&gameobject create --name Cube --primitiveType Cube'
```

| Option | Description |
|--------|-------------|
| `--cmd <commands>` | Commands separated by `&` |
| `--stdin` | Read from stdin (one per line) |

---

**Skill Version**: 1.0
**Package**: cn.lys.aibridge
