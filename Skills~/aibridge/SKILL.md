---
description: "Core AIBridge Unity skill for CLI usage, editor automation, and choosing when to use flow-specific skills."
---

# AIBridge Unity Skill

## When to Use This Skill

Use this skill when you need to:

- Control Unity Editor through `AIBridgeCLI`
- Read logs, compile results, scene hierarchy, and asset metadata
- Create or modify GameObjects, Components, Scenes, and Prefabs
- Use Unity-aware search before generic filesystem search
- Decide whether a task should stay as direct CLI commands or become a reusable `.flow.txt` workflow

## Core Principle

AIBridge is the Unity command bus.

- Use direct AIBridge CLI commands for short, atomic editor actions.
- Use the flow skills when the task is repeatable, long-running, or needs explicit verification and resume-friendly structure.

## Search Priority

For Unity-managed assets and paths:

1. Prefer `asset search` / `asset find --format paths`
2. Use `asset get_path` only when starting from a GUID
3. Use `asset load` for metadata confirmation
4. After resolving the canonical path, use the host AI's native read tool for file contents
5. Fall back to `asset read_text` only when native file reads are unavailable

## CLI Path

Use the cached CLI path from the Unity project root:

```bash
./AIBridgeCache/CLI/AIBridgeCLI.exe
```

On macOS/Linux:

```bash
dotnet ./AIBridgeCache/CLI/AIBridgeCLI.dll <command> <action> [options]
```

## Recommended Command Patterns

```bash
./AIBridgeCache/CLI/AIBridgeCLI.exe compile unity --raw
./AIBridgeCache/CLI/AIBridgeCLI.exe get_logs --logType Error --raw
./AIBridgeCache/CLI/AIBridgeCLI.exe asset search --mode script --keyword "Player" --format paths --raw
./AIBridgeCache/CLI/AIBridgeCLI.exe gameobject create --name "Cube" --primitiveType Cube --raw
./AIBridgeCache/CLI/AIBridgeCLI.exe transform set_position --path "Player" --x 0 --y 1 --z 0 --raw
```

## When to Switch to Flow Skills

Use the flow skills when you need:

- Repeatable scene setup or bulk object generation
- PlayMode or editor automation test sequences
- Version bump + build + artifact verification pipelines
- Any workflow that needs explicit `WAIT`, `ASSERT`, or `VERIFY` steps

Current shipped workflow jobs:

- `compile.unity`
- `version.bump`
- `android.preflight`
- `build.android`
- `ios.preflight`
- `build.ios`
- `scene.bulk_create`

## Flow Skill Map

- `aibridge-flow-author` — how to write `.flow.txt`
- `aibridge-flow-scene` — scene and prefab workflows
- `aibridge-flow-test` — test workflows
- `aibridge-flow-build` — build and packaging workflows

Current build-chain example:
- `STEP bump_version JOB version.bump --bundleVersion "1.2.3" --androidVersionCode 123`
- `STEP preflight JOB android.preflight --outputPath "Builds/Android/app.apk"`
- `STEP build_apk JOB build.android --outputPath "Builds/Android/app.apk"`
- `WAIT build_done JOB build.android ...`

Preflight note:
- `android.preflight` checks current editor state only; it does not reserve that state or guarantee the following build will succeed.
- `ios.preflight` does the same for iOS export prerequisites and expected Xcode-project output shape.

Current iOS limit:
- `build.ios` exports an Xcode project directory and expects a missing or empty output folder; `version.bump` does not yet set an iOS-specific build number.

Current scene automation chain:
- `STEP bulk_create JOB scene.bulk_create --manifestPath "Assets/Automation/SpawnPoints.json" --rootName "SpawnPointsRoot"`
- `ASSERT last.success == true`
- `STEP save_scene UNITY menu_item --menuPath "File/Save"`

Scene job note:
- `scene.bulk_create` is an editor-authoring job for the current open scene context; it is not a generic merge/update primitive.

## Safety Rules

- Prefer `--raw` for machine-readable responses
- Treat compile/reload-sensitive operations as workflow boundaries
- Do not rely on editor window coordinates or pixel-click automation
- Prefer deterministic commands and explicit result checks
