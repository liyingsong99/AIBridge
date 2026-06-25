---
name: aibridge-code-index
description: Optional read-only AIBridge Code Index semantic lookup for Unity C# code search and source navigation. Use when this Skill is installed, Code Index is enabled, and Codex needs to find C# symbols, definitions, references, implementations, derived types, callers, or diagnostics, including code lookup during development tasks. Do not use for literal text, config, asset, scene, prefab, or non-C# searches; use text_index when available, then rg, host file reads, or regular AIBridge commands instead
---

# AIBridge Code Index Skill

If `code_index status` reports `enabled=false`, `state=disabled`, or `semantic=false`, stop and fall back to `text_index`, then `rg` plus file reads.

## Operating Rules

- `code_index` is CLI-only, read-only, and does not rename, refactor, or write files.
- Use it for C# symbol lookup and source navigation before broad code edits when semantic lookup is useful.
- For literal strings, comments, config values, asset paths, scene objects, prefabs, or non-C# files, use `text_index` when available, then `rg`, host file reads, or regular AIBridge commands instead.
- Trust only results marked `semantic=true`; fallback text candidates are explicitly marked `semantic=false`.
- If disabled or unavailable, do not retry repeatedly. Use `text_index`, `rg`, file reads, and regular AIBridge commands.

## Query Selection

- Find a class, method, property, field, enum, or interface by name: `symbol`.
- Jump from a source location to its declaration: `definition`.
- Find usages of a symbol: `references`.
- Find interface or abstract member implementations: `implementations`.
- Find derived classes or interfaces: `derived`.
- Find callers of a method or property: `callers`.
- Inspect compiler diagnostics for a file or project: `diagnostics`.

Start with `code_index status` when availability is unknown. If the result is disabled, unavailable, empty, or only returns `semantic=false` fallback candidates, switch to `text_index` when available, then `rg` and direct file reads

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

