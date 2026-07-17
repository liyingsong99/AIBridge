---
name: aibridge
description: Unity Editor and Player Runtime CLI integration for AIBridge. Use for harness snapshot reads, Unity compile/logs/assets/scenes/prefabs/Inspector, screenshots/GIFs, Play Mode input, Runtime Player targets, focus/menu/game view, or AIBridgeCLI syntax. Route batch/multi, complex prefab patching, and direct UnityYAML edits to specialized Skills
---

# AI Bridge Unity Skill

## Invocation

Run from Unity project root. `$CLI` is the platform AIBridge CLI; on Windows PowerShell prefer:

```powershell
& "./.aibridge/cli/AIBridgeCLI.exe" <command> [action] [options]
```

Common options: `--timeout`, `--raw`, `--pretty`, `--json`, `--stdin`, `--help`, `--on-dialog`. Installed command references live under `references/` after Skill install

## Harness Snapshot

Project-side capabilities are in Root Rule / workflow rules. `$CLI harness status` is optional diagnosis only — not enablement/freshness preflight

## Operating Rules

- Unity validation: `compile unity`. `compile dotnet` is an extra check, not a Unity fallback
- Imported asset path discovery: `asset search/find --format paths` when Editor is available
- Edit order: `inspector set_property/set_properties` → `aibridge-prefab-patch` → Editor scripts → `unity-yaml-editing` only for unsupported YAML structure
- Prefab asset edits: `assetPath + objectPath + componentName`/`componentIndex`; `componentInstanceId` is scene-only
- `focus` Windows-only; `dialog` CLI-only; `input` needs Play Mode + EventSystem
- `runtime`: quick `list_targets` first; `--probe true` only for port scan; UI start with `runtime.ui.snapshot`
- Code Index: only when Root Rule declares enabled — load `aibridge-code-index` for declaration-name lookup
- Do not proactively clean `.aibridge` cache unless the user asks for maintenance
