---
templateId: unity-integration
assistant: claude
version: 1
target: root-rule
---
## AIBridge Unity Integration

Use `{{CLI_PATH}}` to interact with Unity Editor through AIBridge.

**Skill**: `aibridge`

**When to Use**:
- Read Unity console errors and warnings
- Trigger compile checks and inspect results
- Create or modify GameObjects, Components, Scenes, and Prefabs
- Search assets and capture screenshots or GIFs from Play Mode

**Quick Reference**:
```bash
# CLI Path
{{CLI_PATH}}

# Common Commands
{{CLI_EXE_NAME}} compile unity --raw
{{CLI_EXE_NAME}} get_logs --logType Error --raw
{{CLI_EXE_NAME}} asset search --mode script --keyword "Player" --raw
{{CLI_EXE_NAME}} gameobject create --name "Cube" --primitiveType Cube --raw
{{CLI_EXE_NAME}} transform set_position --path "Player" --x 0 --y 1 --z 0 --raw
```

**Skill Documentation**: [AIBridge Skill]({{SKILL_DOC_PATH}})
