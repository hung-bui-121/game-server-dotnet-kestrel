using System.Collections;
using System.Globalization;

namespace GameServer.Platform.Web.Requests;

/// <summary>
/// State of one request event (data-model §1): the 14 fields as properties plus <c>{OriginalFormat}</c>, so Serilog keeps the
/// template as <c>@mt</c> and every field as a property (research V5). Built once per logged request; no LINQ, no closures.
/// </summary>
internal readonly struct RequestLogEntry : IReadOnlyList<KeyValuePair<string, object?>>
{
    public const string Template = "{HttpMethod} {Route} -> {Status} {ResultCode} in {DurationMs} ms";

    private const int FieldCount = 15;

    private static readonly object True = true;
    private static readonly object False = false;

    public RequestLogEntry(
        string httpMethod,
        string route,
        string? profile,
        string? operation,
        string? actorType,
        string? actorId,
        string requestId,
        string? traceId,
        int? status,
        string resultCode,
        bool replayed,
        bool notModified,
        bool aborted,
        double durationMs)
    {
        HttpMethod = httpMethod;
        Route = route;
        Profile = profile;
        Operation = operation;
        ActorType = actorType;
        ActorId = actorId;
        RequestId = requestId;
        TraceId = traceId;
        Status = status;
        ResultCode = resultCode;
        Replayed = replayed;
        NotModified = notModified;
        Aborted = aborted;
        DurationMs = durationMs;
    }

    public string HttpMethod { get; }

    public string Route { get; }

    public string? Profile { get; }

    public string? Operation { get; }

    public string? ActorType { get; }

    public string? ActorId { get; }

    public string RequestId { get; }

    public string? TraceId { get; }

    public int? Status { get; }

    public string ResultCode { get; }

    public bool Replayed { get; }

    public bool NotModified { get; }

    public bool Aborted { get; }

    public double DurationMs { get; }

    public int Count => FieldCount;

    public KeyValuePair<string, object?> this[int index] => index switch
    {
        0 => new("HttpMethod", HttpMethod),
        1 => new("Route", Route),
        2 => new("Profile", Profile),
        3 => new("Operation", Operation),
        4 => new("ActorType", ActorType),
        5 => new("ActorId", ActorId),
        6 => new("RequestId", RequestId),
        7 => new("TraceId", TraceId),
        8 => new("Status", Status),
        9 => new("ResultCode", ResultCode),
        10 => new("Replayed", Replayed ? True : False),
        11 => new("NotModified", NotModified ? True : False),
        12 => new("Aborted", Aborted ? True : False),
        13 => new("DurationMs", DurationMs),
        14 => new("{OriginalFormat}", Template),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>The template rendered for providers that format messages; no actor id (existing "not in logs" checks).</summary>
    public static string Format(RequestLogEntry entry, Exception? exception) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.HttpMethod} {entry.Route} -> {entry.Status?.ToString(CultureInfo.InvariantCulture) ?? "-"} {entry.ResultCode} in {entry.DurationMs} ms");

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var i = 0; i < FieldCount; i++)
        {
            yield return this[i];
        }
    }

    public override string ToString() => Format(this, null);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
