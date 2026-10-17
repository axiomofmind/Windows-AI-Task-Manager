using AITaskManager.Adapters.Nvidia;

namespace AITaskManager.UnitTests;

public sealed class FanSafetyTests
{
    [Fact]
    public void RapidTemperatureRiseImmediatelyRequestsMaximumCooling()
    {
        var policy = new FanWatchdogPolicy(30, 30, 100, 90);
        var start = DateTimeOffset.UtcNow;

        _ = policy.Evaluate(50, start, 30);
        var decision = policy.Evaluate(58, start.AddSeconds(4), 30);

        Assert.True(decision.IsValid);
        Assert.Equal("emergency", decision.State);
        Assert.Equal(40, decision.DesiredDutyPercent);
        Assert.Contains("rapid", decision.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImplausibleTemperatureJumpFaultsInsteadOfHoldingManualDuty()
    {
        var policy = new FanWatchdogPolicy(30, 30, 100, 90);
        var start = DateTimeOffset.UtcNow;

        _ = policy.Evaluate(40, start, 30);
        var decision = policy.Evaluate(80, start.AddSeconds(1), 30);

        Assert.False(decision.IsValid);
        Assert.Equal("faulted", decision.State);
    }

    [Fact]
    public void EmergencyStateUsesHysteresisAndMinimumHold()
    {
        var policy = new FanWatchdogPolicy(30, 30, 100, 90);
        var start = DateTimeOffset.UtcNow;

        var hot = policy.Evaluate(90, start, 80);
        var held = policy.Evaluate(70, start.AddSeconds(5), hot.DesiredDutyPercent);
        var released = policy.Evaluate(70, start.AddSeconds(11), held.DesiredDutyPercent);

        Assert.Equal("emergency", hot.State);
        Assert.Equal("emergency", held.State);
        Assert.Equal("manual", released.State);
    }

    [Fact]
    public void PolicyOutputIsAlwaysBoundedAcrossRecordedThermalRamp()
    {
        var policy = new FanWatchdogPolicy(35, 30, 100, 90);
        var observedAt = DateTimeOffset.UtcNow;
        int? applied = null;

        foreach (var temperature in Enumerable.Range(30, 61).Concat(Enumerable.Range(30, 61).Reverse()))
        {
            var decision = policy.Evaluate(temperature, observedAt, applied);
            Assert.True(decision.IsValid);
            Assert.InRange(decision.DesiredDutyPercent, 30, 100);
            applied = decision.DesiredDutyPercent;
            observedAt = observedAt.AddSeconds(3);
        }
    }

    [Fact]
    public void PartialMultiFanWriteRestoresEveryChannelToAutomatic()
    {
        using var api = new FaultInjectingNvmlApi(2) { FailSetFanIndex = 1 };

        var result = NvidiaFanOperations.SetAndVerifyManual(api, (nint)1, 2, 55);

        Assert.Equal(NvmlResult.GpuIsLost, result);
        Assert.Equal([0U, 0U], api.Policies);
        Assert.True(api.RestoreCalls >= 2);
    }

    [Fact]
    public void VerificationMismatchRestoresAutomaticPolicy()
    {
        using var api = new FaultInjectingNvmlApi(1) { ReturnMismatchedTarget = true };

        var result = NvidiaFanOperations.SetAndVerifyManual(api, (nint)1, 1, 55);

        Assert.Equal(NvmlResult.Unknown, result);
        Assert.Equal(0U, api.Policies[0]);
    }

    [Fact]
    public void RestoreRetriesAreBoundedAndEventuallyVerify()
    {
        using var api = new FaultInjectingNvmlApi(1) { RestoreFailuresRemaining = 2 };
        api.Policies[0] = 1;

        var result = NvidiaFanOperations.RestoreAndVerifyAutomatic(
            api,
            (nint)1,
            1,
            attempts: 3,
            retryDelay: TimeSpan.Zero);

        Assert.Equal(NvmlResult.Success, result);
        Assert.Equal(3, api.RestoreCalls);
        Assert.Equal(0U, api.Policies[0]);
    }

    [Fact]
    public void OwnershipJournalUsesOpaqueNameAndCanBeClearedAfterRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "AITaskManager.Tests", Guid.NewGuid().ToString("N"));
        const string gpuId = "GPU-sensitive-test-identifier";
        try
        {
            FanOwnershipJournal.Record(gpuId, 2, Guid.NewGuid().ToString("N"), root);

            var path = FanOwnershipJournal.PathFor(gpuId, root);
            Assert.True(FanOwnershipJournal.Exists(gpuId, root));
            Assert.DoesNotContain(gpuId, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(gpuId, File.ReadAllText(path), StringComparison.Ordinal);

            FanOwnershipJournal.Clear(gpuId, root);
            Assert.False(FanOwnershipJournal.Exists(gpuId, root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OwnershipLockCanBeReleasedFromAnAsyncContinuation()
    {
        var root = Path.Combine(Path.GetTempPath(), "AITaskManager.Tests", Guid.NewGuid().ToString("N"));
        const string gpuId = "GPU-lock-test";
        try
        {
            var ownership = FanOwnershipLock.TryAcquire(gpuId, root);
            Assert.NotNull(ownership);
            Assert.Null(FanOwnershipLock.TryAcquire(gpuId, root));

            await Task.Run(ownership.Dispose);

            using var reacquired = FanOwnershipLock.TryAcquire(gpuId, root);
            Assert.NotNull(reacquired);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FaultInjectingNvmlApi(int fanCount) : INvmlApi
    {
        public uint[] Policies { get; } = new uint[fanCount];
        public uint[] Targets { get; } = new uint[fanCount];
        public int? FailSetFanIndex { get; init; }
        public bool ReturnMismatchedTarget { get; init; }
        public int RestoreFailuresRemaining { get; set; }
        public int RestoreCalls { get; private set; }
        public NvmlResult SetFanSpeed(nint device, uint fanIndex, uint value)
        {
            if (FailSetFanIndex == (int)fanIndex) return NvmlResult.GpuIsLost;
            Policies[fanIndex] = 1;
            Targets[fanIndex] = value;
            return NvmlResult.Success;
        }
        public NvmlResult RestoreDefaultFanSpeed(nint device, uint fanIndex)
        {
            RestoreCalls++;
            if (RestoreFailuresRemaining-- > 0) return NvmlResult.Unknown;
            Policies[fanIndex] = 0;
            Targets[fanIndex] = 30;
            return NvmlResult.Success;
        }
        public NvmlResult GetFanControlPolicy(nint device, uint fanIndex, out uint value) { value = Policies[fanIndex]; return NvmlResult.Success; }
        public NvmlResult GetTargetFanSpeed(nint device, uint fanIndex, out uint value) { value = ReturnMismatchedTarget ? Targets[fanIndex] + 1 : Targets[fanIndex]; return NvmlResult.Success; }
        public NvmlResult Initialize() => NvmlResult.Success;
        public NvmlResult Shutdown() => NvmlResult.Success;
        public NvmlResult GetDeviceCount(out uint count) { count = 1; return NvmlResult.Success; }
        public NvmlResult GetDeviceHandle(uint index, out nint device) { device = (nint)1; return NvmlResult.Success; }
        public NvmlResult GetDeviceUuid(nint device, out string value) { value = "GPU-test"; return NvmlResult.Success; }
        public NvmlResult GetDeviceName(nint device, out string value) { value = "Test"; return NvmlResult.Success; }
        public NvmlResult GetMemoryInfo(nint device, out NvmlMemoryInfo value) { value = default; return NvmlResult.Success; }
        public NvmlResult GetUtilization(nint device, out NvmlUtilizationInfo value) { value = default; return NvmlResult.Success; }
        public NvmlResult GetTemperature(nint device, out uint value) { value = 40; return NvmlResult.Success; }
        public NvmlResult GetTemperatureThreshold(nint device, NvmlTemperatureThreshold threshold, out uint value) { value = 95; return NvmlResult.Success; }
        public NvmlResult GetPowerUsage(nint device, out uint value) { value = 0; return NvmlResult.NotSupported; }
        public NvmlResult GetPowerLimit(nint device, out uint value) { value = 0; return NvmlResult.NotSupported; }
        public NvmlResult GetRequestedPowerLimit(nint device, out uint value) { value = 0; return NvmlResult.NotSupported; }
        public NvmlResult GetDefaultPowerLimit(nint device, out uint value) { value = 0; return NvmlResult.NotSupported; }
        public NvmlResult GetPowerLimitConstraints(nint device, out uint minimum, out uint maximum) { minimum = maximum = 0; return NvmlResult.NotSupported; }
        public NvmlResult SetPowerLimit(nint device, uint value) => NvmlResult.NotSupported;
        public NvmlResult GetFanSpeed(nint device, out uint value) { value = 30; return NvmlResult.Success; }
        public NvmlResult GetFanCount(nint device, out uint value) { value = (uint)Policies.Length; return NvmlResult.Success; }
        public NvmlResult GetFanSpeed(nint device, uint fanIndex, out uint value) { value = Targets[fanIndex]; return NvmlResult.Success; }
        public NvmlResult GetFanSpeedConstraints(nint device, out uint minimum, out uint maximum) { minimum = 30; maximum = 100; return NvmlResult.Success; }
        public NvmlResult GetClock(nint device, NvmlClockType clockType, out uint value) { value = 0; return NvmlResult.NotSupported; }
        public NvmlResult GetDriverVersion(out string value) { value = "test"; return NvmlResult.Success; }
        public NvmlResult GetLibraryVersion(out string value) { value = "test"; return NvmlResult.Success; }
        public NvmlResult GetComputeProcesses(nint device, out IReadOnlyList<NvmlProcessInfo> processes) { processes = []; return NvmlResult.Success; }
        public NvmlResult GetGraphicsProcesses(nint device, out IReadOnlyList<NvmlProcessInfo> processes) { processes = []; return NvmlResult.Success; }
        public void Dispose() { }
    }
}
