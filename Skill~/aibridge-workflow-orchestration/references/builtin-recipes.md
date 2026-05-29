# Built-In Recipes

Purpose: summarize the package workflow templates under `Templates~/Workflows/*.aibridge-workflow.json`.

List or copy them with:

```bash
$CLI workflow list
$CLI workflow init --recipe <name>
$CLI workflow validate --recipe <name>
```

## unity-change-implementation

Use for scoped Unity package, Editor tool, Runtime code, or asset-generation changes.

- Phases: analyze, implement, validate, report.
- CLI gates: `compile unity`, `get_logs --logType Error --count 50`.
- Optional evidence: screenshot or Runtime validation.
- Boundary: the main agent applies edits serially; the recipe does not delegate writes to parallel agents.

## unity-sharded-review

Use for broad read-only review by directory, assembly, package, feature, or file list.

- Phases: shard, parallel review, deduplicate, adversarial verify, report.
- Outputs: `Finding`, `Verdict`, `ArtifactRef`.
- Gate: optional `externalVerdict allow=confirmed`.
- Boundary: report only confirmed findings first; uncertain claims must list missing evidence instead of pretending certainty.

## runtime-target-sweep

Use for Editor Play Mode, Windows Player, Android, or LAN Runtime target health checks.

- Phases: discover, collect, compare, report.
- CLI steps: `runtime list_targets`, `runtime status`, `runtime logs`, `runtime screenshot`, `runtime perf`.
- Gates: `runtimeReachable`, `runtimeErrors max=0`, optional `screenshotExists`.
- Boundary: capture target id or URL in evidence so the same target can be diagnosed later.

## runtime-ui-validation

Use for validating UI behavior through Runtime or Play Mode action paths.

- Phases: prepare, perform actions, collect evidence, verdict, report.
- CLI steps: `runtime status`, `runtime logs`, `runtime screenshot`.
- Gates: `runtimeReachable`, `runtimeErrors max=0`, `screenshotExists`, optional `externalVerdict`.
- Boundary: action execution remains serial to avoid input-state races.

## prefab-asset-sweep

Use for broad Prefab, Scene, or `.asset` inspection and controlled edits.

- Phases: search assets, parallel inspect, proposal barrier, dry-run, serial apply, validate, report.
- Gates: optional `artifactRequired kind=patch-proposal`, `unityCompile`, `consoleErrors max=0`.
- Boundary: parallel phase is read-only; writes to Prefab, Scene, `.asset`, and `.meta` files must be serial after proposal review.

## bug-hunter-loop

Use when the failure source is vague and the workflow needs iterative evidence, candidate generation, verification, one fix, and validation.

- Phases: collect evidence, candidate generation, adversarial verify, implement one, validate, loop or report.
- CLI steps: `get_logs`, `compile unity`, and optional tests/runtime evidence.
- Gates: optional `externalVerdict allow=confirmed`, `unityCompile`, `consoleErrors max=0`, optional `testRun`.
- Boundary: implement only one confirmed high-value fix per iteration; do not edit based on weak evidence.
