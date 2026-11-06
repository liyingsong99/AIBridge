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

Arbitrary commands are not forwarded. `code execute` uses the native compiler by default. Explicit `code execute --backend pipeline` invokes verified Pipeline eval through existing Editor authorization, source validation, request auditing, single-flight and persisted operation state. Execution requires both `--dry-run false --confirm true`; without per-request confirmation it is rejected before submission. `.csx` is sent as inline source, and internals/auto-import stay disabled. Timeout/unknown outcomes must not be replayed or switched to another backend; cancel ends local waiting only. Restricted `code run` now invokes Pipeline `script run`; hotpatch and arbitrary Pipeline forwarding remain unavailable. Code's `backend=native|pipeline` is separate from the diagnostic commands' `backend=native|auto|unity-cli`. `--unity-cli-path` / `--backend` override configuration; the environment equivalents are `AIBRIDGE_UNITY_CLI_PATH` / `AIBRIDGE_UNITY_CLI_BACKEND`. `--config` selects a configuration file. `--timeout` bounds each read-only probe in milliseconds (1–60000). Machine output defaults to JSON. Missing CLI returns `CLI_NOT_FOUND`; invalid configuration or unsupported options return `INVALID_CONFIGURATION`. Neither establishes an available enhancement.

## Controlled Pipeline eval

```powershell
& $CLI code execute --backend pipeline --dry-run true --file .aibridge/code/check.csx --timeout 15000
& $CLI code execute --backend pipeline --dry-run false --confirm true --code 'throw new System.InvalidOperationException("controlled test")' --timeout 15000
& $CLI code status --requestId <original-request-id>
& $CLI code cancel --requestId <original-request-id>
```

Pipeline defaults to `dry-run=true`; native defaults to `dry-run=false`. Pipeline execution requires `--dry-run false --confirm true`. Both flags accept boolean values only. The command route and optional adapter entry both check project authorization and per-request confirmation.

The runtime version gate uses only the UPM registered/loaded package `version` and `resolvedPath`; a stale directory in `Library/PackageCache` cannot authorize a request. Only the verified Pipeline `0.10.0-exp.1` protocol is supported. Missing packages, incompatible versions or a mismatched current Editor descriptor fail explicitly. AIBridge reads `.cs`/`.csx` files within its restricted code directory into an inline source snapshot, preserving usings, comments and original line positions. Pipeline uses Roslyn script semantics, which differ from the native method-body wrapper. Requests disable internals and auto-import; dry-run compiles only, while confirmed execution runs the inline eval. HTTP uses literal loopback and a fixed descriptor PID/project/version/start identity and token, with redirects and retries disabled.

Results include `backend=pipeline`, `operationId/requestId`, source SHA-256, target identity, `status`, `httpStatus`, execution value/output and diagnostics. Upstream `id` becomes `code`; coordinates are converted to one-based values and missing positions remain unknown. Compile-only validation requires both success layers and this version's exact receipt. Execution success requires both success layers and complete result fields; recognized runtime/compile errors are terminal, while malformed, timeout or transport-unknown outcomes remain unknown. HTTP 200 alone is insufficient. Records live in `.aibridge/code-operations/`; disk reads preserve structured `resultValue`, arrays, explicit null object members and `executionTimeMs`. `output` only passes through the upstream field; the verified version returns null on success and does not promise Console/Debug log capture. Conflicting error codes/HTTP outcomes, invalid field types, duplicate keys or excessive JSON nesting remain unknown. Redaction covers decoded keys and values; key collisions reject the result. Records contain no separately captured source or tokens; source deliberately returned by user code remains user output.

Timeout ends waiting only and retains single-flight. A late result is available through `code status --requestId` without overwriting an already returned timeout result. `code cancel` cancels waiting, does not confirm remote cancellation, and retains the lock until the Task ends. Transport uncertainty or reload/restart preserves `unknown` and a local lock; explicit `code cancel` releases that lock without proving that the remote operation stopped. Never replay the same request ID or switch backends automatically. State updates require atomic replacement and preserve previous records on failure. A missing `latest` index triggers validation of per-request journals and recovery of busy state; corrupt or contradictory state does not become idle.

## Restricted Pipeline run

```powershell
& $CLI code run --file .aibridge/code/task.cs --entry Tools.Task.Run --args '[42,"label"]' --timeout 15000
& $CLI code run --file .aibridge/code/task.cs --entry Tools.Task.Run --args '[42,"label"]' --dry-run false --confirm true --timeout 15000
& $CLI code status --requestId <original-request-id>
```

Requires verified Pipeline `0.10.0-exp.1`, fixed `ephemeral` mode. Inline code, `.csx`, subdirectories, linked/reparse paths, automatic entry discovery, hotpatch, references/defines and arbitrary commands are rejected. Source must be a complete C# type file directly under `.aibridge/code`, up to 512 KiB, with no eval statement wrapper. Specify a static `Type.Method` / `Namespace.Type.Method` entry; fully qualified types without overloads are recommended. `args` is one JSON array, up to 32768 characters and depth 16 including its wrapper, preserving object null members. An array parameter needs a nested array. Entry resolution/coercion follows upstream semantics; direct ValueTask/Awaitable waiting is not promised.

Default compile-only **checks compilation, not entry existence or argument compatibility**. Execution requires both project authorizations and `--dry-run false --confirm true`. Loaded assemblies cannot be individually unloaded. Run has its own strict parser: HTTP 200 with outer success can still contain an inner compilation, entry/argument or runtime failure. Internal `Timeout`, HTTP 504, unknown failures and conflicting fields remain unknown; never replay.

Captured source is written once to `.aibridge/tmp/pipeline-run/<random-id>.cs` with original-path `#line` mapping. On Windows a read-sharing lock and content recheck protect it while Pipeline reads the internal snapshot rather than the mutable user file. Confirmed outcomes clean only that snapshot; unknown outcomes retain source and the current-process handle even after local cancel. Reload/process exit loses the file lock and does not establish remote termination or later immutability; retain unknown and investigate manually. This path gate is not a C# sandbox. Operation journals store origin, source/args SHA-256, entry, target and results, without a separate source or argument copy.

## Explicit detached run jobs

```powershell
& $CLI code run --file .aibridge/code/task.cs --entry Tools.Task.Run --args '[42]' --dry-run false --job true --confirm true --timeout 5000
& $CLI code status --requestId <original-request-id>
& $CLI code job_list --limit 20
& $CLI code job_wait --requestId <original-request-id> --timeout 10000
& $CLI code job_cancel --requestId <original-request-id> --confirm true
```

`code run` defaults to `job=false`, preserving its execution timeout. Explicit `--job true` uses fixed detached submission: **upstream execution has no timeout**; `timeout` limits local waiting only (1000–60000 ms). Every detached submission, including compile-only, requires `--confirm true`; execution also requires `--dry-run false`. `code execute` rejects the job parameter.

Intent is atomically journaled before submission; the returned `remoteJobId` is bound to the original request. Journals do not store source, raw arguments or tokens. The session fixes the initial PID/port/project/version/startedAt, in-memory token and adapter generation. External IDs, redirects, proxies, POST retries and backend fallback are rejected. Responses require strict parsing/redaction and separate job envelope and run business validation. `jobState=completed` can yield `status=failed`. A bare job has no execution duration, so `executionTimeMs` remains unknown rather than 0.

After the original CLI wait expires, the Editor continues serial observations every 500 ms. Late results update the journal without replacing an already delivered receipt. `code status` returns the latest observation/journal; `job_list` lists local registrations only (at most4096 files scanned, no remote enumeration). `job_wait` observes the original record for a bounded interval and sends no execution or cancellation. `code cancel` still releases local waiting after pending IO completes; unknown snapshot locks remain, and automatic polling for that released job stops.

`job_cancel` requires a new explicit confirmation and controls only an active registered request in the current Editor generation. Its receipt separates `requestAccepted`, `executionEnded`, `notStarted` and `sideEffectsUnknown`. HTTP200 or cancellationRequested does not establish termination. Cooperative cancellation can become canceled after all side effects occurred; it is not rollback, and child tasks can continue. Terminal records do not trigger another cancellation POST.

404/401, transport failure, identity changes and malformed responses retain unknown/single-flight without replay. State-write failures retain the lock without repeating POSTs. Upstream jobs live in memory only (last100 terminal jobs/one hour); reload/restart never reconstructs the session. The original ID remains an audit reference; recovery stays unknown until explicit local cancel. An unchanged token does not prove job continuity. Ordinary eval/run unknowns without a jobId cannot be recovered through job queries. Snapshot locks retain the reload/process-exit boundary described above.

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

When an assistant integration is selected, the installer also synchronizes the primary Skill's `references/unity-cli-reference.md` into the target Skill root. It skips `.meta` files and preserves generated command references and user files when updating the reference. Package snapshots bearing the generated marker are excluded from template copying and regenerated for the current Editor version. Reinstallation preserves timestamps when content is unchanged instead of repeatedly overwriting object-ID parameter names.

Pipeline submission checks persisted local state before probing the optional adapter. A missing Pipeline package must not hide corrupt records or unresolved busy placeholders. Restart recovery may write a failure receipt to the original request without resubmitting its task; an existing result file alone does not prove replay.

## Fallback and result boundaries

- Native AIBridge remains the compatibility path when the enhancement is disabled, missing or unsupported.
- An explicit request for an official capability must report why it is unavailable; a native result must not be presented as an official success.
- Fix the backend and target when an operation starts. After submission, timeout, connection loss or unknown results, query the original operation instead of switching backends or replaying a mutation.
- Exit code 0, HTTP 200 or a successful outer envelope alone does not establish business success. Preserve nested failures, missing terminal states and missing required counts as failures or unknown results.
- A cancellation request is not confirmed cancellation. Official script code may continue after timeout; timeout does not mean that nothing executed.
- Unity validation remains `$CLI compile unity`. `compile dotnet` is an additional check, never a replacement or fallback for Unity compilation.
- Official execution must preserve AIBridge script switches, allowed paths, target authorization and evidence requirements.

## Current scope and future capabilities

This iteration prioritizes read-only diagnostics/discovery and an isolated optional Editor Pipeline adapter. Its assembly uses the `AIBRIDGE_HAS_UNITY_PIPELINE` macro through `versionDefines` and `defineConstraints`; the adapter is not compiled when the package is absent. Runtime detection checks package version, Editor support and descriptor identity (PID, project path and Unity version). `serverReady` becomes true only after a background bearer-auth `/api/status` probe reports `ready`; this Editor's identity is separately checked against the descriptor PID, project path and Unity version. The probe does not block the Editor main thread or expose the token. Neither the macro nor descriptor proves arbitrary Pipeline API compatibility. It does not imply automatic official routing for `compile unity`, `code execute`, `test run` or Runtime commands.

Script execution, compilation, PlayMode tests, builds, Method Reload and generic forwarding require separate validation before release. Native AIBridge `test run` must still start in Edit Mode. Official PlayMode test support does not automatically extend that entry point. The official local Player channel does not replace AIBridge's mobile/LAN Runtime Bridge.

## Diagnostics and sensitive information

Distinguish disabled integration, missing CLI, process failure, missing/incompatible packages, unreachable Editor, non-ready Editor and unsupported command/schema. Network sign-in, Editor licensing and local connectivity are separate conditions; cloud login is not a universal prerequisite for each local operation.

Diagnostic evidence may include versions, command names, canonical project paths, exit codes and sanitized errors. Do not copy bearer tokens, authentication data or raw Pipeline descriptors into shared logs, reports or workflow artifacts. Direct HTTP integration needs its own descriptor refresh, loopback endpoint, identity and token checks; it is not a normal setup step for this iteration.

## Validation status

On 2026-11-04, Unity CLI 1.0.0-beta.16 and Pipeline 0.10.0-exp.1 were installed on Windows with Unity 6000.0.51f1. Official `command --project-path <project> editor status` returned ready and AIBridge CLI capability probes passed. Real installation corrected the `pipeline|pipe` help alias, asmdef package-presence detection (empty versionDefines expression), and the descriptor's `evalToken` field. The macro does not guarantee API compatibility; descriptor presence alone does not prove connectivity. See the [test matrix](UnityCliTesting.md); other Editor versions and business-command delegation remain unverified.
