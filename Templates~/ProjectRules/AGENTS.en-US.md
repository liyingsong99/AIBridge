# AGENTS.md

## Basic Principles
1. Prefer concise English replies unless the user asks otherwise.
2. Add necessary comments for complex business logic.
3. Respect existing user changes and do not revert unrelated files.

## Knowledge Base Rules
- Before any AIBridge feature, command, menu, Workflow, Skill, Runtime, template, or documentation change, read `Packages/cn.lys.aibridge/Doc~/KnowledgeBaseIndex.md`; when needed, also read `Packages/cn.lys.aibridge/Doc~/README.md` and the matching topic document.
- When adding, changing, or removing user-visible capabilities, CLI commands, Editor menus, Runtime capabilities, workflow recipes, Skills, templates, generated artifact paths, or public documentation, update `KnowledgeBaseIndex.md` and the related README / `Doc~` / `Skill~` / `Templates~` documents in the same change without waiting for an extra user reminder.
- If implementation and the knowledge base disagree, verify the current fact from code, CLI output, or `workflow list`, then fix either the implementation or the knowledge base; do not leave the drift for later.
- For planning-oriented or cross-module changes, write `.aibridge/plan/<slug>.md` first, then sync the confirmed content into the formal knowledge base documents.

## Project Validation
- `$CLI` points to the project-local AIBridge CLI: `{{AIBRIDGE_CLI_PATH}}`. In PowerShell, assign `$CLI = "{{AIBRIDGE_CLI_PATH}}"`, then run `& $CLI ...`.
- Unity compilation must use `$CLI compile unity`.
- `compile dotnet` is only an extra check and must not replace or fallback from Unity compilation.
