---
templateId: unity-project-rules
assistant: codex
version: 1
target: root-rule
---
## AIBridge Rules

Use `{{CLI_PATH}}` for Unity Editor automation in this project.

- Prefer `--raw` output for machine-readable responses
- Use AIBridge for compile checks, console log inspection, scene hierarchy changes, GameObject updates, Transform edits, and asset queries
- Use screenshot or GIF commands for visual verification when Play Mode is required

**Quick Reference**:
```bash
{{CLI_EXE_NAME}} compile unity --raw
{{CLI_EXE_NAME}} get_logs --logType Error --raw
{{CLI_EXE_NAME}} gameobject create --name "Cube" --primitiveType Cube --raw
{{CLI_EXE_NAME}} asset search --mode script --keyword "Player" --raw
```

Reference: `{{SKILL_DOC_PATH}}`
