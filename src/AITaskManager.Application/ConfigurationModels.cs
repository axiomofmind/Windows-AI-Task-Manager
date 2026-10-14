namespace AITaskManager.Application;

public sealed class AITaskManagerOptions
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public ServerOptions Server { get; set; } = new();

    public StorageOptions Storage { get; set; } = new();

    public CollectionOptions Collection { get; set; } = new();

    public DiscoveryOptions Discovery { get; set; } = new();

    public AlertOptions Alerts { get; set; } = new();

    public DesktopOptions Desktop { get; set; } = new();

    public ActionOptions Actions { get; set; } = new();
}

public sealed class ServerOptions
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:7312";

    public List<string> AllowedOrigins { get; set; } = ["http://127.0.0.1:5173", "http://localhost:5173"];
}

public sealed class StorageOptions
{
    public string DataDirectory { get; set; } = string.Empty;

    public string LogDirectory { get; set; } = string.Empty;

    public string DatabaseFileName { get; set; } = "aitaskmanager.db";

    public int MetricRetentionDays { get; set; } = 30;

    public int LogRetentionDays { get; set; } = 14;
}

public sealed class CollectionOptions
{
    public double ActiveIntervalSeconds { get; set; } = 1;

    public double IdleIntervalSeconds { get; set; } = 5;

    public double SourceTimeoutSeconds { get; set; } = 3;

    public double StaleAfterMultiplier { get; set; } = 3;
}

public sealed class DiscoveryOptions
{
    public bool EnableLocalRuntimeDiscovery { get; set; } = true;

    public List<RuntimeEndpointOptions> RuntimeEndpoints { get; set; } = [];

    public List<string> ModelRoots { get; set; } = [];

    public List<string> TrainingRoots { get; set; } = [];

    public List<string> DatasetRoots { get; set; } = [];
}

public sealed class RuntimeEndpointOptions
{
    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string? SecretReference { get; set; }

    public string? KeyCacheType { get; set; }

    public string? ValueCacheType { get; set; }
}

public sealed class ActionOptions
{
    public bool Enabled { get; set; } = true;

    public bool PowerControlEnabled { get; set; } = true;

    public bool FanControlEnabled { get; set; } = true;

    public PowerControlPolicyOptions PowerControl { get; set; } = new();

    public FanControlPolicyOptions FanControl { get; set; } = new();
}

public sealed class PowerControlPolicyOptions
{
    public int ConfirmationSeconds { get; set; } = 10;

    public bool RestoreOnExit { get; set; } = true;
}

public sealed class FanControlPolicyOptions
{
    public int WatchdogSeconds { get; set; } = 5;

    public int FailsafeTemperatureCelsius { get; set; } = 90;

    public bool RestoreOnExit { get; set; } = true;
}

public sealed class AlertOptions
{
    public int GpuTemperatureWarningCelsius { get; set; } = 85;

    public int VramWarningPercent { get; set; } = 95;
}

public sealed class DesktopOptions
{
    public bool CloseToTray { get; set; } = true;

    public bool StartMinimized { get; set; }
}

public sealed record ConfigurationError(string Path, string Message);

public sealed class ConfigurationValidationException(IReadOnlyList<ConfigurationError> errors)
    : Exception(
        "AI Task Manager configuration is invalid: " +
        string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}")))
{
    public IReadOnlyList<ConfigurationError> Errors { get; } = errors;
}
