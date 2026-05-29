<p align="center">
  <img src="./Images/aibridge-banner.png" alt="AIBridge banner" width="100%">
</p>

# AIBridge

English | [中文](./README_CN.md)

![Unity 2019.4+](https://img.shields.io/badge/Unity-2019.4%2B-black?style=flat-square&logo=unity)
![Package 1.4.3](https://img.shields.io/badge/Package-1.4.3-5b6cff?style=flat-square)
![MIT License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)
![AI Unity Automation](https://img.shields.io/badge/Workflow-AI%20Unity%20Automation-14b8a6?style=flat-square)

> Design principles: **simple, easy to use, stable**.

AIBridge is no longer just an MCP-like connector. It is an AI Unity development harness that combines project-local workflows, AIBridge Skills, CLI and Runtime tools, code indexing, visual validation, input simulation, Player debugging, and AI-tool integrations into one practical loop around a Unity project.

The current version is still evolving, but it is intentionally simple to start using: install the package, install integrations for tools such as Codex, and developers can immediately try real AI-assisted Unity work. With Codex + AIBridge, Unity development now has a closed loop for design, implementation, compilation, tests, runtime verification, screenshots/logs, and automatic debugging.

It lets agents resolve real Unity asset paths, inspect scenes and prefabs, edit objects through Unity APIs, run Unity compilation, read Console logs, execute batch workflows, run tests, simulate UGUI/EventSystem runtime clicks and drags, connect to built Players for runtime state/log/screenshot checks, and, when HybridCLR is installed, compile temporary runtime C# in the Editor and execute it in the target Player.

## Why AIBridge

Many Unity automation tools stop at a live socket, MCP session, or thin command bridge. AIBridge is the harness around that connection layer: it installs workflow guidance and Skills into AI tools, exposes Unity-aware CLI and Runtime capabilities, and keeps validation artifacts such as command results, logs, screenshots, GIFs, code-index answers, and runtime diagnostics tied to the project.

For Editor automation, AIBridge uses file-based command requests and result files. For Player debugging, it uses an HTTP Runtime control plane. This makes AI-assisted work more resilient across script recompilation, domain reloads, editor focus changes, restarts, and device/player sessions.

| Dimension | AIBridge | MCP / persistent bridge |
|---|---|---|
| Connection model | File-based Editor requests plus HTTP Runtime targets | Live session or tool server |
| Compile-cycle resilience | Polls and resumes across reloads; Runtime targets can be rediscovered | Session can drop |
| Setup | Bundled CLI commands | Server/client wiring |
| Multi-project recognition | Automatic and project-local: no extra mapping, registration, or manual project selection is needed when commands run from the Unity project root | Supported by some MCP tools, but usually depends on server/tool configuration or project selection state |
| AI integration | Project-local workflow, Skills, CLI, JSON output, and tool adapters | Protocol-specific tools |
| Traceability | Command files, results, logs, screenshots, GIFs, code-index answers, and runtime diagnostics | Session state |
| Extensibility | Unity commands, Runtime handlers, CLI builders, Skills, and recommended Skill libraries | Tool server extensions |
| Read-only code index | IDE-independent `code_index` daemon for symbols, definitions, references, implementations, callers, and diagnostics | Usually tied to an IDE/plugin session |
| Mobile Player debugging | LAN/USB HTTP Runtime Bridge supports status, logs, screenshots, perf, handlers, and HybridCLR-gated `code runtime_execute` | Usually needs custom per-tool runtime server support |

## Core Capabilities

- **Unity asset and object inspection**: find assets, read scene hierarchies, inspect components and SerializedProperty values, then write through Unity-aware APIs.
- **Prefab and scene automation**: use simple Inspector field edits, Prefab Patch dry-runs, multi-step batch scripts, and task continuation across domain reloads.
- **UGUI runtime input simulation**: in Play Mode, the `input` command can click, click Unity screen coordinates, click normalized Unity screen coordinates, drag, and long-press EventSystem UI for button, inventory, and runtime panel checks.
- **Player Runtime Bridge**: an `AIBridgeRuntime` component inside a built Player can expose runtime status, logs, screenshots, performance samples, project allowlisted handlers, and HybridCLR-gated runtime code execution for Development Build and mobile debugging.
- **Read-only Code Index**: when enabled, `code_index` starts an IDE-independent daemon that reads Unity compilation snapshots for symbol, definition, reference, implementation, caller, and diagnostic queries. It is disabled by default and reports `semantic=false` when it falls back to text search.
- **Workflow recipes and run artifacts**: `workflow` CLI commands can list, validate, plan, initialize, and run deterministic CLI steps from built-in Unity workflow recipes, then write a project-local run manifest, command results, artifacts, gates, and Markdown report under `.aibridge/workflows/runs/`.
- **Roslyn temporary C# execution**: controlled `code execute` runs `.aibridge/code/*.cs` or `.csx` temporary scripts inside Unity Editor for complex one-off asset generation, structured analysis, diagnostics, and Runtime/Public API calls. It is enabled by default in Settings and can be disabled there for untrusted projects or callers.
- **Visual and log validation**: capture Game/Scene view screenshots or GIFs, read Console logs, run Unity compilation, and invoke tests so agents can close the loop on changes.

## Requirements

- Unity 2019.4 or later.
- .NET 8.0 Runtime for the bundled CLI.
- Unity Editor must be running for Unity-side commands such as `compile unity`, `asset`, `scene`, `inspector`, `prefab`, `input`, `screenshot`, `code`, and `get_logs`.
- `code_index` is disabled by default. Enable it from `AIBridge > Settings > Code Index` when semantic lookup is needed. It requires a Unity compilation snapshot generated by the Editor, not `.sln/.csproj`, Visual Studio, Rider, or Build Tools. If the snapshot is missing, run Code Index prewarm from AIBridge settings.
- `runtime` commands require an `AIBridgeRuntime` component in the Player or Play Mode scene; Editor Play Mode can auto inject one when Runtime Bridge is enabled. `code runtime_execute` also requires the HybridCLR package and Runtime Code Execution to be enabled. Runtime Bridge is disabled by default in Release Builds unless the project explicitly enables it.

## Installation

Install with Unity Package Manager using this Git URL:

```text
https://github.com/liyingsong99/AIBridge.git
```

Backup UPM Git URL:

```text
https://gitee.com/lijoujou99_admin/AIBridge.git
```

You can also clone this repository into a Unity project's `Packages` folder.

## Configure AI Workflow

1. Open `Tools > AIBridge Settings` in Unity Editor.
2. Open the `Skills Installation` section.
3. Select the AI tools you use.
4. Click `Install Selected Integrations`.
5. Optionally click `Install Unity Project AGENTS.md Template` to create a root `AGENTS.md`.

Installed AIBridge Skills are written to each selected tool's default skills directory by default, such as `.codex/skills/` for Codex. You can set a custom directory in the Skills Installation tab, but custom directories may not be discovered automatically by the AI tool. Each AI tool receives a minimal RootRule and, only when needed for a custom directory, a plugin adapter that references the Skill root. The RootRule only includes the fixed CLI path, common commands, Skill root, and `aibridge-development-workflow` entry point; full routing and checklists live in the workflow Skill. Advanced workflow orchestration guidance is installed as an AIBridge Skill and is loaded only for multi-agent workflow, adversarial verification, recipe, or Runtime target sweep tasks. Command references are generated under each installed Skill's `references/` directory.

You can also open the `Recommended Skill Library` tab, refresh the default `obra/superpowers` repository, and install third-party Skills into the selected tools' skills directories.

## CLI And Command Reference

The command examples below are collapsed by default. Full command references are also generated into each installed Skill's `references/` directory.

<details>
<summary>CLI Basics</summary>

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

</details>

<details>
<summary>Common Commands</summary>

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

### Workflow Recipes

Workflow recipes are deterministic run-artifact templates, not a built-in LLM scheduler. `workflow run-cli` executes CLI, barrier, and report steps, while `agent` and `manual` steps are recorded for Codex, Claude, Cursor, or a human executor.

```bash
$CLI workflow list
$CLI workflow validate --recipe runtime-target-sweep
$CLI workflow plan --recipe runtime-ui-validation --format markdown
$CLI workflow init --recipe runtime-ui-validation
$CLI workflow run-cli --file ".aibridge/workflows/recipes/runtime-target-sweep.aibridge-workflow.json"
$CLI workflow status --run wf_20260529_213000_ab12cd34
$CLI workflow report --run wf_20260529_213000_ab12cd34 --format markdown
$CLI workflow clean --older-than 30d --dry-run true
$CLI workflow clean --older-than 3d --save-settings true --auto-clean true
```

Built-in recipes include `unity-change-implementation`, `unity-sharded-review`, `runtime-target-sweep`, `runtime-ui-validation`, `prefab-asset-sweep`, and `bug-hunter-loop`.

Workflow cleanup is conservative by default: `clean` starts in dry-run mode. Auto cleanup is enabled only when saved in `.aibridge/workflows/settings.json`; `run-cli` then removes old runs before starting while preserving failed/blocked runs and the newest retained runs according to settings.

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

Batch scripts can declare persistent dialog auto-click choices. After the declaration line runs, later batch steps auto-click the first matching logical choice or visible button text. A later `dialog click` declaration replaces the previous strategy. Keep the CLI invocation waiting; `--no-wait` cannot continue clicking after the process exits.

```text
dialog click ok | yes | Save
```

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
dialog click ok | yes | Save
delay 1000
get_logs --logType Error --count 1
'@
$script | & "./.aibridge/cli/AIBridgeCLI.exe" multi --stdin
```

</details>

<details>
<summary>Runtime Debugging</summary>

### Runtime Input And Visual Verification

The `input` command automates UGUI/EventSystem interaction in Play Mode. It requires an active `EventSystem` in the current scene. You can target objects by hierarchy path or instance ID, click screen coordinates, click normalized screen coordinates, drag between UI targets, or perform a long press. Both pixel and normalized coordinate actions use Unity screen coordinates with a bottom-left origin; `click_pct` accepts `x` and `y` values from `0` to `1`.

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

Game view screenshots, GIF capture, and `input` commands require Play Mode. Scene view screenshots work in Edit mode when a Scene view is open. A typical runtime UI flow is to enter Play Mode, inspect the scene hierarchy, run `input`, read Error logs, then verify the frame with a screenshot or GIF.

### Built Player Runtime Bridge

The `runtime` command connects to `AIBridgeRuntime` inside a Player or Play Mode scene. HTTP transport is the default Runtime control plane at `http://127.0.0.1:27182`; if that port is already occupied, Runtime Bridge automatically increments within a small port range and writes the actual URL to the live heartbeat so CLI commands launched from the same project can resolve the correct target. LAN discovery starts at UDP `27183` and auto-increments the same way, so multiple Editors or built Players can run on one machine without sharing a discovery socket. File transport remains available for Editor/local compatibility. Built Players can still pass `--aibridge-runtime-dir <path>` and `--aibridge-target-id <id>` when using file transport.

By default, HTTP `runtime list_targets` checks the project heartbeat/cache and scans the local auto-increment port range, while `runtime discover` broadcasts across the matching UDP range. When several local Players are running, use `runtime list_targets` first and pass the returned target id, for example `runtime status --target AIBridgeDev_12345`; `runtime diagnose --target <id>` resolves and checks that target's actual URL.

Use `AIBridge/Settings > Runtime` to configure default enablement, HTTP bind/port, LAN discovery, Editor Play Mode auto injection, Development Build auto injection, Release Build allowance, background running, TargetId, auth token, allowed actions, and log buffer size. The settings tab can write `.aibridge/runtime-config.json` so CLI commands can use project defaults. Use `AIBridge/Players` to inspect file heartbeat targets, local HTTP entry, LAN discovery cache, status, scene, platform, and common CLI commands. Stale File/CACHE entries show a `Delete Cache` button so old target directories or discovery-cache entries can be cleaned without touching online Players. Editor Play Mode auto injection is enabled by default, so entering Play Mode creates a temporary hidden `AIBridgeRuntime` when the scene does not already contain one. `Keep Running In Background` is enabled by default for Editor Play Mode and Development Builds so heartbeat and runtime commands keep working after focus loss.

```bash
$CLI runtime list_targets
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

For remote phones, use LAN discovery first: `$CLI runtime discover`, then target the discovered id or URL. For Android USB debugging, run `adb reverse tcp:27182 tcp:27182`, then connect with `--transport http --url http://127.0.0.1:27182`; `adb` is not a standalone Runtime transport. With HybridCLR installed, `code runtime_execute` can compile a temporary runtime-safe DLL in the Editor, send it to the phone or built Player over the Runtime Bridge, and invoke it inside the target through `Assembly.Load` plus reflection. This is useful for one-off mobile probes that are too specific for generic status/log/screenshot commands.

Runtime Bridge does not include an in-game LLM and does not expose an unrestricted project-method reflection RPC. Its built-in surface includes status, logs, screenshots, perf, handlers, and the explicit `runtime.code.execute` action used by `code runtime_execute` when HybridCLR and Runtime Code Execution are enabled. Treat runtime code execution as a trusted debugging backdoor. Release Builds remain off by default and should only enable this bridge after the project accepts the security boundary, network exposure, auth token, and runtime-code options.

</details>

<details>
<summary>Read-only Code Index</summary>

`code_index` is a CLI-only, read-only semantic query surface. It is disabled by default; enable `AIBridge > Settings > Code Index > Enable Code Index` only for projects that need semantic lookup. It does not require Rider, VS Code, Cursor, Visual Studio, Build Tools, or a Unity-generated solution. Unity Editor writes a compilation snapshot under `.aibridge/code-index/snapshot/`; the project-local `AIBridgeCodeIndex` daemon reads that snapshot and uses Roslyn `AdhocWorkspace` for semantic queries.

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

The command is intentionally read-only: it does not rename, refactor, auto-fix, or write files. When the semantic workspace is unavailable, fallback results are explicitly marked with `semantic=false` and `source=rg-fallback` or `source=text-fallback`. `doctor` reports missing snapshot state directly; `compile unity` remains the final validation authority.

After Code Index is enabled, Unity Editor can generate the snapshot and prewarm the daemon from `AIBridge > Settings > Code Index` after startup idle time. The same panel controls snapshot auto refresh, text fallback, PackageCache source inclusion, ignored assembly/source-path patterns, and quit cleanup. Warmup loads the lightweight snapshot name index first; declarations and unique indexed member definitions can be answered directly from the snapshot, while Roslyn semantic workspace construction is deferred until richer definition/reference/diagnostic-style queries need it. Reference queries also use the snapshot token index to narrow Roslyn candidate files when possible. Excluded source assemblies remain available as metadata references so project semantic queries can still resolve package types. `status` reports `workspaceMode=unity-snapshot`, snapshot metadata, excluded counts, and `stale=true` when the daemon must reload a newer snapshot.

</details>

<details>
<summary>Advanced C# Execution</summary>

### Roslyn Temporary C# Execution

`code execute` runs controlled temporary Editor C# for complex one-off tasks that declarative CLI commands cannot express cleanly, such as generated asset sets, structured diagnostics, reports, Runtime/Public API calls, or multi-step UnityEditor API orchestration. It is not a replacement for `compile unity` or `test run`.

`Enable Code Execution` is enabled by default in `Tools > AIBridge Settings > Basic`; disable it there for untrusted projects or callers. This gate applies to both `code execute` and `code runtime_execute`. File mode is limited to `.aibridge/code/*.cs` or `.aibridge/code/*.csx`, and complex scripts should use file mode. Code execution is single-flight; after a timeout, use `code status` first and only use `code cancel` when you need to release AIBridge's waiting state.

```bash
$CLI code execute --file ".aibridge/code/check.csx" --timeout 5000
$CLI code execute --code "Debug.Log(\"hello\"); return 123;"
$CLI code runtime_execute --file ".aibridge/code/player_probe.csx" --target latest --timeout 10000
$CLI code runtime_execute --code "return Application.platform.ToString();" --transport http --url http://127.0.0.1:27182 --timeout 10000
$CLI code status
$CLI code cancel
```

`code runtime_execute` is the Player-side companion to `code execute`. It compiles a runtime-safe DLL in the Editor, dispatches it through Runtime Bridge, and invokes it in the target Player. It is available only when `com.code-philosophy.hybridclr` is installed, the global code-execution gate is accepted, and Runtime Code Execution remains enabled in `AIBridge/Settings > Runtime`; use it only for trusted debugging builds and explicit mobile/player probes.

</details>

## Recommended AI Work Loop

1. Resolve the real Unity asset or object path.
2. Inspect the scene, prefab, component, or SerializedProperty state.
3. Apply the smallest safe change through Unity-aware commands or source edits.
4. Run `compile unity`.
5. Read `get_logs --logType Error`.
6. For runtime UI, enter Play Mode and verify interaction with `input`, logs, and screenshots or GIFs.
7. Enable `code_index` only when semantic lookup is needed, then use it for read-only lookup before broad source edits.
8. Use `code execute` or HybridCLR-backed `code runtime_execute` only when declarative commands cannot express a complex one-off Editor or Player debugging task.

## Repository Layout

```text
Editor/        Unity Editor commands, settings window, integrations, prefab patching
Runtime/       Runtime bridge contracts and lightweight runtime data
Tools~/       AIBridgeCLI source, CodeIndex daemon source, and bundled platform binaries
Templates~/   AI root-rule templates and Unity project AGENTS.md template
Skill~/       AIBridge Skills and workflow references
Tests/        Unity EditMode tests
Images/       README images
```

## License

MIT License. See [LICENSE](./LICENSE).

## Contributing

Issues and pull requests are welcome. When changing Unity-facing behavior, update the relevant CLI examples, Skill references, and validation notes.
