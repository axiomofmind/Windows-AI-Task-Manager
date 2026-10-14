using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITaskManager.Adapters.Nvidia;
using AITaskManager.Application;
using AITaskManager.Contracts;
using AITaskManager.Core;
using AITaskManager.Persistence;

namespace AITaskManager.Hosting;

public sealed class HardwareActionService
{
    private readonly AITaskManagerOptions _options;
    private readonly IGpuTelemetryProvider _gpuProvider;
    private readonly SqliteDatabase _database;
    private readonly string? _helperPath;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedAction> _completed = new(StringComparer.Ordinal);

    public HardwareActionService(
        AITaskManagerOptions options,
        IGpuTelemetryProvider gpuProvider,
        SqliteDatabase database,
        ApplicationHostSettings settings)
    {
        _options = options;
        _gpuProvider = gpuProvider;
        _database = database;
        _helperPath = string.IsNullOrWhiteSpace(settings.HardwareHelperPath)
            ? null
            : Path.GetFullPath(settings.HardwareHelperPath);
    }

    public ActionSessionResponse GetSession() => new(
        _token,
        _options.Actions.Enabled,
        _options.Actions.PowerControlEnabled,
        _options.Actions.FanControlEnabled,
        OperatingSystem.IsWindows() && _helperPath is not null && File.Exists(_helperPath));

    public bool IsValidToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(_token);
        var supplied = Encoding.UTF8.GetBytes(token);
        return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    public Task<HardwareActionResponse> SetPowerLimitAsync(
        SetPowerLimitRequest request,
        CancellationToken cancellationToken) =>
        ExecutePowerActionAsync(
            request.GpuId,
            request.Watts,
            restoreDefault: false,
            request.Confirmation,
            request.IdempotencyKey,
            cancellationToken);

    public Task<HardwareActionResponse> RestorePowerLimitAsync(
        RestorePowerLimitRequest request,
        CancellationToken cancellationToken) =>
        ExecutePowerActionAsync(
            request.GpuId,
            requestedWatts: null,
            restoreDefault: true,
            request.Confirmation,
            request.IdempotencyKey,
            cancellationToken);

    private async Task<HardwareActionResponse> ExecutePowerActionAsync(
        string gpuId,
        double? requestedWatts,
        bool restoreDefault,
        string confirmation,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(gpuId, idempotencyKey);
        var signature = JsonSerializer.Serialize(new { gpuId, requestedWatts, restoreDefault });
        if (_completed.TryGetValue(idempotencyKey, out var existing))
        {
            return string.Equals(existing.Signature, signature, StringComparison.Ordinal)
                ? existing.Response
                : throw new HardwareActionValidationException("The idempotency key was already used for a different action.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_completed.TryGetValue(idempotencyKey, out existing))
            {
                return string.Equals(existing.Signature, signature, StringComparison.Ordinal)
                    ? existing.Response
                    : throw new HardwareActionValidationException("The idempotency key was already used for a different action.");
            }

            var requestedAt = DateTimeOffset.UtcNow;
            var auditId = Guid.NewGuid().ToString("N");
            GpuSnapshot? gpu = null;
            double? previousWatts = null;
            double? targetWatts = null;
            HardwareActionResponse response;

            try
            {
                ValidateAvailability();
                var snapshot = await _gpuProvider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                gpu = snapshot.Gpus.SingleOrDefault(item =>
                    string.Equals(item.Id.Value, gpuId, StringComparison.OrdinalIgnoreCase));
                if (gpu is null)
                {
                    throw new HardwareActionValidationException("The requested GPU UUID is not present in the current NVML snapshot.");
                }

                EnsureFresh(gpu);
                previousWatts = ReadRequired(gpu.RequestedPowerLimitWatts ?? gpu.PowerLimitWatts, "current power limit");
                var minimum = ReadRequired(gpu.MinimumPowerLimitWatts, "minimum power limit");
                var maximum = ReadRequired(gpu.MaximumPowerLimitWatts, "maximum power limit");
                var defaultWatts = ReadRequired(gpu.DefaultPowerLimitWatts, "default power limit");
                targetWatts = restoreDefault ? defaultWatts : requestedWatts;
                if (targetWatts is null || !double.IsFinite(targetWatts.Value) ||
                    targetWatts.Value < minimum || targetWatts.Value > maximum)
                {
                    throw new HardwareActionValidationException(
                        $"Power limit must be within the device-reported range {minimum:0.###}–{maximum:0.###} W.");
                }

                var milliwatts = checked((uint)Math.Round(targetWatts.Value * 1000d, MidpointRounding.AwayFromZero));
                var suffix = GpuSuffix(gpuId);
                var expectedConfirmation = restoreDefault
                    ? $"RESTORE {suffix} DEFAULT"
                    : $"SET {suffix} {targetWatts.Value.ToString("0.###", CultureInfo.InvariantCulture)}W";
                if (!string.Equals(confirmation, expectedConfirmation, StringComparison.Ordinal))
                {
                    throw new HardwareActionValidationException(
                        $"Confirmation did not match the required phrase: {expectedConfirmation}");
                }

                var helperExitCode = await RunElevatedHelperAsync(
                    gpuId,
                    milliwatts,
                    auditId,
                    cancellationToken).ConfigureAwait(false);
                if (helperExitCode != 0)
                {
                    response = new HardwareActionResponse(
                        auditId,
                        "failed",
                        gpuId,
                        previousWatts,
                        targetWatts,
                        null,
                        HelperFailure(helperExitCode));
                }
                else
                {
                    var verification = await _gpuProvider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    var verifiedGpu = verification.Gpus.SingleOrDefault(item =>
                        string.Equals(item.Id.Value, gpuId, StringComparison.OrdinalIgnoreCase));
                    var appliedWatts = verifiedGpu is null
                        ? null
                        : ReadOptional(verifiedGpu.RequestedPowerLimitWatts ?? verifiedGpu.PowerLimitWatts);
                    var verified = appliedWatts is not null && Math.Abs(appliedWatts.Value - targetWatts.Value) <= 0.01;
                    response = new HardwareActionResponse(
                        auditId,
                        verified ? "verified" : "verification-failed",
                        gpuId,
                        previousWatts,
                        targetWatts,
                        appliedWatts,
                        verified
                            ? "The NVIDIA power limit was applied and read back successfully."
                            : "The helper completed, but NVML did not read back the requested value.");
                }
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                response = new HardwareActionResponse(
                    auditId,
                    "cancelled",
                    gpuId,
                    previousWatts,
                    targetWatts,
                    null,
                    "The Windows administrator prompt was cancelled.");
            }
            catch (Exception exception) when (exception is HardwareActionValidationException or
                                               HardwareActionDisabledException or
                                               HardwareActionUnavailableException)
            {
                var rejected = new HardwareActionResponse(
                    auditId,
                    "rejected",
                    gpuId,
                    previousWatts,
                    targetWatts ?? requestedWatts,
                    null,
                    exception.Message);
                await WriteAuditAsync(
                    rejected,
                    restoreDefault ? "gpu-power-restore-default" : "gpu-power-set",
                    requestedAt,
                    cancellationToken).ConfigureAwait(false);
                throw;
            }

            await WriteAuditAsync(
                response,
                restoreDefault ? "gpu-power-restore-default" : "gpu-power-set",
                requestedAt,
                cancellationToken).ConfigureAwait(false);
            _completed.TryAdd(idempotencyKey, new CachedAction(signature, response));
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateIdempotencyKey(string gpuId, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gpuId);
        if (!Guid.TryParse(idempotencyKey, out _))
        {
            throw new HardwareActionValidationException("IdempotencyKey must be a GUID.");
        }
    }

    private void ValidateAvailability()
    {
        if (!_options.Actions.Enabled || !_options.Actions.PowerControlEnabled)
        {
            throw new HardwareActionDisabledException("GPU power actions are disabled in local configuration.");
        }

        if (!OperatingSystem.IsWindows() || _helperPath is null || !File.Exists(_helperPath))
        {
            throw new HardwareActionUnavailableException("The elevated hardware helper is unavailable in this host.");
        }

    }

    private async Task<int> RunElevatedHelperAsync(
        string gpuId,
        uint milliwatts,
        string auditId,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _helperPath!,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add(NvidiaPrivilegedCommand.Switch);
        startInfo.ArgumentList.Add("power-set");
        startInfo.ArgumentList.Add(gpuId);
        startInfo.ArgumentList.Add(milliwatts.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(auditId);

        using var process = Process.Start(startInfo)
            ?? throw new HardwareActionUnavailableException("Windows did not start the elevated hardware helper.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        return process.ExitCode;
    }

    private async Task WriteAuditAsync(
        HardwareActionResponse response,
        string kind,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        await _database.WriteActionAuditAsync(new ActionAuditWrite(
            response.AuditId,
            kind,
            response.GpuId,
            $"{Environment.UserDomainName}\\{Environment.UserName}",
            requestedAt,
            DateTimeOffset.UtcNow,
            response.Result,
            JsonSerializer.Serialize(new { watts = response.PreviousWatts }),
            JsonSerializer.Serialize(new { watts = response.RequestedWatts }),
            JsonSerializer.Serialize(new { watts = response.AppliedWatts }),
            JsonSerializer.Serialize(new { response.Detail })), cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureFresh(GpuSnapshot gpu)
    {
        if (DateTimeOffset.UtcNow - gpu.TemperatureCelsius.ObservedAt > TimeSpan.FromSeconds(10))
        {
            throw new HardwareActionUnavailableException("GPU telemetry is stale; no hardware action was attempted.");
        }
    }

    private static double ReadRequired(ObservedValue<double>? value, string label) =>
        ReadOptional(value) ?? throw new HardwareActionUnavailableException($"The device did not report its {label}.");

    private static double? ReadOptional(ObservedValue<double>? value) =>
        value is not null && value.State is ObservationState.Available or ObservationState.Stale
            ? value.Value
            : null;

    private static string GpuSuffix(string gpuId) =>
        gpuId.Length <= 8 ? gpuId.ToUpperInvariant() : gpuId[^8..].ToUpperInvariant();

    private static string HelperFailure(int exitCode) => exitCode switch
    {
        65 => "The helper rejected a value outside the device-reported range.",
        66 => "The helper could not find the GPU UUID.",
        69 => "The helper could not load NVIDIA NVML.",
        70 => "NVML reported that the GPU was lost.",
        77 => "NVIDIA denied the operation even after elevation.",
        78 => "This GPU or driver does not support power-limit changes.",
        74 => "The helper could not verify the requested power limit.",
        _ => $"The elevated helper failed with exit code {exitCode}.",
    };

    private sealed record CachedAction(string Signature, HardwareActionResponse Response);
}

public sealed class HardwareActionValidationException(string message) : Exception(message);

public sealed class HardwareActionDisabledException(string message) : Exception(message);

public sealed class HardwareActionUnavailableException(string message) : Exception(message);
