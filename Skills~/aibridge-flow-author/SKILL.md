---
description: "Write deterministic AIBridge .flow.txt scripts for resumable Unity automation."
---

# AIBridge Flow Authoring

## Use This Skill When

- You need to generate `.flow.txt` automation scripts
- You need structured, repeatable Unity workflows
- The task crosses compile, reload, long waits, or multiple verification points

## Output Rules

- Output only valid `.flow.txt` DSL
- Use only supported statements:
  - `FLOW`
  - `VAR`
  - `STEP`
  - `WAIT`
  - `ASSERT`
  - `VERIFY`
  - `END`
- Do not emit shell commands, C# snippets, or natural-language instructions inside the flow
- Do not use loops or branching in v1

## Authoring Rules

1. Use `UNITY` for short atomic AIBridge commands
2. Use `JOB` for long-running or reload-sensitive work
3. Every long-running step must have a timeout
4. Every compile/reload-sensitive operation must be followed by `WAIT`
5. End build and test flows with `VERIFY` when possible
6. Prefer stable names, stable paths, and idempotent actions

## Example

```txt
FLOW android_package

VAR output_apk = "Builds/Android/app.apk"

STEP compile_start UNITY compile start
WAIT compile_done UNITY compile status
  UNTIL $.data.status == "success"
  FAIL_IF $.data.status == "failed"
  POLL 1000
  TIMEOUT 300000

STEP build_apk JOB build.android --outputPath "${output_apk}"
WAIT build_done JOB build.android
  UNTIL $.data.status == "success"
  FAIL_IF $.data.status == "failed"
  POLL 2000
  TIMEOUT 1800000

VERIFY FILE_EXISTS "${output_apk}"
END
```

## Preferred Flow Types

- Batch scene setup
- PlayMode smoke tests
- Build and packaging pipelines
- Asset preparation + verification flows

## Supported JOB Types in Current MVP

- `compile.unity`
- `android.preflight`
- `build.android`
- `ios.preflight`
- `build.ios`
- `scene.bulk_create`
- `version.bump`

Current `build.android` boundary:
- Requires `--outputPath`
- Requires Unity active build target already set to Android
- Does not switch build target automatically
- Use `WAIT JOB build.android` for polling

Current `android.preflight` boundary:
- Supports `--outputPath`
- Supports optional `--buildAppBundle`
- Validates a point-in-time snapshot of compile/build busy state, Android target, enabled scenes, and output path shape
- Does not guarantee that the later `build.android` step will succeed if editor state changes afterward
- Completes immediately; `WAIT` is usually unnecessary

Current `ios.preflight` boundary:
- Supports `--outputPath`
- Validates a point-in-time snapshot of compile/build busy state, iOS target, enabled scenes, bundle identifier, iOS Build Support, and directory-style output path
- Requires the output directory to be missing or empty for the later `build.ios` step
- Does not guarantee that the later `build.ios` step will succeed if editor state changes afterward
- Completes immediately; `WAIT` is usually unnecessary

Current `build.ios` boundary:
- Exports an Xcode project directory, not a final `.ipa`
- Requires macOS Unity Editor in the MVP
- Requires Unity active build target already set to iOS
- Requires outputPath to be a missing or empty directory to avoid stale Xcode project contents
- Use `WAIT JOB build.ios` for polling

Current `scene.bulk_create` boundary:
- Supports `--manifestPath`
- Supports optional `--rootName` and `--rootParentPath`
- Creates scene objects synchronously from a JSON manifest
- Fails fast when a target object path already exists
- Usually uses `ASSERT last.success == true` instead of `WAIT`
- Targets Edit Mode / active-scene context only; Play Mode and Prefab Stage are unsupported in the MVP

Current `version.bump` boundary:
- Sets `PlayerSettings.bundleVersion` via `--bundleVersion`
- Sets `PlayerSettings.Android.bundleVersionCode` via `--androidVersionCode`
- Requires at least one of those fields
- Completes immediately; `WAIT` is usually unnecessary
