using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AITaskManager.Adapters.LlamaCpp;
using AITaskManager.Adapters.Nvidia;
using AITaskManager.Application;
using AITaskManager.Contracts;
using AITaskManager.Core;
using AITaskManager.Infrastructure.Windows;
using AITaskManager.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AITaskManager.Hosting;

public sealed record ApplicationHostSettings(
    string? ListenUrl = null,
    string? WebRootPath = null,
    string? ContentRootPath = null,
    AppPaths? DefaultPaths = null,
    string? HardwareHelperPath = null);

public static class ApplicationHost
{
    public static async Task<WebApplication> BuildAsync(
        string[] args,
        ApplicationHostSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= new ApplicationHostSettings();
        var defaultPaths = settings.DefaultPaths ?? AppPaths.CreateDefault();
        var configurationFileManager = new ConfigurationFileManager();
        var configurationStatus = await configurationFileManager
            .InitializeAsync(defaultPaths.ConfigurationFile, cancellationToken)
            .ConfigureAwait(false);

        var webRootPath = ResolveWebRoot(settings.WebRootPath);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = settings.ContentRootPath ?? AppContext.BaseDirectory,
            WebRootPath = webRootPath,
        });
        builder.Configuration
            .AddJsonFile(defaultPaths.ConfigurationFile, optional: false, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "AITM_")
            .AddCommandLine(args);

        var options = new AITaskManagerOptions();
        builder.Configuration.Bind(options);
        ConfigurationValidator.ThrowIfInvalid(options);
        var resolvedConfiguredPaths = ConfiguredPathResolver.Resolve(options, defaultPaths);

        var paths = new AppPaths(
            defaultPaths.RootDirectory,
            defaultPaths.ConfigurationFile,
            resolvedConfiguredPaths.DataDirectory,
            resolvedConfiguredPaths.LogDirectory);
        paths.EnsureDirectoriesExist();

        var redactor = new SensitiveDataRedactor();
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);
        builder.Logging.AddProvider(new RedactingLoggerProvider(
            redactor,
            paths.LogDirectory,
            options.Storage.LogRetentionDays));
        builder.WebHost.UseUrls(settings.ListenUrl ?? options.Server.ListenUrl);

        var databasePath = Path.Combine(paths.DataDirectory, options.Storage.DatabaseFileName);
        var database = new SqliteDatabase(databasePath);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(resolvedConfiguredPaths);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(configurationStatus);
        builder.Services.AddSingleton<ISensitiveDataRedactor>(redactor);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton<SqliteWriteQueue>();
        builder.Services.AddSingleton<LiveTelemetryStore>();
        builder.Services.AddHealthChecks();

        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddSingleton<ISecretStore>(
                new DpapiSecretStore(Path.Combine(paths.DataDirectory, "secrets")));
        }

        builder.Services.AddSingleton<IGpuTelemetryProvider, NvmlGpuTelemetryProvider>();
        builder.Services.AddSingleton<HardwareActionService>();
        builder.Services.AddSingleton<FanControlService>();
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<FanControlService>());
        builder.Services.AddSingleton<ILocalEndpointProcessResolver, WindowsEndpointProcessResolver>();
        builder.Services.AddSingleton<IRuntimeAdapter, LlamaCppRuntimeAdapter>();
        builder.Services.AddHostedService<GpuTelemetryCollectorService>();
        builder.Services.AddHostedService<RuntimeTelemetryCollectorService>();

        var app = builder.Build();
        var writeQueue = app.Services.GetRequiredService<SqliteWriteQueue>();
        await writeQueue
            .SetMetadataAsync("last_started_at_utc", DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
            .ConfigureAwait(false);

        MapMiddlewareAndRoutes(app, webRootPath is not null);
        return app;
    }

    public static Uri GetListeningAddress(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;
        var address = addresses?.SingleOrDefault(value =>
            value.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase));

        return address is not null
            ? new Uri(address, UriKind.Absolute)
            : throw new InvalidOperationException("The application host did not publish a loopback HTTP address.");
    }

    private static string? ResolveWebRoot(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(configuredPath);
        return File.Exists(Path.Combine(fullPath, "index.html")) ? fullPath : null;
    }

    private static void MapMiddlewareAndRoutes(WebApplication app, bool hasWebAssets)
    {
        app.Use(async (context, next) =>
        {
            if (!IsLoopbackHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (RequiresSameOriginCheck(context.Request.Method) &&
                context.Request.Headers.TryGetValue("Origin", out var originValues) &&
                !IsSameOrigin(originValues.ToString(), context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; connect-src 'self' ws: wss:";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            await next(context).ConfigureAwait(false);
        });

        if (hasWebAssets)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }

        if (!hasWebAssets)
        {
            app.MapGet("/", () => Results.Ok(new
            {
                product = "AI Task Manager",
                status = "foundation-ready",
                api = "/api/v1/summary",
                diagnostics = "/api/v1/diagnostics",
            }));
        }

        app.MapGet("/health/live", () =>
            Results.Ok(new HealthResponse("healthy", DateTimeOffset.UtcNow)));

        app.MapGet("/health/ready", async (SqliteDatabase store, CancellationToken cancellationToken) =>
        {
            var status = await store.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return string.Equals(status.Integrity, "ok", StringComparison.OrdinalIgnoreCase)
                ? Results.Ok(new HealthResponse("ready", DateTimeOffset.UtcNow))
                : Results.Json(
                    new HealthResponse("database-integrity-failed", DateTimeOffset.UtcNow),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/api/v1/version", () => Results.Ok(new ServiceVersionResponse(
            "AI Task Manager",
            GetProductVersion(),
            1,
            Environment.Version.ToString())));

        app.MapGet("/api/v1/summary", (AITaskManagerOptions currentOptions, LiveTelemetryStore telemetry) =>
        {
            var gpus = telemetry.GetGpus();
            var runtimes = telemetry.GetRuntimes();
            var activeRuntimeCount = runtimes.Count(runtime =>
                runtime.State is not (WorkloadState.Stopped or WorkloadState.Failed));
            return Results.Ok(new SummaryResponse(
                DateTimeOffset.UtcNow,
                gpus.State,
                gpus.Gpus.Count,
                activeRuntimeCount,
                currentOptions.Actions.Enabled,
                currentOptions.Actions.FanControlEnabled));
        });

        app.MapGet("/api/v1/gpus", (LiveTelemetryStore telemetry) =>
            Results.Ok(ToResponse(telemetry.GetGpus())));

        app.MapGet("/api/v1/runtimes", (LiveTelemetryStore telemetry) =>
        {
            var runtimes = telemetry.GetRuntimes();
            return Results.Ok(new RuntimeCollectionResponse(
                runtimes.Count == 0 ? DateTimeOffset.UtcNow : runtimes.Max(runtime => runtime.ObservedAt),
                runtimes.Select(ToResponse).ToArray()));
        });

        app.MapGet("/api/v1/history/gpus", (int? seconds, LiveTelemetryStore telemetry) =>
        {
            var windowSeconds = Math.Clamp(seconds ?? 60, 10, 900);
            var history = telemetry.GetGpuHistory(DateTimeOffset.UtcNow.AddSeconds(-windowSeconds));
            return Results.Ok(new GpuHistoryResponse(
                DateTimeOffset.UtcNow,
                windowSeconds,
                history.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<GpuHistoryPointResponse>)pair.Value.Select(point => new GpuHistoryPointResponse(
                        point.ObservedAt,
                        point.UtilizationPercent,
                        point.UsedVramBytes,
                        point.TemperatureCelsius,
                        point.PowerUsageWatts)).ToArray(),
                    StringComparer.Ordinal)));
        });

        app.MapGet("/api/v1/alerts", (LiveTelemetryStore telemetry, AITaskManagerOptions currentOptions) =>
            Results.Ok(BuildAlerts(telemetry, currentOptions)));

        app.MapGet("/api/v1/actions/session", (HardwareActionService actions) =>
            Results.Ok(actions.GetSession()));

        app.MapGet("/api/v1/actions/gpu-fans/state", (FanControlService fans) =>
            Results.Ok(fans.GetStates()));

        app.MapPost(
            "/api/v1/actions/gpu-fans/manual",
            async (
                SetFanManualRequest request,
                HttpContext context,
                HardwareActionService actions,
                FanControlService fans,
                CancellationToken cancellationToken) =>
            {
                if (!actions.IsValidToken(context.Request.Headers["X-AITM-Action-Token"].ToString()))
                {
                    return Results.Json(new { error = "The action token is missing or invalid." }, statusCode: 403);
                }

                try
                {
                    return Results.Ok(await fans.SetManualAsync(request, cancellationToken).ConfigureAwait(false));
                }
                catch (HardwareActionValidationException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }
                catch (HardwareActionDisabledException exception)
                {
                    return Results.Json(new { error = exception.Message }, statusCode: 403);
                }
                catch (HardwareActionUnavailableException exception)
                {
                    return Results.Json(new { error = exception.Message }, statusCode: 503);
                }
            });

        app.MapPost(
            "/api/v1/actions/gpu-fans/restore-automatic",
            async (
                RestoreFanAutomaticRequest request,
                HttpContext context,
                HardwareActionService actions,
                FanControlService fans,
                CancellationToken cancellationToken) =>
            {
                if (!actions.IsValidToken(context.Request.Headers["X-AITM-Action-Token"].ToString()))
                {
                    return Results.Json(new { error = "The action token is missing or invalid." }, statusCode: 403);
                }

                try
                {
                    return Results.Ok(await fans.RestoreAutomaticAsync(request, cancellationToken).ConfigureAwait(false));
                }
                catch (HardwareActionValidationException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }
                catch (HardwareActionDisabledException exception)
                {
                    return Results.Json(new { error = exception.Message }, statusCode: 403);
                }
                catch (HardwareActionUnavailableException exception)
                {
                    return Results.Json(new { error = exception.Message }, statusCode: 503);
                }
            });

        app.MapPost(
            "/api/v1/actions/gpu-power",
            async (
                SetPowerLimitRequest request,
                HttpContext context,
                HardwareActionService actions,
                CancellationToken cancellationToken) =>
            {
                if (!actions.IsValidToken(context.Request.Headers["X-AITM-Action-Token"].ToString()))
                {
                    return Results.Json(new { error = "The action token is missing or invalid." }, statusCode: 403);
                }

                try
                {
                    return Results.Ok(await actions.SetPowerLimitAsync(request, cancellationToken).ConfigureAwait(false));
                }
                catch (HardwareActionValidationException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }
                catch (HardwareActionDisabledException exception)
                {
                    return Results.Json(new { error = exception.Message }, statusCode: 403);
                }
                catch (HardwareActionUnavailableException exception)
                {
                    return Results.Json(new { error = exception.Message }, statusCode: 503);
                }
            });

        app.MapPost(
            "/api/v1/actions/gpu-power/restore-default",
            async (
                RestorePowerLimitRequest request,
                HttpContext context,
                HardwareActionService actions,
                CancellationToken cancellationToken) =>
            {
                if (!actions.IsValidToken(context.Request.Headers["X-AITM-Action-Token"].ToString()))
                {
                    return Results.Json(new { error = "The action token is missing or invalid." }, statusCode: 403);
                }

                try
                {
                    return Results.Ok(await actions.RestorePowerLimitAsync(request, cancellationToken).ConfigureAwait(false));
                }
                catch (HardwareActionValidationException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }
                catch (HardwareActionDisabledException exception)
                {
                    return Results.Json(new { error = exception.Message }, statusCode: 403);
                }
                catch (HardwareActionUnavailableException exception)
                {
                    return Results.Json(new { error = exception.Message }, statusCode: 503);
                }
            });

        app.MapGet(
            "/api/v1/diagnostics",
            async (
                AppPaths currentPaths,
                AITaskManagerOptions currentOptions,
                ConfigurationFileStatus currentConfigurationStatus,
                SqliteDatabase store,
                SqliteWriteQueue queue,
                LiveTelemetryStore telemetry,
                CancellationToken cancellationToken) =>
            {
                var diagnostics = await BuildDiagnosticsAsync(
                    currentPaths,
                    currentOptions,
                    currentConfigurationStatus,
                    store,
                    queue,
                    telemetry,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(diagnostics);
            });

        app.MapGet(
            "/api/v1/diagnostics/export-preview",
            async (
                AppPaths currentPaths,
                AITaskManagerOptions currentOptions,
                ConfigurationFileStatus currentConfigurationStatus,
                SqliteDatabase store,
                SqliteWriteQueue queue,
                LiveTelemetryStore telemetry,
                ISensitiveDataRedactor currentRedactor,
                CancellationToken cancellationToken) =>
            {
                var diagnostics = await BuildDiagnosticsAsync(
                    currentPaths,
                    currentOptions,
                    currentConfigurationStatus,
                    store,
                    queue,
                    telemetry,
                    cancellationToken).ConfigureAwait(false);
                var json = JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true });
                return Results.Text(currentRedactor.Redact(json), "application/json", Encoding.UTF8);
            });

        app.MapPost(
            "/api/v1/diagnostics/export",
            async (
                DiagnosticsExportRequest request,
                AppPaths currentPaths,
                AITaskManagerOptions currentOptions,
                ConfigurationFileStatus currentConfigurationStatus,
                SqliteDatabase store,
                SqliteWriteQueue queue,
                LiveTelemetryStore telemetry,
                ISensitiveDataRedactor currentRedactor,
                CancellationToken cancellationToken) =>
            {
                if (!request.ConfirmReviewedPreview)
                {
                    return Results.BadRequest(new
                    {
                        error = "Review /api/v1/diagnostics/export-preview and explicitly confirm before creating an archive.",
                    });
                }

                var diagnostics = await BuildDiagnosticsAsync(
                    currentPaths,
                    currentOptions,
                    currentConfigurationStatus,
                    store,
                    queue,
                    telemetry,
                    cancellationToken).ConfigureAwait(false);
                var json = currentRedactor.Redact(
                    JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true }));
                var exportDirectory = Path.Combine(currentPaths.DataDirectory, "diagnostics");
                Directory.CreateDirectory(exportDirectory);
                var createdAt = DateTimeOffset.UtcNow;
                var fileName = $"aitaskmanager-diagnostics-{createdAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip";
                var destination = Path.Combine(exportDirectory, fileName);
                var temporary = destination + ".tmp";

                try
                {
                    await using (var stream = new FileStream(
                        temporary,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        FileOptions.Asynchronous | FileOptions.WriteThrough))
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        var entry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
                        await using var entryStream = entry.Open();
                        await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
                        await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }

                    File.Move(temporary, destination, overwrite: false);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }

                return Results.Ok(new DiagnosticsExportResponse(fileName, createdAt, Redacted: true));
            });

        if (hasWebAssets)
        {
            app.MapFallbackToFile("index.html");
        }
    }

    private static string GetProductVersion() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool RequiresSameOriginCheck(string method) =>
        !HttpMethods.IsGet(method) &&
        !HttpMethods.IsHead(method) &&
        !HttpMethods.IsOptions(method);

    private static bool IsSameOrigin(string originValue, HttpRequest request)
    {
        if (!Uri.TryCreate(originValue, UriKind.Absolute, out var origin))
        {
            return false;
        }

        return string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(origin.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase) &&
            origin.Port == request.Host.Port;
    }

    private static async Task<DiagnosticsResponse> BuildDiagnosticsAsync(
        AppPaths paths,
        AITaskManagerOptions options,
        ConfigurationFileStatus configurationStatus,
        SqliteDatabase database,
        SqliteWriteQueue queue,
        LiveTelemetryStore telemetry,
        CancellationToken cancellationToken)
    {
        var status = await database.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var gpuState = telemetry.GetGpus();
        var runtimeStates = telemetry.GetRuntimes();
        var llamaState = runtimeStates.Count == 0
            ? options.Discovery.EnableLocalRuntimeDiscovery
                ? new AdapterDiagnostics(
                    "llama.cpp",
                    "idle",
                    "Automatic local discovery is enabled; no running llama-server process was found.")
                : new AdapterDiagnostics("llama.cpp", "not-configured", null)
            : new AdapterDiagnostics(
                "llama.cpp",
                runtimeStates.Any(runtime => runtime.State is WorkloadState.Ready or WorkloadState.Active or WorkloadState.Idle)
                    ? "online"
                    : runtimeStates.Any(runtime => runtime.State == WorkloadState.Loading) ? "loading" : "offline",
                string.Join("; ", runtimeStates.Where(runtime => runtime.Detail is not null).Select(runtime => runtime.Detail)));
        return new DiagnosticsResponse(
            DateTimeOffset.UtcNow,
            GetProductVersion(),
            Environment.Version.ToString(),
            new PathDiagnostics(
                paths.RootDirectory,
                paths.ConfigurationFile,
                paths.DataDirectory,
                paths.LogDirectory),
            new ConfigurationDiagnostics(
                options.SchemaVersion,
                configurationStatus.Created,
                configurationStatus.Migrated,
                configurationStatus.BackupPath),
            new DatabaseDiagnostics(
                status.Path,
                status.SchemaVersion,
                status.Integrity,
                status.SizeBytes,
                status.WriteAheadLoggingEnabled,
                queue.PendingCount),
            new ActionDiagnostics(
                options.Actions.Enabled,
                options.Actions.PowerControlEnabled,
                options.Actions.FanControlEnabled,
                options.Actions.FanControlEnabled
                    ? "power-and-guarded-fan-control"
                    : options.Actions.PowerControlEnabled
                    ? "power-bounded-fan-read-only"
                    : "hardware-writes-locked"),
            [
                new AdapterDiagnostics(
                    "nvidia",
                    gpuState.State,
                    gpuState.Error ?? $"Driver {gpuState.DriverVersion ?? "unknown"}; NVML {gpuState.LibraryVersion ?? "unknown"}"),
                llamaState,
                new AdapterDiagnostics("lm-studio", "not-included", "The LM Studio adapter is not included in this alpha."),
                new AdapterDiagnostics("training", "not-included", "Training monitoring is not included in this alpha; configured training roots are not collected yet."),
            ],
            []);
    }

    private static GpuCollectionResponse ToResponse(GpuProviderSnapshot snapshot) => new(
        snapshot.ObservedAt,
        snapshot.ProviderName,
        snapshot.State,
        snapshot.DriverVersion,
        snapshot.LibraryVersion,
        snapshot.Gpus.Select(gpu => new GpuResponse(
            gpu.Id.Value,
            gpu.DisplayIndex,
            gpu.Name,
            ToResponse(gpu.TotalVramBytes),
            ToResponse(gpu.FreeVramBytes),
            ToResponse(gpu.UsedVramBytes),
            ToResponse(gpu.UtilizationPercent),
            ToResponse(gpu.MemoryUtilizationPercent),
            ToResponse(gpu.TemperatureCelsius),
            ToResponse(gpu.PowerUsageWatts),
            ToResponse(gpu.PowerLimitWatts),
            ToResponse(gpu.FanSpeedPercent),
            ToResponse(gpu.GraphicsClockMhz),
            ToResponse(gpu.MemoryClockMhz),
            ToResponse(gpu.RequestedPowerLimitWatts ?? Missing<double>(snapshot.ObservedAt)),
            ToResponse(gpu.DefaultPowerLimitWatts ?? Missing<double>(snapshot.ObservedAt)),
            ToResponse(gpu.MinimumPowerLimitWatts ?? Missing<double>(snapshot.ObservedAt)),
            ToResponse(gpu.MaximumPowerLimitWatts ?? Missing<double>(snapshot.ObservedAt)),
            ToResponse(gpu.FanCount ?? Missing<int>(snapshot.ObservedAt)),
            ToResponse(gpu.SlowdownTemperatureCelsius ?? Missing<double>(snapshot.ObservedAt)),
            ToResponse(gpu.ShutdownTemperatureCelsius ?? Missing<double>(snapshot.ObservedAt)),
            ToResponse(gpu.MinimumFanSpeedPercent ?? Missing<double>(snapshot.ObservedAt)),
            ToResponse(gpu.MaximumFanSpeedPercent ?? Missing<double>(snapshot.ObservedAt)),
            gpu.Fans.Select(fan => new GpuFanResponse(
                fan.Index,
                ToResponse(fan.SpeedPercent),
                ToResponse(fan.TargetSpeedPercent),
                ToResponse(fan.ControlPolicy))).ToArray())).ToArray(),
        snapshot.Processes.Select(process => new GpuProcessResponse(
            process.GpuId.Value,
            process.ProcessId,
            process.ContextKind,
            ToResponse(process.UsedVramBytes))).ToArray(),
        snapshot.Error);

    private static RuntimeEndpointResponse ToResponse(RuntimeEndpointSnapshot snapshot) => new(
        snapshot.Id.Value,
        snapshot.Name,
        snapshot.AdapterKind,
        snapshot.BaseUrl,
        snapshot.State.ToString().ToLowerInvariant(),
        snapshot.Model,
        snapshot.ModelFormat,
        snapshot.Build,
        snapshot.Modalities,
        snapshot.ConfiguredContextTokens,
        snapshot.OccupiedContextTokens,
        snapshot.ActiveSlots,
        snapshot.TotalSlots,
        snapshot.QueuedRequests,
        snapshot.PromptTokensPerSecond,
        snapshot.GenerationTokensPerSecond,
        snapshot.PromptTokensTotal,
        snapshot.GeneratedTokensTotal,
        snapshot.KeyCacheType,
        snapshot.ValueCacheType,
        snapshot.Sleeping,
        snapshot.ProcessId,
        snapshot.ProcessName,
        snapshot.ProcessStartedAt,
        snapshot.ObservedAt,
        snapshot.Detail);

    private static ObservedMetricResponse<T> ToResponse<T>(ObservedValue<T> value)
        where T : struct => new(
            value.State is ObservationState.Available or ObservationState.Stale ? value.Value : null,
            value.State.ToString().ToLowerInvariant(),
            value.ObservedAt,
            value.Source,
        value.Detail);

    private static ObservedTextResponse ToResponse(ObservedValue<string> value) => new(
        value.State is ObservationState.Available or ObservationState.Stale ? value.Value : null,
        value.State.ToString().ToLowerInvariant(),
        value.ObservedAt,
        value.Source,
        value.Detail);

    private static ObservedValue<T> Missing<T>(DateTimeOffset observedAt)
        where T : struct => new(default, ObservationState.Unsupported, observedAt, "nvml", "Not reported.");

    private static AlertCollectionResponse BuildAlerts(
        LiveTelemetryStore telemetry,
        AITaskManagerOptions options)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var alerts = new List<AlertResponse>();
        var gpuSnapshot = telemetry.GetGpus();

        if (!string.Equals(gpuSnapshot.State, "online", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(new AlertResponse(
                "gpu-collector-state",
                "error",
                "active",
                "nvidia",
                "GPU telemetry unavailable",
                gpuSnapshot.Error ?? $"Collector state is {gpuSnapshot.State}.",
                observedAt));
        }
        else if (observedAt - gpuSnapshot.ObservedAt > TimeSpan.FromSeconds(
                     options.Collection.ActiveIntervalSeconds * options.Collection.StaleAfterMultiplier))
        {
            alerts.Add(new AlertResponse(
                "gpu-collector-stale",
                "warning",
                "active",
                "nvidia",
                "GPU telemetry is stale",
                $"The last sample is {(observedAt - gpuSnapshot.ObservedAt).TotalSeconds:F1} seconds old.",
                observedAt));
        }

        foreach (var gpu in gpuSnapshot.Gpus)
        {
            if (gpu.TemperatureCelsius.State == ObservationState.Available &&
                gpu.TemperatureCelsius.Value >= options.Alerts.GpuTemperatureWarningCelsius)
            {
                alerts.Add(new AlertResponse(
                    $"gpu-temperature-{gpu.Id.Value}",
                    "warning",
                    "active",
                    gpu.Id.Value,
                    $"GPU {gpu.DisplayIndex} temperature high",
                    $"{gpu.TemperatureCelsius.Value:F0} °C exceeds the configured {options.Alerts.GpuTemperatureWarningCelsius} °C threshold.",
                    observedAt));
            }

            if (gpu.TotalVramBytes.State == ObservationState.Available &&
                gpu.UsedVramBytes.State == ObservationState.Available &&
                gpu.TotalVramBytes.Value > 0)
            {
                var percent = 100d * gpu.UsedVramBytes.Value / gpu.TotalVramBytes.Value;
                if (percent >= options.Alerts.VramWarningPercent)
                {
                    alerts.Add(new AlertResponse(
                        $"gpu-vram-{gpu.Id.Value}",
                        "warning",
                        "active",
                        gpu.Id.Value,
                        $"GPU {gpu.DisplayIndex} VRAM pressure",
                        $"VRAM usage is {percent:F1}%, above the configured {options.Alerts.VramWarningPercent}% threshold.",
                        observedAt));
                }
            }
        }

        foreach (var runtime in telemetry.GetRuntimes().Where(runtime =>
                     runtime.State is WorkloadState.Failed or WorkloadState.Stopped))
        {
            alerts.Add(new AlertResponse(
                $"runtime-{runtime.Id.Value}",
                "error",
                "active",
                runtime.Id.Value,
                $"{runtime.Name} is {runtime.State.ToString().ToLowerInvariant()}",
                runtime.Detail ?? "The configured runtime endpoint is not ready.",
                observedAt));
        }

        return new AlertCollectionResponse(observedAt, alerts);
    }
}
