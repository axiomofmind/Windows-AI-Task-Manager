namespace AITaskManager.Core;

public enum ObservationState
{
    Available,
    Unsupported,
    Unavailable,
    PermissionDenied,
    Stale,
    Error,
}

public sealed record ObservedValue<T>(
    T? Value,
    ObservationState State,
    DateTimeOffset ObservedAt,
    string Source,
    string? Detail = null)
{
    public bool HasUsableValue =>
        Value is not null && State is ObservationState.Available or ObservationState.Stale;
}

public readonly record struct GpuId
{
    public GpuId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record GpuSnapshot(
    GpuId Id,
    int DisplayIndex,
    string Name,
    ObservedValue<long> TotalVramBytes,
    ObservedValue<long> FreeVramBytes,
    ObservedValue<long> UsedVramBytes,
    ObservedValue<double> UtilizationPercent,
    ObservedValue<double> MemoryUtilizationPercent,
    ObservedValue<double> TemperatureCelsius,
    ObservedValue<double> PowerUsageWatts,
    ObservedValue<double> PowerLimitWatts,
    ObservedValue<double> FanSpeedPercent,
    ObservedValue<int> GraphicsClockMhz,
    ObservedValue<int> MemoryClockMhz)
{
    public ObservedValue<double>? RequestedPowerLimitWatts { get; init; }

    public ObservedValue<double>? DefaultPowerLimitWatts { get; init; }

    public ObservedValue<double>? MinimumPowerLimitWatts { get; init; }

    public ObservedValue<double>? MaximumPowerLimitWatts { get; init; }

    public ObservedValue<int>? FanCount { get; init; }

    public ObservedValue<double>? SlowdownTemperatureCelsius { get; init; }

    public ObservedValue<double>? ShutdownTemperatureCelsius { get; init; }

    public ObservedValue<double>? MinimumFanSpeedPercent { get; init; }

    public ObservedValue<double>? MaximumFanSpeedPercent { get; init; }

    public IReadOnlyList<GpuFanSnapshot> Fans { get; init; } = [];
}

public sealed record GpuFanSnapshot(
    int Index,
    ObservedValue<double> SpeedPercent,
    ObservedValue<double> TargetSpeedPercent,
    ObservedValue<string> ControlPolicy);

public sealed record GpuProviderSnapshot(
    string ProviderName,
    string State,
    string? DriverVersion,
    string? LibraryVersion,
    DateTimeOffset ObservedAt,
    IReadOnlyList<GpuSnapshot> Gpus,
    string? Error = null)
{
    public IReadOnlyList<GpuProcessSnapshot> Processes { get; init; } = [];
}

public sealed record GpuProcessSnapshot(
    GpuId GpuId,
    int ProcessId,
    string ContextKind,
    ObservedValue<long> UsedVramBytes);

public sealed record RuntimeEndpointSnapshot(
    RuntimeEndpointId Id,
    string Name,
    string AdapterKind,
    string BaseUrl,
    WorkloadState State,
    string? Model,
    string? ModelFormat,
    string? Build,
    IReadOnlyList<string> Modalities,
    long? ConfiguredContextTokens,
    long? OccupiedContextTokens,
    int? ActiveSlots,
    int? TotalSlots,
    int? QueuedRequests,
    double? PromptTokensPerSecond,
    double? GenerationTokensPerSecond,
    long? PromptTokensTotal,
    long? GeneratedTokensTotal,
    string? KeyCacheType,
    string? ValueCacheType,
    bool? Sleeping,
    int? ProcessId,
    string? ProcessName,
    DateTimeOffset? ProcessStartedAt,
    DateTimeOffset ObservedAt,
    string? Detail = null);
