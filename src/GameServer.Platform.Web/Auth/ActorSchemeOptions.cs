using GameServer.Errors;
using Microsoft.AspNetCore.Http;

namespace GameServer.Platform.Web.Auth;

/// <summary>
/// Authentication schemes per actor type (SEC-02): Player endpoints only try <see cref="PlayerSchemes"/>, GM endpoints
/// only <see cref="GmSchemes"/>, so a player credential can never authenticate on the GM API and vice versa.
/// </summary>
public sealed class ActorSchemeOptions
{
    public List<string> PlayerSchemes { get; } = [];

    public List<string> GmSchemes { get; } = [];
}

/// <summary>Lets an authentication handler report a precise catalog code (e.g. SESSION_REPLACED) for its failure.</summary>
public static class AuthenticationFailures
{
    private const string CodeKey = "gs.auth.error";
    private const string DetailsKey = "gs.auth.details";

    public static void Report(HttpContext context, ErrorCode code, IReadOnlyDictionary<string, string>? details = null)
    {
        // Keep the most specific (first) failure reported for this request.
        if (!context.Items.ContainsKey(CodeKey))
        {
            context.Items[CodeKey] = code;
            context.Items[DetailsKey] = details;
        }
    }

    public static (ErrorCode Code, IReadOnlyDictionary<string, string>? Details) Get(HttpContext context) =>
        context.Items[CodeKey] is ErrorCode code
            ? (code, context.Items[DetailsKey] as IReadOnlyDictionary<string, string>)
            : (CoreErrorCodes.Unauthenticated, null);
}
