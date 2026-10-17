using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITaskManager.Adapters.Nvidia;
using AITaskManager.Application;
using AITaskManager.Contracts;
using AITaskManager.Core;
using AITaskManager.Persistence;
using Microsoft.Extensions.Hosting;

namespace AITaskManager.Hosting;

public sealed class FanControlService : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] KnownControllerProcesses = ["FanControl", "MSIAfterburner", "PrecisionX_x64"];
    private readonly AITaskManagerOptions _options;
    private readonly IGpuTelemetryProvider _gpuProvider;
    private readonly SqliteDatabase _database;
    private readonly string? _helperPath;
    private readonly ConcurrentDictionary<string, FanSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedFanAction> _completed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _watchdog;
    private int _disposed;

    public FanControlService(
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _watchdog = WatchdogAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetime?.Cancel();
        if (_watchdog is not null)
        {
            try { await _watchdog.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        var restores = _sessions.Keys.Select(id => RestoreForShutdownAsync(id)).ToArray();
        await Task.WhenAll(restores).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    public FanControlStateCollectionResponse GetStates() => new(
        DateTimeOffset.UtcNow,
        _sessions.Values
            .OrderBy(session => session.GpuId, StringComparer.OrdinalIgnoreCase)
            .Select(session => session.ToState())
            .ToArray());

    public async Task<FanControlActionResponse> SetManualAsync(
        SetFanManualRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdempotency(request.GpuId, request.IdempotencyKey);
        var signature = JsonSerializer.Serialize(new { request.GpuId, request.DutyPercent });
        if (TryGetCached(request.IdempotencyKey, signature, out var cached)) return cached;

        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(request.IdempotencyKey, signature, out cached)) return cached;
            var requestedAt = DateTimeOffset.UtcNow;
            var auditId = Guid.NewGuid().ToString("N");
            try
            {
                ValidateAvailability();
                var gpu = await GetGpuAsync(request.GpuId, cancellationToken).ConfigureAwait(false);
                var (minimum, maximum) = ValidateGpu(gpu);
                if (request.DutyPercent < minimum || request.DutyPercent > maximum)
                {
                    throw new HardwareActionValidationException($"Fan duty must be within the device-reported range {minimum}–{maximum} percent.");
                }

                var hasActiveSession = _sessions.TryGetValue(request.GpuId, out var existing);
                ValidateSessionAuthorization(request, hasActiveSession);
                FanHelperReply reply;
                if (hasActiveSession)
                {
                    Debug.Assert(existing is not null);
                    reply = await SendAsync(existing, new("update", existing.Secret, DutyPercent: request.DutyPercent), cancellationToken).ConfigureAwait(false);
                    existing.RequestedDuty = request.DutyPercent;
                    existing.Update(reply);
                }
                else
                {
                    var conflict = KnownControllerProcesses.FirstOrDefault(name => Process.GetProcessesByName(name).Length > 0);
                    if (conflict is not null)
                    {
                        throw new HardwareActionUnavailableException($"A competing fan controller is running: {conflict}.");
                    }

                    var session = await StartSessionAsync(gpu, request.DutyPercent, auditId, cancellationToken).ConfigureAwait(false);
                    if (!_sessions.TryAdd(request.GpuId, session))
                    {
                        await RestoreAndDisposeAsync(session, CancellationToken.None).ConfigureAwait(false);
                        throw new HardwareActionUnavailableException("A fan-control session was started concurrently; retry the request.");
                    }

                    reply = session.LastReply;
                }

                var response = ToAction(auditId, request.GpuId, request.DutyPercent, reply);
                await WriteAuditAsync(
                    response,
                    hasActiveSession ? "gpu-fan-minimum-update" : "gpu-fan-session-start",
                    requestedAt,
                    cancellationToken).ConfigureAwait(false);
                _completed.TryAdd(request.IdempotencyKey, new(signature, response));
                return response;
            }
            catch (Exception exception) when (exception is HardwareActionValidationException or HardwareActionDisabledException or HardwareActionUnavailableException)
            {
                var rejected = _sessions.TryGetValue(request.GpuId, out var activeSession)
                    ? new FanControlActionResponse(
                        auditId,
                        "rejected",
                        request.GpuId,
                        activeSession.LastReply.State,
                        activeSession.RequestedDuty,
                        activeSession.LastReply.AppliedDutyPercent,
                        activeSession.LastReply.TemperatureCelsius,
                        activeSession.LastReply.LeaseRemainingSeconds,
                        exception.Message)
                    : new FanControlActionResponse(auditId, "rejected", request.GpuId, "automatic", request.DutyPercent, null, null, 0, exception.Message);
                await WriteAuditAsync(
                    rejected,
                    activeSession is null ? "gpu-fan-session-start" : "gpu-fan-minimum-update",
                    requestedAt,
                    cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                var cancelled = new FanControlActionResponse(auditId, "cancelled", request.GpuId, "automatic", request.DutyPercent, null, null, 0, "The Windows administrator prompt was cancelled.");
                await WriteAuditAsync(cancelled, "gpu-fan-session-start", requestedAt, cancellationToken).ConfigureAwait(false);
                _completed.TryAdd(request.IdempotencyKey, new(signature, cancelled));
                return cancelled;
            }
        }
        finally
        {
            _actionGate.Release();
        }
    }

    public async Task<FanControlActionResponse> RestoreAutomaticAsync(
        RestoreFanAutomaticRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdempotency(request.GpuId, request.IdempotencyKey);
        var signature = JsonSerializer.Serialize(new { request.GpuId, restore = true });
        if (TryGetCached(request.IdempotencyKey, signature, out var cached)) return cached;

        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(request.IdempotencyKey, signature, out cached)) return cached;
            ValidateHelperAvailability();
            var requestedAt = DateTimeOffset.UtcNow;
            var auditId = Guid.NewGuid().ToString("N");
            FanHelperReply reply;
            try
            {
                if (_sessions.TryRemove(request.GpuId, out var session))
                {
                    reply = await RestoreAndDisposeAsync(session, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var gpu = await GetGpuAsync(request.GpuId, cancellationToken).ConfigureAwait(false);
                    var manual = gpu.Fans.Any(fan => string.Equals(ReadText(fan.ControlPolicy), "manual", StringComparison.OrdinalIgnoreCase));
                    if (manual)
                    {
                        var exitCode = await RunRestoreHelperAsync(request.GpuId, auditId, cancellationToken).ConfigureAwait(false);
                        reply = exitCode == 0
                            ? new(true, "automatic", null, ReadInteger(gpu.TemperatureCelsius), 0, "NVIDIA automatic fan control restored.")
                            : new(false, "restore-failed", null, ReadInteger(gpu.TemperatureCelsius), 0, HelperFailure(exitCode));
                    }
                    else
                    {
                        reply = new(true, "automatic", null, ReadInteger(gpu.TemperatureCelsius), 0, "NVIDIA automatic fan control is already active.");
                    }
                }
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                reply = new(false, "restore-cancelled", null, null, 0, "The Windows administrator prompt was cancelled.");
            }

            var response = ToAction(auditId, request.GpuId, null, reply);
            await WriteAuditAsync(response, "gpu-fan-restore-automatic", requestedAt, cancellationToken).ConfigureAwait(false);
            _completed.TryAdd(request.IdempotencyKey, new(signature, response));
            return response;
        }
        finally
        {
            _actionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime?.Cancel();
        foreach (var session in _sessions.Values) await session.DisposeAsync().ConfigureAwait(false);
        _lifetime?.Dispose();
        _actionGate.Dispose();
    }

    private async Task WatchdogAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var pair in _sessions.ToArray())
                {
                    try
                    {
                        var reply = await SendAsync(pair.Value, new("heartbeat", pair.Value.Secret), cancellationToken).ConfigureAwait(false);
                        var previousState = pair.Value.LastReply.State;
                        pair.Value.Update(reply);
                        if (!string.Equals(previousState, reply.State, StringComparison.Ordinal) && reply.State is "emergency" or "manual")
                        {
                            await WriteWatchdogAuditAsync(
                                pair.Key,
                                $"gpu-fan-watchdog-{reply.State}",
                                reply.Success ? "verified" : "failed",
                                reply,
                                cancellationToken).ConfigureAwait(false);
                        }
                        if (!reply.Success) throw new IOException(reply.Detail);
                    }
                    catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException)
                    {
                        if (_sessions.TryRemove(pair.Key, out var failed))
                        {
                            failed.MarkFaulted("Watchdog link lost; helper lease will restore automatic control.");
                            await WriteWatchdogAuditAsync(
                                pair.Key,
                                "gpu-fan-watchdog-failure",
                                "failed",
                                failed.LastReply,
                                CancellationToken.None).ConfigureAwait(false);
                            await failed.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<FanSession> StartSessionAsync(GpuSnapshot gpu, int duty, string auditId, CancellationToken cancellationToken)
    {
        var pipeName = $"aitm-fan-{Guid.NewGuid():N}";
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
        Process? process = null;
        try
        {
            var startInfo = CreateHelperStartInfo("fan-session", pipeName, secret, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), auditId);
            process = Process.Start(startInfo) ?? throw new HardwareActionUnavailableException("Windows did not start the elevated fan watchdog.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var session = new FanSession(gpu.Id.Value, secret, duty, process, pipe, reader, writer);
            var command = new FanHelperCommand(
                "enable",
                secret,
                gpu.Id.Value,
                duty,
                Math.Clamp(_options.Actions.FanControl.WatchdogSeconds, 3, 15),
                _options.Actions.FanControl.FailsafeTemperatureCelsius);
            var reply = await SendAsync(session, command, timeout.Token).ConfigureAwait(false);
            session.Update(reply);
            if (!reply.Success)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new HardwareActionUnavailableException(reply.Detail);
            }

            return session;
        }
        catch (Exception exception)
        {
            pipe.Dispose();
            process?.Dispose();
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                throw new HardwareActionUnavailableException(
                    $"The elevated fan watchdog could not establish its local control channel: {exception.Message}");
            }

            throw;
        }
    }

    private static async Task<FanHelperReply> SendAsync(FanSession session, FanHelperCommand command, CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.Writer.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions)).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var line = await session.Reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new IOException("The elevated fan watchdog disconnected.");
            return JsonSerializer.Deserialize<FanHelperReply>(line, JsonOptions)
                ?? throw new JsonException("The elevated fan watchdog returned an invalid response.");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<FanHelperReply> RestoreActiveSessionAsync(string gpuId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(gpuId, out var session)) return new(true, "automatic", null, null, 0, "No active session.");
        return await RestoreAndDisposeAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreForShutdownAsync(string gpuId)
    {
        var reply = await RestoreActiveSessionAsync(gpuId, CancellationToken.None).ConfigureAwait(false);
        await WriteWatchdogAuditAsync(
            gpuId,
            "gpu-fan-restore-service-stop",
            reply.Success ? "verified" : "failed",
            reply,
            CancellationToken.None).ConfigureAwait(false);
    }

    internal static async Task<FanHelperReply> RestoreAndDisposeAsync(FanSession session, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await SendAsync(session, new("restore", session.Secret), cancellationToken).ConfigureAwait(false);
            session.Update(reply);
            return reply;
        }
        catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException)
        {
            return new(false, "restore-pending", null, null, 0, "The watchdog link closed; its lease will restore automatic control.");
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<int> RunRestoreHelperAsync(string gpuId, string auditId, CancellationToken cancellationToken)
    {
        using var process = Process.Start(CreateHelperStartInfo("fan-restore", gpuId, auditId))
            ?? throw new HardwareActionUnavailableException("Windows did not start the elevated restoration helper.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        return process.ExitCode;
    }

    private ProcessStartInfo CreateHelperStartInfo(params string[] arguments)
    {
        var info = new ProcessStartInfo { FileName = _helperPath!, UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(NvidiaPrivilegedCommand.Switch);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private void ValidateAvailability()
    {
        if (!_options.Actions.Enabled || !_options.Actions.FanControlEnabled)
            throw new HardwareActionDisabledException("GPU fan actions are disabled in local configuration.");
        ValidateHelperAvailability();
    }

    internal static void ValidateSessionAuthorization(SetFanManualRequest request, bool hasActiveSession)
    {
        if (hasActiveSession) return;
        if (!request.AcknowledgeManualControl)
            throw new HardwareActionValidationException("Starting a manual fan session requires an explicit safety acknowledgement.");
        if (!request.ConfirmOtherControllersDisabled)
            throw new HardwareActionValidationException("Confirm that other fan-control applications are disabled.");
    }

    private void ValidateHelperAvailability()
    {
        if (!OperatingSystem.IsWindows() || _helperPath is null || !File.Exists(_helperPath))
            throw new HardwareActionUnavailableException("The elevated hardware helper is unavailable in this host.");
    }

    private async Task<GpuSnapshot> GetGpuAsync(string gpuId, CancellationToken cancellationToken)
    {
        var snapshot = await _gpuProvider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var gpu = snapshot.Gpus.SingleOrDefault(item => string.Equals(item.Id.Value, gpuId, StringComparison.OrdinalIgnoreCase))
            ?? throw new HardwareActionValidationException("The requested GPU UUID is not present in the current NVML snapshot.");
        if (DateTimeOffset.UtcNow - gpu.TemperatureCelsius.ObservedAt > TimeSpan.FromSeconds(10))
            throw new HardwareActionUnavailableException("GPU telemetry is stale; no fan action was attempted.");
        return gpu;
    }

    private static (int Minimum, int Maximum) ValidateGpu(GpuSnapshot gpu)
    {
        var count = ReadInteger(gpu.FanCount) ?? throw new HardwareActionUnavailableException("The device did not report its fan count.");
        var minimum = ReadInteger(gpu.MinimumFanSpeedPercent) ?? throw new HardwareActionUnavailableException("The device did not report its minimum fan duty.");
        var maximum = ReadInteger(gpu.MaximumFanSpeedPercent) ?? throw new HardwareActionUnavailableException("The device did not report its maximum fan duty.");
        var slowdown = ReadInteger(gpu.SlowdownTemperatureCelsius) ?? throw new HardwareActionUnavailableException("The device did not report its slowdown temperature; manual mode is refused.");
        _ = ReadInteger(gpu.TemperatureCelsius) ?? throw new HardwareActionUnavailableException("Fresh GPU temperature is unavailable.");
        if (count is < 1 or > 16 || minimum < 0 || maximum > 100 || minimum > maximum || slowdown is < 65 or > 120)
            throw new HardwareActionUnavailableException("The GPU reported invalid fan or thermal safety limits.");
        return (minimum, maximum);
    }

    private bool TryGetCached(string idempotencyKey, string signature, out FanControlActionResponse response)
    {
        if (_completed.TryGetValue(idempotencyKey, out var cached))
        {
            if (!string.Equals(cached.Signature, signature, StringComparison.Ordinal))
                throw new HardwareActionValidationException("The idempotency key was already used for a different action.");
            response = cached.Response;
            return true;
        }

        response = null!;
        return false;
    }

    private static void ValidateIdempotency(string gpuId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gpuId);
        if (!Guid.TryParse(key, out _)) throw new HardwareActionValidationException("IdempotencyKey must be a GUID.");
    }

    private async Task WriteAuditAsync(FanControlActionResponse response, string kind, DateTimeOffset requestedAt, CancellationToken cancellationToken) =>
        await _database.WriteActionAuditAsync(new ActionAuditWrite(
            response.AuditId, kind, response.GpuId, $"{Environment.UserDomainName}\\{Environment.UserName}", requestedAt, DateTimeOffset.UtcNow,
            response.Result, null, JsonSerializer.Serialize(new { dutyPercent = response.RequestedDutyPercent }),
            JsonSerializer.Serialize(new { dutyPercent = response.AppliedDutyPercent, response.State }),
            JsonSerializer.Serialize(new { response.TemperatureCelsius, response.LeaseRemainingSeconds, response.Detail })), cancellationToken).ConfigureAwait(false);

    private async Task WriteWatchdogAuditAsync(
        string gpuId,
        string kind,
        string result,
        FanHelperReply reply,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            await _database.WriteActionAuditAsync(new ActionAuditWrite(
                Guid.NewGuid().ToString("N"),
                kind,
                gpuId,
                "elevated-watchdog",
                now,
                now,
                result,
                null,
                null,
                JsonSerializer.Serialize(new { dutyPercent = reply.AppliedDutyPercent, reply.State }),
                JsonSerializer.Serialize(new { reply.TemperatureCelsius, reply.LeaseRemainingSeconds, reply.Detail })), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException)
        {
            // Audit persistence cannot be allowed to interfere with the independent elevated safety loop.
        }
    }

    private static FanControlActionResponse ToAction(string auditId, string gpuId, int? requested, FanHelperReply reply) =>
        new(auditId, reply.Success ? "verified" : "failed", gpuId, reply.State, requested, reply.AppliedDutyPercent, reply.TemperatureCelsius, reply.LeaseRemainingSeconds, reply.Detail);

    private static int? ReadInteger(ObservedValue<double>? value) =>
        value?.Value is { } number && value.State is ObservationState.Available or ObservationState.Stale ? checked((int)Math.Round(number)) : null;
    private static int? ReadInteger(ObservedValue<int>? value) =>
        value?.Value is { } number && value.State is ObservationState.Available or ObservationState.Stale ? number : null;
    private static string? ReadText(ObservedValue<string> value) => value.HasUsableValue ? value.Value : null;
    private static string HelperFailure(int code) => code switch { 66 => "The helper could not find the GPU UUID.", 69 => "The helper could not load NVIDIA NVML.", 70 => "NVML reported that the GPU was lost.", 73 => "Another process already owns this GPU fan-control channel.", 77 => "NVIDIA denied the operation after elevation.", 78 => "This GPU or driver does not support fan control.", _ => $"The elevated restoration helper failed with exit code {code}." };

    private sealed record CachedFanAction(string Signature, FanControlActionResponse Response);

    internal sealed class FanSession : IAsyncDisposable
    {
        private int _disposed;

        public FanSession(string gpuId, string secret, int requestedDuty, Process process, PipeStream pipe, StreamReader reader, StreamWriter writer)
        { GpuId = gpuId; Secret = secret; RequestedDuty = requestedDuty; Process = process; Pipe = pipe; Reader = reader; Writer = writer; LastReply = new(true, "connecting", null, null, 0, "Connecting to elevated watchdog."); }
        public string GpuId { get; }
        public string Secret { get; }
        public int RequestedDuty { get; set; }
        public Process Process { get; }
        public PipeStream Pipe { get; }
        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public FanHelperReply LastReply { get; private set; }
        public DateTimeOffset ObservedAt { get; private set; } = DateTimeOffset.UtcNow;
        public void Update(FanHelperReply reply) { LastReply = reply; ObservedAt = DateTimeOffset.UtcNow; }
        public void MarkFaulted(string detail) => Update(new(false, "faulted", LastReply.AppliedDutyPercent, LastReply.TemperatureCelsius, 0, detail));
        public FanControlStateResponse ToState() => new(GpuId, LastReply.State, RequestedDuty, LastReply.AppliedDutyPercent, LastReply.TemperatureCelsius, LastReply.LeaseRemainingSeconds, ObservedAt, LastReply.Detail);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await Writer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The helper exits immediately after acknowledging restore, so its end of the
                // duplex pipe can be closed before StreamWriter performs its final flush.
            }
            finally
            {
                Reader.Dispose();
                Pipe.Dispose();
                Process.Dispose();
                Gate.Dispose();
            }
        }
    }
}
