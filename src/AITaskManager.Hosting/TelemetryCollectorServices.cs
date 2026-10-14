using AITaskManager.Application;
using AITaskManager.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AITaskManager.Persistence;

namespace AITaskManager.Hosting;

internal sealed class GpuTelemetryCollectorService(
    IGpuTelemetryProvider provider,
    LiveTelemetryStore store,
    AITaskManagerOptions options,
    SqliteWriteQueue writeQueue,
    ILogger<GpuTelemetryCollectorService> logger) : BackgroundService
{
    private int _pollCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Collection.ActiveIntervalSeconds));
        do
        {
            try
            {
                var snapshot = await provider.GetSnapshotAsync(stoppingToken).ConfigureAwait(false);
                store.SetGpus(snapshot);
                _ = writeQueue.TryEnqueueMetricSamples(ToMetricSamples(snapshot));
                if (Interlocked.Increment(ref _pollCount) % 60 == 0)
                {
                    _ = writeQueue.TryEnqueueMetricRetention(
                        DateTimeOffset.UtcNow.AddDays(-options.Storage.MetricRetentionDays));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The NVIDIA telemetry poll failed.");
                store.SetGpus(new GpuProviderSnapshot(
                    provider.ProviderName,
                    "error",
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    [],
                    exception.Message));
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private static IReadOnlyList<MetricSampleWrite> ToMetricSamples(GpuProviderSnapshot snapshot)
    {
        var samples = new List<MetricSampleWrite>(snapshot.Gpus.Count * 5);
        foreach (var gpu in snapshot.Gpus)
        {
            Add("utilization_percent", gpu.UtilizationPercent);
            Add("used_vram_bytes", gpu.UsedVramBytes);
            Add("temperature_celsius", gpu.TemperatureCelsius);
            Add("power_usage_watts", gpu.PowerUsageWatts);
            Add("fan_speed_percent", gpu.FanSpeedPercent);

            void Add<T>(string metric, ObservedValue<T> value)
                where T : struct, IConvertible => samples.Add(new MetricSampleWrite(
                    value.Source,
                    gpu.Id.Value,
                    metric,
                    value.ObservedAt,
                    value.State is ObservationState.Available or ObservationState.Stale
                        ? Convert.ToDouble(value.Value, System.Globalization.CultureInfo.InvariantCulture)
                        : null,
                    value.State.ToString().ToLowerInvariant()));
        }

        return samples;
    }
}

internal sealed class RuntimeTelemetryCollectorService(
    IRuntimeAdapter adapter,
    LiveTelemetryStore store,
    AITaskManagerOptions options,
    ILogger<RuntimeTelemetryCollectorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Collection.ActiveIntervalSeconds));
        do
        {
            try
            {
                store.SetRuntimes(await adapter.GetSnapshotsAsync(stoppingToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The llama.cpp telemetry poll failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
