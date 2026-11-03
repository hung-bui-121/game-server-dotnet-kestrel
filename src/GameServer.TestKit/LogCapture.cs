using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GameServer.TestKit;

/// <summary>Log sink of one <see cref="TestHost"/>: formatted lines (<see cref="Logs"/>) and structured entries.</summary>
internal sealed class LogCapture
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public ConcurrentQueue<string> Logs { get; } = new();

    public IReadOnlyCollection<CapturedLog> Entries => _entries;

    public ILoggerProvider CreateProvider() => new Provider(this);

    public ILogger<T> CreateLogger<T>() => new TypedLogger<T>(new Logger(typeof(T).FullName ?? typeof(T).Name, this));

    private void Write<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        Logs.Enqueue(message + (exception is null ? string.Empty : " | " + exception));
        _entries.Enqueue(new CapturedLog(category, level, eventId, message, PropertiesOf(state), exception));
    }

    private static Dictionary<string, object?> PropertiesOf<TState>(TState state)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            for (var i = 0; i < values.Count; i++)
            {
                properties[values[i].Key] = values[i].Value;
            }
        }

        return properties;
    }

    private sealed class Provider(LogCapture capture) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, capture);

        public void Dispose()
        {
        }
    }

    private sealed class Logger(string category, LogCapture capture) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            capture.Write(category, logLevel, eventId, state, exception, formatter);
    }

    private sealed class TypedLogger<T>(Logger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
