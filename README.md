<p align="center">
  <img src="./Images/aibridge-banner.png" alt="AIBridge banner" width="100%">
</p>

# AIBridge

English | [中文](./README_CN.md)

![Unity 2019.4+](https://img.shields.io/badge/Unity-2019.4%2B-black?style=flat-square&logo=unity)
![Package 1.2.3](https://img.shields.io/badge/Package-1.2.3-5b6cff?style=flat-square)
![MIT License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)
![AI Unity Automation](https://img.shields.io/badge/Workflow-AI%20Unity%20Automation-14b8a6?style=flat-square)

AIBridge is a Unity package that gives AI coding assistants a stable command bridge into Unity Editor. It helps agents locate assets, inspect scenes and prefabs, edit Unity objects, run compile/build checks, read console logs, and capture screenshots or GIFs for visual verification.

It is built for teams that want AI to do real Unity work, not only generate text or code suggestions.

## Highlights

- **File-based bridge:** command requests and results survive editor restarts, script compilation, and domain reloads.
- **CLI-first workflow:** standard commands with compact JSON output are easy for AI agents, shell scripts, and CI-like automation to consume.
- **Unity-aware operations:** asset lookup, scene hierarchy, prefab inspection, component properties, selection, transforms, and menu actions are exposed through commands.
- **Visual verification loop:** screenshot and GIF capture make UI/gameplay validation observable instead of guess-based.
- **Agent workflow templates:** install AGENTS.md rules for Codex, Claude, Cursor, Cline, and other AI coding workflows.
- **Extensible command model:** add Unity-side commands and CLI builders without depending on a persistent MCP server.

## Why AIBridge?

Many Unity AI integrations depend on a live socket or MCP session. AIBridge uses durable command files and persisted results, so work can continue across the moments Unity is most likely to interrupt tools: recompiles, reloads, editor focus changes, and restarts.

| Dimension | AIBridge | Persistent MCP-style bridge |
|---|---|---|
| Connection stability | File-based requests and results | Depends on a live connection |
| Compile-cycle resilience | Keeps polling and resumes after reloads | Session may drop during compilation |
| Setup cost | Works through bundled CLI commands | Requires server/client configuration |
| AI integration | CLI commands plus JSON output | Requires protocol-specific tool wiring |
| Traceability | Command files, result files, screenshots, logs | Often tied to current session state |
| Extensibility | Unity command handlers plus CLI builders | Usually tool-server based |

## What You Can Automate

- Find the correct Unity asset path before editing scripts, prefabs, scenes, materials, textures, or ScriptableObjects.
- Inspect scene hierarchy, selected objects, prefab metadata, components, and serialized properties.
- Create, rename, delete, duplicate, parent, and transform GameObjects from automation.
- Modify component fields through Unity serialization APIs instead of hand-editing YAML.
- Trigger Unity compilation and inspect real compiler results.
- Read Unity Console errors and warnings after automated changes.
- Run multi-step editor scripts with `batch` and `multi`.
- Capture Game view screenshots and GIFs during Play Mode.
- Set Game view resolution for repeatable visual testing.

## Installation

Install AIBridge with Unity Package Manager using this Git URL:

```text
https://github.com/liyingsong99/AIBridge.git
```

You can also clone or download this repository and place it under your Unity project's `Packages` folder.

## Configure AI Workflow

AIBridge includes a ready-to-use AGENTS.md workflow template for AI collaboration:

1. Open `Tools > AIBridge Settings` in Unity Editor.
2. Switch to the `Skills Installation` tab.
3. Click `Install AGENTS.md`.
4. Confirm the installation into your Unity project root.

The installed rules include requirement confirmation, implementation flow, Unity compile checks, console diagnostics, C# compatibility rules, and quality checklists.

## Requirements

- Unity 2019.4 or later.
- .NET 8.0 Runtime for the bundled CLI tools.

## CLI Quick Reference

Run commands from the Unity project root after AIBridge has installed its CLI cache:

```powershell
$CLI = "./AIBridgeCache/CLI/AIBridgeCLI.exe"
```

On macOS/Linux, use the bundled platform CLI or run the DLL with `dotnet` according to your project setup.

### Asset Lookup

```bash
./AIBridgeCache/CLI/AIBridgeCLI.exe asset search --mode script --keyword "Player" --format paths
./AIBridgeCache/CLI/AIBridgeCLI.exe asset find --filter "t:Prefab" --format paths
./AIBridgeCache/CLI/AIBridgeCLI.exe asset get_path --guid "abc123..."
```

### Scene, Selection, And Prefab Context

```bash
./AIBridgeCache/CLI/AIBridgeCLI.exe scene get_hierarchy --depth 3 --includeInactive false
./AIBridgeCache/CLI/AIBridgeCLI.exe selection get --includeComponents true
./AIBridgeCache/CLI/AIBridgeCLI.exe prefab get_info --prefabPath "Assets/Prefabs/Player.prefab"
./AIBridgeCache/CLI/AIBridgeCLI.exe prefab get_hierarchy --prefabPath "Assets/Prefabs/Player.prefab"
```

### Component Inspection And Editing

```bash
./AIBridgeCache/CLI/AIBridgeCLI.exe inspector get_components --path "Player"
./AIBridgeCache/CLI/AIBridgeCLI.exe inspector get_properties --path "Player" --componentName "Transform"
```

PowerShell tip for complex JSON values:

```powershell
$values = (@{ 'm_LocalPosition.x' = 0; 'm_LocalPosition.y' = 1 } | ConvertTo-Json -Compress) -replace '"', '\"'
& "./AIBridgeCache/CLI/AIBridgeCLI.exe" inspector set_properties --assetPath 'Assets/Prefabs/Player.prefab' --componentName Transform --values $values
```

### Scene Object Operations

```bash
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject create --name "MyCube" --primitiveType Cube
./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_position --path "Player" --x 0 --y 1 --z 0
```

### Compile, Logs, And Automation

```bash
./AIBridgeCache/CLI/AIBridgeCLI.exe compile unity
./AIBridgeCache/CLI/AIBridgeCLI.exe get_logs --logType Error
./AIBridgeCache/CLI/AIBridgeCLI.exe batch from_text --text "call editor log 'Hello'\ndelay 1000"
./AIBridgeCache/CLI/AIBridgeCLI.exe multi --cmd "editor log --message Step1&get_logs --logType Error --count 1"
```

`multi --cmd` writes plain CLI lines as Batch `call` lines. Use `multi --stdin` for longer scripts or commands with complex JSON.

### Visual Verification

```bash
./AIBridgeCache/CLI/AIBridgeCLI.exe screenshot game
./AIBridgeCache/CLI/AIBridgeCLI.exe screenshot gif --frameCount 50
./AIBridgeCache/CLI/AIBridgeCLI.exe gameview set_resolution --width 1920 --height 1080
./AIBridgeCache/CLI/AIBridgeCLI.exe gameview get_resolution
```

## Recommended AI Work Loop

1. Resolve the real Unity asset or object path first.
2. Inspect the current scene, prefab, component, or serialized properties.
3. Apply the smallest safe change through Unity-aware commands or source edits.
4. Run `compile unity`.
5. Read `get_logs --logType Error`.
6. Capture screenshots or GIFs when the result is visual.

## Repository Layout

```text
Editor/        Unity Editor commands, tools, settings window, and integrations
Runtime/       Runtime bridge contracts and lightweight runtime data
Tools~/       AIBridgeCLI source and bundled platform binaries
Templates~/   AI workflow rule templates
Skill~/       AIBridge skill definition for AI assistants
Images/       README and promotional images
```

## License

MIT License. See [LICENSE](./LICENSE).

## Contributing

Issues and pull requests are welcome. When changing Unity-facing behavior, include the relevant CLI command examples and validation notes in the documentation.
