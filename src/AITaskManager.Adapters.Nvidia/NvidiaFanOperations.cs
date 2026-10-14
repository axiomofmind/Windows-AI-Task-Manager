namespace AITaskManager.Adapters.Nvidia;

public static class NvidiaFanOperations
{
    public static NvmlResult SetAndVerifyManual(INvmlApi api, nint device, uint fanCount, uint duty)
    {
        ArgumentNullException.ThrowIfNull(api);
        for (uint fan = 0; fan < fanCount; fan++)
        {
            var result = api.SetFanSpeed(device, fan, duty);
            if (result != NvmlResult.Success)
            {
                _ = RestoreAndVerifyAutomatic(api, device, fanCount);
                return result;
            }
        }

        for (uint fan = 0; fan < fanCount; fan++)
        {
            var policy = api.GetFanControlPolicy(device, fan, out var currentPolicy);
            if (policy != NvmlResult.Success)
            {
                _ = RestoreAndVerifyAutomatic(api, device, fanCount);
                return policy;
            }
            if (currentPolicy != 1)
            {
                _ = RestoreAndVerifyAutomatic(api, device, fanCount);
                return NvmlResult.Unknown;
            }
            var target = api.GetTargetFanSpeed(device, fan, out var currentTarget);
            if (target != NvmlResult.Success)
            {
                _ = RestoreAndVerifyAutomatic(api, device, fanCount);
                return target;
            }
            if (currentTarget != duty)
            {
                _ = RestoreAndVerifyAutomatic(api, device, fanCount);
                return NvmlResult.Unknown;
            }
        }

        return NvmlResult.Success;
    }

    public static NvmlResult RestoreAndVerifyAutomatic(
        INvmlApi api,
        nint device,
        uint fanCount,
        int attempts = 3,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (fanCount is 0 or > 16) return NvmlResult.InvalidArgument;
        attempts = Math.Clamp(attempts, 1, 5);
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(200);
        var last = NvmlResult.Unknown;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            last = NvmlResult.Success;
            for (uint fan = 0; fan < fanCount; fan++)
            {
                var result = api.RestoreDefaultFanSpeed(device, fan);
                if (result != NvmlResult.Success) last = result;
            }

            if (last == NvmlResult.Success)
            {
                for (uint fan = 0; fan < fanCount; fan++)
                {
                    var policy = api.GetFanControlPolicy(device, fan, out var currentPolicy);
                    if (policy == NvmlResult.Success && currentPolicy != 0) last = NvmlResult.Unknown;
                    else if (policy is not (NvmlResult.Success or NvmlResult.NotSupported or NvmlResult.FunctionNotFound)) last = policy;
                }
            }

            if (last == NvmlResult.Success) return NvmlResult.Success;
            if (attempt + 1 < attempts && delay > TimeSpan.Zero) Thread.Sleep(delay);
        }

        return last;
    }
}
