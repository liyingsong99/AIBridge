# Orchestration Patterns

Purpose: choose safe workflow structure for AIBridge tasks that need more than a single linear agent pass.

## Boundaries

| Surface | Responsibility | Do not use for |
|---|---|---|
| Workflow orchestration | Control flow, agent roles, dependencies, artifacts, gates, and final report shape | Direct Unity object mutation or command syntax lookup |
| Skill | Domain rules loaded by an AI agent | Persisted run state or execution scheduling |
| `batch` / `multi` | Linear AIBridge CLI automation inside one command stream | Multi-agent reasoning, voting, merge ownership, or adversarial review |
| Runtime Bridge | Player or Play Mode data plane for status, logs, screenshots, perf, handlers, and calls | Agent scheduling or workflow recipe execution |
| Code Index | Read-only semantic code evidence | Runtime state, asset mutation, or project build validation |

Current workflow CLI support covers recipe list/validate/plan/init, deterministic `run-cli` steps, run artifacts, gates, and reports. It is not a cross-tool agent runtime; `agent` and `manual` steps still require an external executor.

## Pattern Selection

Use parallel when:

- Work items are independent.
- Agents are read-only or produce proposals only.
- Results can be merged by schema, path, target id, or artifact id.
- Examples: directory-sharded review, multiple Runtime target status checks, independent log/screenshot collection.

Use pipeline when:

- Each item must pass through ordered stages.
- A later stage depends on the prior stage's evidence or verdict.
- Examples: discover target -> collect evidence -> validate claim -> report; inspect Prefab -> propose patch -> apply serially -> validate.

Use a barrier when:

- A downstream step needs every upstream result.
- Findings must be deduplicated, ranked, voted, or compared globally.
- A final implementer needs all patch proposals before editing.

Use a single linear `batch` / `multi` script when:

- The work is deterministic command automation.
- There is no need for independent agent reasoning.
- Commands share one Unity Editor state and must run in order.

## Parallel Read, Serial Write

- Parallel agents default to read-only behavior.
- Parallel agents return `Finding`, `Verdict`, `PatchProposal`, `ValidationResult`, `ArtifactRef`, or `RuntimeTargetRef` objects.
- The main agent or one implementer applies edits serially after reviewing proposals.
- Parallel writes require explicit file ownership, separate worktrees or isolated generated outputs, a merge plan, and post-merge validation.
- Never let two write agents modify the same Unity serialized asset, `.meta` file, scene, Prefab, package manifest, or generated command reference.

## Structured Outputs

Use structured outputs for intermediate data. Keep prose for the final human report.

Recommended fields:

- `Finding`: `id`, `severity`, `file`, `line`, `claim`, `evidence`, `repro`, `confidence`.
- `Verdict`: `claimId`, `status`, `evidence`, `reason`, `remainingRisk`.
- `PatchProposal`: `id`, `files`, `summary`, `risk`, `validation`.
- `ValidationResult`: `gate`, `status`, `command`, `evidence`, `artifacts`.
- `ArtifactRef`: `kind`, `path`, `summary`, `sourceCommand`.
- `RuntimeTargetRef`: `targetId`, `url`, `platform`, `status`, `evidence`.

Large outputs should be saved as artifacts and referenced by path or id instead of pasted into the main context.

## Adversarial Verification

- Split generator and verifier roles when correctness is high risk.
- Give the verifier claims and evidence, not the generator's full reasoning.
- Require one of three verdicts: `confirmed`, `refuted`, or `uncertain`.
- Treat `uncertain` as actionable: request more evidence, narrow scope, or downgrade the claim.
- Prefer verification gates that can be repeated: compile, tests, logs, screenshots, Runtime calls, or semantic lookup.

## AIBridge Evidence Gates

Choose gates that match the change:

- `compile unity`: Unity compile validation.
- `get_logs --logType Error`: Editor Console error check.
- `test run`: targeted Unity tests when available.
- `screenshot game`, `screenshot scene_view`, `screenshot gif`: visual evidence.
- `runtime list_targets`, `runtime status`, `runtime logs`, `runtime screenshot`, `runtime perf`, `runtime handlers`, `runtime call`: Player or Play Mode validation.
- `code_index`: optional read-only semantic evidence only when the Skill and project settings enable it.

Evidence should include the command, relevant output summary, artifact path, target id or URL when applicable, and the final verdict.
