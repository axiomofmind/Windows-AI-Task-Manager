using AITaskManager.Application;
using Microsoft.Extensions.Logging;
using System.Text;

namespace AITaskManager.Hosting;

public sealed class RedactingLoggerProvider : ILoggerProvider
{
    private const long MaximumLogFileBytes = 5 * 1024 * 1024;
    private readonly ISensitiveDataRedactor _redactor;
    private readonly string _logDirectory;
    private readonly int _retentionDays;
    private readonly object _writeLock = new();

    public RedactingLoggerProvider(ISensitiveDataRedactor redactor, string logDirectory, int retentionDays)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        _redactor = redactor;
        _logDirectory = Path.GetFullPath(logDirectory);
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_logDirectory);
        DeleteExpiredLogs();
    }

    public ILogger CreateLogger(string categoryName) => new RedactingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var rendered = $"{timestamp:O} [{level}] {category}: {_redactor.Redact(message)}";
        if (exception is not null)
        {
            rendered += Environment.NewLine + _redactor.Redact(exception.ToString());
        }

        lock (_writeLock)
        {
            Console.WriteLine(rendered);
            var logPath = Path.Combine(_logDirectory, $"aitaskmanager-{timestamp:yyyyMMdd}.log");
            var entry = rendered + Environment.NewLine;
            RotateIfNeeded(logPath, Encoding.UTF8.GetByteCount(entry));
            File.AppendAllText(logPath, entry);
        }
    }

    private static void RotateIfNeeded(string logPath, int incomingBytes)
    {
        if (!File.Exists(logPath) || new FileInfo(logPath).Length + incomingBytes <= MaximumLogFileBytes)
        {
            return;
        }

        var previousPath = Path.ChangeExtension(logPath, "previous.log");
        if (File.Exists(previousPath))
        {
            File.Delete(previousPath);
        }

        File.Move(logPath, previousPath);
    }

    private void DeleteExpiredLogs()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_logDirectory, "aitaskmanager-*.log", SearchOption.TopDirectoryOnly))
        {
            var fullPath = Path.GetFullPath(file);
            if (!string.Equals(Path.GetDirectoryName(fullPath), _logDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.GetLastWriteTimeUtc(fullPath) < cutoff)
            {
                File.Delete(fullPath);
            }
        }
    }

    private sealed class RedactingLogger(RedactingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
