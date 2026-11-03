using System.Collections;
using System.Globalization;

namespace GameServer.Platform.Web.Requests;

/// <summary>
/// State of one request event (data-model §1): the 15 fields as properties plus <c>{OriginalFormat}</c>, so Serilog keeps the
/// template as <c>@mt</c> and every field as a property (research V5). Built once per logged request; no LINQ, no closures.
/// </summary>
internal readonly struct RequestLogEntry : IReadOnlyList<KeyValuePair<string, object?>>
{
    public const string Template = "{HttpMethod} {Route} -> {Status} {ResultCode} in {DurationMs} ms";

    private const int FieldCount = 16;

    private static readonly object True = true;
    private static readonly object False = false;

    public RequestLogEntry(
        string httpMethod,
        string route,
        string? routeName,
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
        RouteName = routeName;
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

    /// <summary>Name of the matched catalog route (FR-015, slice 014); not part of the console line.</summary>
    public string? RouteName { get; }

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
        2 => new("RouteName", RouteName),
        3 => new("Profile", Profile),
        4 => new("Operation", Operation),
        5 => new("ActorType", ActorType),
        6 => new("ActorId", ActorId),
        7 => new("RequestId", RequestId),
        8 => new("TraceId", TraceId),
        9 => new("Status", Status),
        10 => new("ResultCode", ResultCode),
        11 => new("Replayed", Replayed ? True : False),
        12 => new("NotModified", NotModified ? True : False),
        13 => new("Aborted", Aborted ? True : False),
        14 => new("DurationMs", DurationMs),
        15 => new("{OriginalFormat}", Template),
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
