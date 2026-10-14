using System.Text.Json;
using System.Text.Json.Nodes;
using AITaskManager.Application;

namespace AITaskManager.Infrastructure.Windows;

public sealed record ConfigurationFileStatus(
    string Path,
    bool Created,
    bool Migrated,
    int SchemaVersion,
    string? BackupPath);

public sealed class ConfigurationFileManager
{
    private const long MaximumConfigurationBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public async Task<ConfigurationFileStatus> InitializeAsync(
        string configurationFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFile);

        var fullPath = Path.GetFullPath(configurationFile);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The configuration file has no parent directory.");

        Directory.CreateDirectory(directory);

        if (!File.Exists(fullPath))
        {
            var defaultOptions = new AITaskManagerOptions();
            await WriteAtomicallyAsync(fullPath, defaultOptions, cancellationToken).ConfigureAwait(false);
            return new(fullPath, true, false, defaultOptions.SchemaVersion, null);
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaximumConfigurationBytes)
        {
            throw new ConfigurationValidationException([
                new("ConfigurationFile", "The configuration file exceeds the 1 MiB safety limit."),
            ]);
        }

        JsonObject root;
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new JsonException("The configuration root must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new ConfigurationValidationException([
                new("ConfigurationFile", $"The JSON is invalid: {exception.Message}"),
            ]);
        }

        var version = ReadSchemaVersion(root);
        if (version > AITaskManagerOptions.CurrentSchemaVersion)
        {
            throw new ConfigurationValidationException([
                new(
                    "SchemaVersion",
                    $"This file requires schema version {version}, but this build supports {AITaskManagerOptions.CurrentSchemaVersion}."),
            ]);
        }

        if (version == AITaskManagerOptions.CurrentSchemaVersion)
        {
            return new(fullPath, false, false, version, null);
        }

        var backupPath = CreateBackupPath(fullPath);
        File.Copy(fullPath, backupPath, overwrite: false);

        while (version < AITaskManagerOptions.CurrentSchemaVersion)
        {
            version = ApplyMigration(root, version);
        }

        await WriteAtomicallyAsync(fullPath, root, cancellationToken).ConfigureAwait(false);
        return new(fullPath, false, true, version, backupPath);
    }

    private static int ReadSchemaVersion(JsonObject root)
    {
        if (!root.TryGetPropertyValue("schemaVersion", out var node) || node is null)
        {
            return 0;
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var version) && version >= 0)
        {
            return version;
        }

        throw new ConfigurationValidationException([
            new("SchemaVersion", "Use a non-negative integer schema version."),
        ]);
    }

    private static int ApplyMigration(JsonObject root, int version)
    {
        if (version != 0)
        {
            throw new ConfigurationValidationException([
                new("SchemaVersion", $"No migration is available from schema version {version}."),
            ]);
        }

        root["schemaVersion"] = 1;
        root["actions"] ??= new JsonObject
        {
            ["enabled"] = true,
            ["powerControlEnabled"] = true,
            ["fanControlEnabled"] = true,
        };

        return 1;
    }

    private static string CreateBackupPath(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath)!;
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(directory, $"{name}.backup-{timestamp}-{Guid.NewGuid():N}{extension}");
    }

    private static async Task WriteAtomicallyAsync<T>(
        string destination,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
