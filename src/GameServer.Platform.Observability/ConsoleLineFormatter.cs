using System.Globalization;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Requests;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Parsing;

namespace GameServer.Platform.Observability;

/// <summary>
/// Console sink format (FR-087, research R12, data-model §1): request events as one compact line built from their properties
/// (null parts omitted), other events as <c>HH:mm:ss.fff LVL message [SourceContext]</c> with the exception on the following
/// lines. ANSI colours on the level only when asked (stdout is a terminal).
/// </summary>
internal sealed class ConsoleLineFormatter(bool colours) : ITextFormatter
{
    private const int RequestIdPrefixLength = 8;
    private const string Reset = "\u001b[0m";

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);
        output.Write(logEvent.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        output.Write(' ');
        WriteLevel(logEvent.Level, output);
        output.Write(' ');
        if (string.Equals(Text(logEvent, "SourceContext"), RequestLog.Category, StringComparison.Ordinal))
        {
            WriteRequest(logEvent, output);
        }
        else
        {
            WriteMessage(logEvent, output);
            if (Text(logEvent, "SourceContext") is { } source)
            {
                output.Write(" [");
                output.Write(source);
                output.Write(']');
            }
        }

        output.WriteLine();
        if (logEvent.Exception is not null)
        {
            output.WriteLine(logEvent.Exception.ToString());
        }
    }

    /// <summary>
    /// <c>{HttpMethod} {Route}[ ({Operation})][ player=|staff={ActorId}] req={RequestId[0..8]} -> {Status|-} {ResultCode}[ replayed][ not_modified]
    /// {DurationMs} ms</c>.
    /// </summary>
    private static void WriteRequest(LogEvent logEvent, TextWriter output)
    {
        output.Write(Text(logEvent, "HttpMethod"));
        output.Write(' ');
        output.Write(Text(logEvent, "Route"));
        if (Text(logEvent, "Operation") is { } operation)
        {
            output.Write(" (");
            output.Write(operation);
            output.Write(')');
        }

        if (Text(logEvent, "ActorId") is { } actorId)
        {
            output.Write(string.Equals(Text(logEvent, "ActorType"), ActorClaims.GmType, StringComparison.Ordinal) ? " staff=" : " player=");
            output.Write(actorId);
        }

        if (Text(logEvent, "RequestId") is { } requestId)
        {
            output.Write(" req=");
            output.Write(requestId.Length > RequestIdPrefixLength ? requestId.AsSpan(0, RequestIdPrefixLength) : requestId);
        }

        output.Write(" -> ");
        output.Write(Scalar(logEvent, "Status") is int status ? status.ToString(CultureInfo.InvariantCulture) : "-");
        output.Write(' ');
        output.Write(Text(logEvent, "ResultCode"));
        if (Scalar(logEvent, "Replayed") is true)
        {
            output.Write(" replayed");
        }

        if (Scalar(logEvent, "NotModified") is true)
        {
            output.Write(" not_modified");
        }

        output.Write(' ');
        output.Write(Scalar(logEvent, "DurationMs") is double duration ? duration.ToString("0.0", CultureInfo.InvariantCulture) : "-");
        output.Write(" ms");
    }

    /// <summary>The message with string values unquoted (Serilog's <c>:l</c>); other values in their default rendering.</summary>
    private static void WriteMessage(LogEvent logEvent, TextWriter output)
    {
        foreach (var token in logEvent.MessageTemplate.Tokens)
        {
            if (token is PropertyToken property && logEvent.Properties.TryGetValue(property.PropertyName, out var value) &&
                value is ScalarValue { Value: string text })
            {
                output.Write(text);
            }
            else
            {
                token.Render(logEvent.Properties, output, CultureInfo.InvariantCulture);
            }
        }
    }

    private void WriteLevel(LogEventLevel level, TextWriter output)
    {
        var (name, colour) = level switch
        {
            LogEventLevel.Verbose => ("VRB", "\u001b[90m"),
            LogEventLevel.Debug => ("DBG", "\u001b[90m"),
            LogEventLevel.Information => ("INF", "\u001b[32m"),
            LogEventLevel.Warning => ("WRN", "\u001b[33m"),
            LogEventLevel.Error => ("ERR", "\u001b[31m"),
            _ => ("FTL", "\u001b[97;41m"),
        };
        if (colours)
        {
            output.Write(colour);
            output.Write(name);
            output.Write(Reset);
        }
        else
        {
            output.Write(name);
        }
    }

    private static object? Scalar(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar ? scalar.Value : null;

    private static string? Text(LogEvent logEvent, string name) => Scalar(logEvent, name) as string;
}
