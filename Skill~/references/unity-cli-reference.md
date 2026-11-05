# Optional Unity CLI and Pipeline

Load for Unity CLI setup, diagnostics, or an explicit official-tool task; never as general AIBridge preflight

## Scope

- `$CLI` is the bundled AIBridgeCLI, not official `unity` or Editor `Unity.exe`
- Official Unity CLI / `com.unity.pipeline` are optional; native AIBridge remains usable without them
- Standalone CLI environment management and Pipeline live Editor control are separate capabilities; Pipeline requires Unity 6.0 LTS+ and a compatible package
- Native routes stay default. Explicit `code execute --backend pipeline` supports verified Pipeline 0.10.0-exp.1 eval: compile-only uses `--dry-run true`; real execution requires per-request `--dry-run false --confirm true`. `code run --file .aibridge/code/task.cs --entry Namespace.Type.Method --args '[...]'` uses fixed Pipeline ephemeral run, default compile-only; execution needs the same confirm flags. Hotpatch remains unsupported; compile/test use only the explicit bounded interfaces below
- Code Pipeline checks project authorization and boolean per-request flags at both command/adapter entries; restricted source paths, audit and single-flight apply; `.csx` is read into code, internals/auto-import stay disabled
- Result/status preserve nested values, explicit null and duration; output only passes through upstream (verified version returns null). Malformed/conflicting/duplicate-key/deep responses stay unknown; decoded keys and values are redacted
- `code run`: direct `.cs` files only (512KiB), explicit static entry, strict JSON array args (32768 chars/depth16); no inline/csx/reparse paths/defines/references. Dry-run verifies compilation only, not entry/args. Source snapshot under `.aibridge/tmp/pipeline-run`; unknown retains source/lock in current process, reload loses lock; never treat this as a C# sandbox
- Explicit `code run --job true --confirm true` detaches (including dry-run): upstream has no execution timeout; `timeout` bounds local waiting only. Defaults stay job=false; eval rejects job. After timeout use `code status --requestId`, bounded `code job_wait --requestId --timeout 5000`, or local `code job_list --limit 20`. `code job_cancel --requestId --confirm true` is cooperative remote cancellation for an active registered job; accepted/ended/sideEffectsUnknown are separate. `code cancel` only releases local waiting; no external jobId control, reload/session loss stays unknown, never replay.
- Query `code status --requestId <id>` after timeout; cancel ends waiting only. Unknown/reload retains a local lock until explicit cancel, never replay or switch backend. Records: `.aibridge/code-operations/`, no source or token

## Explicit compilation and tests

- `compile status --backend pipeline`: uncorrelated global observation, unavailable counts/duration stay null. Add `--requestId <id>` to read only the local trigger journal.
- `compile start --backend pipeline --confirm true --timeout 10000`: one trigger POST, focus=false. acknowledged/exit0 confirms only the response; compilationSuccess stays null and every acknowledgement keeps busy. Exact `compile release --backend pipeline --requestId <id> --confirm true` releases locally, never cancels or proves completion. Use native `compile unity` for validation.
- `test list --backend pipeline`: EditMode catalogue, not executed count. `test run --backend pipeline --pipeline-filter <text> --filter-type testName --timeout 120000`: EditMode synchronous only; testName/assembly case-insensitive substring, category case-insensitive exact. No native filter mapping/async/remote cancel.
- `test status --backend pipeline --requestId <id>` reads local journal; `test release --backend pipeline --requestId <id> --confirm true` only releases unknown locally. Only passed is success; no_tests/all skipped/inconclusive fail. timeout/reload/identity/IO uncertainty keeps unknown/busy, never replay. Both backends share native activity gates.

## AIBridge diagnostics

From the project root; Windows PowerShell example:

```powershell
$CLI = "./.aibridge/cli/AIBridgeCLI.exe"
& $CLI unity_cli version --raw
& $CLI unity_cli doctor --raw
& $CLI unity_cli capabilities --raw
& $CLI pipeline capabilities --raw
& $CLI unity_cli help
```

- `version` invokes official `unity --version`
- `doctor` / `capabilities` probe CLI identity, version, root help and Pipeline help; root `available` confirms only the requested capability
- `pipeline capabilities` confirms official `unity pipeline --help`, not Editor readiness or command-schema compatibility
- `unity_cli help` is local help; `--format text` gives a compact summary, JSON includes full diagnostics
- This surface does not forward arbitrary official commands

## Configuration

- Optional `.aibridge/unity-cli.json`: `schemaVersion=1`, `backend=native|auto|unity-cli`, `executablePath`, `probeTimeoutMs`
- Default `native`; `auto` and `unity-cli` affect diagnostics/selection reports, not existing business-command routing
- Overrides: `--unity-cli-path`, `--backend`, `--config`, `--timeout` (1..60000 ms per AIBridge probe); environment: `AIBRIDGE_UNITY_CLI_PATH`, `AIBRIDGE_UNITY_CLI_BACKEND`
- Point at the standalone CLI, never Editor `Unity.exe`; if PATH is stale, use the verified absolute CLI path
- `CLI_NOT_FOUND`, `NOT_UNITY_CLI`, `INVALID_CONFIGURATION` and failed probes are not available capabilities

## Verify an official Editor connection

Use the same verified executable as diagnostics; `"unity"` below assumes PATH resolves to it

```powershell
$UnityCLI = "unity"
& $UnityCLI command --help
& $UnityCLI command --project-path . --format json --timeout 15 editor status
```

- Confirm the returned `projectPath` matches this project and `status` is `ready`; do not reuse another open project's result
- Read the installed command's help/schema before each new operation; official timeout units are command-specific (the example uses seconds)
- Missing package: install only when requested/authorized; absent or settling server: wait for import/reload and inspect `Window/Pipeline/Start Server` before retrying a read
- Adapter compilation or descriptor presence alone does not prove connectivity; do not print raw descriptors or `evalToken`

## Installation and execution policy

- Diagnosis alone does not authorize installation, upgrades, account changes or dependency changes; an explicit install request already authorizes the scoped installation
- For authorized setup, install the standalone CLI using the official guide; `AIBridge/Install Unity CLI Dependencies` installs only the current Editor's UPM-compatible Pipeline (Unity 6+), preserving an existing package; alternatively inspect `unity pipeline install --help` for explicit project installation
- Menu installation is asynchronous; after reload or unknown results inspect installed packages before retrying, never equate package installation with CLI/server readiness
- Keep setup scoped: a CLI-only request must not silently replace Agent rules/Skills or run broad Agent setup; preserve existing AIBridge configuration
- Missing/disabled enhancement preserves native commands; explicit official requests must report unavailable capability
- After submission, timeout or unknown result, query the original operation; do not replay writes or switch backend
- Exit code 0 or a successful outer envelope does not prove business success; missing terminal state remains unknown
- Unity verification stays `compile unity`; `compile dotnet` is extra only
- Official tools must not bypass AIBridge execution controls; never store credentials or raw descriptors in shared artifacts

## Official setup references

- https://docs.unity.com/en-us/unity-cli/use-unity-cli
- https://docs.unity.com/en-us/unity-cli/unity-pipeline/unity-pipeline-package

Use installed help/package documentation when versioned behavior differs from web examples
