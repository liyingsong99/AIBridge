# Native Profiler files and Memory snapshots

[简体中文](NativeProfiling.md)

These commands use the current Unity Editor's native APIs independently of the official Unity CLI, Pipeline, and optional Memory Profiler package. They save existing Profiler history or capture local Editor memory evidence; they do not provide automatic analysis, Player capture, or cross-version conversion.

## Choose the correct format

| Command | Output or purpose | Boundary |
|---|---|---|
| `profiler save_data/load_data` | AIBridge JSON diagnostics | Existing behavior; not native Profiler history |
| `profiler native_capabilities` | Native API signatures and current connection | Availability does not prove nonempty history or successful IO |
| `profiler save_profile/load_profile` | Unity `.data` | Explicit confirmation, stopped recording; load replaces shared history |
| `memory capture_snapshot/snapshot_status/release_snapshot` | Unity `.snap` and persistent operation state | Local Editor, asynchronous capture; unknown is never replayed; release is local |
| `memory load_snapshot` | Reserved analysis entry | Currently returns structured `unsupported` |

A `.data` file and a `.snap` file are not interchangeable. Snapshots may contain object names, managed strings, and sensitive heap data. Assess disk space, Editor pauses, and sharing scope before confirming.

## Profiler: save and load existing history

```powershell
$CLI = './.aibridge/cli/AIBridgeCLI.exe'
& $CLI profiler native_capabilities
& $CLI profiler save_profile --confirm true
# Use the actual returned path; loading replaces shared history.
& $CLI profiler load_profile --path '.aibridge/profiler/native/<saved-file>.data' --confirm true
```

Both operations require literal `--confirm true` and native Profiler recording to be stopped. Play Mode/transitions, compilation, asset updates, Player builds, active/unknown AIBridge build or code operations, and the shared native Profiler/Memory gate can block them. Commands do not start/stop recording, clear history, change deep profiling, switch targets, or open the Profiler window.

Saving requires existing native history. Record the desired Editor data in the Unity Profiler, then stop recording and confirm the connection. `profiler start` or an AIBridge JSON sample does not establish that history exists.

Output defaults to a new `.aibridge/profiler/native/<guid>.data`. A custom path must be a direct `.data` child of that directory. Existing outputs, traversal, and reparse-point/symlink ancestors are rejected. Loading requires an existing nonempty file there, no larger than 2 GiB.

Only exact static `SaveProfile(string)` returning `void`/`bool` and `LoadProfile(string, bool)` returning `bool` are accepted. Unsupported signatures are not guessed and do not fall back to JSON. Saving uses a new temporary file, permits the native exclusive file open, then verifies identity and nonempty length before publishing without overwrite. A `void` return alone does not establish success.

Successful data includes path, size, `format=unity-profiler-data`, Unity version, current target GUID/name, and `historyProvenance=unknown`; it excludes file contents. The current connection does not prove the source of every historical frame.

Loading calls `LoadProfile(path, false)` to replace shared Editor history. Failure or an exception does not prove the previous history survived; there is no rollback. An uncertain native result may retain the gate and temporary resources as `native_outcome_unknown`. Inspect the original request and retained paths before proceeding; do not replay or substitute JSON commands. External Profiler UI and other plugins are outside AIBridge's in-process gate.

## Memory: capture a local Editor snapshot

```powershell
& $CLI memory capture_snapshot --confirm true --target editor --timeout 30000
# Save the original requestId; queued does not mean completed.
& $CLI memory snapshot_status --requestId '<original-requestId>'
# Only after inspecting an unknown operation:
& $CLI memory release_snapshot --requestId '<unknown-requestId>' --confirm true
```

### Capture conditions and cost

Capture requires literal `--confirm true`, stopped native Profiler recording, and the local Editor connection (`guid=-1`, checked as `CurrentTargetGuid == -1`). Only `--target editor` is supported; it does not switch the actual Profiler connection. Player capture is unsupported.

Memory capture supports Edit Mode and Play Mode by default, without an extra flag. Compilation, asset updates, active/unknown build or code operations, Pipeline tests/compiles, and competing native Profiler/Memory work remain blocked. Only the independent Memory Play Mode/transition check is removed: transitions may still trigger these gates or domain reload, so capture during a transition is not guaranteed. Existing Play Mode restrictions on Profiler `.data` save/load are unchanged. It probes the exact built-in `TakeSnapshot(string, Action<string,bool>, CaptureFlags)` signature across supported namespaces. Flags are fixed to `ManagedObjects | NativeObjects`; it does not explicitly request GC, screenshots, arbitrary flags, or package installation.

There is no snapshot size estimate or free-disk-space preflight. Output can reach GB size. Check available storage yourself; a capture receipt does not guarantee sufficient space. Native capture may pause the Editor, allocate additional memory, or perform internal processing. An asynchronous callback does not imply nonblocking or cancellable capture.

### Play Mode capture and domain reload risks

`memory capture_snapshot` allows Play Mode by default, without an extra flag. Stopped native Profiler recording and the local Editor connection (`CurrentTargetGuid == -1`) remain required. `--target editor` does not switch the actual connection or capture a remote/device Player. Do not infer the Profiler target from Play Mode, or allow arbitrary GUIDs. Play Mode snapshots may include live objects, account tokens, strings, and other sensitive runtime heap data; assess storage and sharing scope.

The callback belongs to the managed domain that submitted the asynchronous capture. After a domain reload or Editor restart, the new engine does not reconstruct that callback or submit capture again. Persistent active records recover as `unknown`, retain `busy`, and hold the shared `ProfilerNativeGate`. Entering/exiting Play Mode has this boundary if it triggers domain reload; not every transition necessarily reloads the domain, and recovery does not prove native work stopped.

Before capture, wait for Play Mode entry and compilation/import to finish, stop recording in the Unity Profiler, and select the local Editor. Keep the mode and connection unchanged until completion; avoid entering/exiting Play Mode or recompiling scripts during capture. For other rejections, follow the actual receipt instead of bypassing guards with `code execute`. An Edit Mode snapshot is not a Play Mode sample.

For a recovered unknown request, query `snapshot_status` using the original requestId, inspect its record and retained files, then explicitly call `release_snapshot --requestId '<original-requestId>' --confirm true` if appropriate. Release frees only that request's local reservation. It does not cancel native work or establish completion; other busy/unknown operations or corrupt journals can still block the gate. Never automatically resubmit capture or delete journals to bypass it.

### Output and asynchronous state

Output defaults to `.aibridge/memory/<guid>.snap`. Custom paths must be direct children of that directory with a 32-character lowercase hexadecimal GUID filename and `.snap` extension. Existing files, traversal, and reparse points are rejected. A separate temporary file is published without overwrite only after a matching successful callback and nonempty file validation.

The original requestId, operationId, project, Editor PID/generation, target, and paths are persisted under `.aibridge/memory-operations/` before submission. A requestId is never replayed. Corrupt/unreadable journals and unresolved busy records block new captures rather than being interpreted as idle.

| State | Meaning |
|---|---|
| `queued` | Intent registered; native invocation is not yet established |
| `capturing` | Native capture submitted; awaiting callback/publication |
| `completed` | Matching successful callback, nonempty file, and state publication; `result=true`, `busy=false` |
| `failed` | Confirmed failure; not a successful output |
| `unknown` | Timeout, reload, or identity/path/persistence uncertainty; busy is retained and replay is prohibited |

Native callbacks only enqueue results; Editor update validates and finalizes once. Stale/duplicate callbacks cannot replace another request. Outer status-query success is not capture success: inspect `status/result/busy`. Status uses the Editor command queue and may not respond immediately while native capture blocks the main thread.

### Timeout, late results, and release

`--timeout` accepts 1000–60000 ms and limits local waiting only. It does not cancel capture. The CLI capture builder passes 30000 ms by default, while the generic transport default is 5000 ms; explicitly supplying `--timeout 30000` fixes both budgets. Direct Editor requests without timeout do not inherit the CLI default.

A timeout can retain unknown/busy state, the original callback, and temporary files. A late callback may still complete the original request within the same generation before release. Query that request rather than capturing again. Reload/restart does not reconstruct callbacks or replay native work.

Release accepts only the exact registered unknown requestId and `--confirm true`, not an active queued/capturing or completed record. It sets the local released state with `nativeCancellationConfirmed=false`; it neither stops native work, deletes/rolls back files, nor turns unknown into success. Late callbacks after release cannot publish completion for that request.

### Temporary code references and snapshot analysis

Native `code execute` collects references from currently loaded AppDomain assemblies with file paths. It does not resolve arbitrary `UnityEngine.*Module` assemblies from namespace names. Check that the required assembly is loaded; if reflection is necessary, use the actual fully qualified type name. This is a temporary compiler reference limitation, not a rule that Unity APIs can only be called through reflection.

`memory load_snapshot` currently returns structured `unsupported`, including when `com.unity.memoryprofiler` is installed: no package/version-specific public analysis-loading API has been verified. AIBridge does not implement internal reflection loading, install a package, guess OpenSnapshot methods, open arbitrary UI, or parse `.snap` contents. Use the Unity Memory Profiler window manually to analyze a compatible snapshot. A captured file does not mean AIBridge analyzed it.

## Existing validation and coverage limits

On 2026-11-05, Unity 6000.0.51f1 saved and loaded a native `.data` file of 3,343,124 bytes. Earlier empty-history and file-sharing failures were not reported as success. A Memory request also reached `completed/result=true/busy=false` with a 1,559,242,224-byte output; this is one observed size, not a limit.

On 2026-11-06, Unity 6000.6.0a7 also completed a real Play Mode capture. Editor state before/after capture was `isPlaying=true`; the Profiler remained `currentTargetGuid=-1/currentTargetName=Editor/recording=false`, so the local Editor target check was left unchanged. Request `cmd_1793951476374_10e18a6dac8243`, operationId `fa6717c6aea84d468f9c21e077bf8d31`, reached `completed/result=true/busy=false/released=false`. Published `.aibridge/memory/d3bfd2b094c3464595f43fa0b956f682.snap` was 992,468,501 bytes, matching sizeBytes, and the temporary file was removed. Evidence is under the test project's `Logs/memory-playmode-20261106-153422/`: capture-receipt.json, capture-status.json, capture-file-verification.json, and Editor/Profiler state receipts. Package metadata at capture time was still 1.6.0 plus the current patch; it was subsequently synchronized to the main repository's 1.6.1 metadata. This is not presented as testing an already released unmodified 1.6.1 package.

The new real Play Mode regression failed against the old guard and passed after removing that guard (red-test.xml/green-test.xml). The associated Memory/Profiler fixtures produced 115 cases: 113 passed, 0 failed, and 2 skipped because the host lacks a symbolic-link API (fixtures.xml). AIBridge's test-status query was lost across domain reload, so its CLI receipt does not establish a pass; the native Unity XML provides the result. Actual Unity compilation returned errorCount=0 and warningCount=0.

Real `.data` save/load evidence still covers Unity 6000.0 only; `.snap` has the additional Unity 6000.6 Play Mode evidence above. Unity 2019.4 native capture has not been verified. Previous core compilation on Unity 2019.4, 6000.0, and 6000.6 does not establish this patch's cross-version native capture, interoperability, or other-platform acceptance. Empty-project fixtures and missing test assemblies leave coverage gaps; zero tests is not a pass. Consult [Unity CLI testing](UnityCliTesting.md) and the project's implementation evidence for current results.
