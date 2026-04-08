---
templateId: unity-integration
assistant: claude
version: 1
target: root-rule
---
## AIBridge Unity Integration

Use `{{CLI_PATH}}` to interact with Unity Editor through AIBridge.

AIBridgeCLI outputs raw JSON by default for AI consumption. Use `--pretty` only when human-readable output is needed.

**Skill**: `aibridge`

**When to Use**:
- Read Unity console errors and warnings
- Trigger compile checks and inspect results
- Create or modify GameObjects, Components, Scenes, and Prefabs
- Search assets and capture screenshots or GIFs from Play Mode

**Compile Guidance**:
- Use `compile unity` as the default compile command for Unity projects
- Use `compile dotnet` only for a separate solution-build validation, not as a fallback for Unity compile failures

**Search Priority**:
- For Unity project files/assets/resources/scripts/configs, prefer AIBridge over generic filesystem search
- Use `asset search` / `asset find` with `format=paths` first to resolve canonical asset paths via Unity's AssetDatabase index with minimal response payload
- Use `asset get_path` only when you start from a GUID and `asset load` only when you want metadata confirmation
- After locating the canonical path, prefer the host AI's native file-read tool for contents
- Use `asset read_text` only as a fallback when native reads are unavailable or a Unity-side line window is specifically needed
- Fall back to generic grep/read tools only when AIBridge cannot cover the target

**Quick Reference**:
```bash
# CLI Path
{{CLI_PATH}}

# Common Commands
{{CLI_PATH}} compile unity
{{CLI_PATH}} get_logs --logType Error
{{CLI_PATH}} asset search --mode script --keyword "Player" --format paths
{{CLI_PATH}} asset get_path --guid "abc123..."
{{CLI_PATH}} gameobject create --name "Cube" --primitiveType Cube
{{CLI_PATH}} transform set_position --path "Player" --x 0 --y 1 --z 0

# Game view resolution
{{CLI_PATH}} gameview set_resolution --width 1920 --height 1080
{{CLI_PATH}} gameview get_resolution

# Fallback only
{{CLI_PATH}} asset read_text --assetPath "Assets/Scripts/Player.cs" --startLine 1 --maxLines 120
```

**Skill Documentation**: [AIBridge Skill]({{SKILL_DOC_PATH}})
