using System.Text.RegularExpressions;

namespace AITaskManager.Application;

public interface ISensitiveDataRedactor
{
    string Redact(string value);
}

public sealed class SensitiveDataRedactor : ISensitiveDataRedactor
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex BearerTokenPattern = new(
        @"(?i)\bBearer\s+[^\s,;]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeout);

    private static readonly Regex CommandSecretPattern = new(
        "(?i)(--(?:api[-_]?key|token|password|secret))(?:\\s+|=)(?:\"[^\"]*\"|'[^']*'|[^\\s]+)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeout);

    private static readonly Regex QuerySecretPattern = new(
        @"(?i)([?&](?:api[-_]?key|access[-_]?token|token|password|secret)=)[^&#\s]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeout);

    private static readonly Regex AssignmentSecretPattern = new(
        @"(?im)\b((?:api[-_]?key|access[-_]?token|token|password|secret)\s*=\s*)[^\s;]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeout);

    private static readonly Regex EnvironmentSecretPattern = new(
        @"(?im)\b([A-Z][A-Z0-9_]*(?:API_KEY|ACCESS_TOKEN|AUTH_TOKEN|PASSWORD|SECRET|TOKEN)\s*=\s*)[^\s;]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeout);

    private static readonly Regex UrlCredentialPattern = new(
        @"(?i)(https?://)[^/\s:@]+:[^@/\s]+@",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeout);

    private readonly IReadOnlyList<(string Path, string Replacement)> _pathReplacements;

    public SensitiveDataRedactor()
    {
        var replacements = new List<(string, string)>();
        AddKnownPath(replacements, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        AddKnownPath(replacements, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
        _pathReplacements = replacements.OrderByDescending(item => item.Item1.Length).ToArray();
    }

    public string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var result = value;
        foreach (var (path, replacement) in _pathReplacements)
        {
            result = result.Replace(path, replacement, StringComparison.OrdinalIgnoreCase);
        }

        result = BearerTokenPattern.Replace(result, "Bearer [REDACTED]");
        result = CommandSecretPattern.Replace(result, match => $"{match.Groups[1].Value}=[REDACTED]");
        result = QuerySecretPattern.Replace(result, match => $"{match.Groups[1].Value}[REDACTED]");
        result = EnvironmentSecretPattern.Replace(result, match => $"{match.Groups[1].Value}[REDACTED]");
        result = AssignmentSecretPattern.Replace(result, match => $"{match.Groups[1].Value}[REDACTED]");
        result = UrlCredentialPattern.Replace(result, match => $"{match.Groups[1].Value}[REDACTED]@");

        return result;
    }

    private static void AddKnownPath(ICollection<(string, string)> replacements, string path, string replacement)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path);
            replacements.Add((trimmed.Replace("\\", "\\\\", StringComparison.Ordinal), replacement));
            replacements.Add((trimmed, replacement));
        }
    }
}
