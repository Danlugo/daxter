using System.Collections.Concurrent;
using Daxter.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Daxter.Web.Services;

/// <summary>A single captured log line.</summary>
public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message);

/// <summary>
/// A bounded, in-memory ring buffer of recent log entries that the console's Logs page reads.
/// Singleton; thread-safe. <see cref="Version"/> bumps on every change so a UI can poll cheaply.
/// Holds at most <see cref="Capacity"/> entries (oldest dropped); never persisted.
/// </summary>
public sealed class LogSink
{
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();

    public int Capacity { get; }
    public long Version { get; private set; }

    public LogSink()
    {
        Capacity = int.TryParse(Environment.GetEnvironmentVariable("DAXTER_LOG_BUFFER"), out var n) && n > 0
            ? n
            : 1000;
    }

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity) _entries.Dequeue();
            Version++;
        }
    }

    /// <summary>Newest-first snapshot, optionally filtered to a minimum level.</summary>
    public IReadOnlyList<LogEntry> Snapshot(LogLevel minLevel = LogLevel.Trace)
    {
        lock (_gate)
        {
            return _entries
                .Where(e => e.Level >= minLevel)
                .Reverse()
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Version++;
        }
    }
}

/// <summary>Routes <see cref="ILogger"/> output into the <see cref="LogSink"/> for the Logs page.</summary>
public sealed class LogSinkLoggerProvider(LogSink sink) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, LogSinkLogger> _loggers = new();

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new LogSinkLogger(sink, ShortCategory(name)));

    public void Dispose() => _loggers.Clear();

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    private sealed class LogSinkLogger(LogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message += $" — {exception.GetType().Name}: {exception.Message}";
            // Defense-in-depth: never let a credential/token reach the in-app log.
            sink.Add(new LogEntry(DateTimeOffset.Now, logLevel, category, SecretRedactor.Redact(message)));
        }
    }
}
