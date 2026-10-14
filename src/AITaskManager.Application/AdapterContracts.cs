using AITaskManager.Core;

namespace AITaskManager.Application;

public interface IGpuTelemetryProvider
{
    string ProviderName { get; }

    Task<GpuProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public interface ISystemSensorProvider
{
    string ProviderName { get; }
}

public interface IRuntimeAdapter
{
    string AdapterName { get; }

    Task<IReadOnlyList<RuntimeEndpointSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken);
}

public sealed record LocalEndpointProcess(
    int ProcessId,
    string ExecutableName,
    DateTimeOffset? StartedAt,
    string? CommandLine = null);

public sealed record LocalRuntimeListener(
    Uri Endpoint,
    LocalEndpointProcess Process);

public interface ILocalEndpointProcessResolver
{
    LocalEndpointProcess? Resolve(Uri endpoint);

    IReadOnlyList<LocalRuntimeListener> DiscoverListeners(IReadOnlyCollection<string> executableNames);
}

public interface ITrainingAdapter
{
    string AdapterName { get; }
}

public interface IActionProvider
{
    string ProviderName { get; }
}

public interface IGpuFanControlProvider
{
    string ProviderName { get; }
}
