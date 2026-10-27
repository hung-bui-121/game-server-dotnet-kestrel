using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GameServer.Platform.Web.Security;

/// <summary>An endpoint that needs CSRF proof even when the caller is anonymous (e.g. local GM login) (SEC-05).</summary>
public sealed class CsrfRequiredMetadata
{
    public static CsrfRequiredMetadata Instance { get; } = new();
}

/// <summary>
/// SEC-05 / ACC-11: which authentication schemes are cookie based (their unsafe requests need the
/// <c>X-CSRF-Token</c> header) and which other origins may send them (CORS allowlist, SEC-06).
/// </summary>
public sealed class CsrfOptions
{
    public const string HeaderName = "X-CSRF-Token";

    public List<string> CookieSchemes { get; } = [];

    public List<string> TrustedOrigins { get; } = [];
}

/// <summary>CORS policy applied by the platform to <c>/gm/**</c>; registered by the GM module only when origins are configured (SEC-06).</summary>
public static class GmCorsPolicy
{
    public const string Name = "gs-gm";
}

public static class CsrfEndpointExtensions
{
    public static TBuilder RequireCsrf<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(CsrfRequiredMetadata.Instance);
}

internal static class CsrfValidation
{
    public static bool IsUnsafe(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method) && !HttpMethods.IsTrace(method);

    /// <summary>
    /// Cookie mutations must come from this site (or a configured origin) and carry the antiforgery token bound to the
    /// caller (header token + cookie token, validated by <see cref="IAntiforgery"/> — not only for forms, V8).
    /// </summary>
    public static async Task<bool> IsValidAsync(HttpContext context, IAntiforgery antiforgery, CsrfOptions options)
    {
        var request = context.Request;
        if (string.Equals(request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0
            && !string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
            && !options.TrustedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return await antiforgery.IsRequestValidAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
