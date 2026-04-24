---
templateId: unity-integration
assistant: cline
version: 2
target: root-rule
---
## AIBridge Rules

**Skill**: `aibridge` - Unity CLI automation

**CLI**: `{{CLI_PATH}}` (JSON output)

**Priority**:
- **Compile**: `compile unity` (default), `compile dotnet` (optional)
- **Asset Search**: `asset search/find --format paths` before filesystem search
- **Console**: `get_logs --logType Error`

**Quick Reference**:
```bash
{{CLI_PATH}} compile unity
{{CLI_PATH}} get_logs --logType Error
{{CLI_PATH}} asset search --mode script --keyword "Player" --format paths
{{CLI_PATH}} gameobject create --name "Cube" --primitiveType Cube
```

Reference: `{{SKILL_DOC_PATH}}`
