# AIBridge Command Examples and Advanced Usage

[Back to README](../README.md) | [中文](CommandExamples.md)

Start with the README for installation. For exact parameters, use the current CLI --help and installed Skill references.

## CLI And Command Reference

The command examples below are collapsed by default. Full command references are also generated into each installed Skill's `references/` directory.

<details>
<summary>CLI Basics</summary>

Run commands from the Unity project root after AIBridge has copied its CLI cache:

```powershell
& $CLI = "./.aibridge/cli/AIBridgeCLI.exe"
```

Generated RootRules state this path explicitly and include the concise PowerShell `$CLI` usage.

On macOS/Linux, use the bundled platform executable or run the DLL with `dotnet` according to your project setup.

Most commands use this form:

```powershell
& $CLI <command> <action> [options]
```

CLI-only helpers differ slightly: `focus` and `menu_item` have no action, `dialog` uses `status/click/wait`, and `multi` uses `--cmd` or `--stdin`.

</details>

<details>
<summary>Common Commands</summary>

### Editor, Compile, Logs, Tests

```powershell
& $CLI focus
& $CLI dialog status
& $CLI dialog click --choice cancel
& $CLI editor log --message "Hello" --logType Warning
& $CLI editor get_state
& $CLI compile unity
& $CLI get_logs --logType Error --count 50
& $CLI get_logs --logType Warning --count 50
& $CLI get_logs --regex "NullReference|MissingReference"
& $CLI test run --mode EditMode
& $CLI test status
```

`focus` resolves the current project's Unity Editor from an out-of-repo instance heartbeat (`%LOCALAPPDATA%/AIBridge/instances/<projectKey>/editor-instance.json` on Windows; override the root with `AIBRIDGE_STATE_DIR`). It still falls back to a legacy `.aibridge/editor-instance.json` when the external file is missing. Project exchange files (commands, results, screenshots, workflows) remain under `.aibridge/`.

`test run` must start while the Editor is in Edit Mode. If Unity is already in Play Mode, the command fails immediately with a Play Mode reason and asks the agent to stop Play Mode before retrying.

Use `compile unity` for Unity validation. `compile dotnet` is only an extra solution build check and is not a replacement for Unity compilation.

`compile dotnet` streams child-process output through a bounded reader. Raw output retains at most 200,000 characters, diagnostic lines are capped at 32,768 characters, and at most 1,000 errors plus 1,000 warnings are returned; total, filtered, omitted, and truncation counts remain available in the result.

### Selection, Menu Items, and Profiler

```powershell
& $CLI harness status
& $CLI selection get --includeComponents true
& $CLI selection set --path "Player"
& $CLI selection clear
& $CLI menu_item --menuPath "GameObject/Create Empty"
& $CLI profiler start
& $CLI profiler get_status
& $CLI profiler capture_frame
& $CLI profiler save_data --path ".aibridge/profiler/latest.json"
& $CLI profiler stop
```

Use `harness status` for optional diagnosis only (project-side capabilities come from Root Rule / workflow rules), `selection` to manage the active Unity selection, `menu_item` to invoke Unity menu paths, and `profiler` for editor diagnostics and snapshot capture.

### Workflow Recipes

Workflow recipes are deterministic run-artifact templates. `workflow run-cli` executes CLI, barrier, and report steps, while `agent` and `manual` steps are recorded for Codex, Claude, Cursor, or a human executor.

```powershell
& $CLI workflow list
& $CLI workflow validate --recipe runtime-target-sweep
& $CLI workflow plan --recipe runtime-debug-investigation --format markdown
& $CLI workflow plan --recipe runtime-ui-validation --format markdown
& $CLI workflow init --recipe runtime-ui-validation
& $CLI workflow begin --recipe unity-change-implementation
& $CLI get_logs --logType Error --count 50 --workflow-run wf_20260529_213000_ab12cd34
& $CLI runtime screenshot --target latest --workflow-run wf_20260529_213000_ab12cd34
& $CLI workflow import --run wf_20260529_213000_ab12cd34 --step adversarial-verify --schema Verdict --file verdicts.json
& $CLI workflow export --recipe runtime-ui-validation --target codex-task-pack --output .aibridge/workflows/exports
& $CLI workflow run-cli --file ".aibridge/workflows/recipes/runtime-target-sweep.aibridge-workflow.json" --inputs ".aibridge/workflows/inputs.json"
& $CLI workflow run-cli --recipe performance-hotspot-investigation --inputs ".aibridge/workflows/perf-inputs.json" --timeout 30000
& $CLI workflow run-cli --recipe unity-sharded-review --allow-partial true
& $CLI workflow run-cli --recipe unity-sharded-review --resume wf_20260529_213000_ab12cd34 --rerun failed
& $CLI workflow status --run wf_20260529_213000_ab12cd34
& $CLI workflow report --run wf_20260529_213000_ab12cd34 --format markdown
& $CLI workflow finish --run wf_20260529_213000_ab12cd34 --status passed
& $CLI workflow clean --older-than 30d --dry-run true
& $CLI workflow clean --older-than 3d --save-settings true --auto-clean true
```

Built-in recipes include `bug-hunter-loop`, `harness-readiness-check`, `performance-hotspot-investigation`, `prefab-asset-sweep`, `runtime-debug-investigation`, `runtime-target-sweep`, `runtime-ui-validation`, `unity-change-implementation`, and `unity-sharded-review`.

`performance-hotspot-investigation` is a one-click routine hotspot report. It collects Editor Profiler snapshots, Runtime status/log/screenshot evidence, and a bounded `runtime perf` sample, then writes a Markdown report with parsed FPS, frame-time, hitch, memory, GC, rendering, warning, and unsupported summaries. The default sample duration is 15 seconds, so pass a workflow timeout such as `--timeout 30000`.

`runtime-debug-investigation` is for investigating Runtime, Player, Play Mode, UI, log, or performance symptoms. It checks evidence completeness first and does not treat Runtime errors themselves as workflow failure conditions; once a root cause is confirmed and a fix is requested, hand off to an implementation workflow.

`workflow begin` creates an active run; ordinary commands can attach evidence with `--workflow-run`, `AIBRIDGE_WORKFLOW_RUN_ID`, or the active run pointer. `workflow status` and `workflow report` always require explicit `--run`; read `.aibridge/workflows/active-run.json` first when you need the active run id. `workflow run-cli --resume <runId>` resumes an existing run but still requires `--recipe` or `--file` so the CLI can load the recipe definition. Prefer a JSON file path for `--inputs`; inline JSON is fragile in PowerShell. `workflow import` stores structured external results such as `Verdict`, and `externalVerdict` gates only pass from imported artifacts. `workflow export` writes handoff packages for external tools. `partial` workflow status is not treated as CLI success unless `--allow-partial true` is passed explicitly. `workflow status`, `workflow run-cli`, `workflow finish`, and JSON `workflow report` are compact by default; use `--detail full` only when you need the full manifest JSON. Compact output keeps `terminalState`, `terminalReason`, `runDirectory`, `manifestPath`, `reportPath`, `artifactIds`, gate summaries, and external gaps, while `stepGaps`, `evidenceFreshness`, and `failedCommands` stay full-detail only.

Nested `workflow run-cli` stdout/stderr retains at most 1,000,000 characters per stream. Command results expose `stdoutCharsRead`, `stderrCharsRead`, `stdoutTruncated`, and `stderrTruncated`; truncation fails the step so incomplete JSON is never treated as valid evidence.

Workflow cleanup is explicit maintenance: `workflow clean` starts in dry-run mode and should be used when you intentionally inspect or prune workflow artifacts. Routine expired run directories are handled by the AIBridge Settings > Cache cleanup policy; the active run is preserved, while old failed/blocked runs are not permanently exempt once they exceed the retention window.

Use `dialog status` when Unity commands time out and the Editor may be blocked by a modal save/confirm dialog. When no dialog is detected, compact JSON omits `blockedByDialog` and `dialogs`; missing fields mean no dialog. macOS dialog inspection/clicking requires Accessibility permission. Unity commands can opt into explicit timeout handling, for example `--on-dialog cancel` or `--on-dialog discard`.

`get_logs` supports the Settings window defaults and optional regex filtering:

- The Logs tab lets you set the default minimum log level and an optional global regex filter.
- If you omit `--logType`, `get_logs` uses the Settings window default level filter.
- If you pass `--regex`, that regex is applied to the log message text before returning results.
- The global regex filter can be enabled from the Logs tab and will apply whenever `--regex` is not provided.

### Modal Dialog Handling

Unity modal dialogs can block the Editor main thread before AIBridge has a chance to process normal Unity-side commands. AIBridge includes a CLI-only `dialog` helper that inspects and clicks those OS-level windows directly, so an AI assistant can recover from save, discard, cancel, delete, replace, and confirmation prompts without guessing.

```powershell
& $CLI dialog status
& $CLI dialog click --choice discard
& $CLI dialog click --button "Don't Save"
& $CLI dialog wait --timeout 5000 --click cancel
```

When Unity is already blocked by a modal dialog, normal Unity commands return the detected dialog details instead of silently waiting for a timeout. Each enabled button reports its exact visible text plus a `choices` list when a stable logical choice is available, such as `save`, `discard`, or `cancel`; if no logical choice is shown, click by exact `--button` text. AIBridge rejects disabled or ambiguous logical matches instead of guessing between multiple dialogs or buttons. For unattended flows, pass `--on-dialog <choice>` to a Unity command, for example:

```powershell
& $CLI scene load --scenePath "Assets/Scenes/Main.unity" --on-dialog discard
```

On Windows, dialog buttons are detected through window APIs and support button mnemonics such as `&Don't Save`. On macOS, dialog inspection and clicking require Accessibility permission.

Batch scripts can declare persistent dialog auto-click choices. After the declaration line runs, later batch steps auto-click the first matching logical choice or visible button text. A later `dialog click` declaration replaces the previous strategy. Keep the CLI invocation waiting; `--no-wait` cannot continue clicking after the process exits.

```text
dialog click ok | yes | Save
```

### Assets And Scenes

```powershell
& $CLI asset search "Player" --mode script --format paths
& $CLI asset search --mode script --keyword "Player" --format paths
& $CLI asset find --filter "t:Prefab" --format paths
& $CLI asset get_path --guid "abc123..."
& $CLI asset read_text --assetPath "Assets/Configs/GameConfig.asset"

& $CLI scene get_hierarchy --depth 3 --includeInactive false
& $CLI scene get_active
& $CLI scene load --scenePath "Assets/Scenes/Main.unity" --mode single
& $CLI scene save
```

### GameObjects And Transforms

```powershell
& $CLI gameobject create --name "MyCube" --primitiveType Cube
& $CLI gameobject find --withComponent "Rigidbody" --maxResults 20
& $CLI gameobject set_active --path "Player" --active true

& $CLI transform get --path "Player"
& $CLI transform set_position --path "Player" --x 0 --y 1 --z 0
& $CLI transform look_at --path "Player" --targetPath "Enemy"
& $CLI transform look_at --path "Player" --targetInstanceId 12345
& $CLI transform set_sibling_index --path "Canvas/Button" --first true
```

### Inspector And Prefabs

```powershell
& $CLI inspector get_components --path "Player"
& $CLI inspector get_properties --path "Player" --componentName "Transform"
& $CLI inspector find_property --path "Player" --componentName "Rigidbody" --keyword "mass"
& $CLI inspector set_property --path "Player" --componentName "Rigidbody" --propertyName "mass" --value 10

& $CLI prefab get_info --prefabPath "Assets/Prefabs/Player.prefab"
& $CLI prefab get_hierarchy --prefabPath "Assets/Prefabs/Player.prefab" --includeComponents true
& $CLI prefab patch --prefabPath "Assets/Prefabs/Player.prefab" --ops ".aibridge/patch_ops/player_patch.json" --dryRun true
```

For simple prefab field edits, use `inspector set_property` with `assetPath + objectPath + componentName`. For multi-step prefab edits, use `prefab patch --ops <file>` and run `--dryRun true` first. For unsupported Prefab, Scene, ScriptableObjectTable, or custom `.asset` structural edits, use `unity-yaml-editing` as the direct YAML fallback.

PowerShell JSON tip:

```powershell
$values = (@{ 'm_LocalPosition.x' = 0; 'm_LocalPosition.y' = 1 } | ConvertTo-Json -Compress) -replace '"', '\"'
& "./.aibridge/cli/AIBridgeCLI.exe" inspector set_properties --assetPath 'Assets/Prefabs/Player.prefab' --componentName Transform --values $values
```

### Batch And Multi

```powershell
& $CLI batch from_text --text "call editor log 'Hello'`ndelay 1000"
& $CLI batch from_file --file ".aibridge/scripts/setup_scene.txt"
& $CLI multi --cmd "editor log --message Step1&get_logs --logType Error --count 1"
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

```powershell
& $CLI editor play
& $CLI input click --path "Canvas/StartButton"
& $CLI input click_at --x 960 --y 540
& $CLI input click_pct --x 0.5 --y 0.5
& $CLI input drag --path "Canvas/Item" --toPath "Canvas/Slot" --frames 12
& $CLI input long_press --instanceId 12345 --duration-ms 800

& $CLI gameview get_resolution
& $CLI gameview set_resolution --width 1920 --height 1080
& $CLI gameview list_resolutions
& $CLI screenshot game
& $CLI screenshot scene_view
& $CLI screenshot scene_view --width 1920 --height 1080
& $CLI screenshot editor_window --target editor
& $CLI screenshot editor_window --target active
& $CLI screenshot editor_window --windowType MyNamespace.MyCustomEditorWindow
& $CLI screenshot gif --frameCount 50 --fps 20 --scale 0.5
& $CLI editor stop
```

Game view screenshots, GIF capture, and `input` commands require Play Mode. Scene view and Editor window screenshots work in Edit mode. `editor_window` captures only Unity-owned native window pixels on Windows 10 1803+ or macOS 12.3+; it does not capture the desktop. Use `--windowType`, `--title`, or `--instanceId` for built-in and custom Editor windows. A typical runtime UI flow is to enter Play Mode, inspect the scene hierarchy, run `input`, read Error logs, then verify the frame with a screenshot or GIF.

### Built Player Runtime Bridge

The `runtime` command connects to `AIBridgeRuntime` inside a Player or Play Mode scene. HTTP transport is the default Runtime control plane at `http://127.0.0.1:27182`; if that port is already occupied, Runtime Bridge automatically increments within a small port range and writes the actual URL to the live heartbeat so CLI commands launched from the same project can resolve the correct target. LAN discovery starts at UDP `27183` and auto-increments the same way, so multiple Editors or built Players can run on one machine without sharing a discovery socket. File transport remains available as a local compatibility fallback, but Runtime only polls the file command directory when HTTP transport is not running and HTTP command results are returned through the in-memory pending channel instead of the file `results` directory. Built Players can still pass `--aibridge-runtime-dir <path>` and `--aibridge-target-id <id>` when using file transport.

By default, HTTP `runtime list_targets` uses the quick path: it checks the project heartbeat/cache and the configured or explicit URL, but it does not scan the local auto-increment port range. Quick HTTP health probes use a short bounded retry policy, automatic discovery honors the configured `discovery.udpPort`, and concurrent CLI or `AIBridge/Players` discovery requests serialize cache updates, merge fresh targets, and publish the cache atomically so `--target latest` remains stable for remote or mobile Players. HTTP Runtime accepts at most 128 queued HTTP commands; duplicate active/recent command ids are rejected with `409`, a full queue with `429`, and health/status reports expose the current queue depth. Use `runtime list_targets --probe true` when you need local port scanning, and use `runtime discover` for LAN UDP discovery. When several local Players are running, use `runtime list_targets` first and pass the returned target id, for example `runtime status --target AIBridgeDev_12345`; `runtime diagnose --target <id>` resolves and deeply checks that target's actual URL.

Use `AIBridge/Settings > Runtime` to configure default enablement, HTTP bind/port, LAN discovery, Editor Play Mode auto injection, Development Build auto injection, Release Build allowance, background running, TargetId, auth token, allowed actions, heartbeat interval, and log buffer size. Runtime heartbeat defaults to 5 seconds. The settings tab can write `.aibridge/runtime-config.json` so CLI commands can use project defaults. Use `AIBridge/Players` to inspect file heartbeat targets, local HTTP entry, LAN discovery cache, status, scene, platform, and common CLI commands. Stale File/CACHE entries show a `Delete Cache` button so old target directories or discovery-cache entries can be cleaned without touching online Players. Editor Play Mode auto injection is enabled by default, so entering Play Mode creates a temporary hidden `AIBridgeRuntime` when the scene does not already contain one. `Keep Running In Background` is enabled by default for Editor Play Mode and Development Builds so heartbeat and runtime commands keep working after focus loss. For UI automation, use `runtime.ui.snapshot` first; each button entry includes screen coordinates and screen rects so the next step can click the exact pixel location without extra guessing. Snapshot/find commands skip per-button raycast by default; pass `includeRaycastDetails=true` when you need obstruction diagnostics.

```powershell
& $CLI runtime list_targets
& $CLI runtime list_targets --probe true
& $CLI runtime status --target latest
& $CLI runtime discover
& $CLI runtime diagnose --target latest
& $CLI runtime logs --target latest --logType Error --count 100
& $CLI runtime perf --target latest --duration 5s --interval 100ms
& $CLI runtime screenshot --target latest
& $CLI runtime handlers --target latest
& $CLI runtime call --target latest --action qa.open_panel --json '{"panel":"Inventory"}'
& $CLI code runtime_execute --file ".aibridge/code/player_probe.csx" --target latest --timeout 10000
```

For remote phones, use LAN discovery first: `$CLI runtime discover`, then target the discovered id or URL. For Android USB debugging, run `adb reverse tcp:27182 tcp:27182`, then connect with `--transport http --url http://127.0.0.1:27182`; `adb` is not a standalone Runtime transport. With HybridCLR installed, `code runtime_execute` can compile a temporary runtime-safe DLL in the Editor, send it to the phone or built Player over the Runtime Bridge, and invoke it inside the target through `Assembly.Load` plus reflection. This is useful for one-off mobile probes that are too specific for generic status/log/screenshot commands.

Runtime Bridge does not include an in-game LLM and does not expose an unrestricted project-method reflection RPC. Its built-in surface includes status, logs, screenshots, perf, handlers, and the explicit `runtime.code.execute` action used by `code runtime_execute` when HybridCLR and Runtime Code Execution are enabled. Treat runtime code execution as a trusted debugging backdoor. Release Builds remain off by default and should only enable this bridge after the project accepts the security boundary, network exposure, auth token, and runtime-code options.

</details>

<details>
<summary>Read-only Code Index</summary>

`code_index` is a CLI-only, read-only lightweight query surface. It is disabled by default; enable `AIBridge > Settings > Code Index > Enable Code Index` only for projects that need fast C# declaration-name lookup. It does not require Rider, VS Code, Cursor, Visual Studio, Build Tools, or a Unity-generated solution. Unity Editor writes a compilation snapshot under `.aibridge/code-index/snapshot/`; the project-local `AIBridgeCodeIndex` daemon reads that snapshot and uses the snapshot name index for lightweight lookup.

```powershell
& $CLI code_index symbol --query PlayerController
& $CLI code_index definition --query PlayerController
```

The command is intentionally read-only: it does not rename, refactor, auto-fix, or write files. `symbol` returns declaration candidates and `definition` returns the best declaration position by name. Pass a C# declaration identifier (or substring) to `--query` — not natural language, localized business names, or comment text. Public query responses stay compact (hits plus essential error fields); use `status` / `doctor` for snapshot and queue diagnostics. After you get the path, read the returned `.cs` files directly for real analysis. Daemon lifecycle, snapshot build, warmup, and recovery are internal implementation details rather than part of the public AI-facing CLI contract. `compile unity` remains the final validation authority.

After Code Index is enabled, Unity Editor can generate the snapshot and prewarm the daemon from `AIBridge > Settings > Code Index` after startup idle time. The same panel still controls snapshot auto refresh, PackageCache source inclusion, ignored assembly/source-path patterns, and quit cleanup. Public usage stays intentionally narrow: ask for declaration-name lookup through `symbol` / `definition`, and let the internal lifecycle manage daemon startup, recovery, and snapshot refresh.

The following capabilities were intentionally removed from the public `code_index` surface because they are often too slow, too noisy, or too fragile in large projects:

- `references`
- `callers`
- `implementations`
- `derived`
- `diagnostics --all`
- first-query semantic workspace prewarm / full Roslyn semantic graph build
- single-queue daemon `batch` semantic queries

</details>

<details>
<summary>Advanced C# Execution</summary>

### Roslyn Temporary C# Execution

`code execute` runs temporary Editor C# for one-off tasks that declarative CLI commands cannot express cleanly, such as generated asset sets, structured diagnostics, reports, Runtime/Public API calls, or multi-step UnityEditor API orchestration. It is not a replacement for `compile unity` or `test run`.

`Enable Code Execution` in `AIBridge/Settings > Basic` applies to both `code execute` and `code runtime_execute`. Disable it for untrusted projects or callers. File mode is limited to `.aibridge/code/*.cs` or `.aibridge/code/*.csx`, and complex scripts should use file mode. Code execution is single-flight; after a timeout, use `code status` first and only use `code cancel` when you need to release AIBridge's waiting state.

```powershell
& $CLI code execute --file ".aibridge/code/check.csx" --timeout 5000
& $CLI code execute --code 'Debug.Log("hello"); return 123;'
& $CLI code runtime_execute --file ".aibridge/code/player_probe.csx" --target latest --timeout 10000
& $CLI code runtime_execute --code "return Application.platform.ToString();" --transport http --url http://127.0.0.1:27182 --timeout 10000
& $CLI code status
& $CLI code cancel
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
7. Enable `code_index` only when fast C# declaration-name lookup is needed, then use it for `symbol` or `definition`, read the returned `.cs` files directly, and keep reference/caller/relationship analysis out of Code Index.
8. Use `code execute` or HybridCLR-backed `code runtime_execute` only when declarative commands cannot express a complex one-off Editor or Player debugging task.
