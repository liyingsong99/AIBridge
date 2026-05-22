---
name: unity-yaml-editing
description: Unity YAML text serialization editing workflow. Use when Codex needs to directly inspect, modify, create, or repair Unity serialized YAML files such as .unity scenes, .prefab assets, .asset ScriptableObject/config files, .mat, .controller, or other text-serialized Unity assets, especially when AIBridge inspector/prefab/scene APIs do not support the requested Prefab, Scene, ScriptableObjectTable, or custom asset operation.
---

# Unity YAML Editing

Use this Skill only after considering Unity/AIBridge APIs. UnityYAML is fragile; direct text edits are the fallback for unsupported serialized asset operations, not the default path.

## Decision Order

1. Prefer `aibridge` Inspector/SerializedProperty commands for readable field edits on scene objects, prefab assets, and `.asset` files.
2. Prefer `aibridge-prefab-patch` for complex Prefab child/component/property/array/reference edits it supports.
3. Use direct UnityYAML editing when AIBridge cannot express the operation, for example:
   - Scene `.unity` structure/object creation or unsupported scene edits.
   - Prefab or Prefab Variant structures not covered by `prefab patch`.
   - ScriptableObjectTable, custom ScriptableObject `.asset`, `.mat`, `.controller`, or other serialized assets requiring unsupported structural changes.
   - Repairing malformed text serialization while preserving existing IDs and references.

## Required Workflow

1. Read `references/unity-yaml-reference.md` before editing.
2. Make a small, reviewable patch; preserve ordering, indentation, document headers, anchors, `m_Script`, GUIDs, and local fileIDs unless a new object must be created.
3. For new MonoBehaviour/ScriptableObject documents, resolve the script `.meta` GUID first and use it in `m_Script`.
4. For new documents, allocate local fileIDs that do not exist in the same file and update every owning reference consistently.
5. Validate with Unity import/compile and a targeted AIBridge inspection when possible.

## Hard Rules

- Do not use generic YAML formatters or parsers to rewrite UnityYAML files.
- Do not change `%YAML`, `%TAG`, `--- !u!<classID> &<fileID>` headers, class IDs, GUIDs, or fileIDs without a clear reference update plan.
- Do not invent Unity schema fields. Copy shape from an existing nearby object of the same class/component/script when possible.
- Prefer decimal floats in hand edits. Avoid editing Unity-generated hexadecimal float values unless preserving existing text unchanged.
- Keep `.meta` GUIDs stable. Creating a new asset requires creating/retaining the paired `.meta` file through Unity when possible.

## Reference

- `references/unity-yaml-reference.md`: detailed UnityYAML format, editing patterns, safety checks, and validation checklist.
