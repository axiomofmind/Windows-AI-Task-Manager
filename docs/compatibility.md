# Compatibility and qualification status

## Supported release profile

- Windows x64 desktop application.
- Windows 10/11 versions supported by the bundled .NET 10 runtime.
- NVIDIA driver with a compatible driver-installed NVML library.
- Microsoft Edge WebView2 Evergreen Runtime.
- Standard-user dashboard with per-action Windows administrator approval for hardware writes.

Missing NVIDIA hardware or unsupported NVML fields must reduce functionality rather than prevent the application from starting.

## Tested development system

| Component | Observed configuration |
| --- | --- |
| Workstation | Lenovo ThinkStation P8, model 30HH002QUS |
| Operating system | Windows 11 Pro, build 26200 |
| NVIDIA driver | 616.92 |
| GPU 0 | RTX PRO 6000 Blackwell Max-Q Workstation Edition, VBIOS 98.02.6A.00.03 |
| GPU 1 | RTX PRO 5000 Blackwell, VBIOS 98.02.92.00.01 |
| GPU 2 | RTX PRO 4500 Blackwell, VBIOS 98.03.A2.00.01 |
| Runtime adapter | llama.cpp with automatic local process discovery and explicit endpoint fallback |

Read-only telemetry was verified on all three GPUs. One low-load manual fan enable followed by explicit restoration was verified; afterward all three devices reported automatic policy, 30% target/current duty, no active watchdog session, and no remaining elevated helper. This is a basic qualification result, not completion of the failure-injection matrix.

## Known limitations

- Manual fan control remains experimental. Its controls are visible by default, but no write occurs until the user deliberately starts an elevated, watchdog-protected session.
- The three installed boards do not yet have separate completed crash, sleep/resume, driver-reset, thermal-ramp, and channel-mapping records.
- Per-board results should use [`fan-qualification-template.md`](fan-qualification-template.md); no board is considered fully qualified until its complete matrix is reviewed.
- NVML exposes one controllable channel on each tested GPU; this may represent more than one physical fan.
- Lenovo chassis and CPU fans remain firmware-controlled and are not exposed here.
- LM Studio, generic OpenAI-compatible runtimes, training adapters, and model inventory are scaffolded or incomplete.
- The portable alpha is unsigned and depends on an installed WebView2 Evergreen Runtime.
