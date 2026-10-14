# Fan-control qualification record

Copy this template for each exact GPU board, VBIOS, driver, and operating-system combination. A successful low-load manual/automatic cycle is not a substitute for the failure tests below.

## Test identity

| Field | Value |
| --- | --- |
| Date and tester | |
| Application version and commit | |
| Windows version/build | |
| NVIDIA driver | |
| GPU marketing name | |
| GPU UUID (redact in public reports if needed) | |
| PCI device/subsystem ID | |
| VBIOS | |
| Reported fan channels | |
| Independent monitor used | |

## Preconditions

- [ ] No automatic fan write occurred and the guarded manual session was started deliberately.
- [ ] GPU was idle and every channel initially reported NVIDIA automatic policy.
- [ ] An independent temperature and fan monitor was visible.
- [ ] A recovery/reboot path was available and no unattended workload was running.
- [ ] Audit log and ownership-journal locations were recorded.

## Functional and failure matrix

For each test, record the observed policy, target/current duty, temperature, helper state, ownership-journal state, audit event, and recovery time.

| Test | Expected outcome | Pass/fail | Evidence/notes |
| --- | --- | --- | --- |
| Low-load manual then automatic | All channels verify the requested duty, then verify automatic policy | | |
| Lease/heartbeat expiry | Helper restores every channel to automatic within the documented timeout | | |
| Dashboard host termination | Independent helper restores automatic policy after its lease expires | | |
| Elevated helper termination | Next controlled start detects the journal and restores before accepting manual control | | |
| IPC disconnect or malformed command | Manual control is rejected or restored; no channel remains orphaned | | |
| Partial multi-channel write | Successful earlier writes are rolled back to automatic | | |
| Sensor unavailable/implausible | Manual operation stops and automatic policy is restored | | |
| Controlled thermal ramp | Emergency ceiling and rise-rate rules engage without exceeding device limits | | |
| Sleep and resume | Automatic policy is restored or the feature fails closed | | |
| NVIDIA driver reset/upgrade | Automatic policy is restored or recovery is clearly reported | | |
| Application restart with stale journal | Restore occurs before a new manual session | | |
| System reboot after manual session | NVIDIA/firmware automatic policy is confirmed after boot | | |

## Result

- Qualification status: `pass`, `fail`, or `incomplete`
- Scope approved for publication:
- Known limitations:
- Evidence attachments:
- Reviewer:

Do not mark a hardware combination qualified while any destructive or thermal test is incomplete. Publish failures and unsupported behavior in the compatibility matrix without exposing private machine identifiers.
