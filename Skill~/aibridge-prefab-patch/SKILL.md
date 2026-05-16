---
name: aibridge-prefab-patch
description: Unity Prefab asset patch workflow for AIBridge. Use when modifying complex prefab assets with prefab patch operations, child or component creation, SerializedProperty writes, array edits, internal GameObject/component references, dry-run validation, or when deciding between prefab patch, inspector set_property, and scene object commands.
---

# AIBridge Prefab Patch

Use `prefab patch` for complex Prefab asset edits that need multiple operations in one load/save cycle. Use `inspector set_property` for a single simple serialized field. Use `gameobject`、`transform`、`inspector` for scene objects.

`$CLI` means the platform-appropriate AIBridge CLI invocation, usually `./AIBridgeCache/CLI/AIBridgeCLI.exe` on Windows.

## 参数选择

- Prefer `--ops <file>` for multi-step edits, nested JSON, arrays, references, or anything run from PowerShell.
- Use `--ops-json <json>` only for one or two very small operations.
- Put temporary operation files under `AIBridgeCache/patch_ops/`.
- Always run `--dryRun true` before writing the prefab.

## 标准流程

1. Find the target prefab with `asset find/search --format paths`.
2. Inspect structure with `prefab get_hierarchy --prefabPath "<prefab>"`.
3. Create `AIBridgeCache/patch_ops/<task>.json`.
4. Run dry-run:

```bash
$CLI prefab patch --prefabPath "Assets/Prefabs/Player.prefab" --ops "AIBridgeCache/patch_ops/player_hp_patch.json" --dryRun true
```

5. If dry-run succeeds, run the same command without `--dryRun true`.
6. Re-check hierarchy/properties, then run `compile unity` and `get_logs --logType Error`.

## 操作示例

```json
[
  { "op": "ensure_child", "path": "Player/HP" },
  { "op": "ensure_component", "path": "Player/HP", "typeName": "Animator" },
  {
    "op": "set_property",
    "target": { "path": "Player/HP", "componentName": "Animator" },
    "propertyName": "m_Enabled",
    "value": true
  },
  {
    "op": "set_array",
    "target": { "path": "Player", "componentName": "YourComponent" },
    "propertyName": "items",
    "items": [
      { "key": "HP", "value": { "$gameObject": "Player/HP" } },
      {
        "key": "HPAnimator",
        "value": {
          "$component": { "path": "Player/HP", "typeName": "Animator" }
        }
      }
    ]
  }
]
```

## 支持的操作

- `ensure_child`: Ensure a GameObject path exists. Optional fields: `active`, `tag`, `layer`.
- `ensure_component`: Ensure a component exists on `path`. Use `typeName`.
- `set_property`: Set one SerializedProperty on a GameObject/component/asset target.
- `set_properties`: Set multiple SerializedProperties on one target.
- `set_array`: Replace an array property.
- `append_array`: Append items to an array property.
- `clear_array`: Clear an array property.

## 引用写法

Use these object reference values inside `value` or array items:

```json
{ "$gameObject": "Player/HP" }
{ "$component": { "path": "Player/HP", "typeName": "Animator" } }
{ "$asset": "Assets/Materials/HPMat.mat" }
{ "$guid": "asset-guid" }
null
```

## 注意事项

- Do not edit Prefab YAML directly unless no Unity API path exists.
- Paths are normalized against the prefab root; both `Root/Child` and `Child` can work when unambiguous.
- Duplicate child names under the same parent are ambiguous; use exact hierarchy paths.
- Duplicate components of the same type are ambiguous; use `componentIndex` when needed.
- Keep operation JSON under `AIBridgeCache/patch_ops/`; do not put it under `Assets/` or commit it.

## References

- `references/prefab-reference.md`: generated CLI reference for general `prefab` commands.
