using AITaskManager.Adapters.Nvidia;
using AITaskManager.Core;

namespace AITaskManager.UnitTests;

public sealed class NvmlGpuTelemetryProviderTests
{
    [Theory]
    [InlineData()]
    [InlineData("--hardware-helper", "power-set", "not-a-gpu", "300000", "00000000000000000000000000000000")]
    [InlineData("--hardware-helper", "arbitrary-command", "GPU-test", "300000", "00000000000000000000000000000000")]
    [InlineData("--hardware-helper", "fan-restore", "not-a-gpu", "00000000000000000000000000000000")]
    [InlineData("--hardware-helper", "fan-session", "bad-pipe", "bad-secret", "1", "00000000000000000000000000000000")]
    public void PrivilegedCommandRejectsMalformedOrNonAllowListedInput(params string[] args)
    {
        Assert.Equal(64, NvidiaPrivilegedCommand.Execute(args));
    }

    [Theory]
    [InlineData(30, 50, 85, 30, 100, 30)]
    [InlineData(45, 65, 85, 30, 100, 45)]
    [InlineData(30, 75, 85, 30, 100, 72)]
    [InlineData(30, 85, 85, 30, 100, 100)]
    [InlineData(30, 95, 85, 30, 100, 100)]
    public void FanSafetyCurveHonorsRequestedFloorAndThermalCeiling(
        int requested,
        int temperature,
        int ceiling,
        int minimum,
        int maximum,
        int expected)
    {
        Assert.Equal(expected, FanSafetyCurve.CalculateDuty(requested, temperature, ceiling, minimum, maximum));
    }

    [Fact]
    public void FanSafetyCurveIsMonotonicAcrossItsThermalRamp()
    {
        var values = Enumerable.Range(50, 36)
            .Select(temperature => FanSafetyCurve.CalculateDuty(30, temperature, 85, 30, 100))
            .ToArray();

        Assert.Equal(values.OrderBy(value => value), values);
    }

    [Fact]
    public async Task MapsEachMetricIndependentlyAndPreservesUnsupportedFanState()
    {
        using var provider = new NvmlGpuTelemetryProvider(new FakeNvmlApi());

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        var gpu = Assert.Single(snapshot.Gpus);
        Assert.Equal("online", snapshot.State);
        Assert.Equal("GPU-test-uuid", gpu.Id.Value);
        Assert.Equal(24L * 1024 * 1024 * 1024, gpu.TotalVramBytes.Value);
        Assert.Equal(42d, gpu.UtilizationPercent.Value);
        Assert.Equal(67d, gpu.TemperatureCelsius.Value);
        Assert.Equal(125.5d, gpu.PowerUsageWatts.Value);
        Assert.Equal(300d, gpu.RequestedPowerLimitWatts?.Value);
        Assert.Equal(300d, gpu.DefaultPowerLimitWatts?.Value);
        Assert.Equal(250d, gpu.MinimumPowerLimitWatts?.Value);
        Assert.Equal(300d, gpu.MaximumPowerLimitWatts?.Value);
        Assert.Equal(ObservationState.Unsupported, gpu.FanSpeedPercent.State);
        Assert.Equal(2, gpu.FanCount?.Value);
        Assert.All(gpu.Fans, fan => Assert.Equal("automatic", fan.ControlPolicy.Value));
        var process = Assert.Single(snapshot.Processes);
        Assert.Equal(1234, process.ProcessId);
        Assert.Equal("compute", process.ContextKind);
        Assert.Equal(ObservationState.Unavailable, process.UsedVramBytes.State);
    }

    private sealed class FakeNvmlApi : INvmlApi
    {
        public NvmlResult Initialize() => NvmlResult.Success;

        public NvmlResult Shutdown() => NvmlResult.Success;

        public NvmlResult GetDeviceCount(out uint count)
        {
            count = 1;
            return NvmlResult.Success;
        }

        public NvmlResult GetDeviceHandle(uint index, out nint device)
        {
            device = (nint)1;
            return NvmlResult.Success;
        }

        public NvmlResult GetDeviceUuid(nint device, out string value)
        {
            value = "GPU-test-uuid";
            return NvmlResult.Success;
        }

        public NvmlResult GetDeviceName(nint device, out string value)
        {
            value = "NVIDIA Test GPU";
            return NvmlResult.Success;
        }

        public NvmlResult GetMemoryInfo(nint device, out NvmlMemoryInfo value)
        {
            value = new NvmlMemoryInfo(24UL * 1024 * 1024 * 1024, 16UL * 1024 * 1024 * 1024, 8UL * 1024 * 1024 * 1024);
            return NvmlResult.Success;
        }

        public NvmlResult GetUtilization(nint device, out NvmlUtilizationInfo value)
        {
            value = new NvmlUtilizationInfo(42, 18);
            return NvmlResult.Success;
        }

        public NvmlResult GetTemperature(nint device, out uint value)
        {
            value = 67;
            return NvmlResult.Success;
        }

        public NvmlResult GetTemperatureThreshold(
            nint device,
            NvmlTemperatureThreshold threshold,
            out uint value)
        {
            value = threshold == NvmlTemperatureThreshold.Slowdown ? 90u : 95u;
            return NvmlResult.Success;
        }

        public NvmlResult GetPowerUsage(nint device, out uint value)
        {
            value = 125_500;
            return NvmlResult.Success;
        }

        public NvmlResult GetPowerLimit(nint device, out uint value)
        {
            value = 300_000;
            return NvmlResult.Success;
        }

        public NvmlResult GetRequestedPowerLimit(nint device, out uint value)
        {
            value = 300_000;
            return NvmlResult.Success;
        }

        public NvmlResult GetDefaultPowerLimit(nint device, out uint value)
        {
            value = 300_000;
            return NvmlResult.Success;
        }

        public NvmlResult GetPowerLimitConstraints(nint device, out uint minimum, out uint maximum)
        {
            minimum = 250_000;
            maximum = 300_000;
            return NvmlResult.Success;
        }

        public NvmlResult SetPowerLimit(nint device, uint value) => NvmlResult.NoPermission;

        public NvmlResult GetFanSpeed(nint device, out uint value)
        {
            value = 0;
            return NvmlResult.NotSupported;
        }

        public NvmlResult GetFanCount(nint device, out uint value)
        {
            value = 2;
            return NvmlResult.Success;
        }

        public NvmlResult GetFanSpeed(nint device, uint fanIndex, out uint value)
        {
            value = 30 + fanIndex;
            return NvmlResult.Success;
        }

        public NvmlResult GetTargetFanSpeed(nint device, uint fanIndex, out uint value)
        {
            value = 35 + fanIndex;
            return NvmlResult.Success;
        }

        public NvmlResult GetFanControlPolicy(nint device, uint fanIndex, out uint value)
        {
            value = 0;
            return NvmlResult.Success;
        }

        public NvmlResult GetFanSpeedConstraints(nint device, out uint minimum, out uint maximum)
        {
            minimum = 30;
            maximum = 100;
            return NvmlResult.Success;
        }

        public NvmlResult SetFanSpeed(nint device, uint fanIndex, uint value) => NvmlResult.NoPermission;

        public NvmlResult RestoreDefaultFanSpeed(nint device, uint fanIndex) => NvmlResult.NoPermission;

        public NvmlResult GetClock(nint device, NvmlClockType clockType, out uint value)
        {
            value = clockType == NvmlClockType.Memory ? 5_000u : 2_100u;
            return NvmlResult.Success;
        }

        public NvmlResult GetDriverVersion(out string value)
        {
            value = "test-driver";
            return NvmlResult.Success;
        }

        public NvmlResult GetLibraryVersion(out string value)
        {
            value = "test-nvml";
            return NvmlResult.Success;
        }

        public NvmlResult GetComputeProcesses(nint device, out IReadOnlyList<NvmlProcessInfo> processes)
        {
            processes = [new NvmlProcessInfo(1234, ulong.MaxValue)];
            return NvmlResult.Success;
        }

        public NvmlResult GetGraphicsProcesses(nint device, out IReadOnlyList<NvmlProcessInfo> processes)
        {
            processes = [];
            return NvmlResult.Success;
        }

        public void Dispose()
        {
        }
    }
}
