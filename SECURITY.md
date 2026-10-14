# Security policy

## Reporting a vulnerability

Do not open a public issue for a vulnerability that could enable privilege escalation, arbitrary local commands, secret disclosure, unsafe GPU control, or remote access. Use the [repository Security tab](https://github.com/axiomofmind/Windows-AI-Task-Manager/security) to submit a private vulnerability report. If private reporting has not yet been enabled, contact the maintainer through the [axiomofmind GitHub profile](https://github.com/axiomofmind) without including exploit details in a public message.

## Supported versions

No version is supported yet. Security support begins with the first tagged prerelease.

## Security defaults

- The HTTP service binds to loopback only.
- Hardware-write actions are disabled by default.
- Prompt and response bodies are not collected.
- Outbound telemetry is disabled.
- Secrets do not belong in JSON configuration.

The privileged-control threat model and accepted alpha risks are documented in `docs/threat-model.md`.
