using Microsoft.Data.Sqlite;

namespace AITaskManager.Persistence;

public sealed record DatabaseStatus(
    string Path,
    int SchemaVersion,
    string Integrity,
    long SizeBytes,
    bool WriteAheadLoggingEnabled);

public sealed class SqliteDatabase
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _integrityGate = new(1, 1);
    private string? _cachedIntegrity;

    public SqliteDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("The database path has no parent directory.");
        Directory.CreateDirectory(directory);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
        await ExecuteScalarAsync<string>(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);

        var currentVersion = await ExecuteScalarAsync<long>(
            connection,
            "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;",
            cancellationToken).ConfigureAwait(false);

        if (currentVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema {currentVersion} is newer than supported schema {CurrentSchemaVersion}.");
        }

        if (currentVersion < 1)
        {
            await ApplyVersionOneAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        _ = await GetCachedIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var schemaVersion = await ExecuteScalarAsync<long>(
            connection,
            "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;",
            cancellationToken).ConfigureAwait(false);

        var integrity = await GetCachedIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);

        var journalMode = await ExecuteScalarAsync<string>(
            connection,
            "PRAGMA journal_mode;",
            cancellationToken).ConfigureAwait(false);

        var size = File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0;
        return new(
            DatabasePath,
            checked((int)schemaVersion),
            integrity,
            size,
            string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> GetCachedIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _cachedIntegrity);
        if (cached is not null)
        {
            return cached;
        }

        await _integrityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _cachedIntegrity);
            if (cached is not null)
            {
                return cached;
            }

            cached = await ExecuteScalarAsync<string>(
                connection,
                "PRAGMA integrity_check;",
                cancellationToken).ConfigureAwait(false) ?? "unknown";
            Volatile.Write(ref _cachedIntegrity, cached);
            return cached;
        }
        finally
        {
            _integrityGate.Release();
        }
    }

    public async Task<string?> GetMetadataAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<long> GetMetricSampleCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM metric_samples;",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> GetActionAuditCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM action_audits;",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteActionAuditAsync(
        ActionAuditWrite audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO action_audits(
                audit_id, action_kind, target_id, requester, requested_at_utc,
                completed_at_utc, result, previous_value_json, requested_value_json,
                applied_value_json, verification_json)
            VALUES (
                $auditId, $actionKind, $targetId, $requester, $requestedAtUtc,
                $completedAtUtc, $result, $previousValueJson, $requestedValueJson,
                $appliedValueJson, $verificationJson);
            """;
        command.Parameters.AddWithValue("$auditId", audit.AuditId);
        command.Parameters.AddWithValue("$actionKind", audit.ActionKind);
        command.Parameters.AddWithValue("$targetId", audit.TargetId);
        command.Parameters.AddWithValue("$requester", audit.Requester);
        command.Parameters.AddWithValue("$requestedAtUtc", audit.RequestedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$completedAtUtc", audit.CompletedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$result", audit.Result);
        command.Parameters.AddWithValue("$previousValueJson", DbValue(audit.PreviousValueJson));
        command.Parameters.AddWithValue("$requestedValueJson", DbValue(audit.RequestedValueJson));
        command.Parameters.AddWithValue("$appliedValueJson", DbValue(audit.AppliedValueJson));
        command.Parameters.AddWithValue("$verificationJson", DbValue(audit.VerificationJson));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (string.Equals(DatabasePath, fullDestinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The backup path must differ from the live database path.", nameof(destinationPath));
        }

        var directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("The backup path has no parent directory.");
        Directory.CreateDirectory(directory);

        cancellationToken.ThrowIfCancellationRequested();
        await using var source = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullDestinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ApplyVersionOneAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            CREATE TABLE app_metadata (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE devices (
                gpu_uuid TEXT NOT NULL PRIMARY KEY,
                pci_bus_id TEXT,
                display_index INTEGER,
                name TEXT NOT NULL,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                capabilities_json TEXT NOT NULL DEFAULT '{}'
            );

            CREATE TABLE process_instances (
                process_instance_id TEXT NOT NULL PRIMARY KEY,
                process_id INTEGER NOT NULL,
                started_at_utc TEXT NOT NULL,
                ended_at_utc TEXT,
                executable_name TEXT,
                command_line_redacted TEXT
            );

            CREATE TABLE workloads (
                workload_id TEXT NOT NULL PRIMARY KEY,
                kind TEXT NOT NULL,
                state TEXT NOT NULL,
                process_instance_id TEXT,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                details_json TEXT NOT NULL DEFAULT '{}',
                FOREIGN KEY(process_instance_id) REFERENCES process_instances(process_instance_id)
            );

            CREATE TABLE models (
                model_id TEXT NOT NULL PRIMARY KEY,
                root_id TEXT,
                relative_path TEXT,
                display_name TEXT NOT NULL,
                format TEXT,
                architecture TEXT,
                quantization TEXT,
                size_bytes INTEGER,
                metadata_json TEXT NOT NULL DEFAULT '{}',
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE runtime_endpoints (
                endpoint_id TEXT NOT NULL PRIMARY KEY,
                adapter_kind TEXT NOT NULL,
                base_url TEXT NOT NULL,
                process_instance_id TEXT,
                last_state TEXT NOT NULL,
                last_observed_utc TEXT,
                FOREIGN KEY(process_instance_id) REFERENCES process_instances(process_instance_id)
            );

            CREATE TABLE training_runs (
                training_run_id TEXT NOT NULL PRIMARY KEY,
                process_instance_id TEXT,
                state TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                ended_at_utc TEXT,
                output_root_id TEXT,
                output_relative_path TEXT,
                details_json TEXT NOT NULL DEFAULT '{}',
                FOREIGN KEY(process_instance_id) REFERENCES process_instances(process_instance_id)
            );

            CREATE TABLE alerts (
                alert_id TEXT NOT NULL PRIMARY KEY,
                rule_id TEXT NOT NULL,
                severity TEXT NOT NULL,
                state TEXT NOT NULL,
                target_id TEXT,
                opened_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                resolved_at_utc TEXT,
                details_json TEXT NOT NULL DEFAULT '{}'
            );

            CREATE TABLE action_audits (
                audit_id TEXT NOT NULL PRIMARY KEY,
                action_kind TEXT NOT NULL,
                target_id TEXT NOT NULL,
                requester TEXT NOT NULL,
                requested_at_utc TEXT NOT NULL,
                completed_at_utc TEXT,
                result TEXT NOT NULL,
                previous_value_json TEXT,
                requested_value_json TEXT,
                applied_value_json TEXT,
                verification_json TEXT
            );

            CREATE TABLE metric_samples (
                source TEXT NOT NULL,
                target_id TEXT NOT NULL,
                metric TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                numeric_value REAL,
                state TEXT NOT NULL,
                PRIMARY KEY(source, target_id, metric, observed_at_utc)
            );

            CREATE INDEX ix_process_instances_pid_started
                ON process_instances(process_id, started_at_utc);
            CREATE INDEX ix_workloads_updated
                ON workloads(updated_at_utc);
            CREATE INDEX ix_training_runs_started
                ON training_runs(started_at_utc);
            CREATE INDEX ix_alerts_state_updated
                ON alerts(state, updated_at_utc);
            CREATE INDEX ix_metric_samples_target_time
                ON metric_samples(target_id, observed_at_utc);

            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (1, $appliedAtUtc);
            """;
        command.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T?> ExecuteScalarAsync<T>(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            return default;
        }

        return (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;
}

public sealed record ActionAuditWrite(
    string AuditId,
    string ActionKind,
    string TargetId,
    string Requester,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Result,
    string? PreviousValueJson,
    string? RequestedValueJson,
    string? AppliedValueJson,
    string? VerificationJson);
