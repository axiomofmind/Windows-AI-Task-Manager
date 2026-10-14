# Changelog

All notable changes to AI Task Manager will be documented here.

The project follows semantic versioning after its first tagged release.

## Unreleased

- Restored the dashboard window with a single left-click on the notification-area icon while preserving the right-click menu.
- Added compact per-GPU fan controls to the GPU telemetry tab, reusing the guarded watchdog session, one-time safety acknowledgement, and NVIDIA automatic-policy restoration flow.
- Added read-only NVIDIA NVML telemetry for GPU identity, VRAM, utilization, temperature, power, fans, and clocks.
- Added explicit llama.cpp endpoint monitoring with health, model, context, slot, and Windows PID correlation.
- Replaced the static cockpit GPU and workload placeholders with live visual cards.
- Added llama.cpp queue, context, slot, KV-type, prompt-rate, generation-rate, model-format, build, and modality telemetry.
- Added NVML compute and graphics process enumeration with WDDM-aware unavailable memory states.
- Added rolling GPU sparklines, queued SQLite metric samples with bounded retention cleanup, and live thermal, VRAM, freshness, and runtime alerts.
- Added device-reported NVIDIA power envelopes, audited UUID-bound power-limit actions through an on-demand elevated helper, restore-to-default, and read-only per-fan capability and policy discovery.
- Added an experimental, session-gated NVIDIA GPU fan controller with an elevated lease watchdog, device-derived emergency ramp, per-write verification, audited controls, and explicit restoration to NVIDIA automatic policy.
- Hardened fan writes with a cross-process per-GPU ownership lock, crash-recovery journal, implausible-sensor rejection, thermal rise/ceiling handling, rate limiting, multi-channel rollback, and bounded verified restoration.
- Replaced per-adjustment typed fan confirmations with a single guarded session approval; subsequent minimum-duty updates reuse the authenticated elevated watchdog while remaining audited.
- Created the Windows-first .NET 10 and React project scaffold.
- Added portable configuration defaults and public-repository safeguards.
- Added versioned per-user configuration creation, validation, and migration backups.
- Added DPAPI-backed secret storage and centralized redaction for logs and diagnostics.
- Added the SQLite schema, WAL integrity checks, database backups, and a bounded write queue.
- Added live, ready, version, summary, diagnostics, and reviewed diagnostics-export endpoints.
- Made guarded hardware controls available in public defaults while retaining deliberate confirmation, elevation, verification, audit, restoration, and a separate safety acknowledgement for experimental fan sessions.
- Added a WPF and WebView2 desktop executable with bundled UI assets, an internal dynamic loopback host, single-instance activation, tray lifecycle, and persisted window placement.
- Extracted reusable application hosting so the desktop and headless service share identical configuration, persistence, diagnostics, and API behavior.
- Added Windows portable packaging, a CycloneDX SBOM, checksums, dependency notices, repository hygiene and vulnerability gates, CodeQL/dependency-review workflows, a threat model, and a per-board fan qualification template.
