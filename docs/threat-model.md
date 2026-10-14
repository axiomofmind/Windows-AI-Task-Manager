# Security threat model

## Security objective

AI Task Manager is a single-user, loopback-only Windows desktop application. Its principal security objective is to prevent untrusted web content or local processes from turning the application into an arbitrary privileged command runner, while ensuring that a failed fan-control session returns responsibility to the NVIDIA driver.

## Trust boundaries

| Boundary | Untrusted input | Controls |
| --- | --- | --- |
| WebView/browser → loopback API | HTTP headers and JSON bodies | Loopback binding, host filtering, same-origin checks, CSP, random in-memory action token, strict contracts, idempotency keys |
| Runtime endpoints → collectors | Runtime JSON, metrics, status codes | Explicit endpoints or loopback listeners owned by allow-listed `llama-server` process names, timeouts, bounded reads, optional fields, no runtime response execution |
| Local process metadata → runtime discovery | Process names, listener ports, command lines | Exact executable-name allow-list, no general port probing, loopback requests only, recognized arguments only, raw command lines and transient API keys are never returned, logged, or persisted |
| Standard-user host → elevated helper | Pipe messages and helper arguments | Current-user-only named pipe, 256-bit session secret, exact allow-list, strict UUID/numeric parsing, short lease |
| Elevated helper → NVML | Driver results and sensor values | Documented entry points only, device-reported bounds, read-back verification, thermal plausibility checks, bounded retries |
| Filesystem → configuration and diagnostics | Paths, JSON, databases | Per-user roots, path normalization, explicit discovery roots, DPAPI secret store, centralized redaction |

## Privileged fan-control invariants

- The WPF process and WebView never run elevated.
- The helper accepts only `fan-session`, `fan-restore`, and the bounded power-limit operation.
- Fan ownership is exclusive through a named cross-process GPU lock.
- The host creates a current-user-only pipe before elevation; messages also require a random per-session secret.
- The helper records ownership atomically before its first manual write and attempts automatic restoration before reclaiming a recorded GPU.
- Missing temperature, implausible jumps, rapid rises, lease expiry, parent exit, IPC loss, write failure, or verification failure leave the loop and trigger bounded automatic-policy restoration.
- Public configuration disables all hardware writes by default.

## Residual risks accepted for alpha

- Power loss, kernel failure, driver failure, or forceful termination can prevent cleanup code from running. The ownership journal enables recovery on the next helper start but cannot execute while Windows is down.
- Absence of a known competing-controller process does not prove exclusive ownership outside AI Task Manager. Users must explicitly confirm other fan writers are disabled.
- The first alpha is unsigned. Windows may display an unknown-publisher warning; checksums must be verified.
- Hardware support is capability-driven and experimental. An NVML function being present does not constitute board qualification.

## Out of scope

The application does not write Lenovo embedded-controller, chassis, CPU, pump, or motherboard fan registers. It does not expose non-loopback access, remote administration, arbitrary scripts, DLL loading, shell execution, or automatic updates.
