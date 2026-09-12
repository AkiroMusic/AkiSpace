using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AkiSpace;

/// <summary>
/// File logger provider backed by a single persistent <see cref="StreamWriter"/>.
/// Replaces the old open/append/close-per-line pattern (which churned file handles)
/// with a buffered writer flushed per line, prunes stale daily logs on construction,
/// and makes the minimum log level configurable.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    // UTF-8 without a BOM to stay byte-compatible with the previous
    // File.AppendAllText(...) output so existing log parsers keep working.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly StreamWriter? _writer;
    private readonly LogLevel _minimumLevel;

    public FileLoggerProvider(string filePath, LogLevel minimumLevel = LogLevel.Debug, int retainedDays = 14)
    {
        _minimumLevel = minimumLevel;

        // Best-effort pruning of old daily logs; must never throw out of the ctor.
        PruneOldLogs(filePath, retainedDays);

        // Open one persistent writer. Other tools can still read the file because
        // we open it with FileShare.ReadWrite. If the file is locked or the path
        // is unavailable we degrade to dropping log lines instead of crashing.
        try
        {
            var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = false };
        }
        catch (Exception ex)
        {
            Debug.Write($"FileLoggerProvider failed to open log file '{filePath}': {ex}");
            _writer = null;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName, _minimumLevel);

    /// <summary>
    /// Writes one preformatted line. Shared by every logger this provider creates,
    /// guarded by a single lock so concurrent categories interleave safely.
    /// </summary>
    internal void Write(string line)
    {
        var writer = _writer;
        if (writer is null) return;

        try
        {
            lock (_gate)
            {
                writer.Write(line);
                writer.Flush();
            }
        }
        catch (ObjectDisposedException)
        {
            // Provider was disposed concurrently; dropping the line is fine.
        }
        catch (Exception ex)
        {
            // The log pipeline must never crash the app.
            Debug.Write($"FileLoggerProvider write failed: {ex}");
        }
    }

    public void Dispose()
    {
        try
        {
            lock (_gate)
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.Write($"FileLoggerProvider dispose failed: {ex}");
        }
    }

    private static void PruneOldLogs(string filePath, int retainedDays)
    {
        if (retainedDays <= 0) return;

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory)) return;

        var fileName = Path.GetFileName(filePath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        // Match this app's daily-log family, e.g. "akspace-*.log".
        var pattern = fileName.StartsWith("akspace-", StringComparison.OrdinalIgnoreCase)
            ? "akspace-*.log"
            : $"{fileNameWithoutExtension}-*.log";

        try
        {
            var cutoffUtc = DateTime.UtcNow.AddDays(-retainedDays);
            foreach (var candidate in Directory.GetFiles(directory, pattern))
            {
                try
                {
                    // Never delete the file currently in use.
                    if (string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (File.GetLastWriteTimeUtc(candidate) < cutoffUtc)
                        File.Delete(candidate);
                }
                catch (Exception ex)
                {
                    // Individual failures (locked/read-only file) are non-fatal.
                    Debug.Write($"FileLoggerProvider failed to prune '{candidate}': {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.Write($"FileLoggerProvider failed to enumerate log directory '{directory}': {ex}");
        }
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly FileLoggerProvider _provider;
    private readonly string _category;
    private readonly LogLevel _minimumLevel;

    public FileLogger(FileLoggerProvider provider, string category, LogLevel minimumLevel)
    {
        _provider = provider;
        _category = category;
        _minimumLevel = minimumLevel;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
        sb.Append(" [").Append(logLevel.ToString().ToUpperInvariant()).Append("] ");
        sb.Append(_category).Append(": ").Append(message);
        if (exception != null)
            sb.Append("\n").Append(exception);
        sb.AppendLine();

        _provider.Write(sb.ToString());
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
