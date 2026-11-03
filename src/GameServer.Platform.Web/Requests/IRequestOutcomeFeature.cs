using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Web.Requests;

/// <summary>
/// Outcome of the current request (FR-084), on <see cref="HttpContext.Features"/>. Set by the framework for every response it
/// writes; a consumer endpoint may set <see cref="ResultCode"/> and the flags for its own responses. After the framework
/// pipeline completed (e.g. in a consumer middleware placed before <c>UseGameServer</c>, after <c>await next()</c>), every
/// member is final and <see cref="ResultCode"/> is never null. Reading it never needs the response body. Not a wire contract:
/// clients never see it.
/// </summary>
public interface IRequestOutcomeFeature
{
    /// <summary>
    /// The code the client received (API-11): <c>OK</c>, a command's business code, the error-envelope / Problem Details code,
    /// <see cref="RequestResultCodes.Aborted"/>, <c>INTERNAL</c> or <see cref="RequestResultCodes.ForStatus"/>. During the
    /// request: what was set so far (null until a writer set it).
    /// </summary>
    string? ResultCode { get; set; }

    /// <summary>Command operation (<c>counter.increment</c>); null for routes that are not commands.</summary>
    string? Operation { get; set; }

    /// <summary>The command was answered from its receipt.</summary>
    bool Replayed { get; set; }

    /// <summary>The config request was answered <c>not_modified</c>.</summary>
    bool NotModified { get; set; }

    /// <summary>HTTP status sent; null when the client aborted before the response started.</summary>
    int? StatusCode { get; }

    /// <summary>The client went away before the response started.</summary>
    bool Aborted { get; }

    /// <summary>The framework pipeline finished and every member is final.</summary>
    bool Completed { get; }

    /// <summary>
    /// Name of the matched catalog route (FG-026, e.g. <c>platform.config</c>): compare with <c>PlatformRoutes.Config.Name</c> instead of a
    /// copied route string. Null for unmatched paths; final once <see cref="Completed"/>.
    /// </summary>
    string? RouteName => null;
}

public static class RequestOutcomeExtensions
{
    /// <summary>
    /// The request's outcome feature: installed by the framework's outermost middleware; created on first use otherwise (e.g. a
    /// consumer endpoint in a host without that middleware). Never null.
    /// </summary>
    public static IRequestOutcomeFeature RequestOutcome(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var outcome = context.Features.Get<IRequestOutcomeFeature>();
        if (outcome is null)
        {
            outcome = new RequestOutcome();
            context.Features.Set(outcome);
        }

        return outcome;
    }
}

/// <summary>Result codes that are not error-catalog entries (request event and outcome only, never sent to clients).</summary>
public static class RequestResultCodes
{
    /// <summary>The client went away before the response started.</summary>
    public const string Aborted = "ABORTED";

    /// <summary>Code of a non-2xx response the framework did not write: <c>HTTP_{status}</c>.</summary>
    public static string ForStatus(int statusCode) => "HTTP_" + statusCode.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Logger category and event id of the request event (FR-080; renaming either is a breaking change, PCR-01).</summary>
public static class RequestLog
{
    public const string Category = "GameServer.Requests";

    public static readonly EventId EventId = new(1, "Request");
}
