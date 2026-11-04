# Optional Unity CLI and Pipeline

Load for Unity CLI setup, diagnostics, or an explicit official-tool task; never as general AIBridge preflight

## Scope

- `$CLI` is the bundled AIBridgeCLI, not official `unity` or Editor `Unity.exe`
- Official Unity CLI / `com.unity.pipeline` are optional; native AIBridge remains usable without them
- Standalone CLI environment management and Pipeline live Editor control are separate capabilities; Pipeline requires Unity 6.0 LTS+ and a compatible package
- Current AIBridge integration is read-only CLI diagnostics plus an isolated Pipeline adapter; `compile unity`, `test run`, `code execute` and Runtime commands keep their native route

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
