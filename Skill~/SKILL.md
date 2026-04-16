---
name: aibridge
description: "Unity CLI Tool. Execute compile, asset search, gameobject manipulation, transform operations, component inspection, scene/prefab management, screenshot capture, and GIF recording. Supports multi-command execution and runtime extension."
commands: [compile, asset, gameobject, transform, inspector, selection, scene, prefab, screenshot, gameview, get_logs, focus, batch, multi, menu_item, editor]
capabilities: [asset-lookup, scene-editing, build-automation, visual-verification, component-inspection, hierarchy-manipulation, prefab-management, console-monitoring, editor-control]
triggers: [unity, compile, gameobject, transform, component, scene, prefab, screenshot, gif, console, log, asset, hierarchy, inspector, selection, menu, editor, focus, batch, gameview, resolution]
---

# AI Bridge Unity Skill

## AI Operating Rules

**Compile Priority:**
- Use `compile unity` (default) - requires Unity Editor running
- Use `compile dotnet` (optional) - separate solution-build validation only, NOT a fallback

**Asset Lookup Priority:**
1. `asset search/find --format paths` (Unity AssetDatabase index - fastest)
2. `asset get_path` (only when starting from GUID)
3. `asset load` (only for metadata confirmation)
4. Host AI native file-read tool (for file contents)
5. `asset read_text` (fallback when native reads unavailable)
6. Generic grep/filesystem search (last resort)

**Special Constraints:**
- `focus` - Windows-only, CLI-only, triggers Unity refresh/compile
- `screenshot` - Requires Play Mode
- `multi` - Preferred for batch operations

---

## Invocation

**CLI Path:** `./AIBridgeCache/CLI/AIBridgeCLI.exe` (run from Unity project root)

**Alias (used in examples below):** `$CLI`

**OS Syntax:**
- Windows: `./AIBridgeCache/CLI/AIBridgeCLI.exe <command> <action> [options]`
- macOS/Linux: `dotnet ./AIBridgeCache/CLI/AIBridgeCLI.dll <command> <action> [options]`
- PowerShell: `& "./AIBridgeCache/CLI/AIBridgeCLI.exe" <command> <action> [options]`

**Global Options:**
- `--timeout <ms>` - Timeout (default: 5000)
- `--raw` / `--pretty` - JSON output (default: raw)
- `--json <json>` / `--stdin` - Complex parameters
- `--help` - Show help

**Cache Directory:** `AIBridgeCache/` (commands, results, screenshots)

---

## Command Reference

### `focus` - Bring Unity to Foreground

CLI-only, Windows-only. Triggers Unity refresh/compile via Windows API.

```bash
$CLI focus
# Output: {"Success":true,"ProcessId":1234,"WindowTitle":"MyProject - Unity 6000.0.51f1","Error":null}
```

### `multi` - Execute Multiple Commands (Recommended)

```bash
$CLI multi --cmd 'editor log --message Step1&gameobject create --name Cube --primitiveType Cube'
$CLI multi --stdin  # Read from stdin (one per line)
```

<!-- AIBRIDGE:COMMANDS -->

---

**Skill Version**: 1.0
**Package**: cn.lys.aibridge
