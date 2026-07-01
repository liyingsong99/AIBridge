---
name: aibridge-code-index
description: Optional read-only AIBridge Code Index lightweight lookup for Unity C# declaration names. Use when this Skill is installed, Code Index is enabled, and Codex needs to quickly map a class, interface, enum, field, property, method, constructor, or delegate name to declaration files or declaration positions. Do not use for references, callers, implementations, derived types, diagnostics, literal text, config, asset, scene, prefab, or non-C# searches
---

# AIBridge Code Index Skill

## Operating Rules

- `code_index` is CLI-only, read-only, and only for fast C# declaration-name lookup.
- Public query actions are only `symbol` and `definition`.
- Use it to locate candidate `.cs` files or declaration positions, then read those files yourself for the real analysis.
- Do not use it for references, callers, implementations, derived types, diagnostics, or any whole-project relationship query.
- If it fails or is unavailable, do not keep retrying lifecycle/status commands. Switch to direct file inspection and regular AIBridge commands.

## Query Selection

- Find classes, methods, properties, fields, enums, interfaces, constructors, or delegates by name: `symbol`
- Return the best declaration position for a declaration name: `definition`

## Commands

```bash
$CLI code_index symbol --query PlayerController
$CLI code_index definition --query PlayerController
```
