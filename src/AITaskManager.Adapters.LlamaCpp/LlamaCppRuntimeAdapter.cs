using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AITaskManager.Application;
using AITaskManager.Core;

namespace AITaskManager.Adapters.LlamaCpp;

public sealed class LlamaCppRuntimeAdapter : IRuntimeAdapter, IDisposable
{
    private static readonly string[] LlamaServerExecutableNames = ["llama-server", "llama_server"];
    private static readonly Regex ApiKeyArgumentPattern = new(
        "(?:^|\\s)--api[-_]?key(?:\\s+|=)(?:\"(?<quoted>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex KeyCacheArgumentPattern = new(
        "(?:^|\\s)--cache-type-k(?:\\s+|=)(?:\"(?<quoted>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex ValueCacheArgumentPattern = new(
        "(?:^|\\s)--cache-type-v(?:\\s+|=)(?:\"(?<quoted>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private readonly AITaskManagerOptions _options;
    private readonly ISecretStore _secretStore;
    private readonly ILocalEndpointProcessResolver _processResolver;
    private readonly HttpClient _client;

    public LlamaCppRuntimeAdapter(
        AITaskManagerOptions options,
        ISecretStore secretStore,
        ILocalEndpointProcessResolver processResolver)
    {
        _options = options;
        _secretStore = secretStore;
        _processResolver = processResolver;
        _client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string AdapterName => "llama.cpp";

    public async Task<IReadOnlyList<RuntimeEndpointSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken)
    {
        var endpoints = GetEndpoints();
        var snapshots = await Task.WhenAll(endpoints.Select(endpoint =>
            ReadEndpointAsync(endpoint, cancellationToken))).ConfigureAwait(false);
        return snapshots;
    }

    public void Dispose() => _client.Dispose();

    private IReadOnlyList<RuntimeEndpointOptions> GetEndpoints()
    {
        var endpoints = _options.Discovery.RuntimeEndpoints
            .Where(endpoint => string.Equals(endpoint.Kind, AdapterName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!_options.Discovery.EnableLocalRuntimeDiscovery)
        {
            return endpoints;
        }

        var configuredLoopbackPorts = endpoints
            .Select(endpoint => Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && IsLoopback(uri.Host))
            .Select(uri => uri!.Port)
            .ToHashSet();
        foreach (var listener in _processResolver.DiscoverListeners(LlamaServerExecutableNames))
        {
            if (!configuredLoopbackPorts.Add(listener.Endpoint.Port))
            {
                continue;
            }

            endpoints.Add(new RuntimeEndpointOptions
            {
                Name = $"llama-server :{listener.Endpoint.Port}",
                Kind = AdapterName,
                BaseUrl = listener.Endpoint.GetLeftPart(UriPartial.Authority),
            });
        }

        return endpoints;
    }

    private async Task<RuntimeEndpointSnapshot> ReadEndpointAsync(
        RuntimeEndpointOptions endpoint,
        CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var id = new RuntimeEndpointId(endpoint.Name);
        LocalEndpointProcess? process = null;
        var keyCacheType = endpoint.KeyCacheType;
        var valueCacheType = endpoint.ValueCacheType;

        if (!Uri.TryCreate(endpoint.BaseUrl.TrimEnd('/') + '/', UriKind.Absolute, out var baseUri))
        {
            return EmptySnapshot(WorkloadState.Failed, "The configured base URL is invalid.");
        }

        process = _processResolver.Resolve(baseUri);
        keyCacheType ??= ReadKeyCacheType(process?.CommandLine);
        valueCacheType ??= ReadValueCacheType(process?.CommandLine);
        string? apiKey = null;
        if (!string.IsNullOrWhiteSpace(endpoint.SecretReference))
        {
            apiKey = await _secretStore.GetAsync(endpoint.SecretReference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return EmptySnapshot(WorkloadState.Failed, "The configured API-key secret was not found.");
            }
        }
        else
        {
            apiKey = ReadApiKey(process?.CommandLine);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.Collection.SourceTimeoutSeconds));

        try
        {
            using var healthResponse = await SendAsync(baseUri, "health", apiKey, timeout.Token).ConfigureAwait(false);
            if (healthResponse.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return EmptySnapshot(WorkloadState.Loading, "llama.cpp is loading the model.");
            }

            if (!healthResponse.IsSuccessStatusCode)
            {
                return EmptySnapshot(
                    WorkloadState.Failed,
                    $"The health endpoint returned HTTP {(int)healthResponse.StatusCode}.");
            }

            string? model = null;
            string? modelFormat = null;
            string? build = null;
            IReadOnlyList<string> modalities = [];
            long? contextTokens = null;
            long? occupiedContextTokens = null;
            bool? sleeping = null;
            int? activeSlots = null;
            int? totalSlots = null;
            int? queuedRequests = null;
            double? promptTokensPerSecond = null;
            double? generationTokensPerSecond = null;
            long? promptTokensTotal = null;
            long? generatedTokensTotal = null;
            var details = new List<string>();

            using (var propsResponse = await SendAsync(baseUri, "props", apiKey, timeout.Token).ConfigureAwait(false))
            {
                if (propsResponse.IsSuccessStatusCode)
                {
                    await using var propsStream = await propsResponse.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using var props = await JsonDocument.ParseAsync(
                        propsStream,
                        cancellationToken: timeout.Token).ConfigureAwait(false);
                    model = ReadString(props.RootElement, "model_alias") ??
                        FileNameOrValue(ReadString(props.RootElement, "model_path"));
                    modelFormat = ReadString(props.RootElement, "model_ftype");
                    build = ReadString(props.RootElement, "build_info");
                    modalities = ReadModalities(props.RootElement);
                    sleeping = ReadBoolean(props.RootElement, "is_sleeping");
                    totalSlots = ReadInt32(props.RootElement, "total_slots");
                    if (props.RootElement.TryGetProperty("default_generation_settings", out var generationSettings))
                    {
                        contextTokens = ReadInt64(generationSettings, "n_ctx");
                    }
                }
                else
                {
                    details.Add($"props HTTP {(int)propsResponse.StatusCode}");
                }
            }

            using (var slotsResponse = await SendAsync(baseUri, "slots", apiKey, timeout.Token).ConfigureAwait(false))
            {
                if (slotsResponse.IsSuccessStatusCode)
                {
                    await using var slotsStream = await slotsResponse.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using var slots = await JsonDocument.ParseAsync(
                        slotsStream,
                        cancellationToken: timeout.Token).ConfigureAwait(false);
                    if (slots.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        totalSlots = slots.RootElement.GetArrayLength();
                        activeSlots = slots.RootElement.EnumerateArray().Count(slot =>
                            ReadBoolean(slot, "is_processing") == true);
                        var occupiedValues = slots.RootElement.EnumerateArray()
                            .Select(ReadOccupiedTokens)
                            .Where(value => value.HasValue)
                            .Select(value => value!.Value)
                            .ToArray();
                        occupiedContextTokens = occupiedValues.Length == 0 ? null : occupiedValues.Sum();
                    }
                }
                else if (slotsResponse.StatusCode != HttpStatusCode.NotImplemented)
                {
                    details.Add($"slots HTTP {(int)slotsResponse.StatusCode}");
                }
            }

            using (var metricsResponse = await SendAsync(baseUri, "metrics", apiKey, timeout.Token).ConfigureAwait(false))
            {
                if (metricsResponse.IsSuccessStatusCode)
                {
                    var metrics = await metricsResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    promptTokensPerSecond = ReadPrometheusDouble(metrics, "llamacpp:prompt_tokens_seconds");
                    generationTokensPerSecond = ReadPrometheusDouble(metrics, "llamacpp:predicted_tokens_seconds");
                    queuedRequests = ToInt32(ReadPrometheusDouble(metrics, "llamacpp:requests_deferred"));
                    promptTokensTotal = ToInt64(ReadPrometheusDouble(metrics, "llamacpp:prompt_tokens_total"));
                    generatedTokensTotal = ToInt64(ReadPrometheusDouble(metrics, "llamacpp:tokens_predicted_total"));
                }
                else if (metricsResponse.StatusCode != HttpStatusCode.NotImplemented)
                {
                    details.Add($"metrics HTTP {(int)metricsResponse.StatusCode}");
                }
            }

            return new RuntimeEndpointSnapshot(
                id,
                model ?? endpoint.Name,
                AdapterName,
                endpoint.BaseUrl,
                sleeping == true
                    ? WorkloadState.Idle
                    : activeSlots > 0 ? WorkloadState.Active : WorkloadState.Ready,
                model,
                modelFormat,
                build,
                modalities,
                contextTokens,
                occupiedContextTokens,
                activeSlots,
                totalSlots,
                queuedRequests,
                promptTokensPerSecond,
                generationTokensPerSecond,
                promptTokensTotal,
                generatedTokensTotal,
                keyCacheType,
                valueCacheType,
                sleeping,
                process?.ProcessId,
                process?.ExecutableName,
                process?.StartedAt,
                observedAt,
                details.Count == 0 ? null : string.Join("; ", details));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return EmptySnapshot(WorkloadState.Failed, "The endpoint did not respond before the configured timeout.");
        }
        catch (HttpRequestException exception)
        {
            return EmptySnapshot(WorkloadState.Stopped, exception.Message);
        }
        catch (JsonException exception)
        {
            return EmptySnapshot(WorkloadState.Failed, $"The endpoint returned malformed JSON: {exception.Message}");
        }

        RuntimeEndpointSnapshot EmptySnapshot(WorkloadState state, string detail) => new(
            id,
            endpoint.Name,
            AdapterName,
            endpoint.BaseUrl,
            state,
            null,
            null,
            null,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            keyCacheType,
            valueCacheType,
            null,
            process?.ProcessId,
            process?.ExecutableName,
            process?.StartedAt,
            observedAt,
            detail);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri baseUri,
        string relativePath,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, relativePath));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? ReadInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;

    private static int? ReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static bool? ReadBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static string? FileNameOrValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.GetFileName(value);

    private static IReadOnlyList<string> ReadModalities(JsonElement root)
    {
        if (!root.TryGetProperty("modalities", out var value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.True)
                .Select(property => property.Name)
                .ToArray();
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => item is not null)
                .Cast<string>()
                .ToArray();
        }

        return [];
    }

    private static long? ReadOccupiedTokens(JsonElement slot)
    {
        var candidates = new List<long>();
        foreach (var name in new[] { "n_past", "n_tokens", "n_prompt_tokens" })
        {
            if (ReadInt64(slot, name) is { } value)
            {
                candidates.Add(value);
            }
        }

        if (slot.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "n_prompt_tokens_processed", "n_decoded" })
            {
                if (ReadInt64(metrics, name) is { } value)
                {
                    candidates.Add(value);
                }
            }
        }

        return candidates.Count == 0 ? null : candidates.Max();
    }

    private static double? ReadPrometheusDouble(string document, string metricName)
    {
        foreach (var line in document.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#') ||
                !(line.StartsWith(metricName + " ", StringComparison.Ordinal) ||
                  line.StartsWith(metricName + "{", StringComparison.Ordinal)))
            {
                continue;
            }

            var separator = line.LastIndexOfAny([' ', '\t']);
            if (separator >= 0 &&
                double.TryParse(line[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? ToInt32(double? value) =>
        value is null || value < int.MinValue || value > int.MaxValue ? null : (int)Math.Round(value.Value);

    private static long? ToInt64(double? value) =>
        value is null || value < long.MinValue || value > long.MaxValue ? null : (long)Math.Round(value.Value);

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    internal static string? ReadApiKey(string? commandLine)
        => ReadCommandArgument(ApiKeyArgumentPattern, commandLine);

    internal static string? ReadKeyCacheType(string? commandLine)
        => ReadCommandArgument(KeyCacheArgumentPattern, commandLine);

    internal static string? ReadValueCacheType(string? commandLine)
        => ReadCommandArgument(ValueCacheArgumentPattern, commandLine);

    private static string? ReadCommandArgument(Regex pattern, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var match = pattern.Match(commandLine);
        if (!match.Success)
        {
            return null;
        }

        foreach (var groupName in new[] { "quoted", "single", "bare" })
        {
            var value = match.Groups[groupName].Value;
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }
}
