using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GameServer.Tests.Shared;

/// <summary>
/// Slice 016 T015: an <see cref="ILoggerProvider"/> that records every log call as the kit's <see cref="CapturedLog"/>, for a
/// host the test composes itself (<c>services.AddSingleton&lt;ILoggerProvider&gt;(sink)</c>). A <see cref="TestHost"/> already
/// captures its logs in <see cref="TestHost.LogEntries"/>; the queries of <see cref="CapturedLogQueries"/> work on both.
/// </summary>
public sealed class CapturedLogSink : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public IReadOnlyCollection<CapturedLog> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    /// <summary>Messages logged in <paramref name="category"/>, in order.</summary>
    public IReadOnlyList<string> Lines(string category) => _entries.Lines(category);

    /// <summary>Entries of <paramref name="level"/> whose message starts with <paramref name="prefix"/>.</summary>
    public int Count(LogLevel level, string prefix) => _entries.Count(level, prefix);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    properties[key] = value;
                }
            }

            entries.Enqueue(new CapturedLog(category, logLevel, eventId, formatter(state, exception), properties, exception));
        }
    }
}

/// <summary>Queries over captured log entries (<see cref="TestHost.LogEntries"/> or <see cref="CapturedLogSink.Entries"/>).</summary>
public static class CapturedLogQueries
{
    /// <summary>Messages logged in <paramref name="category"/>, in order.</summary>
    public static IReadOnlyList<string> Lines(this IEnumerable<CapturedLog> entries, string category) =>
        entries.Where(e => string.Equals(e.Category, category, StringComparison.Ordinal)).Select(e => e.Message).ToArray();

    /// <summary>Entries of <paramref name="level"/> whose message starts with <paramref name="prefix"/>.</summary>
    public static int Count(this IEnumerable<CapturedLog> entries, LogLevel level, string prefix) =>
        entries.Count(e => e.Level == level && e.Message.StartsWith(prefix, StringComparison.Ordinal));
}
