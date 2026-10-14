using AITaskManager.Application;
using AITaskManager.Core;

namespace AITaskManager.Adapters.Nvidia;

public sealed class NvmlGpuTelemetryProvider : IGpuTelemetryProvider, IDisposable
{
    private const string Source = "nvml";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private INvmlApi? _api;
    private bool _initialized;
    private bool _disposed;

    public NvmlGpuTelemetryProvider()
    {
    }

    public NvmlGpuTelemetryProvider(INvmlApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public string ProviderName => "nvidia-nvml";

    public async Task<GpuProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            if (!EnsureInitialized(out var initializationError))
            {
                return new GpuProviderSnapshot(
                    ProviderName,
                    "unavailable",
                    null,
                    null,
                    observedAt,
                    [],
                    initializationError);
            }

            var driverVersion = ReadString(_api!.GetDriverVersion);
            var libraryVersion = ReadString(_api.GetLibraryVersion);
            var countResult = _api.GetDeviceCount(out var count);
            if (countResult != NvmlResult.Success)
            {
                return new GpuProviderSnapshot(
                    ProviderName,
                    StateFor(countResult).ToString().ToLowerInvariant(),
                    driverVersion,
                    libraryVersion,
                    observedAt,
                    [],
                    $"nvmlDeviceGetCount_v2 returned {countResult}.");
            }

            var snapshots = new List<GpuSnapshot>((int)count);
            var processRows = new List<RawGpuProcess>();
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var handleResult = _api.GetDeviceHandle(index, out var device);
                if (handleResult != NvmlResult.Success)
                {
                    continue;
                }

                var snapshot = ReadDevice(device, checked((int)index), observedAt);
                snapshots.Add(snapshot);
                ReadProcesses(device, snapshot.Id, processRows);
            }

            return new GpuProviderSnapshot(
                ProviderName,
                "online",
                driverVersion,
                libraryVersion,
                observedAt,
                snapshots)
            {
                Processes = processRows
                    .GroupBy(row => (row.GpuId, row.ProcessId))
                    .Select(group =>
                    {
                        var memory = group.Select(row => row.UsedGpuMemory)
                            .FirstOrDefault(value => value != ulong.MaxValue, ulong.MaxValue);
                        return new GpuProcessSnapshot(
                            group.Key.GpuId,
                            group.Key.ProcessId,
                            string.Join('+', group.Select(row => row.ContextKind).Distinct(StringComparer.Ordinal)),
                            ObserveProcessMemory(memory, observedAt));
                    })
                    .ToArray(),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Wait();
        try
        {
            if (_initialized)
            {
                _ = _api?.Shutdown();
                _initialized = false;
            }

            _api?.Dispose();
            _api = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private bool EnsureInitialized(out string? error)
    {
        error = null;
        if (_initialized)
        {
            return true;
        }

        if (_api is null && !NvmlNativeApi.TryLoad(out _api, out error))
        {
            return false;
        }

        var result = _api!.Initialize();
        if (result is not (NvmlResult.Success or NvmlResult.AlreadyInitialized))
        {
            error = $"nvmlInit_v2 returned {result}.";
            _api.Dispose();
            _api = null;
            return false;
        }

        _initialized = true;
        return true;
    }

    private GpuSnapshot ReadDevice(nint device, int index, DateTimeOffset observedAt)
    {
        var uuidResult = _api!.GetDeviceUuid(device, out var uuid);
        var nameResult = _api.GetDeviceName(device, out var name);
        var memoryResult = _api.GetMemoryInfo(device, out var memory);
        var utilizationResult = _api.GetUtilization(device, out var utilization);
        var temperatureResult = _api.GetTemperature(device, out var temperature);
        var slowdownTemperatureResult = _api.GetTemperatureThreshold(
            device,
            NvmlTemperatureThreshold.Slowdown,
            out var slowdownTemperature);
        var shutdownTemperatureResult = _api.GetTemperatureThreshold(
            device,
            NvmlTemperatureThreshold.Shutdown,
            out var shutdownTemperature);
        var powerResult = _api.GetPowerUsage(device, out var powerMilliwatts);
        var powerLimitResult = _api.GetPowerLimit(device, out var powerLimitMilliwatts);
        var requestedPowerLimitResult = _api.GetRequestedPowerLimit(device, out var requestedPowerLimitMilliwatts);
        var defaultPowerLimitResult = _api.GetDefaultPowerLimit(device, out var defaultPowerLimitMilliwatts);
        var powerConstraintsResult = _api.GetPowerLimitConstraints(
            device,
            out var minimumPowerLimitMilliwatts,
            out var maximumPowerLimitMilliwatts);
        var fanResult = _api.GetFanSpeed(device, out var fanPercent);
        var fanCountResult = _api.GetFanCount(device, out var fanCount);
        var fanConstraintsResult = _api.GetFanSpeedConstraints(
            device,
            out var minimumFanSpeedPercent,
            out var maximumFanSpeedPercent);
        var graphicsClockResult = _api.GetClock(device, NvmlClockType.Graphics, out var graphicsClock);
        var memoryClockResult = _api.GetClock(device, NvmlClockType.Memory, out var memoryClock);

        var id = uuidResult == NvmlResult.Success && !string.IsNullOrWhiteSpace(uuid)
            ? uuid
            : $"nvidia-index-{index}";

        var snapshot = new GpuSnapshot(
            new GpuId(id),
            index,
            nameResult == NvmlResult.Success && !string.IsNullOrWhiteSpace(name) ? name : $"NVIDIA GPU {index}",
            Observe(memoryResult, memory.Total, observedAt, value => checked((long)value)),
            Observe(memoryResult, memory.Free, observedAt, value => checked((long)value)),
            Observe(memoryResult, memory.Used, observedAt, value => checked((long)value)),
            Observe(utilizationResult, utilization.Gpu, observedAt, value => (double)value),
            Observe(utilizationResult, utilization.Memory, observedAt, value => (double)value),
            Observe(temperatureResult, temperature, observedAt, value => (double)value),
            Observe(powerResult, powerMilliwatts, observedAt, value => value / 1000d),
            Observe(powerLimitResult, powerLimitMilliwatts, observedAt, value => value / 1000d),
            Observe(fanResult, fanPercent, observedAt, value => (double)value),
            Observe(graphicsClockResult, graphicsClock, observedAt, value => checked((int)value)),
            Observe(memoryClockResult, memoryClock, observedAt, value => checked((int)value)))
        {
            RequestedPowerLimitWatts = Observe(
                requestedPowerLimitResult,
                requestedPowerLimitMilliwatts,
                observedAt,
                value => value / 1000d),
            DefaultPowerLimitWatts = Observe(
                defaultPowerLimitResult,
                defaultPowerLimitMilliwatts,
                observedAt,
                value => value / 1000d),
            MinimumPowerLimitWatts = Observe(
                powerConstraintsResult,
                minimumPowerLimitMilliwatts,
                observedAt,
                value => value / 1000d),
            MaximumPowerLimitWatts = Observe(
                powerConstraintsResult,
                maximumPowerLimitMilliwatts,
                observedAt,
                value => value / 1000d),
            FanCount = Observe(fanCountResult, fanCount, observedAt, value => checked((int)value)),
            SlowdownTemperatureCelsius = Observe(
                slowdownTemperatureResult,
                slowdownTemperature,
                observedAt,
                value => (double)value),
            ShutdownTemperatureCelsius = Observe(
                shutdownTemperatureResult,
                shutdownTemperature,
                observedAt,
                value => (double)value),
            MinimumFanSpeedPercent = Observe(
                fanConstraintsResult,
                minimumFanSpeedPercent,
                observedAt,
                value => (double)value),
            MaximumFanSpeedPercent = Observe(
                fanConstraintsResult,
                maximumFanSpeedPercent,
                observedAt,
                value => (double)value),
            Fans = ReadFans(device, fanCountResult, fanCount, observedAt),
        };

        return snapshot;
    }

    private IReadOnlyList<GpuFanSnapshot> ReadFans(
        nint device,
        NvmlResult countResult,
        uint count,
        DateTimeOffset observedAt)
    {
        if (countResult != NvmlResult.Success || count == 0 || count > 32)
        {
            return [];
        }

        var fans = new List<GpuFanSnapshot>(checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            var speedResult = _api!.GetFanSpeed(device, index, out var speed);
            var targetResult = _api.GetTargetFanSpeed(device, index, out var target);
            var policyResult = _api.GetFanControlPolicy(device, index, out var policy);
            fans.Add(new GpuFanSnapshot(
                checked((int)index),
                Observe(speedResult, speed, observedAt, value => (double)value),
                Observe(targetResult, target, observedAt, value => (double)value),
                policyResult == NvmlResult.Success
                    ? new ObservedValue<string>(
                        policy == 1 ? "manual" : "automatic",
                        ObservationState.Available,
                        observedAt,
                        Source)
                    : new ObservedValue<string>(
                        null,
                        StateFor(policyResult),
                        observedAt,
                        Source,
                        policyResult.ToString())));
        }

        return fans;
    }

    private void ReadProcesses(nint device, GpuId gpuId, ICollection<RawGpuProcess> destination)
    {
        Add(_api!.GetComputeProcesses(device, out var compute), compute, "compute");
        Add(_api.GetGraphicsProcesses(device, out var graphics), graphics, "graphics");

        void Add(NvmlResult result, IReadOnlyList<NvmlProcessInfo> values, string contextKind)
        {
            if (result != NvmlResult.Success)
            {
                return;
            }

            foreach (var value in values)
            {
                destination.Add(new RawGpuProcess(
                    gpuId,
                    checked((int)value.ProcessId),
                    contextKind,
                    value.UsedGpuMemory));
            }
        }
    }

    private static ObservedValue<long> ObserveProcessMemory(ulong value, DateTimeOffset observedAt) =>
        value == ulong.MaxValue
            ? new ObservedValue<long>(
                default,
                ObservationState.Unavailable,
                observedAt,
                Source,
                "Per-process VRAM is not reported by NVML under Windows WDDM.")
            : new ObservedValue<long>(checked((long)value), ObservationState.Available, observedAt, Source);

    private static ObservedValue<TOutput> Observe<TInput, TOutput>(
        NvmlResult result,
        TInput value,
        DateTimeOffset observedAt,
        Func<TInput, TOutput> convert) =>
        result == NvmlResult.Success
            ? new ObservedValue<TOutput>(convert(value), ObservationState.Available, observedAt, Source)
            : new ObservedValue<TOutput>(default, StateFor(result), observedAt, Source, result.ToString());

    private static ObservationState StateFor(NvmlResult result) => result switch
    {
        NvmlResult.NotSupported or NvmlResult.FunctionNotFound or NvmlResult.FrequencyNotSupported =>
            ObservationState.Unsupported,
        NvmlResult.NoPermission => ObservationState.PermissionDenied,
        NvmlResult.GpuIsLost or NvmlResult.DriverNotLoaded or NvmlResult.LibraryNotFound =>
            ObservationState.Unavailable,
        _ => ObservationState.Error,
    };

    private static string? ReadString(GetString getter) =>
        getter(out var value) == NvmlResult.Success ? value : null;

    private delegate NvmlResult GetString(out string value);

    private sealed record RawGpuProcess(
        GpuId GpuId,
        int ProcessId,
        string ContextKind,
        ulong UsedGpuMemory);
}
