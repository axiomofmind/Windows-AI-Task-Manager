using AITaskManager.Core;

namespace AITaskManager.Hosting;

public sealed class LiveTelemetryStore
{
    private const int MaximumHistoryPointsPerGpu = 900;
    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<GpuHistoryPoint>> _gpuHistory = new(StringComparer.Ordinal);
    private GpuProviderSnapshot _gpus = new(
        "nvidia-nvml",
        "starting",
        null,
        null,
        DateTimeOffset.UtcNow,
        []);
    private IReadOnlyList<RuntimeEndpointSnapshot> _runtimes = [];

    public GpuProviderSnapshot GetGpus()
    {
        lock (_sync)
        {
            return _gpus;
        }
    }

    public IReadOnlyList<RuntimeEndpointSnapshot> GetRuntimes()
    {
        lock (_sync)
        {
            return _runtimes;
        }
    }

    public void SetGpus(GpuProviderSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_sync)
        {
            _gpus = value;
            foreach (var gpu in value.Gpus)
            {
                if (!_gpuHistory.TryGetValue(gpu.Id.Value, out var history))
                {
                    history = new Queue<GpuHistoryPoint>();
                    _gpuHistory.Add(gpu.Id.Value, history);
                }

                if (history.TryPeek(out var last) && last.ObservedAt == value.ObservedAt)
                {
                    continue;
                }

                history.Enqueue(new GpuHistoryPoint(
                    value.ObservedAt,
                    gpu.UtilizationPercent.State == ObservationState.Available ? gpu.UtilizationPercent.Value : null,
                    gpu.UsedVramBytes.State == ObservationState.Available ? gpu.UsedVramBytes.Value : null,
                    gpu.TemperatureCelsius.State == ObservationState.Available ? gpu.TemperatureCelsius.Value : null,
                    gpu.PowerUsageWatts.State == ObservationState.Available ? gpu.PowerUsageWatts.Value : null));
                while (history.Count > MaximumHistoryPointsPerGpu)
                {
                    _ = history.Dequeue();
                }
            }
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<GpuHistoryPoint>> GetGpuHistory(DateTimeOffset since)
    {
        lock (_sync)
        {
            return _gpuHistory.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<GpuHistoryPoint>)pair.Value.Where(point => point.ObservedAt >= since).ToArray(),
                StringComparer.Ordinal);
        }
    }

    public void SetRuntimes(IReadOnlyList<RuntimeEndpointSnapshot> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_sync)
        {
            _runtimes = value.ToArray();
        }
    }
}

public sealed record GpuHistoryPoint(
    DateTimeOffset ObservedAt,
    double? UtilizationPercent,
    long? UsedVramBytes,
    double? TemperatureCelsius,
    double? PowerUsageWatts);
