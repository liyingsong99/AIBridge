---
templateId: unity-integration
assistant: cline
version: 3
target: root-rule
---
## AIBridge Rules

**Skill**: `aibridge` - Unity CLI automation

**CLI**: `{{CLI_PATH}}` (JSON output)

**Priority**:
- **Compile**: `compile unity` (default), `compile dotnet` (optional)
- **Asset Search**: `asset search/find --format paths` before filesystem search
- **Property Edits**: use `inspector get_properties/find_property/set_property/set_properties`; for prefab assets pass `assetPath + objectPath + componentName`
- **Console**: `get_logs --logType Error`
- **PowerShell JSON**: avoid inline complex `--json`; build a JSON variable, escape embedded quotes, and pass `--values $values`
- **Multi**: `multi --cmd` auto-wraps plain CLI lines as Batch `call`; use `multi --stdin` for long or JSON-heavy scripts

**Quick Reference**:
```bash
{{CLI_PATH}} compile unity
{{CLI_PATH}} get_logs --logType Error
{{CLI_PATH}} asset search --mode script --keyword "Player" --format paths
{{CLI_PATH}} gameobject create --name "Cube" --primitiveType Cube
{{CLI_PATH}} inspector set_property --assetPath "Assets/UI/LoginPanel.prefab" --objectPath "Root/Button" --componentName "RectTransform" --propertyName "m_AnchoredPosition.x" --value 100
{{CLI_PATH}} multi --cmd "editor log --message Step1&get_logs --logType Error --count 1"
```

Reference: `{{SKILL_DOC_PATH}}`
