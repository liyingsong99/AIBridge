# Workflow CLI Reference

On-demand command list for AIBridge workflow runs. Not default Preflight. Project-side capabilities stay in Root Rule / preferences; `$CLI harness status` is explicit diagnosis only

## Commands

```bash
$CLI workflow list
$CLI workflow validate --recipe runtime-target-sweep
$CLI workflow plan --recipe runtime-debug-investigation --format markdown
$CLI workflow plan --recipe runtime-ui-validation --format markdown
$CLI workflow init --recipe runtime-ui-validation
$CLI workflow begin --recipe unity-change-implementation
$CLI workflow run-cli --file ".aibridge/workflows/recipes/runtime-target-sweep.aibridge-workflow.json" --inputs ".aibridge/workflows/inputs.json"
$CLI workflow run-cli --recipe performance-hotspot-investigation --inputs ".aibridge/workflows/perf-inputs.json" --timeout 30000
$CLI workflow run-cli --recipe unity-sharded-review --allow-partial true
$CLI workflow run-cli --recipe unity-sharded-review --resume <runId> --rerun failed
$CLI harness status
$CLI get_logs --logType Error --workflow-run <runId>
$CLI runtime screenshot --target latest --workflow-run <runId>
$CLI workflow import --run <runId> --step adversarial-verify --schema Verdict --file verdicts.json
$CLI workflow import --run <runId> --step collect-evidence --schema EvidenceRef --kind evidence --file evidence-refs.json
$CLI workflow export --recipe runtime-ui-validation --target codex-task-pack --output .aibridge/workflows/exports
$CLI workflow status --run <runId>
$CLI workflow report --run <runId> --format markdown
$CLI workflow finish --run <runId> --status passed
$CLI workflow clean --older-than 30d --dry-run true
$CLI workflow clean --older-than 3d --dry-run false --keep-failed true --keep-latest 20
$CLI workflow clean --older-than 3d --save-settings true --auto-clean true
```

## Semantics

`run-cli` executes only deterministic `cli`, `barrier`, and `report` steps. It records `agent` and `manual` as `skipped_requires_external_executor`. `workflow run-cli --resume <runId>` still requires `--file` or `--recipe`. `partial` is not success unless `--allow-partial true`

`begin` creates a run and writes `.aibridge/workflows/active-run.json`. Ordinary commands attach evidence via `--workflow-run`, `AIBRIDGE_WORKFLOW_RUN_ID`, or active run. `workflow status` / `report` need explicit `--run <runId>`. `finish` refreshes gates/report and clears active run; `finish --status passed` is downgraded when required gates fail or evidence is missing

`import` copies structured external results. `Verdict.status` must be `confirmed`/`refuted`/`uncertain`; `externalVerdict` gates pass only from imported Verdict artifacts. `ValidationResult` imports use `validation-report` by default

For resumed work, run `workflow status --run <runId>` before adding evidence. Default handoff keeps `runDirectory`, `manifestPath`, `reportPath`, artifact ids, gate summaries, and gaps as refs; do not read full manifests for routine status

`export` compiles a recipe into an external task package (`codex-task-pack`, `generic-cli`, `claude-workflow`); exporters do not run agents

`workflow clean` is explicit maintenance (`dry-run=true` by default). Do not suggest it for routine upkeep
