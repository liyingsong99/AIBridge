---
name: aibridge-workflow-orchestration
description: AIBridge workflow and multi-agent orchestration guidance. Use when Codex needs to design, review, or execute a multi-agent workflow plan, split Unity work into parallel or pipeline agent roles, define structured workflow artifacts, choose between batch/multi automation and agent orchestration, run adversarial verification, sweep multiple Runtime targets, or prepare AIBridge workflow recipes.
---

# AIBridge Workflow Orchestration

Use this Skill for workflow recipes, multi-agent orchestration, parallel review, pipeline validation, adversarial verification, Runtime target sweeps, or structured workflow artifacts.

Do not use it for routine single-file edits, simple CLI command lookup, or ordinary Unity validation. Use `aibridge-development-workflow` and `aibridge` for those tasks.

## Core Rules

- Keep orchestration explicit: phases, roles, dependencies, gates, artifacts, and expected outputs.
- Prefer parallel read and serial write. Use parallel write only when worktree isolation, file ownership, merge strategy, and validation gates are explicit.
- Use pipeline for staged per-item work. Use parallel barriers only when a downstream step needs all upstream results.
- Use structured outputs for intermediate results: findings, verdicts, plans, patch proposals, validation results, artifact references, and Runtime target references.
- Separate claims from evidence. Treat AIBridge CLI, Runtime, screenshots, logs, tests, and Code Index output as evidence.
- Do not describe `workflow` as a generic AI agent scheduler. Current CLI support is limited to recipe list/validate/plan/init, deterministic `run-cli` steps, run artifacts, gates, and reports; `agent` and `manual` steps require an external executor.

## Reference Loading

- Read `references/orchestration-patterns.md` before selecting parallel, pipeline, barrier, adversarial, or Runtime sweep patterns.
- Read `references/recipe-schema.md` before writing or reviewing a workflow recipe document.
- Read `references/builtin-recipes.md` before drafting common Unity implementation, review, Runtime validation, Prefab sweep, or bug-hunter workflows.
