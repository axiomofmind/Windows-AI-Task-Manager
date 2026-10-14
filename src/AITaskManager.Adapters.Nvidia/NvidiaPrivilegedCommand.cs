using System.Globalization;

namespace AITaskManager.Adapters.Nvidia;

public static class NvidiaPrivilegedCommand
{
    public const string Switch = "--hardware-helper";

    public static bool IsHelperInvocation(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], Switch, StringComparison.Ordinal);

    public static int Execute(IReadOnlyList<string> args)
    {
        if (args.Count > 1 && string.Equals(args[1], "fan-session", StringComparison.Ordinal))
        {
            return NvidiaFanHelperSession.RunAsync(args).GetAwaiter().GetResult();
        }

        if (args.Count == 4 &&
            string.Equals(args[0], Switch, StringComparison.Ordinal) &&
            string.Equals(args[1], "fan-restore", StringComparison.Ordinal) &&
            IsGpuUuid(args[2]) &&
            Guid.TryParseExact(args[3], "N", out _))
        {
            return RestoreFans(args[2]);
        }

        if (args.Count != 5 ||
            !string.Equals(args[0], Switch, StringComparison.Ordinal) ||
            !string.Equals(args[1], "power-set", StringComparison.Ordinal) ||
            !IsGpuUuid(args[2]) ||
            !uint.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var milliwatts) ||
            milliwatts is < 1_000 or > 2_000_000 ||
            !Guid.TryParseExact(args[4], "N", out _))
        {
            return 64;
        }

        if (!NvmlNativeApi.TryLoad(out var api, out _))
        {
            return 69;
        }

        using (api)
        {
            var initializeResult = api!.Initialize();
            if (initializeResult is not (NvmlResult.Success or NvmlResult.AlreadyInitialized))
            {
                return MapResult(initializeResult);
            }

            try
            {
                var findResult = FindDevice(api, args[2], out var device);
                if (findResult != NvmlResult.Success)
                {
                    return MapResult(findResult);
                }

                var constraintResult = api.GetPowerLimitConstraints(device, out var minimum, out var maximum);
                if (constraintResult != NvmlResult.Success)
                {
                    return MapResult(constraintResult);
                }

                if (milliwatts < minimum || milliwatts > maximum)
                {
                    return 65;
                }

                var setResult = api.SetPowerLimit(device, milliwatts);
                if (setResult != NvmlResult.Success)
                {
                    return MapResult(setResult);
                }

                var verifyResult = api.GetRequestedPowerLimit(device, out var applied);
                return verifyResult == NvmlResult.Success && applied == milliwatts
                    ? 0
                    : verifyResult == NvmlResult.Success ? 74 : MapResult(verifyResult);
            }
            finally
            {
                _ = api.Shutdown();
            }
        }
    }

    private static int RestoreFans(string gpuUuid)
    {
        using var ownershipLock = FanOwnershipLock.TryAcquire(gpuUuid);
        if (ownershipLock is null)
        {
            return 73;
        }

        if (!NvmlNativeApi.TryLoad(out var api, out _))
        {
            return 69;
        }

        using (api)
        {
            var initialize = api!.Initialize();
            if (initialize is not (NvmlResult.Success or NvmlResult.AlreadyInitialized))
            {
                return MapResult(initialize);
            }

            try
            {
                var find = FindDevice(api, gpuUuid, out var device);
                uint count = 0;
                var countResult = find == NvmlResult.Success ? api.GetFanCount(device, out count) : find;
                if (countResult != NvmlResult.Success || count is 0 or > 16)
                {
                    return MapResult(countResult);
                }

                var restore = NvidiaFanOperations.RestoreAndVerifyAutomatic(api, device, count);
                if (restore != NvmlResult.Success) return MapResult(restore);
                FanOwnershipJournal.Clear(gpuUuid);
                return 0;
            }
            finally
            {
                _ = api.Shutdown();
            }
        }
    }

    private static NvmlResult FindDevice(INvmlApi api, string gpuUuid, out nint device)
    {
        device = 0;
        var countResult = api.GetDeviceCount(out var count);
        if (countResult != NvmlResult.Success)
        {
            return countResult;
        }

        for (uint index = 0; index < count; index++)
        {
            var handleResult = api.GetDeviceHandle(index, out var candidate);
            if (handleResult != NvmlResult.Success)
            {
                continue;
            }

            var uuidResult = api.GetDeviceUuid(candidate, out var candidateUuid);
            if (uuidResult == NvmlResult.Success &&
                string.Equals(candidateUuid, gpuUuid, StringComparison.OrdinalIgnoreCase))
            {
                device = candidate;
                return NvmlResult.Success;
            }
        }

        return NvmlResult.NotFound;
    }

    private static bool IsGpuUuid(string value) =>
        value.Length is >= 8 and <= 96 &&
        value.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static int MapResult(NvmlResult result) => result switch
    {
        NvmlResult.NoPermission => 77,
        NvmlResult.NotSupported or NvmlResult.FunctionNotFound => 78,
        NvmlResult.NotFound => 66,
        NvmlResult.GpuIsLost => 70,
        _ => 74,
    };
}
