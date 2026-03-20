---
description: "Author AIBridge build and packaging flows for version bump, resource preparation, and artifact verification."
---

# AIBridge Build Flow Skill

## Best For

- Version bump workflows
- Resource or Addressables preparation
- Android and iOS build orchestration
- Artifact verification after packaging

## Rules

- Treat compile, reload, and target-switch operations as checkpoint boundaries
- Prefer coarse-grained `JOB build.*` and `JOB version.*` handlers
- Add explicit `WAIT` after compilation or long-running build steps
- End with artifact verification such as `VERIFY FILE_EXISTS`
- Use `version.bump` before `build.android` when the flow must set exact bundleVersion / Android versionCode values deterministically
- Use `android.preflight` before `build.android` when the flow should fail fast on target/scenes/output misconfiguration
- Use `ios.preflight` before `build.ios` when the flow should fail fast on target/scenes/bundle-identifier/output misconfiguration
- In the current MVP, `build.android` requires the Unity active build target to already be Android
- In the current MVP, `build.ios` requires the Unity active build target to already be iOS and exports an Xcode project directory
- Persisted workflow jobs are inspectable in the current MVP, not resumable
- `version.bump` changes project PlayerSettings persistently; it is not a temporary per-build override
- `android.preflight` is a snapshot validator, not a guarantee of later build success, and it does not check SDK/JDK/NDK/signing environment issues
- `ios.preflight` is a snapshot validator, not a guarantee of later build success, and it does not validate signing/provisioning/archive/export steps
- `build.ios` expects a missing or empty export directory; reusing a non-empty folder is treated as unsafe in the MVP
- `version.bump` currently does not expose an iOS-specific build number setter; only bundleVersion is shared with iOS

## Example

```txt
FLOW android_package

VAR output_apk = "Builds/Android/app.apk"
VAR version_name = "1.2.3"
VAR version_code = 123

STEP bump_version JOB version.bump --bundleVersion "${version_name}" --androidVersionCode ${version_code}
ASSERT last.success == true

STEP preflight JOB android.preflight --outputPath "${output_apk}"
ASSERT last.success == true

STEP build_apk JOB build.android --outputPath "${output_apk}"
WAIT build_done JOB build.android
  UNTIL $.data.status == "success"
  FAIL_IF $.data.status == "failed"
  POLL 2000
  TIMEOUT 1800000

VERIFY FILE_EXISTS "${output_apk}"
END
```

## Preferred Outputs

- Artifact paths
- Version/build metadata
- Preflight validation details
- Build duration summary
- Explicit failure messages

## Minimal iOS Export Example

```txt
FLOW ios_export

VAR output_dir = "Builds/iOS/XcodeProject"

STEP preflight JOB ios.preflight --outputPath "${output_dir}"
ASSERT last.success == true

STEP export_xcode JOB build.ios --outputPath "${output_dir}"
WAIT export_done JOB build.ios
  UNTIL $.data.status == "success"
  FAIL_IF $.data.status == "failed"
  POLL 2000
  TIMEOUT 1800000

VERIFY DIR_EXISTS "${output_dir}"
END
```
