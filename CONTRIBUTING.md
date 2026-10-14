# Contributing to AI Task Manager

AI Task Manager is in early development. Start with an [issue](https://github.com/axiomofmind/Windows-AI-Task-Manager/issues) before undertaking a large feature or a new hardware-write path.

## Local checks

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build
npm ci --prefix src/AITaskManager.Web
npm run lint --prefix src/AITaskManager.Web
npm run build --prefix src/AITaskManager.Web
```

Do not commit local configuration, logs, diagnostics, models, datasets, checkpoints, credentials, or telemetry containing usernames and local paths.

All hardware mutations require validation, audit, rollback, fault-injection tests, and explicit maintainer review.
