---
templateId: unity-project-rules
assistant: codex
version: 2
target: root-rule
---
## AIBridge Rules

Use `{{CLI_PATH}}` for Unity Editor automation in this project.

Primary reference skill: `{{PRIMARY_SKILL_ID}}`

Available installed skills:
{{AVAILABLE_SKILLS_MARKDOWN}}

For Unity-project lookup, prefer AIBridge over generic filesystem search whenever possible.

- Prefer `--raw` output for machine-readable responses
- Use AIBridge for compile checks, console log inspection, scene hierarchy changes, GameObject updates, Transform edits, and asset queries
- For Unity files/assets/resources/scripts/configs, use `asset search` / `asset find` with `format=paths` first because Unity's AssetDatabase index is faster and more reliable on large projects while returning only the canonical asset paths AI usually needs
- Use `asset get_path` only when starting from a GUID and `asset load` only when metadata confirmation helps
- After locating a path, prefer the host AI's native file-read tool for text-based Unity assets
- Use `asset read_text` only as a fallback when native reads are unavailable or when a Unity-side line window is specifically needed before falling back to generic `grep`/filesystem tools
- Use screenshot or GIF commands for visual verification when Play Mode is required

**Quick Reference**:
```bash
{{CLI_PATH}} compile unity --raw
{{CLI_PATH}} get_logs --logType Error --raw
{{CLI_PATH}} gameobject create --name "Cube" --primitiveType Cube --raw
{{CLI_PATH}} asset search --mode script --keyword "Player" --format paths --raw
{{CLI_PATH}} asset get_path --guid "abc123..." --raw

# Fallback only
{{CLI_PATH}} asset read_text --assetPath "Assets/Scripts/Player.cs" --startLine 1 --maxLines 120 --raw
```

If Claude Code is also used in this project, the installed AIBridge skills are exposed under `.claude/skills/` for Claude-native discovery.
