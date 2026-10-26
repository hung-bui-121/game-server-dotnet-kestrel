using Microsoft.AspNetCore.Http;

namespace GameServer.Modules.Gm;

/// <summary>
/// Minimal GM page (FR-012): one static same-origin HTML file, no framework (TBD-21 open). It is a convenience only:
/// every decision is taken by the backend on each API call.
/// </summary>
public static class GmUi
{
    private const string ResourceName = "GameServer.Modules.Gm.ui.index.html";

    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; " +
        "img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    private static readonly Lazy<byte[]> Page = new(() =>
    {
        using var stream = typeof(GmUi).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    public static IResult Serve(HttpContext http)
    {
        http.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "no-store";
        return Results.Bytes(Page.Value, "text/html; charset=utf-8");
    }
}
