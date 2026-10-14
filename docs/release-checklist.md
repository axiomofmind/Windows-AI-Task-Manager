# Alpha release checklist

## Automated gates

- [ ] Clean Windows GitHub runner restores locked dependencies, formats, builds, and passes all tests.
- [x] Web lint, type checking, and production build pass locally.
- [ ] CodeQL, dependency review, NuGet vulnerability checks, npm audit, and repository hygiene checks pass.
- [x] The portable script produces a self-contained `win-x64` ZIP, CycloneDX SBOM, and SHA-256 checksum.
- [x] Artifact inspection finds no local configuration, databases, logs, dumps, models, datasets, checkpoints, credentials, certificates, or developer paths.

## Manual gates

- [ ] Extract and launch on a clean Windows 11 VM without .NET SDK or Node.js.
- [ ] Confirm the WebView2-missing recovery message on a VM without the runtime.
- [ ] Confirm no outbound connection occurs in default configuration.
- [ ] Confirm NVIDIA-absent startup remains usable.
- [ ] Review screenshots, keyboard navigation, and high-contrast behavior.
- [ ] Review `CHANGELOG.md`, compatibility matrix, known limitations, SBOM, notices, and security policy.
- [ ] Confirm the exact WebView2 license and notice are present under `licenses/` in the portable artifact.
- [ ] Verify the published checksum from a newly downloaded ZIP.

## Fan-control alpha gate

- [x] Keep every public-default hardware write deliberate, confirmation-gated, elevated, and audited.
- [x] Label the feature experimental in UI and documentation.
- [ ] Complete lease-expiry, host-crash, helper-crash, IPC-loss, sleep/resume, and driver-reset qualification or explicitly exclude fan writes from the binary.
- [ ] Keep an independent temperature monitor open during controlled thermal-ramp qualification.
- [ ] Record each board/driver/VBIOS result using `fan-qualification-template.md`.
