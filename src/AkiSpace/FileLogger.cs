using System.Text;
using Microsoft.Extensions.Logging;

namespace AkiSpace;

/// <summary>
/// File logger provider: appends formatted lines to a file with thread-safe locking.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _gate = new();

    public FileLoggerProvider(string filePath) => _filePath = filePath;

    public ILogger CreateLogger(string categoryName) => new FileLogger(_filePath, _gate, categoryName);

    public void Dispose() { }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _filePath;
    private readonly object _gate;
    private readonly string _category;

    public FileLogger(string filePath, object gate, string category)
    {
        _filePath = filePath;
        _gate = gate;
        _category = category;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

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

        lock (_gate)
        {
            try { File.AppendAllText(_filePath, sb.ToString()); }
            catch { /* file may be locked briefly; drop this line */ }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
