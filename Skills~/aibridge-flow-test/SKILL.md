---
description: "Author AIBridge automation test flows for PlayMode and editor verification with explicit waiting and teardown."
---

# AIBridge Test Flow Skill

## Best For

- PlayMode smoke tests
- Editor automation checks
- Log-driven validation
- Screenshot or GIF capture as part of verification

## Rules

- Prefer dedicated `JOB test.*` handlers over fragile UI-driving flows
- Always poll test status with `WAIT`
- Always stop PlayMode in teardown when the flow started it
- Capture logs or screenshots on failure-prone flows when useful

## Example

```txt
FLOW playmode_smoke_test

STEP enter_play UNITY editor play
ASSERT last.success == true

STEP start_test JOB test.playmode_smoke --suite "Smoke"
WAIT test_done JOB test.playmode_smoke
  UNTIL $.status == "success"
  FAIL_IF $.status == "failed"
  POLL 1000
  TIMEOUT 180000

STEP exit_play UNITY editor stop
ASSERT last.success == true

END
```

## Verify Well

- Test result status
- Error logs count
- Optional screenshot path
- Explicit cleanup steps
