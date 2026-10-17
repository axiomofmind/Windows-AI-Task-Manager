using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AITaskManager.Adapters.Nvidia;

internal sealed class FanOwnershipLock : IDisposable
{
    private FileStream? _lockStream;

    private FanOwnershipLock(FileStream lockStream)
    {
        _lockStream = lockStream;
    }

    public static FanOwnershipLock? TryAcquire(string gpuId, string? rootDirectory = null)
    {
        var lockPath = FanOwnershipJournal.LockPathFor(gpuId, rootDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return new FanOwnershipLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _lockStream, null)?.Dispose();
}

internal static class FanOwnershipJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool Exists(string gpuId, string? rootDirectory = null) => File.Exists(PathFor(gpuId, rootDirectory));

    public static void Record(string gpuId, uint fanCount, string auditId, string? rootDirectory = null)
    {
        var directory = JournalDirectory(rootDirectory);
        Directory.CreateDirectory(directory);
        var path = PathFor(gpuId, rootDirectory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = JsonSerializer.Serialize(
                new OwnershipRecord(gpuId, fanCount, auditId, DateTimeOffset.UtcNow),
                JsonOptions);
            File.WriteAllText(temporary, payload, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Clear(string gpuId, string? rootDirectory = null)
    {
        var path = PathFor(gpuId, rootDirectory);
        if (File.Exists(path)) File.Delete(path);
    }

    internal static string Key(string gpuId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gpuId.ToUpperInvariant())))[..24];

    internal static string PathFor(string gpuId, string? rootDirectory = null) =>
        Path.Combine(JournalDirectory(rootDirectory), $"{Key(gpuId)}.json");

    internal static string LockPathFor(string gpuId, string? rootDirectory = null) =>
        Path.Combine(JournalDirectory(rootDirectory), $"{Key(gpuId)}.lock");

    private static string JournalDirectory(string? rootDirectory) => rootDirectory is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AITaskManager", "fan-ownership")
        : Path.GetFullPath(rootDirectory);

    private sealed record OwnershipRecord(string GpuId, uint FanCount, string AuditId, DateTimeOffset OwnedAt);
}
