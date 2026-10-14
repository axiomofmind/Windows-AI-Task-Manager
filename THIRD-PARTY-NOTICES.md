# Third-party notices

This inventory covers direct runtime dependencies in the `0.1.0-alpha` source and portable build. Transitive components are recorded in the CycloneDX SBOM generated with each release. Copyright remains with the respective authors.

| Component | Version | Use | License |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite / Core | 10.0.13 | Runtime persistence | MIT |
| SQLitePCLRaw packages | 2.1.13 | Bundled SQLite native provider | Apache-2.0; SQLite itself is public domain |
| System.Security.Cryptography.ProtectedData | 10.0.13 | Windows DPAPI integration | MIT |
| Microsoft.Web.WebView2 | 1.0.4258.31 | Desktop UI host | Microsoft WebView2 SDK license; exact `LICENSE.txt` and `NOTICE.txt` are included under `licenses/` in the portable release |
| React / React DOM | 19.2.8 | Bundled web interface | MIT |

Build and test tooling—including the .NET SDK, Microsoft.NET.Test.Sdk, xUnit, coverlet, TypeScript, Vite, and oxlint—is not redistributed as application tooling. Its package metadata remains captured by the source lock files and release SBOM.

AI Task Manager loads the NVIDIA driver-installed NVML library at runtime. NVIDIA driver binaries are not copied into the repository or release. Fan Control, models, datasets, and checkpoints are not included.
