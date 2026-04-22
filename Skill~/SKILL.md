---
name: aibridge
description: "Unity CLI 工具。执行编译、资源搜索、游戏对象操作、变换操作、组件检查、场景/预制体管理、截图捕获和 GIF 录制。支持多命令执行、运行时扩展和脚本自动化。"
commands: [compile, asset, gameobject, transform, inspector, selection, scene, prefab, screenshot, gameview, get_logs, focus, batch, multi, menu_item, editor, script]
capabilities: [asset-lookup, scene-editing, build-automation, visual-verification, component-inspection, hierarchy-manipulation, prefab-management, console-monitoring, editor-control, script-automation]
triggers: [unity, compile, gameobject, transform, component, scene, prefab, screenshot, gif, console, log, asset, hierarchy, inspector, selection, menu, editor, focus, batch, gameview, resolution, script, automation]
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

### AIBridge 脚本自动化

**用途**：自动化 Unity 编辑器操作和 CLI 命令执行（`.txt` 文件，存放于 `Assets/AIBridgeScripts/`）

**命令语法**：
```
log "消息"              # 输出日志
delay 毫秒数            # 延迟执行
call [CLI命令] [参数]   # 调用 AIBridge CLI（可选 --timeout 毫秒数）
menu 菜单路径           # 执行编辑器菜单项
```

**语法规则**：`#` 注释，空行跳过，命令不区分大小写

**常用示例**：
```
# 自动化构建
log "开始构建"
call compile unity
delay 2000
call scene get_hierarchy --depth 2
menu File/Save Project
```

**典型场景**：编译流程、场景批处理、资源管理、重复任务自动化

<!-- AIBRIDGE:COMMANDS -->

---

**Skill Version**: 1.0
**Package**: cn.lys.aibridge
