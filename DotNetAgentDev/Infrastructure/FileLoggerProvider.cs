using System.Collections.Concurrent;
using System.Globalization;

namespace DotNetAgentDev.Infrastructure;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _logDirectory;
    private readonly object _gate = new();

    public FileLoggerProvider(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _logDirectory, _gate));

    public void Dispose() => _loggers.Clear();

    private sealed class FileLogger(
        string categoryName,
        string logDirectory,
        object gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            var fileName = $"app-{DateTimeOffset.Now:yyyyMMdd}.log";
            var path = Path.Combine(logDirectory, fileName);
            var message = formatter(state, exception);
            var line = $"{timestamp} [{logLevel}] {categoryName}: {message}";
            if (exception is not null)
            {
                line = $"{line}{Environment.NewLine}{exception}";
            }

            lock (gate)
            {
                File.AppendAllText(path, $"{line}{Environment.NewLine}");
            }
        }
    }
}
