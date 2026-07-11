---
name: aibridge-code-index
description: Optional read-only AIBridge Code Index lightweight lookup for Unity C# declaration names. Use only when Root Rule declares Code Index enabled and this Skill is installed. Map declaration names to files or positions. Do not use for references, callers, implementations, derived types, diagnostics, or non-C# searches
---

# AIBridge Code Index Skill

## Operating Rules

- Enablement: Root Rule only; do not call `$CLI harness status` to re-check
- Public actions: `symbol`, `definition` only — locate `.cs` paths/positions, then read files yourself
- Do not use for references/callers/implementations/diagnostics/relationship queries
- On failure: do not retry lifecycle/status; fall back to direct file reads

## Commands

```bash
$CLI code_index symbol --query PlayerController
$CLI code_index definition --query PlayerController
```
