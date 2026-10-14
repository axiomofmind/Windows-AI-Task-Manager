using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AITaskManager.Adapters.Nvidia;

public sealed record FanHelperCommand(
    string Kind,
    string Secret,
    string? GpuId = null,
    int? DutyPercent = null,
    int? LeaseSeconds = null,
    int? FailsafeTemperatureCelsius = null);

public sealed record FanHelperReply(
    bool Success,
    string State,
    int? AppliedDutyPercent,
    int? TemperatureCelsius,
    int LeaseRemainingSeconds,
    string Detail);

internal static class NvidiaFanHelperSession
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count != 6 ||
            !string.Equals(args[0], NvidiaPrivilegedCommand.Switch, StringComparison.Ordinal) ||
            !string.Equals(args[1], "fan-session", StringComparison.Ordinal) ||
            !IsPipeName(args[2]) ||
            !IsSecret(args[3]) ||
            !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId) ||
            parentProcessId <= 0 ||
            !Guid.TryParseExact(args[5], "N", out _))
        {
            return 64;
        }

        await using var pipe = new NamedPipeClientStream(
            ".",
            args[2],
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await pipe.ConnectAsync(connectionTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 75;
        }

        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var firstLine = await ReadLineAsync(reader, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var enable = Deserialize(firstLine);
        if (enable is null ||
            !string.Equals(enable.Kind, "enable", StringComparison.Ordinal) ||
            !SecretEquals(args[3], enable.Secret) ||
            !IsGpuUuid(enable.GpuId) ||
            enable.DutyPercent is null ||
            enable.LeaseSeconds is null ||
            enable.FailsafeTemperatureCelsius is null)
        {
            await ReplyAsync(writer, new(false, "rejected", null, null, 0, "Invalid enable request.")).ConfigureAwait(false);
            return 65;
        }

        using var ownershipLock = FanOwnershipLock.TryAcquire(enable.GpuId!);
        if (ownershipLock is null)
        {
            await ReplyAsync(writer, new(false, "rejected", null, null, 0, "Another process already owns this GPU fan-control channel.")).ConfigureAwait(false);
            return 73;
        }

        if (!NvmlNativeApi.TryLoad(out var api, out var loadError))
        {
            await ReplyAsync(writer, new(false, "faulted", null, null, 0, loadError ?? "NVML unavailable.")).ConfigureAwait(false);
            return 69;
        }

        using (api)
        {
            var initialize = api!.Initialize();
            if (initialize is not (NvmlResult.Success or NvmlResult.AlreadyInitialized))
            {
                await ReplyAsync(writer, new(false, "faulted", null, null, 0, $"NVML initialization returned {initialize}.")).ConfigureAwait(false);
                return MapResult(initialize);
            }

            nint device = 0;
            uint fanCount = 0;
            var restored = false;
            try
            {
                var find = FindDevice(api, enable.GpuId!, out device);
                var countResult = find == NvmlResult.Success ? api.GetFanCount(device, out fanCount) : find;
                uint minimumDuty = 0;
                uint maximumDuty = 0;
                var constraintsResult = countResult == NvmlResult.Success
                    ? api.GetFanSpeedConstraints(device, out minimumDuty, out maximumDuty)
                    : countResult;
                if (constraintsResult != NvmlResult.Success || fanCount is 0 or > 16)
                {
                    await ReplyAsync(writer, new(false, "faulted", null, null, 0, $"Fan capability discovery returned {constraintsResult}.")).ConfigureAwait(false);
                    return MapResult(constraintsResult);
                }

                if (FanOwnershipJournal.Exists(enable.GpuId!))
                {
                    var recovery = NvidiaFanOperations.RestoreAndVerifyAutomatic(api, device, fanCount);
                    if (recovery != NvmlResult.Success)
                    {
                        await ReplyAsync(writer, new(false, "faulted", null, null, 0, "A previous ownership record exists and automatic-policy recovery failed.")).ConfigureAwait(false);
                        return MapResult(recovery);
                    }

                    FanOwnershipJournal.Clear(enable.GpuId!);
                }

                var slowdownResult = api.GetTemperatureThreshold(
                    device,
                    NvmlTemperatureThreshold.Slowdown,
                    out var slowdownTemperature);
                if (slowdownResult != NvmlResult.Success || slowdownTemperature is < 65 or > 120)
                {
                    await ReplyAsync(writer, new(false, "faulted", null, null, 0, "A defensible device thermal ceiling could not be established.")).ConfigureAwait(false);
                    return MapResult(slowdownResult);
                }

                var minimum = checked((int)minimumDuty);
                var maximum = checked((int)maximumDuty);
                var requestedDuty = enable.DutyPercent.Value;
                if (requestedDuty < minimum || requestedDuty > maximum)
                {
                    await ReplyAsync(writer, new(false, "rejected", null, null, 0, $"Duty must be within {minimum}–{maximum} percent.")).ConfigureAwait(false);
                    return 65;
                }

                var configuredFailsafe = enable.FailsafeTemperatureCelsius.Value;
                var emergencyCeiling = Math.Min(configuredFailsafe, checked((int)slowdownTemperature) - 5);
                if (emergencyCeiling < 60)
                {
                    await ReplyAsync(writer, new(false, "faulted", null, null, 0, "The derived emergency ceiling is unsafe.")).ConfigureAwait(false);
                    return 65;
                }

                var leaseSeconds = Math.Clamp(enable.LeaseSeconds.Value, 3, 15);
                var leaseDeadline = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds);
                var temperatureResult = api.GetTemperature(device, out var initialTemperature);
                if (temperatureResult != NvmlResult.Success || initialTemperature > 120)
                {
                    await ReplyAsync(writer, new(false, "faulted", null, null, 0, "Fresh temperature telemetry is unavailable.")).ConfigureAwait(false);
                    return MapResult(temperatureResult);
                }

                var policy = new FanWatchdogPolicy(requestedDuty, minimum, maximum, emergencyCeiling);
                var initialDecision = policy.Evaluate(checked((int)initialTemperature), DateTimeOffset.UtcNow, null);
                var appliedDuty = initialDecision.DesiredDutyPercent;
                FanOwnershipJournal.Record(enable.GpuId!, fanCount, args[5]);
                var setResult = NvidiaFanOperations.SetAndVerifyManual(api, device, fanCount, checked((uint)appliedDuty));
                if (setResult != NvmlResult.Success)
                {
                    if (NvidiaFanOperations.RestoreAndVerifyAutomatic(api, device, fanCount) == NvmlResult.Success)
                        FanOwnershipJournal.Clear(enable.GpuId!);
                    await ReplyAsync(writer, new(false, "faulted", null, checked((int)initialTemperature), 0, "Manual fan control could not be applied and verified.")).ConfigureAwait(false);
                    return MapResult(setResult);
                }

                await ReplyAsync(writer, Status(
                    "manual",
                    appliedDuty,
                    checked((int)initialTemperature),
                    leaseDeadline,
                    $"Manual minimum active; emergency ceiling {emergencyCeiling} °C.")).ConfigureAwait(false);

                Task<string?>? pendingRead = null;
                var state = "manual";
                while (pipe.IsConnected)
                {
                    pendingRead ??= reader.ReadLineAsync();
                    var tick = Task.Delay(TimeSpan.FromMilliseconds(500));
                    var completed = await Task.WhenAny(pendingRead, tick).ConfigureAwait(false);
                    if (completed == pendingRead)
                    {
                        var line = await pendingRead.ConfigureAwait(false);
                        pendingRead = null;
                        if (line is null)
                        {
                            break;
                        }

                        var command = Deserialize(line);
                        if (command is null || !SecretEquals(args[3], command.Secret))
                        {
                            await ReplyAsync(writer, new(false, "faulted", appliedDuty, null, 0, "Authentication or command parsing failed.")).ConfigureAwait(false);
                            break;
                        }

                        if (string.Equals(command.Kind, "restore", StringComparison.Ordinal))
                        {
                            restored = NvidiaFanOperations.RestoreAndVerifyAutomatic(api, device, fanCount) == NvmlResult.Success;
                            if (restored) FanOwnershipJournal.Clear(enable.GpuId!);
                            await ReplyAsync(writer, new(restored, restored ? "automatic" : "restore-failed", null, null, 0, restored ? "NVIDIA automatic control restored." : "Automatic restoration failed.")).ConfigureAwait(false);
                            return restored ? 0 : 74;
                        }

                        if (string.Equals(command.Kind, "update", StringComparison.Ordinal))
                        {
                            if (command.DutyPercent is null || command.DutyPercent < minimum || command.DutyPercent > maximum)
                            {
                                await ReplyAsync(writer, new(false, state, appliedDuty, null, Remaining(leaseDeadline), $"Duty must be within {minimum}–{maximum} percent.")).ConfigureAwait(false);
                                continue;
                            }

                            requestedDuty = command.DutyPercent.Value;
                            policy.UpdateRequestedDuty(requestedDuty);
                        }
                        else if (!string.Equals(command.Kind, "heartbeat", StringComparison.Ordinal))
                        {
                            await ReplyAsync(writer, new(false, state, appliedDuty, null, Remaining(leaseDeadline), "The helper rejected a non-allow-listed command.")).ConfigureAwait(false);
                            break;
                        }

                        leaseDeadline = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds);
                        var heartbeatTemperatureResult = api.GetTemperature(device, out var heartbeatTemperature);
                        await ReplyAsync(writer, heartbeatTemperatureResult == NvmlResult.Success
                            ? Status(state, appliedDuty, checked((int)heartbeatTemperature), leaseDeadline, state == "emergency" ? "Emergency cooling active." : "Watchdog healthy.")
                            : new(false, "faulted", appliedDuty, null, 0, "Temperature telemetry failed.")).ConfigureAwait(false);
                    }

                    if (!IsProcessAlive(parentProcessId) || DateTimeOffset.UtcNow >= leaseDeadline)
                    {
                        break;
                    }

                    var currentTemperatureResult = api.GetTemperature(device, out var currentTemperature);
                    if (currentTemperatureResult != NvmlResult.Success)
                    {
                        break;
                    }

                    var decision = policy.Evaluate(checked((int)currentTemperature), DateTimeOffset.UtcNow, appliedDuty);
                    if (!decision.IsValid)
                    {
                        state = "faulted";
                        break;
                    }

                    state = decision.State;
                    var nextDuty = decision.DesiredDutyPercent;
                    if (nextDuty != appliedDuty)
                    {
                        var updateResult = NvidiaFanOperations.SetAndVerifyManual(api, device, fanCount, checked((uint)nextDuty));
                        if (updateResult != NvmlResult.Success)
                        {
                            break;
                        }

                        appliedDuty = nextDuty;
                    }
                }

                return 0;
            }
            finally
            {
                if (device != 0 && fanCount > 0 && !restored)
                {
                    restored = NvidiaFanOperations.RestoreAndVerifyAutomatic(api, device, fanCount) == NvmlResult.Success;
                    if (restored && enable.GpuId is not null) FanOwnershipJournal.Clear(enable.GpuId);
                }

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
            if (api.GetDeviceHandle(index, out var candidate) == NvmlResult.Success &&
                api.GetDeviceUuid(candidate, out var uuid) == NvmlResult.Success &&
                string.Equals(uuid, gpuUuid, StringComparison.OrdinalIgnoreCase))
            {
                device = candidate;
                return NvmlResult.Success;
            }
        }

        return NvmlResult.NotFound;
    }

    private static FanHelperReply Status(
        string state,
        int duty,
        int temperature,
        DateTimeOffset leaseDeadline,
        string detail) => new(true, state, duty, temperature, Remaining(leaseDeadline), detail);

    private static int Remaining(DateTimeOffset deadline) =>
        Math.Max(0, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds));

    private static async Task<string?> ReadLineAsync(StreamReader reader, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            return await reader.ReadLineAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static FanHelperCommand? Deserialize(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 4096)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FanHelperCommand>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task ReplyAsync(StreamWriter writer, FanHelperReply reply) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(reply, JsonOptions));

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsPipeName(string value) =>
        value.Length is >= 16 and <= 96 &&
        value.StartsWith("aitm-fan-", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsSecret(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsGpuUuid(string? value) =>
        value is not null && value.Length is >= 8 and <= 96 &&
        value.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool SecretEquals(string expected, string? supplied)
    {
        if (supplied is null)
        {
            return false;
        }

        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var suppliedBytes = Encoding.ASCII.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static int MapResult(NvmlResult result) => result switch
    {
        NvmlResult.NoPermission => 77,
        NvmlResult.NotSupported or NvmlResult.FunctionNotFound => 78,
        NvmlResult.NotFound => 66,
        NvmlResult.GpuIsLost => 70,
        _ => 74,
    };
}

public static class FanSafetyCurve
{
    public static int CalculateDuty(int requested, int temperature, int ceiling, int minimum, int maximum)
    {
        if (minimum < 0 || maximum > 100 || minimum > maximum || ceiling <= 50)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), "Fan safety-curve bounds are invalid.");
        }

        var rampStart = Math.Max(50, ceiling - 25);
        if (temperature <= rampStart)
        {
            return Math.Clamp(requested, minimum, maximum);
        }

        if (temperature >= ceiling)
        {
            return maximum;
        }

        var progress = (temperature - rampStart) / (double)(ceiling - rampStart);
        var thermalDuty = minimum + (int)Math.Ceiling(progress * (maximum - minimum));
        return Math.Clamp(Math.Max(requested, thermalDuty), minimum, maximum);
    }
}
