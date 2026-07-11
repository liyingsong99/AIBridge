---
name: aibridge-code-index
description: Optional read-only AIBridge Code Index lightweight lookup for Unity C# declaration names. Use only when Root Rule declares Code Index enabled and this Skill is installed. Map declaration names to files or positions. Do not use for references, callers, implementations, derived types, diagnostics, comments, strings, or non-C# searches
---

# AIBridge Code Index Skill

## Operating Rules

- Enablement: Root Rule only; do not call `$CLI harness status` to re-check
- Public actions: `symbol`, `definition` only — locate `.cs` paths/positions, then read files yourself
- `--query` must be a C# identifier or identifier substring (e.g. `PlayerController`, `OpenInventory`)
- Do not query natural language, Chinese business names, comments, string literals, or file paths
- Unknown identifier: use host text search first, then call `code_index` with the discovered name
- Matching is declaration-name substring only — not full-text or semantic search
- Read `success` / `items` / `error`; ignore absent diagnostic fields. `items: []` means no name hit, not a daemon failure
- Do not use for references/callers/implementations/diagnostics/relationship queries
- On failure: do not retry lifecycle/status; fall back to direct file reads

## Commands

```bash
$CLI code_index symbol --query PlayerController
$CLI code_index definition --query PlayerController
```
