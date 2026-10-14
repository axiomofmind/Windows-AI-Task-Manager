namespace AITaskManager.Core;

public enum CapabilityState
{
    Unknown,
    Supported,
    Unsupported,
    PermissionDenied,
    TemporarilyUnavailable,
}

public enum WorkloadState
{
    Unknown,
    Loading,
    Ready,
    Active,
    Idle,
    Draining,
    Failed,
    Stopped,
}

public enum ObservationSourcePriority
{
    Heuristic = 0,
    OperatingSystem = 100,
    Runtime = 200,
    VendorApi = 300,
}

public readonly record struct ProcessInstanceId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct WorkloadId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct RuntimeEndpointId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct ModelId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct TrainingRunId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct AlertId(string Value)
{
    public override string ToString() => Value;
}

public sealed record Capability(
    CapabilityState State,
    string Source,
    string? Detail = null);

public sealed record SystemTelemetrySnapshot(
    ObservedValue<double> CpuUtilizationPercent,
    ObservedValue<long> UsedMemoryBytes,
    ObservedValue<long> TotalMemoryBytes);

public sealed record ProcessSnapshot(
    ProcessInstanceId Id,
    int ProcessId,
    string ExecutableName,
    DateTimeOffset StartedAt,
    ObservedValue<double> CpuUtilizationPercent,
    ObservedValue<long> WorkingSetBytes,
    string? RedactedCommandLine = null);

public sealed record ContextSnapshot(
    ObservedValue<long> ConfiguredTokens,
    ObservedValue<long> OccupiedTokens,
    ObservedValue<int> ActiveSlots,
    ObservedValue<int> TotalSlots);

public sealed record KvCacheSnapshot(
    ObservedValue<long> AllocatedBytes,
    ObservedValue<long> UsedBytes,
    ObservedValue<string> KeyType,
    ObservedValue<string> ValueType);

public sealed record RuntimeSnapshot(
    RuntimeEndpointId Id,
    string AdapterKind,
    WorkloadState State,
    ModelId? LoadedModelId,
    ContextSnapshot? Context,
    KvCacheSnapshot? KvCache,
    DateTimeOffset ObservedAt);

public sealed record TrainingSnapshot(
    TrainingRunId Id,
    WorkloadState State,
    ObservedValue<long> Step,
    ObservedValue<long> MaximumSteps,
    ObservedValue<double> Loss,
    ObservedValue<double> LearningRate,
    ObservedValue<TimeSpan> EstimatedRemaining,
    DateTimeOffset ObservedAt);

public sealed record FanSnapshot(
    GpuId GpuId,
    Capability ControlCapability,
    ObservedValue<double> SpeedPercent,
    ObservedValue<int> RevolutionsPerMinute,
    ObservedValue<bool> AutomaticControlEnabled);

public sealed record WorkloadSnapshot(
    WorkloadId Id,
    WorkloadState State,
    string Kind,
    ProcessInstanceId? ProcessInstanceId,
    ModelId? ModelId,
    IReadOnlyList<GpuId> GpuIds,
    DateTimeOffset ObservedAt);

public static class ObservationConflictResolver
{
    public static ObservedValue<T> Prefer<T>(
        ObservedValue<T> first,
        ObservationSourcePriority firstPriority,
        ObservedValue<T> second,
        ObservationSourcePriority secondPriority)
    {
        if (first.HasUsableValue != second.HasUsableValue)
        {
            return first.HasUsableValue ? first : second;
        }

        if (firstPriority != secondPriority)
        {
            return firstPriority > secondPriority ? first : second;
        }

        return first.ObservedAt >= second.ObservedAt ? first : second;
    }
}
