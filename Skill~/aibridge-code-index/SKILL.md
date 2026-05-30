---
name: aibridge-code-index
description: Optional read-only AIBridge Code Index semantic lookup for Unity C# code search and source navigation. Use when this Skill is installed, Code Index is enabled, and Codex needs to find C# symbols, definitions, references, implementations, derived types, callers, or diagnostics, including code lookup during development tasks. Do not use for literal text, config, asset, scene, prefab, or non-C# searches; use rg, host file reads, or regular AIBridge commands instead.
---

# AIBridge Code Index Skill

Run commands from the Unity project root.

**CLI Path:** `./.aibridge/cli/AIBridgeCLI.exe`

**Availability Rule:** This Skill is installed only when `AIBridge/Settings > Code Index > Enable Code Index` is enabled. If project rules say Code Index is disabled, or `code_index status` reports `enabled=false` or `state=disabled`, stop using `code_index` and fall back to `rg` plus normal file reads.

## Operating Rules

- `code_index` is CLI-only, read-only, and does not rename, refactor, or write files.
- Use it for C# symbol lookup and source navigation before broad code edits when semantic lookup is useful.
- For literal strings, comments, config values, asset paths, scene objects, prefabs, or non-C# files, use `rg`, host file reads, or regular AIBridge commands instead.
- Trust only results marked `semantic=true`; fallback text candidates are explicitly marked `semantic=false`.
- If disabled or unavailable, do not retry repeatedly. Use `rg`, file reads, and regular AIBridge commands.

## Query Selection

- Find a class, method, property, field, enum, or interface by name: `symbol`.
- Jump from a source location to its declaration: `definition`.
- Find usages of a symbol: `references`.
- Find interface or abstract member implementations: `implementations`.
- Find derived classes or interfaces: `derived`.
- Find callers of a method or property: `callers`.
- Inspect compiler diagnostics for a file or project: `diagnostics`.

Start with `code_index status` when availability is unknown. If the result is disabled, unavailable, or only returns `semantic=false` fallback candidates, switch to `rg` and direct file reads.

## Commands

```bash
$CLI code_index status
$CLI code_index doctor
$CLI code_index warmup
$CLI code_index symbol --query PlayerController
$CLI code_index definition --file Assets/Scripts/Foo.cs --line 42 --column 17
$CLI code_index references --file Assets/Scripts/Foo.cs --line 42 --column 17
$CLI code_index implementations --type Game.IFoo
$CLI code_index derived --type Game.BasePanel
$CLI code_index callers --file Assets/Scripts/Foo.cs --line 42 --column 17
$CLI code_index diagnostics --file Assets/Scripts/Foo.cs
```

Results include `enabled`, `semantic`, `source`, `state`, `stale`, `workspaceMode`, snapshot metadata, excluded snapshot counts, `projectRoot`, and `solution`. Unity can generate/prewarm the snapshot from `AIBridge/Settings > Code Index`, where PackageCache source indexing and ignored assembly/source-path patterns can also be configured.

---

**Skill Version**: 1.0
**Package**: cn.lys.aibridge
