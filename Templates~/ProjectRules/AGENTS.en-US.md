# AGENTS.md

## Basic Principles
1. Prefer concise English replies unless the user asks otherwise.
2. Add necessary comments for complex business logic.
3. Respect existing user changes and do not revert unrelated files.

## Project Validation
- Unity compilation must use `$CLI compile unity`.
- `compile dotnet` is only an extra check and must not replace or fallback from Unity compilation.