# Recipe Schema

Purpose: define AIBridge workflow recipe JSON files used by `workflow validate`, `workflow plan`, `workflow init`, and `workflow run-cli`.

## Locations

```text
Templates~/Workflows/<name>.aibridge-workflow.json
.aibridge/workflows/recipes/<name>.aibridge-workflow.json
.aibridge/workflows/runs/<runId>/
```

Package templates live under `Templates~/Workflows`. Project-local recipes live under `.aibridge/workflows/recipes`. A run writes its manifest, inputs, phase/step state, command results, artifacts, gates, and report under `.aibridge/workflows/runs/<runId>/`.

## CLI

```bash
$CLI workflow list
$CLI workflow validate --recipe runtime-target-sweep
$CLI workflow plan --recipe runtime-ui-validation --format markdown
$CLI workflow init --recipe runtime-ui-validation
$CLI workflow run-cli --file ".aibridge/workflows/recipes/runtime-target-sweep.aibridge-workflow.json" --inputs ".aibridge/workflows/inputs.json"
$CLI workflow status --run <runId>
$CLI workflow report --run <runId> --format markdown
$CLI workflow clean --older-than 30d --dry-run true
```

`run-cli` executes only deterministic `cli`, `barrier`, and `report` steps. It records `agent` and `manual` steps as `skipped_requires_external_executor`; external tools such as Codex, Claude, or Cursor remain responsible for those steps.

## Recipe Shape

Required top-level fields:

- `schemaVersion`: must be `1`.
- `name`: lower kebab-case recipe id.
- `description`: concise purpose.
- `phases`: ordered phase definitions.
- `gates`: validation gates.

Optional fields:

- `title`
- `version`
- `inputs`
- `artifacts`

```json
{
  "schemaVersion": 1,
  "name": "runtime-target-sweep",
  "title": "Runtime Target Sweep",
  "description": "Collect Runtime target evidence.",
  "version": "1.0.0",
  "inputs": {
    "target": { "default": "latest" }
  },
  "phases": [],
  "gates": [],
  "artifacts": []
}
```

## Phase Shape

```json
{
  "id": "collect",
  "type": "serial",
  "description": "Collect Runtime evidence.",
  "dependsOn": ["discover"],
  "itemSource": "inputs.targets",
  "steps": []
}
```

Allowed `type` values:

- `serial`
- `parallel`
- `pipeline`
- `barrier`
- `report`

`dependsOn` may only reference earlier phases. `itemSource` is syntax-only in the current CLI and is intended for later parallel/pipeline expansion by an external executor.

## Step Shape

```json
{
  "id": "runtime-status",
  "kind": "cli",
  "description": "Check target status.",
  "command": "runtime status --target {{target}}",
  "outputs": ["RuntimeTargetRef", "ValidationResult"]
}
```

Allowed `kind` values:

- `cli`: executed by `workflow run-cli`.
- `agent`: external AI executor; recorded but not executed by AIBridge.
- `manual`: main-agent or human decision; recorded but not executed by AIBridge.
- `barrier`: lightweight merge/check step; recorded as passed by `run-cli`.
- `report`: final reporting step; recorded as passed by `run-cli`.

Template variables use `{{name}}` or `{{inputs.name}}` and are resolved from the merged recipe defaults plus `--inputs`.

## ArtifactRef

Run artifacts are normalized into manifest `artifactRefs` and individual `artifacts/<artifactId>/artifact.json` files.

Standard kinds:

- `command-result`
- `console-log`
- `screenshot`
- `gif`
- `code-index-result`
- `runtime-status`
- `runtime-log`
- `runtime-screenshot`
- `runtime-perf`
- `runtime-handler-result`
- `patch-proposal`
- `validation-report`
- `workflow-report`

Screenshots, GIFs, and readable output files are copied into the run artifact directory when they are under the copy limit. Large files may be referenced by `sourcePath`.

## Gates

Allowed `kind` values:

- `unityCompile`
- `dotnetBuild`
- `consoleErrors`
- `testRun`
- `screenshotExists`
- `runtimeReachable`
- `runtimeErrors`
- `artifactRequired`
- `externalVerdict`

Required gates failing make the run `failed` or `blocked`. Optional gate failures make evidence visible without forcing the run to fail.

## Boundaries

- Do not use workflow recipes as a generic LLM scheduler.
- Do not imply `agent` or `manual` steps are executed by AIBridge.
- Keep parallel agents read-only unless isolated worktrees, ownership, merge, and validation gates are explicit.
- Never parallel-write Prefab, Scene, `.asset`, or `.meta` files.
