<p align="center">
  <img src="./Images/aibridge-banner.png" alt="AIBridge banner" width="100%">
</p>

# AIBridge

English | [中文](./README_CN.md)

![Unity 2019.4+](https://img.shields.io/badge/Unity-2019.4%2B-black?style=flat-square&logo=unity)
![Package 1.3.5](https://img.shields.io/badge/Package-1.3.5-5b6cff?style=flat-square)
![MIT License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)
![AI Unity Automation](https://img.shields.io/badge/Workflow-AI%20Unity%20Automation-14b8a6?style=flat-square)

AIBridge is a Unity package that gives AI coding assistants a stable CLI bridge into Unity Editor. It lets agents resolve real Unity asset paths, inspect scenes and prefabs, edit objects through Unity APIs, run Unity compilation, read Console logs, execute batch workflows, run tests, and capture screenshots or GIFs for visual verification.

The package is designed for AI-assisted Unity development where the assistant must validate changes inside the Editor, not only edit files.

## Why AIBridge

Many Unity automation tools depend on a live socket or MCP session. AIBridge uses file-based command requests and result files, so work can survive script recompilation, domain reloads, editor focus changes, and restarts.

| Dimension | AIBridge | Persistent bridge |
|---|---|---|
| Connection model | File-based requests and results | Live session |
| Compile-cycle resilience | Polls and resumes across reloads | Session can drop |
| Setup | Bundled CLI commands | Server/client wiring |
| AI integration | CLI plus JSON output | Protocol-specific tools |
| Traceability | Command files, results, logs, screenshots | Session state |
| Extensibility | Unity commands plus CLI builders | Tool server extensions |

## Requirements

- Unity 2019.4 or later.
- .NET 8.0 Runtime for the bundled CLI.
- Unity Editor must be running for Unity-side commands such as `compile unity`, `asset`, `scene`, `inspector`, `prefab`, `screenshot`, and `get_logs`.

## Installation

Install with Unity Package Manager using this Git URL:

```text
https://github.com/liyingsong99/AIBridge.git
```

You can also clone this repository into a Unity project's `Packages` folder.

## Configure AI Workflow

1. Open `Tools > AIBridge Settings` in Unity Editor.
2. Open the `Skills Installation` section.
3. Select the AI tools you use.
4. Click `Install Selected Integrations`.
5. Optionally click `Install Unity Project AGENTS.md Template` to create a root `AGENTS.md`.

Installed AIBridge Skills are written to the project-root `.skills/` directory by default. You can change that shared directory in the Skills Installation tab, for example to `.skill`. Each AI tool only receives a minimal RootRule or plugin adapter that references the shared Skill root. The RootRule only includes the fixed CLI path, common commands, shared Skill root, and `aibridge-development-workflow` entry point; full routing and checklists live in the workflow Skill. Command references are generated under each installed Skill's `references/` directory.

You can also open the `Recommended Skill Library` tab, refresh the default `obra/superpowers` repository, and install third-party Skills into the same shared root.

## CLI Basics

Run commands from the Unity project root after AIBridge has copied its CLI cache:

```powershell
$CLI = "./.aibridge/cli/AIBridgeCLI.exe"
```

On macOS/Linux, use the bundled platform executable or run the DLL with `dotnet` according to your project setup.

Most commands use this form:

```bash
$CLI <command> <action> [options]
```

CLI-only helpers differ slightly: `focus` has no action, `dialog` uses `status/click/wait`, and `multi` uses `--cmd` or `--stdin`.

## Common Commands

### Editor, Compile, Logs, Tests

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

Use `compile unity` for Unity validation. `compile dotnet` is only an extra solution build check and is not a replacement for Unity compilation.

Use `dialog status` when Unity commands time out and the Editor may be blocked by a modal save/confirm dialog. When no dialog is detected, compact JSON omits `blockedByDialog` and `dialogs`; missing fields mean no dialog. macOS dialog inspection/clicking requires Accessibility permission. Unity commands can opt into explicit timeout handling, for example `--on-dialog cancel` or `--on-dialog discard`.

`get_logs` supports the Settings window defaults and optional regex filtering:

- The Logs tab lets you set the default minimum log level and an optional global regex filter.
- If you omit `--logType`, `get_logs` uses the Settings window default level filter.
- If you pass `--regex`, that regex is applied to the log message text before returning results.
- The global regex filter can be enabled from the Logs tab and will apply whenever `--regex` is not provided.

### Modal Dialog Handling

Unity modal dialogs can block the Editor main thread before AIBridge has a chance to process normal Unity-side commands. AIBridge includes a CLI-only `dialog` helper that inspects and clicks those OS-level windows directly, so an AI assistant can recover from save, discard, cancel, delete, replace, and confirmation prompts without guessing.

```bash
$CLI dialog status
$CLI dialog click --choice discard
$CLI dialog click --button "Don't Save"
$CLI dialog wait --timeout 5000 --click cancel
```

When Unity is already blocked by a modal dialog, normal Unity commands return the detected dialog details instead of silently waiting for a timeout. The response includes visible button text and logical choices such as `save`, `discard`, and `cancel`, so the assistant can choose the next explicit click. For unattended flows, pass `--on-dialog <choice>` to a Unity command, for example:

```bash
$CLI scene load --scenePath "Assets/Scenes/Main.unity" --on-dialog discard
```

On Windows, dialog buttons are detected through window APIs and support button mnemonics such as `&Don't Save`. On macOS, dialog inspection and clicking require Accessibility permission.

### Assets And Scenes

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

### GameObjects And Transforms

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

### Inspector And Prefabs

```bash
$CLI inspector get_components --path "Player"
$CLI inspector get_properties --path "Player" --componentName "Transform"
$CLI inspector find_property --path "Player" --componentName "Rigidbody" --keyword "mass"
$CLI inspector set_property --path "Player" --componentName "Rigidbody" --propertyName "mass" --value 10

$CLI prefab get_info --prefabPath "Assets/Prefabs/Player.prefab"
$CLI prefab get_hierarchy --prefabPath "Assets/Prefabs/Player.prefab" --includeComponents true
$CLI prefab patch --prefabPath "Assets/Prefabs/Player.prefab" --ops ".aibridge/patch_ops/player_patch.json" --dryRun true
```

For simple prefab field edits, use `inspector set_property` with `assetPath + objectPath + componentName`. For multi-step prefab edits, use `prefab patch --ops <file>` and run `--dryRun true` first. For unsupported Prefab, Scene, ScriptableObjectTable, or custom `.asset` structural edits, use `unity-yaml-editing` as the direct YAML fallback.

PowerShell JSON tip:

```powershell
$values = (@{ 'm_LocalPosition.x' = 0; 'm_LocalPosition.y' = 1 } | ConvertTo-Json -Compress) -replace '"', '\"'
& "./.aibridge/cli/AIBridgeCLI.exe" inspector set_properties --assetPath 'Assets/Prefabs/Player.prefab' --componentName Transform --values $values
```

### Batch And Multi

```bash
$CLI batch from_text --text "call editor log 'Hello'\ndelay 1000"
$CLI batch from_file --file ".aibridge/scripts/setup_scene.txt"
$CLI multi --cmd "editor log --message Step1&get_logs --logType Error --count 1"
```

Use `multi --stdin` for long scripts or JSON-heavy commands:

```powershell
$script = @'
editor log --message "Start"
delay 1000
get_logs --logType Error --count 1
'@
$script | & "./.aibridge/cli/AIBridgeCLI.exe" multi --stdin
```

### Visual Verification

```bash
$CLI gameview get_resolution
$CLI gameview set_resolution --width 1920 --height 1080
$CLI gameview list_resolutions
$CLI screenshot game
$CLI screenshot gif --frameCount 50 --fps 20 --scale 0.5
```

Game view screenshots and GIF capture require Play Mode.

## Recommended AI Work Loop

1. Resolve the real Unity asset or object path.
2. Inspect the scene, prefab, component, or SerializedProperty state.
3. Apply the smallest safe change through Unity-aware commands or source edits.
4. Run `compile unity`.
5. Read `get_logs --logType Error`.
6. Use screenshots or GIFs for visual changes.

## Repository Layout

```text
Editor/        Unity Editor commands, settings window, integrations, prefab patching
Runtime/       Runtime bridge contracts and lightweight runtime data
Tools~/       AIBridgeCLI source and bundled platform binaries
Templates~/   AI root-rule templates and Unity project AGENTS.md template
Skill~/       AIBridge Skills and workflow references
Tests/        Unity EditMode tests
Images/       README images
```

## License

MIT License. See [LICENSE](./LICENSE).

## Contributing

Issues and pull requests are welcome. When changing Unity-facing behavior, update the relevant CLI examples, Skill references, and validation notes.
