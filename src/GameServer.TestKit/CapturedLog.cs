using Microsoft.Extensions.Logging;

namespace GameServer.TestKit;

/// <summary>
/// One log call captured by a <see cref="TestHost"/>: category, level, event id, formatted message, the structured state
/// (every key/value of an <c>IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> state, <c>{OriginalFormat}</c>
/// included; empty for other states) and the exception.
/// </summary>
public sealed record CapturedLog(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception);
