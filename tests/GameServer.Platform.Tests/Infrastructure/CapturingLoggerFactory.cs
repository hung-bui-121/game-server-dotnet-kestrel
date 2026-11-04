using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Tests.Infrastructure;

/// <summary>One captured log call.</summary>
public sealed record LogLine(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>An <see cref="ILoggerFactory"/> that records every log call, for services built without a host.</summary>
public sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<LogLine> _lines = new();

    public IReadOnlyCollection<LogLine> Lines => _lines;

    /// <summary>Messages of <paramref name="category"/>, in order.</summary>
    public IReadOnlyList<string> Messages(string category) =>
        _lines.Where(l => string.Equals(l.Category, category, StringComparison.Ordinal)).Select(l => l.Message).ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<LogLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(new LogLine(category, logLevel, formatter(state, exception), exception));
    }
}
