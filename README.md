# AIBridge

English | [中文](./README_CN.md)

A Unity plugin for durable AI-assisted Unity work — asset lookup, scene editing, build automation, and visual verification.

![Unity 2019.4+](https://img.shields.io/badge/Unity-2019.4%2B-black?style=flat-square&logo=unity) ![MIT License](https://img.shields.io/badge/License-MIT-blue?style=flat-square) ![AI-assisted Unity ops](https://img.shields.io/badge/Workflow-AI--assisted%20Unity%20ops-5b6cff?style=flat-square)

## What is AIBridge?

AIBridge helps AI assistants work with Unity projects in a way that fits real production workflows.

Instead of only generating code, it can help find the correct Unity assets, inspect and modify scenes or prefabs, run compile and build steps, and capture screenshots or GIFs for verification.

It is built for teams who want AI to do real Unity work, not just talk about code.

## Why AIBridge?

AIBridge and UnityMCP solve a similar problem from different angles.

UnityMCP is centered on a live MCP connection between AI clients and the Unity Editor. AIBridge is centered on durable Unity task execution: Unity-aware asset lookup, repeatable automation, and project operations that remain practical across compile cycles and editor restarts.

If you want an MCP-native live editor connection, UnityMCP is a strong fit. If you want inspectable, repeatable Unity task flows that stay comfortable across compile cycles and editor restarts, AIBridge is optimized for that tradeoff.

| Topic | AIBridge | UnityMCP |
|---|---|---|
| Primary model | Durable Unity task execution | Live MCP/editor connection |
| Best fit | Repeatable, inspectable task flows | Interactive live tool use |
| Reuse style | Durable project operations | Client-session tool usage |
| Typical strength | Build, verification, and durable project ops | Live editor control from MCP clients |

## What you can do with it

- Find the right Unity asset and resolve canonical project paths before editing.
- Inspect and modify scenes, GameObjects, transforms, components, and prefabs.
- Run compile, preflight, packaging, and build tasks from AI-driven automation.
- Capture screenshots and animated GIFs for visual verification.
- Automate repeated Unity tasks without falling back to text-only guidance.

## Common Unity workflows

### 1. Let AI find the correct asset path first
In larger Unity projects, the first problem is often locating the right script, prefab, scene, or ScriptableObject. AIBridge is designed to let AI resolve the real Unity path before it edits anything.

### 2. Ask AI to modify a scene like a Unity teammate
Use AI to create objects, adjust transforms, update component values, reorganize hierarchy, and save scene changes without turning the task into manual editor work.

### 3. Automate repeated build steps
Preflight checks, version bumps, Android packaging, iOS packaging, and other repeated build steps can be handled through AI-assisted automation instead of manual repetition.

### 4. Generate repeatable scene content
When scene setup follows a manifest or a predictable pattern, AIBridge can help apply the same structure consistently instead of repeating the same manual setup work.

### 5. Verify visual results instead of guessing
Screenshots and GIF capture make it easier for AI-assisted tasks to validate UI or gameplay changes with actual editor output instead of text-only descriptions.

### 6. Keep AI-assisted project work verifiable
By combining Unity-aware operations with screenshots and build-oriented automation, teams can review AI output against real project state instead of relying on vague prompts alone.

## Installation

Add AIBridge to your Unity project with Unity Package Manager using this Git URL:

`https://github.com/liyingsong99/AIBridge.git`

You can also clone or download this repository and place it under your project's `Packages` folder.

## Requirements

- Unity 2019.4 or later
- .NET 8.0 Runtime for the bundled CLI tools

## CLI command quick reference

The bundled AIBridgeCLI is designed to expose the main AIBridge workflows as direct commands. Commands return compact JSON by default, which makes them easy to use in AI and automation pipelines.

- **Locate the correct Unity asset or project file first** through Unity's AssetDatabase
  ```bash
  ./AIBridgeCache/CLI/AIBridgeCLI.exe asset search --mode script --keyword "Player" --format paths
  ./AIBridgeCache/CLI/AIBridgeCLI.exe asset find --filter "t:Prefab" --format paths
  ./AIBridgeCache/CLI/AIBridgeCLI.exe asset get_path --guid "abc123..."
  ```
- **Inspect prefab metadata and hierarchy** before changing a prefab instance or asset
  ```bash
  ./AIBridgeCache/CLI/AIBridgeCLI.exe prefab get_info --prefabPath "Assets/Prefabs/Player.prefab"
  ./AIBridgeCache/CLI/AIBridgeCLI.exe prefab get_hierarchy --prefabPath "Assets/Prefabs/Player.prefab"
  ```
- **Inspect scene hierarchy and current editor context** so AI can understand existing state before editing
  ```bash
  ./AIBridgeCache/CLI/AIBridgeCLI.exe scene get_hierarchy --depth 3 --includeInactive false
  ./AIBridgeCache/CLI/AIBridgeCLI.exe selection get --includeComponents true
  ```
- **Inspect components and serialized properties** instead of guessing what a GameObject contains
  ```bash
  ./AIBridgeCache/CLI/AIBridgeCLI.exe inspector get_components --path "Player"
  ./AIBridgeCache/CLI/AIBridgeCLI.exe inspector get_properties --path "Player" --componentName "Transform"
  ```
- **Create or modify scene objects** without turning the task into manual editor work
  ```bash
  ./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject create --name "MyCube" --primitiveType Cube
  ./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_position --path "Player" --x 0 --y 1 --z 0
  ```
- **Trigger Unity-side compilation** and verify script compile results
  ```bash
  ./AIBridgeCache/CLI/AIBridgeCLI.exe compile unity
  ./AIBridgeCache/CLI/AIBridgeCLI.exe compile dotnet
  ```
- **Inspect Unity console output** so AI-driven tasks can react to real errors and warnings
  ```bash
  ./AIBridgeCache/CLI/AIBridgeCLI.exe get_logs --logType Error
  ```
- **Capture screenshots or GIFs for visual verification** during Play Mode workflows
  ```bash
  ./AIBridgeCache/CLI/AIBridgeCLI.exe screenshot game
  ./AIBridgeCache/CLI/AIBridgeCLI.exe screenshot gif --frameCount 50
  ```
- **Set or inspect the Game view resolution** for consistent visual testing
  ```bash
  ./AIBridgeCache/CLI/AIBridgeCLI.exe gameview set_resolution --width 1920 --height 1080
  ./AIBridgeCache/CLI/AIBridgeCLI.exe gameview get_resolution
  ```

## License

MIT License

## Contributing

Issues and pull requests are welcome.
