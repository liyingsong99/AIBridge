---
templateId: unity-integration
assistant: claude
version: 2
target: root-rule
---
## AIBridge Unity Integration

**Skill**: `aibridge` - Unity CLI automation tool

**CLI**: `{{CLI_PATH}}` (outputs JSON by default)

**Core Workflows**:
- **Compile**: Use `compile unity` (default), `compile dotnet` (optional validation only)
- **Asset Search**: Use `asset search/find --format paths` before generic filesystem search
- **Console Logs**: `get_logs --logType Error`
- **Scene/GameObject**: Create, modify, inspect hierarchy
- **Visual Verification**: `screenshot game`, `screenshot gif --frameCount 50` (Play Mode)

**Quick Reference**:
```bash
{{CLI_PATH}} compile unity
{{CLI_PATH}} get_logs --logType Error
{{CLI_PATH}} asset search --mode script --keyword "Player" --format paths
{{CLI_PATH}} gameobject create --name "Cube" --primitiveType Cube
```

**Skill Documentation**: [AIBridge Skill]({{SKILL_DOC_PATH}})
