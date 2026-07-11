---
name: aibridge-workflow-orchestration
description: AIBridge workflow and multi-agent orchestration guidance. Use when Codex needs to design, review, or execute a multi-agent workflow plan, split Unity work into parallel or pipeline agent roles, define structured workflow artifacts, choose between batch/multi automation and agent orchestration, run adversarial verification, investigate Runtime debug evidence, sweep multiple Runtime targets, or prepare AIBridge workflow recipes
---

# AIBridge Workflow Orchestration

Focus on recipes, multi-agent orchestration, parallel/pipeline validation, adversarial verification, Runtime sweeps, or structured artifacts. Routine edits and ordinary Unity validation stay with `aibridge-development-workflow` / `aibridge`

## Core Rules

- Keep phases, roles, dependencies, gates, artifacts, and expected outputs explicit
- Prefer parallel read and serial write; parallel write only with isolation, ownership, merge, and gates
- Use structured outputs for intermediate results; separate claims from evidence
- Skill routing is preflight; scope phase Skills then pass compact handoff + artifact refs
- `agent`/`manual` need an external executor; AIBridge CLI does not run them as an LLM scheduler

## Reference Loading

- Patterns: `references/orchestration-patterns.md`
- Recipe shape/gates: `references/recipe-schema.md`
- Workflow CLI commands: `references/workflow-cli-reference.md` (on demand)
- Evidence import: `references/evidence-schema.md`
- Built-in recipes: `references/builtin-recipes.md`
