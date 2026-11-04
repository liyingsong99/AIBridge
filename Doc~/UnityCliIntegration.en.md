# Optional Unity CLI enhancement

English | [简体中文](UnityCliIntegration.md)

## Purpose and compatibility

The optional integration provides official environment diagnostics, command discovery and a path toward project-specific Pipeline commands. AIBridge keeps its project-local entry point and native transport. Existing features remain available without the official CLI or `com.unity.pipeline`.

The core package supports Unity 2019.4+. Official Pipeline live Editor control requires Unity 6.0 LTS or newer and a compatible `com.unity.pipeline`. Detect standalone environment-management capabilities separately from live Editor control. Meeting the Editor version requirement does not prove that a particular command is supported: CLI/Pipeline versions, target identity, parameter schema and result semantics also matter.

The official CLI and Pipeline are experimental. Diagnostics do not automatically download tools, upgrade Unity, install Pipeline, switch accounts or change project dependencies. Those changes require an explicit user decision.

## Install project dependencies from the Editor

Click `AIBridge/Install Unity CLI Dependencies` to install `com.unity.pipeline` through Unity Package Manager. This installs the CLI's project dependency; install the standalone `unity` executable separately using the official guide.

- Unity 6.0 LTS+: query UPM for the latest version compatible with the current Editor and install that exact version, which may be experimental. Stop with an explanation if no compatible version is available.
- An existing Pipeline package is preserved without an automatic upgrade or downgrade. Older Editors receive an unsupported-version message; native AIBridge remains available.
- Installation is asynchronous and duplicate submissions are prevented. After package installation triggers domain reload, verify the actual installed version. Unknown results do not trigger automatic reinstallation.
- Package installation does not establish standalone CLI availability or Pipeline service readiness. Allow import/compilation to finish, then check connectivity with `unity command --project-path . --format json editor status`.

## Configuration and invocation

Configure the optional integration in `.aibridge/unity-cli.json` at the project root:

```json
{
  "schemaVersion": 1,
  "backend": "native",
  "executablePath": "",
  "probeTimeoutMs": 5000
}
```

`backend` accepts `native`, `auto` and `unity-cli`. The default, `native`, preserves the existing transport. In this iteration, `auto` affects diagnostics and capability reporting only; every existing business command keeps its native route. `executablePath` identifies the local official CLI. `probeTimeoutMs` bounds diagnostic waiting and does not prove that an official task has stopped. Invalid configuration must produce a diagnostic, not an enabled or successful state.

Run the project-local AIBridge CLI from the project root:

```powershell
$CLI = "./.aibridge/cli/AIBridgeCLI.exe"
& $CLI unity_cli version --raw
& $CLI unity_cli capabilities --raw
& $CLI unity_cli doctor --raw
& $CLI pipeline capabilities --raw
& $CLI unity_cli help
```

`version` invokes official `unity --version`. `doctor` / `capabilities` perform read-only version, official help and Pipeline-help probes and return structured diagnostics. The root `available` is `true` only when the version output identifies Unity CLI and the requested capability probe succeeds. `pipeline capabilities` checks the availability of official `unity pipeline --help`; it does not prove Editor readiness or business-command schema compatibility. `unity_cli help` returns local AIBridge command help without starting the official process.

This iteration does not forward arbitrary commands. `--unity-cli-path` / `--backend` override configuration; the environment equivalents are `AIBRIDGE_UNITY_CLI_PATH` / `AIBRIDGE_UNITY_CLI_BACKEND`. `--config` selects a configuration file. `--timeout` bounds each read-only probe in milliseconds (1–60000). Machine output defaults to JSON. Missing CLI returns `CLI_NOT_FOUND`; invalid configuration or unsupported options return `INVALID_CONFIGURATION`. Neither establishes an available enhancement.

## Capability detection

Read relevant diagnostics and command schemas only when the task needs them. Avoid a full scan before every AIBridge command.

| Layer | Required evidence | When unavailable |
|---|---|---|
| Configuration | Integration policy and valid executable configuration | Keep native AIBridge; do not silently enable the enhancement |
| CLI | Actual executable, version and successful process startup | Report missing tools or startup failure; do not install them |
| Project | Canonical project path, Editor and Pipeline versions | Do not reuse another project's capability result |
| Instance | Project/instance identity, connectivity and readiness | Connectivity alone does not establish readiness |
| Command | Required command, parameter schema and matching result semantics | Do not automatically use an unverified mapping |

The standalone CLI command list differs from the connected Editor command list. Discovery proves existence, not complete AIBridge support. Load only schemas needed for the current task; do not expand the RootRule with the entire official catalog.

When an assistant integration is selected, the installer also synchronizes the primary Skill's `references/unity-cli-reference.md` into the target Skill root. It skips `.meta` files and preserves generated command references and user files when updating the reference.

## Fallback and result boundaries

- Native AIBridge remains the compatibility path when the enhancement is disabled, missing or unsupported.
- An explicit request for an official capability must report why it is unavailable; a native result must not be presented as an official success.
- Fix the backend and target when an operation starts. After submission, timeout, connection loss or unknown results, query the original operation instead of switching backends or replaying a mutation.
- Exit code 0, HTTP 200 or a successful outer envelope alone does not establish business success. Preserve nested failures, missing terminal states and missing required counts as failures or unknown results.
- A cancellation request is not confirmed cancellation. Official script code may continue after timeout; timeout does not mean that nothing executed.
- Unity validation remains `$CLI compile unity`. `compile dotnet` is an additional check, never a replacement or fallback for Unity compilation.
- Official execution must preserve AIBridge script switches, allowed paths, target authorization and evidence requirements.

## Current scope and future capabilities

This iteration prioritizes read-only diagnostics/discovery and an isolated optional Editor Pipeline adapter. Its assembly uses the `AIBRIDGE_HAS_UNITY_PIPELINE` macro through `versionDefines` and `defineConstraints`; the adapter is not compiled when the package is absent. Runtime detection still checks package version, Editor support, descriptor and service state separately. The macro does not establish service availability or future Pipeline API compatibility. It does not imply automatic official routing for `compile unity`, `code execute`, `test run` or Runtime commands.

Script execution, compilation, PlayMode tests, builds, Method Reload and generic forwarding require separate validation before release. Native AIBridge `test run` must still start in Edit Mode. Official PlayMode test support does not automatically extend that entry point. The official local Player channel does not replace AIBridge's mobile/LAN Runtime Bridge.

## Diagnostics and sensitive information

Distinguish disabled integration, missing CLI, process failure, missing/incompatible packages, unreachable Editor, non-ready Editor and unsupported command/schema. Network sign-in, Editor licensing and local connectivity are separate conditions; cloud login is not a universal prerequisite for each local operation.

Diagnostic evidence may include versions, command names, canonical project paths, exit codes and sanitized errors. Do not copy bearer tokens, authentication data or raw Pipeline descriptors into shared logs, reports or workflow artifacts. Direct HTTP integration needs its own descriptor refresh, loopback endpoint, identity and token checks; it is not a normal setup step for this iteration.

## Validation status

On 2026-11-04, Unity CLI 1.0.0-beta.16 and Pipeline 0.10.0-exp.1 were installed on Windows with Unity 6000.0.51f1. Official `command --project-path <project> editor status` returned ready and AIBridge CLI capability probes passed. Real installation corrected the `pipeline|pipe` help alias, asmdef package-presence detection (empty versionDefines expression), and the descriptor's `evalToken` field. The macro does not guarantee API compatibility; descriptor presence alone does not prove connectivity. See the [test matrix](UnityCliTesting.md); other Editor versions and business-command delegation remain unverified.
