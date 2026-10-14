using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AITaskManager.Application;

namespace AITaskManager.Infrastructure.Windows;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly Regex ValidNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    private static readonly byte[] AdditionalEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("AI Task Manager secret store v1"));

    private readonly string _directory;

    public DpapiSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public async Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(value);

        var plaintext = Encoding.UTF8.GetBytes(value);
        byte[]? protectedBytes = null;

        try
        {
            protectedBytes = ProtectedData.Protect(plaintext, AdditionalEntropy, DataProtectionScope.CurrentUser);
            await WriteAtomicallyAsync(GetSecretPath(name), protectedBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var path = GetSecretPath(name);

        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[]? plaintext = null;

        try
        {
            plaintext = ProtectedData.Unprotect(protectedBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        cancellationToken.ThrowIfCancellationRequested();

        var path = GetSecretPath(name);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!ValidNamePattern.IsMatch(name))
        {
            throw new ArgumentException(
                "Secret names may contain letters, numbers, periods, underscores, and hyphens.",
                nameof(name));
        }
    }

    private string GetSecretPath(string name)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return Path.Combine(_directory, $"{Convert.ToHexStringLower(digest)}.secret");
    }

    private static async Task WriteAtomicallyAsync(
        string destination,
        byte[] value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";
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
                await stream.WriteAsync(value, cancellationToken).ConfigureAwait(false);
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
