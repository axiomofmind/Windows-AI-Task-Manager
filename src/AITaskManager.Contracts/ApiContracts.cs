namespace AITaskManager.Contracts;

public sealed record ServiceVersionResponse(
    string Product,
    string Version,
    int ApiVersion,
    string Runtime);

public sealed record HealthResponse(
    string Status,
    DateTimeOffset ObservedAt);

public sealed record SummaryResponse(
    DateTimeOffset ObservedAt,
    string CollectionState,
    int GpuCount,
    int WorkloadCount,
    bool ActionsEnabled,
    bool FanControlEnabled)
{
    public static SummaryResponse Empty(DateTimeOffset observedAt) =>
        new(observedAt, "starting", 0, 0, false, false);
}

public sealed record DiagnosticsResponse(
    DateTimeOffset ObservedAt,
    string ProductVersion,
    string RuntimeVersion,
    PathDiagnostics Paths,
    ConfigurationDiagnostics Configuration,
    DatabaseDiagnostics Database,
    ActionDiagnostics Actions,
    IReadOnlyList<AdapterDiagnostics> Adapters,
    IReadOnlyList<string> Errors);

public sealed record PathDiagnostics(
    string RootDirectory,
    string ConfigurationFile,
    string DataDirectory,
    string LogDirectory);

public sealed record ConfigurationDiagnostics(
    int SchemaVersion,
    bool CreatedThisRun,
    bool MigratedThisRun,
    string? MigrationBackupPath);

public sealed record DatabaseDiagnostics(
    string Path,
    int SchemaVersion,
    string Integrity,
    long SizeBytes,
    bool WriteAheadLoggingEnabled,
    int PendingWrites);

public sealed record ActionDiagnostics(
    bool Enabled,
    bool PowerControlEnabled,
    bool FanControlEnabled,
    string SafetyState);

public sealed record AdapterDiagnostics(
    string Name,
    string State,
    string? Detail);

public sealed record DiagnosticsExportRequest(bool ConfirmReviewedPreview);

public sealed record DiagnosticsExportResponse(
    string FileName,
    DateTimeOffset CreatedAt,
    bool Redacted);

public sealed record ObservedMetricResponse<T>(
    T? Value,
    string State,
    DateTimeOffset ObservedAt,
    string Source,
    string? Detail)
    where T : struct;

public sealed record GpuResponse(
    string Id,
    int DisplayIndex,
    string Name,
    ObservedMetricResponse<long> TotalVramBytes,
    ObservedMetricResponse<long> FreeVramBytes,
    ObservedMetricResponse<long> UsedVramBytes,
    ObservedMetricResponse<double> UtilizationPercent,
    ObservedMetricResponse<double> MemoryUtilizationPercent,
    ObservedMetricResponse<double> TemperatureCelsius,
    ObservedMetricResponse<double> PowerUsageWatts,
    ObservedMetricResponse<double> PowerLimitWatts,
    ObservedMetricResponse<double> FanSpeedPercent,
    ObservedMetricResponse<int> GraphicsClockMhz,
    ObservedMetricResponse<int> MemoryClockMhz,
    ObservedMetricResponse<double> RequestedPowerLimitWatts,
    ObservedMetricResponse<double> DefaultPowerLimitWatts,
    ObservedMetricResponse<double> MinimumPowerLimitWatts,
    ObservedMetricResponse<double> MaximumPowerLimitWatts,
    ObservedMetricResponse<int> FanCount,
    ObservedMetricResponse<double> SlowdownTemperatureCelsius,
    ObservedMetricResponse<double> ShutdownTemperatureCelsius,
    ObservedMetricResponse<double> MinimumFanSpeedPercent,
    ObservedMetricResponse<double> MaximumFanSpeedPercent,
    IReadOnlyList<GpuFanResponse> Fans);

public sealed record GpuFanResponse(
    int Index,
    ObservedMetricResponse<double> SpeedPercent,
    ObservedMetricResponse<double> TargetSpeedPercent,
    ObservedTextResponse ControlPolicy);

public sealed record ObservedTextResponse(
    string? Value,
    string State,
    DateTimeOffset ObservedAt,
    string Source,
    string? Detail);

public sealed record GpuCollectionResponse(
    DateTimeOffset ObservedAt,
    string Provider,
    string State,
    string? DriverVersion,
    string? LibraryVersion,
    IReadOnlyList<GpuResponse> Gpus,
    IReadOnlyList<GpuProcessResponse> Processes,
    string? Error);

public sealed record GpuProcessResponse(
    string GpuId,
    int ProcessId,
    string ContextKind,
    ObservedMetricResponse<long> UsedVramBytes);

public sealed record RuntimeEndpointResponse(
    string Id,
    string Name,
    string Kind,
    string BaseUrl,
    string State,
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
    string? Detail);

public sealed record RuntimeCollectionResponse(
    DateTimeOffset ObservedAt,
    IReadOnlyList<RuntimeEndpointResponse> Runtimes);

public sealed record GpuHistoryResponse(
    DateTimeOffset ObservedAt,
    int WindowSeconds,
    IReadOnlyDictionary<string, IReadOnlyList<GpuHistoryPointResponse>> Series);

public sealed record GpuHistoryPointResponse(
    DateTimeOffset ObservedAt,
    double? UtilizationPercent,
    long? UsedVramBytes,
    double? TemperatureCelsius,
    double? PowerUsageWatts);

public sealed record AlertResponse(
    string Id,
    string Severity,
    string State,
    string TargetId,
    string Title,
    string Detail,
    DateTimeOffset ObservedAt);

public sealed record AlertCollectionResponse(
    DateTimeOffset ObservedAt,
    IReadOnlyList<AlertResponse> Alerts);

public sealed record ActionSessionResponse(
    string Token,
    bool ActionsEnabled,
    bool PowerControlEnabled,
    bool FanControlEnabled,
    bool PrivilegedHelperAvailable);

public sealed record SetPowerLimitRequest(
    string GpuId,
    double Watts,
    string Confirmation,
    string IdempotencyKey);

public sealed record RestorePowerLimitRequest(
    string GpuId,
    string Confirmation,
    string IdempotencyKey);

public sealed record HardwareActionResponse(
    string AuditId,
    string Result,
    string GpuId,
    double? PreviousWatts,
    double? RequestedWatts,
    double? AppliedWatts,
    string Detail);

public sealed record SetFanManualRequest(
    string GpuId,
    int DutyPercent,
    bool ConfirmOtherControllersDisabled,
    bool AcknowledgeManualControl,
    string IdempotencyKey);

public sealed record RestoreFanAutomaticRequest(
    string GpuId,
    string IdempotencyKey);

public sealed record FanControlActionResponse(
    string AuditId,
    string Result,
    string GpuId,
    string State,
    int? RequestedDutyPercent,
    int? AppliedDutyPercent,
    int? TemperatureCelsius,
    int LeaseRemainingSeconds,
    string Detail);

public sealed record FanControlStateResponse(
    string GpuId,
    string State,
    int? RequestedDutyPercent,
    int? AppliedDutyPercent,
    int? TemperatureCelsius,
    int LeaseRemainingSeconds,
    DateTimeOffset ObservedAt,
    string Detail);

public sealed record FanControlStateCollectionResponse(
    DateTimeOffset ObservedAt,
    IReadOnlyList<FanControlStateResponse> Gpus);
