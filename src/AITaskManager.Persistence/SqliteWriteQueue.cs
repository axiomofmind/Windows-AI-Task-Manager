using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace AITaskManager.Persistence;

public sealed record MetricSampleWrite(
    string Source,
    string TargetId,
    string Metric,
    DateTimeOffset ObservedAt,
    double? NumericValue,
    string State);

public sealed class SqliteWriteQueue : IAsyncDisposable
{
    private readonly SqliteDatabase _database;
    private readonly Channel<WriteRequest> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _processor;

    public SqliteWriteQueue(SqliteDatabase database, int capacity = 1024)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _database = database;
        _channel = Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _processor = ProcessAsync(_shutdown.Token);
    }

    public int PendingCount => _channel.Reader.CanCount ? _channel.Reader.Count : -1;

    public async Task SetMetadataAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new WriteRequest(key, value, null, null, cancellationToken, completion);

        await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool TryEnqueueMetricSamples(IReadOnlyList<MetricSampleWrite> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return samples.Count == 0 || _channel.Writer.TryWrite(new WriteRequest(
            null,
            null,
            samples,
            null,
            CancellationToken.None,
            null));
    }

    public bool TryEnqueueMetricRetention(DateTimeOffset deleteBeforeUtc) =>
        _channel.Writer.TryWrite(new WriteRequest(
            null,
            null,
            null,
            deleteBeforeUtc,
            CancellationToken.None,
            null));

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();

        try
        {
            await _processor.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await foreach (var request in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.Completion?.TrySetCanceled(request.CancellationToken);
                    continue;
                }

                try
                {
                    if (request.RetentionBeforeUtc is not null)
                    {
                        await DeleteExpiredMetricsAsync(
                            connection,
                            request.RetentionBeforeUtc.Value,
                            request.CancellationToken).ConfigureAwait(false);
                    }
                    else if (request.MetricSamples is not null)
                    {
                        await WriteMetricsAsync(connection, request.MetricSamples, request.CancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = """
                        INSERT INTO app_metadata(key, value, updated_at_utc)
                        VALUES ($key, $value, $updatedAtUtc)
                        ON CONFLICT(key) DO UPDATE SET
                            value = excluded.value,
                            updated_at_utc = excluded.updated_at_utc;
                        """;
                        command.Parameters.AddWithValue("$key", request.Key!);
                        command.Parameters.AddWithValue("$value", request.Value!);
                        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
                        await command.ExecuteNonQueryAsync(request.CancellationToken).ConfigureAwait(false);
                    }

                    request.Completion?.TrySetResult();
                }
                catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
                {
                    request.Completion?.TrySetCanceled(request.CancellationToken);
                }
                catch (Exception exception)
                {
                    request.Completion?.TrySetException(exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _channel.Writer.TryComplete(exception);
            while (_channel.Reader.TryRead(out var request))
            {
                request.Completion?.TrySetException(exception);
            }
        }
    }

    private static async Task WriteMetricsAsync(
        SqliteConnection connection,
        IReadOnlyList<MetricSampleWrite> samples,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var sample in samples)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO metric_samples(
                    source, target_id, metric, observed_at_utc, numeric_value, state)
                VALUES ($source, $targetId, $metric, $observedAtUtc, $numericValue, $state);
                """;
            command.Parameters.AddWithValue("$source", sample.Source);
            command.Parameters.AddWithValue("$targetId", sample.TargetId);
            command.Parameters.AddWithValue("$metric", sample.Metric);
            command.Parameters.AddWithValue("$observedAtUtc", sample.ObservedAt.ToString("O"));
            command.Parameters.AddWithValue("$numericValue", sample.NumericValue is null ? DBNull.Value : sample.NumericValue.Value);
            command.Parameters.AddWithValue("$state", sample.State);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteExpiredMetricsAsync(
        SqliteConnection connection,
        DateTimeOffset deleteBeforeUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM metric_samples
            WHERE rowid IN (
                SELECT rowid
                FROM metric_samples
                WHERE observed_at_utc < $deleteBeforeUtc
                ORDER BY observed_at_utc
                LIMIT 5000
            );
            """;
        command.Parameters.AddWithValue("$deleteBeforeUtc", deleteBeforeUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record WriteRequest(
        string? Key,
        string? Value,
        IReadOnlyList<MetricSampleWrite>? MetricSamples,
        DateTimeOffset? RetentionBeforeUtc,
        CancellationToken CancellationToken,
        TaskCompletionSource? Completion);
}
