# Recipe Schema

Purpose: define AIBridge workflow recipe JSON used by validate/plan/init/run-cli, active-run attach, import, and export. CLI command details: `workflow-cli-reference.md`

## Locations

```text
Templates~/Workflows/<name>.aibridge-workflow.json
.aibridge/workflows/recipes/<name>.aibridge-workflow.json
.aibridge/workflows/runs/<runId>/
```

## Recipe Shape

Required: `schemaVersion` (`1`), `name` (lower kebab-case), `description`, `phases`, `gates`

Optional: `title`, `version`, `inputs`, `requiredSkills`, `artifacts`, `terminalState`, `terminalReason`, `retryBudget`, `stopWhen`, `loopIteration` (forward-compatible metadata)

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
  "requiredSkills": ["aibridge-development-workflow"],
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
  "requiredSkills": ["aibridge"],
  "releaseSkillsAfter": [],
  "steps": []
}
```

`type`: `serial` | `parallel` | `pipeline` | `barrier` | `report`. `dependsOn` may only reference earlier phases. `itemSource` is syntax-only for later expansion

## Step Shape

```json
{
  "id": "runtime-status",
  "kind": "cli",
  "description": "Check target status.",
  "command": "runtime status --target {{target}}",
  "requiredSkills": ["aibridge"],
  "releaseSkillsAfter": [],
  "outputs": ["RuntimeTargetRef", "ValidationResult"]
}
```

`kind`: `cli` (run-cli), `agent`/`manual` (external; recorded not executed), `barrier`/`report` (recorded passed by run-cli). Templates: `{{name}}` / `{{inputs.name}}`. Prefer `--inputs` as a JSON file path

## Skill Routing And Scope Metadata

`requiredSkills` / `releaseSkillsAfter` are metadata for external harnesses; CLI validates/surfaces them but does not install or unload Skills. Routing is preflight, not a recipe phase. Release at Mode Exit / phase / step handoff

Locations: recipe baseline; phase/step active Skills; phase/step `releaseSkillsAfter`. Pass compact handoff JSON across boundaries (see `evidence-schema.md` `SkillHandoff`)

## ArtifactRef

Standard kinds include: `command-result`, `command-evidence`, `console-log`, `screenshot`, `gif`, `code-index-result`, `runtime-status`, `runtime-log`, `runtime-screenshot`, `runtime-perf`, `runtime-handler-result`, `patch-proposal`, `verdict`, `finding`, `evidence`, `skill-handoff`, `validation-report`, `workflow-report`

Artifacts may include `stepId` and `schema`. Screenshots/GIFs reference cache paths by default; large files may use `sourcePath`

## Gates

Kinds: `unityCompile`, `dotnetBuild`, `consoleErrors`, `testRun`, `screenshotExists`, `runtimeReachable`, `runtimeErrors`, `artifactRequired`, `externalVerdict`, `patchProposalRequired`

Required gate failures → `failed`/`blocked`. Optional failures are visible without forcing fail. `artifactRequired` may filter by `artifactKind`/`schema`/`stepId` (`artifactKind` also matches `semanticKind`). `externalVerdict` uses `allow` such as `confirmed`; `uncertain` is an evidence gap

## External Result Schemas

Use `EvidenceRef`, `CommandEvidence`, `Finding`, `Verdict`, `PatchProposal`, `ValidationResult`, `SkillHandoff` from `evidence-schema.md`. Import examples live in `workflow-cli-reference.md`

## Boundaries

- Do not use recipes as a generic LLM scheduler
- Do not imply `agent`/`manual` are executed by AIBridge
- Adapter exports are handoff artifacts only
- Parallel agents stay read-only unless isolation/ownership/merge/gates are explicit
- Never parallel-write Prefab, Scene, `.asset`, or `.meta`
