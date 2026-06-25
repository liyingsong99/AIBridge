---
name: aibridge-prefab-patch
description: Unity Prefab asset patch workflow for AIBridge. Use when modifying complex prefab assets with prefab patch operations, child or component creation, SerializedProperty writes, array edits, internal GameObject/component references, dry-run validation, or when deciding between prefab patch, inspector set_property, scene object commands, and direct Unity YAML fallback
---

# AIBridge Prefab Patch

Use `prefab patch` when one Prefab edit needs multiple operations in one load/save cycle. For single-field or small batched edits, prefer `inspector set_property` or `inspector set_properties`. For scene objects, prefer `gameobject`, `transform`, and `inspector`. For unsupported operations, load `unity-yaml-editing`.

## 参数选择

- Prefer `--ops <file>` for multi-step edits, nested JSON, arrays, references, or anything run from PowerShell
- Use `--ops-json <json>` only for one or two very small operations
- Put temporary operation files under `.aibridge/patch_ops/`
- Always run `--dryRun true` before writing the prefab

## 标准流程

1. Find the target prefab with `asset find/search --format paths`
2. Inspect structure with `prefab get_hierarchy --prefabPath "<prefab>"`
3. Create `.aibridge/patch_ops/<task>.json`
4. Run dry-run:

```bash
$CLI prefab patch --prefabPath "Assets/Prefabs/Player.prefab" --ops ".aibridge/patch_ops/player_hp_patch.json" --dryRun true
```

5. If dry-run succeeds, run the same command without `--dryRun true`
6. Re-check hierarchy/properties, then run `compile unity` and `get_logs --logType Error`

## 操作示例

```json
[
  { "op": "ensure_child", "path": "Player/HP" },
  { "op": "set_property", "target": { "path": "Player/HP", "componentName": "Animator" }, "propertyName": "m_Enabled", "value": true }
]
```

Supported ops: `ensure_child`, `ensure_component`, `set_property`, `set_properties`, `set_array`, `append_array`, `clear_array`. See `references/prefab-reference.md` for full op schemas.

## 不支持时的处理

If an operation is not covered by patch ops, load `unity-yaml-editing` and follow its Decision Order.

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

- Do not edit Prefab YAML directly; for unsupported operations use `unity-yaml-editing`.
- Duplicate child names or component types are ambiguous; use exact hierarchy paths and `componentIndex`.

## References

- `references/prefab-reference.md`: generated CLI reference for general `prefab` commands
