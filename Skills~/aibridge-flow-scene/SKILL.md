---
description: "Author AIBridge scene automation flows for bulk object creation, prefab placement, and deterministic scene setup."
---

# AIBridge Scene Flow Skill

## Best For

- Batch create GameObjects
- Place prefabs from manifests or repeatable rules
- Build deterministic scene setup flows
- Save scenes after scripted edits

## Recommendations

- Use explicit parent paths and object names
- Prefer one coarse-grained `JOB scene.bulk_create` over hundreds of tiny repeated steps
- Add `ASSERT` after important creation steps
- Save scenes explicitly using `UNITY menu_item` or a dedicated job
- Prefer manifest-driven creation with deterministic local transforms and explicit component/property setup
- In the current MVP, `scene.bulk_create` completes immediately and usually does not need `WAIT`
- Use it only for the currently open scene in Edit Mode; Prefab Stage and Play Mode are out of scope in the MVP

## Example

```txt
FLOW scene_bulk_create

VAR root = "SpawnPointsRoot"
VAR manifest = "Assets/Automation/SpawnPoints.json"

STEP bulk_create JOB scene.bulk_create --rootName "${root}" --manifestPath "${manifest}"
ASSERT last.success == true

STEP save_scene UNITY menu_item --menuPath "File/Save"
ASSERT last.success == true

END
```

## Minimal Manifest Shape

```json
{
  "objects": [
    {
      "name": "SpawnPoint_001",
      "localPosition": { "x": 0, "y": 1, "z": 0 },
      "components": [
        {
          "typeName": "BoxCollider",
          "properties": {
            "isTrigger": true
          }
        }
      ]
    }
  ]
}
```

Supported per-object fields in the current MVP:
- `name` (required)
- `primitiveType` (optional)
- `parentPath` (optional; defaults to the created/reused root when `--rootName` is provided)
- `localPosition`
- `localRotation`
- `localScale`
- `active`
- `components[].typeName`
- `components[].properties` for simple serialized property types

Current path/merge model:
- `--rootName` reuses an existing root if it already exists
- child object creation is create-only and fails fast on duplicate target paths
- `parentPath` and duplicate detection rely on `GameObject.Find(path)` in the current editor context

Current determinism limits:
- Prefer fully qualified or otherwise unambiguous component type names when possible
- Property setting currently targets simple serialized property types only
- Undo rollback is best-effort for scene edits, not a full transaction against arbitrary editor/component side effects

## Avoid

- Layout-dependent GUI click automation
- Unbounded repeated `STEP` blocks for large manifests
- Implicit scene saves
- Reusing an existing target object path; the current MVP fails fast on duplicates instead of merging
- Assuming the job supports additive-scene disambiguation or prefab-stage editing; it targets the active scene context only
