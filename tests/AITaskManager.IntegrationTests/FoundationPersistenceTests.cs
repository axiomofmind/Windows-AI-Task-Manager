using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using AITaskManager.Application;
using AITaskManager.Infrastructure.Windows;
using AITaskManager.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace AITaskManager.IntegrationTests;

public sealed class FoundationPersistenceTests
{
    [Fact]
    public void ConfigurationPrecedenceIsCommandLineThenEnvironmentThenFileThenDefaults()
    {
        var prefix = $"AITM_TEST_{Guid.NewGuid():N}_";
        var environmentKey = prefix + "Server__ListenUrl";
        Environment.SetEnvironmentVariable(environmentKey, "http://127.0.0.1:7314");

        try
        {
            using var json = new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"Server\":{\"ListenUrl\":\"http://127.0.0.1:7313\"}}"));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Server:ListenUrl"] = "http://127.0.0.1:7312",
                })
                .AddJsonStream(json)
                .AddEnvironmentVariables(prefix)
                .AddCommandLine(["--Server:ListenUrl=http://127.0.0.1:7315"])
                .Build();

            Assert.Equal("http://127.0.0.1:7315", configuration["Server:ListenUrl"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentKey, null);
        }
    }

    [Fact]
    public async Task ConfigurationIsCreatedAndLegacyConfigurationIsMigratedWithBackup()
    {
        using var temporary = new TemporaryDirectory();
        var configurationPath = Path.Combine(temporary.Path, "config.json");
        var manager = new ConfigurationFileManager();

        var created = await manager.InitializeAsync(configurationPath);

        Assert.True(created.Created);
        Assert.Equal(AITaskManagerOptions.CurrentSchemaVersion, created.SchemaVersion);

        await File.WriteAllTextAsync(configurationPath, "{\"customField\":\"preserved\"}");
        var migrated = await manager.InitializeAsync(configurationPath);

        Assert.True(migrated.Migrated);
        Assert.NotNull(migrated.BackupPath);
        Assert.True(File.Exists(migrated.BackupPath));
        var root = JsonNode.Parse(await File.ReadAllTextAsync(configurationPath))!.AsObject();
        Assert.Equal(1, root["schemaVersion"]!.GetValue<int>());
        Assert.Equal("preserved", root["customField"]!.GetValue<string>());
    }

    [Fact]
    public async Task InvalidConfigurationIsRejectedWithoutOverwritingIt()
    {
        using var temporary = new TemporaryDirectory();
        var configurationPath = Path.Combine(temporary.Path, "config.json");
        const string invalidJson = "{ definitely-not-json";
        await File.WriteAllTextAsync(configurationPath, invalidJson);

        await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => new ConfigurationFileManager().InitializeAsync(configurationPath));

        Assert.Equal(invalidJson, await File.ReadAllTextAsync(configurationPath));
    }

    [Fact]
    public void PathsExpandEnvironmentVariablesAndSupportUnicodeAndSpaces()
    {
        using var temporary = new TemporaryDirectory("AI Task Manager ünicode");
        var variableName = $"AITM_TEST_ROOT_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, temporary.Path);

        try
        {
            var resolved = ConfiguredPathResolver.ResolveDirectory(
                $"%{variableName}%\\model roots",
                temporary.Path,
                "Discovery:ModelRoots:0");

            Assert.Equal(Path.GetFullPath(Path.Combine(temporary.Path, "model roots")), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void UnresolvedEnvironmentVariableIsRejected()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfiguredPathResolver.ResolveDirectory(
                $"%AITM_MISSING_{Guid.NewGuid():N}%\\models",
                Path.GetTempPath(),
                "Discovery:ModelRoots:0"));

        Assert.Contains(exception.Errors, error => error.Path == "Discovery:ModelRoots:0");
    }

    [Fact]
    public void ResolutionPreservesConfiguredPathTextForDisplay()
    {
        using var temporary = new TemporaryDirectory();
        var options = new AITaskManagerOptions();
        options.Discovery.ModelRoots.Add("%TEMP%\\models");
        var defaults = AppPaths.ForRoot(temporary.Path);

        var resolved = ConfiguredPathResolver.Resolve(options, defaults);

        Assert.Equal("%TEMP%\\models", options.Discovery.ModelRoots[0]);
        Assert.NotEqual(options.Discovery.ModelRoots[0], resolved.ModelRoots[0]);
    }

    [Fact]
    public void WindowsPathShapesAreNormalizedWithoutFilesystemScanning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new[]
        {
            @"D:\AI Models\Qwen",
            @"\\server\share\模型",
            @"C:\AI\" + new string('x', 280),
        };

        foreach (var path in paths)
        {
            var resolved = ConfiguredPathResolver.ResolveDirectory(path, path, "test");
            Assert.True(System.IO.Path.IsPathFullyQualified(resolved));
        }
    }

    [Fact]
    public async Task DatabaseMigratesUsesWalSerializesWritesAndCreatesBackup()
    {
        using var temporary = new TemporaryDirectory();
        var database = new SqliteDatabase(Path.Combine(temporary.Path, "aitaskmanager.db"));
        await database.InitializeAsync();

        await using (var queue = new SqliteWriteQueue(database, capacity: 8))
        {
            var writes = Enumerable.Range(0, 40)
                .Select(index => queue.SetMetadataAsync($"key-{index}", $"value-{index}"));
            await Task.WhenAll(writes);
            Assert.True(queue.TryEnqueueMetricSamples([
                new MetricSampleWrite("test", "GPU-test", "temperature_celsius", DateTimeOffset.UtcNow, 42, "available"),
                new MetricSampleWrite("test", "GPU-test", "temperature_celsius", DateTimeOffset.UtcNow.AddDays(-60), 40, "available"),
            ]));
            Assert.True(queue.TryEnqueueMetricRetention(DateTimeOffset.UtcNow.AddDays(-30)));
            await queue.SetMetadataAsync("metric-flush", "complete");
        }

        var status = await database.GetStatusAsync();
        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, status.SchemaVersion);
        Assert.Equal("ok", status.Integrity);
        Assert.True(status.WriteAheadLoggingEnabled);
        Assert.Equal("value-39", await database.GetMetadataAsync("key-39"));
        Assert.Equal(1, await database.GetMetricSampleCountAsync());

        var backupPath = Path.Combine(temporary.Path, "backups", "aitaskmanager.db");
        await database.CreateBackupAsync(backupPath);
        var backup = new SqliteDatabase(backupPath);
        var backupStatus = await backup.GetStatusAsync();
        Assert.Equal("ok", backupStatus.Integrity);
        Assert.Equal("value-39", await backup.GetMetadataAsync("key-39"));
    }

    [Fact]
    public async Task CorruptDatabaseIsReportedAndPreserved()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "aitaskmanager.db");
        var bytes = Encoding.UTF8.GetBytes("not a sqlite database");
        await File.WriteAllBytesAsync(databasePath, bytes);

        var database = new SqliteDatabase(databasePath);
        await Assert.ThrowsAsync<SqliteException>(() => database.InitializeAsync());

        Assert.Equal(bytes, await File.ReadAllBytesAsync(databasePath));
    }

    [SupportedOSPlatform("windows")]
    [Fact]
    public async Task DpapiSecretStoreRoundTripsWithoutPlaintextAtRest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var store = new DpapiSecretStore(temporary.Path);
        const string value = "private-test-value";

        await store.SetAsync("runtime-token", value);

        Assert.Equal(value, await store.GetAsync("runtime-token"));
        var secretFile = Assert.Single(Directory.GetFiles(temporary.Path, "*.secret"));
        Assert.DoesNotContain(value, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(secretFile)));
        Assert.True(await store.DeleteAsync("runtime-token"));
        Assert.Null(await store.GetAsync("runtime-token"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _basePath;

        public TemporaryDirectory(string? segment = null)
        {
            _basePath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AITaskManager.Tests"));
            Path = System.IO.Path.Combine(_basePath, segment ?? Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (resolved.StartsWith(_basePath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(resolved))
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
