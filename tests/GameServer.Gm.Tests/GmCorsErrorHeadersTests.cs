using System.Net;
using GameServer.Gm.Tests.Infrastructure;
using GameServer.Modules.Gm;
using GameServer.Modules.Identity;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.OpenApi;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 018 FR-002 / SC-001 (VERIFY V2, design H14): every <c>/gm/**</c> answer to the configured console origin carries the
/// CORS headers, whichever writer produced it — the endpoint, profile enforcement, the GM limiter, the GM request timeout or the
/// exception middleware — so a console on another origin can read every error. Another origin gets none of them. Through the
/// real pipeline; the throwing and waiting endpoints come from the test-only <see cref="CorsProbeModule"/>.
/// </summary>
public sealed class GmCorsErrorHeadersTests : IAsyncLifetime
{
    private const string Allowed = "https://gm.example.com";
    private const string Foreign = "https://evil.example.com";
    private const string ExposedHeaders = "X-Request-Id,Retry-After,ETag";

    private static readonly string[] Origins = [Allowed, Foreign];

    private TestHost _host = null!;
    private CookieContainer _jar = null!;
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await StartAsync();
        _jar = new CookieContainer();

        // HTTPS like a deployed console (a pre-018 host makes the cookies Secure as soon as CORS is on).
        _admin = _host.Browser(_jar, https: true);
        (await _admin.PostLoginAsync("admin")).EnsureSuccessStatusCode();
        await _admin.RefreshCsrfAsync();
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static Task<TestHost> StartAsync(Action<Dictionary<string, string?>>? limits = null) =>
        GmTestHost.StartAsync(
            values =>
            {
                GmTestHost.WithConsole(values, Allowed);
                limits?.Invoke(values);
            },
            extra: b => b.AddModule<CorsProbeModule>());

    private static HttpRequestMessage Request(HttpMethod method, string route, string origin, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, route) { Content = content };
        request.Headers.Add("Origin", origin);
        return request;
    }

    /// <summary>Sends the request built for each origin and checks the status, the error code and the CORS headers.</summary>
    private static async Task ShouldCarryCorsAsync(HttpClient client, Func<string, HttpRequestMessage> request, int status, string? code = null)
    {
        foreach (var origin in Origins)
        {
            using var response = await client.SendAsync(request(origin));
            await ShouldMatchAsync(response, origin, status, code);
        }
    }

    private static async Task ShouldMatchAsync(HttpResponseMessage response, string origin, int status, string? code = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).ShouldBe(status, $"{origin}: {body}");
        if (code is not null)
        {
            System.Text.Json.Nodes.JsonNode.Parse(body)!["code"]!.GetValue<string>().ShouldBe(code, origin);
        }

        if (origin == Allowed)
        {
            response.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(Allowed, $"status {status}");
            response.Headers.GetValues("Access-Control-Allow-Credentials").ShouldHaveSingleItem().ShouldBe("true", $"status {status}");
            response.Headers.GetValues("Access-Control-Expose-Headers").ShouldHaveSingleItem().ShouldBe(ExposedHeaders, $"status {status}");
        }
        else
        {
            response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse($"status {status} from {origin}");
            response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse($"status {status} from {origin}");
            response.Headers.Contains("Access-Control-Expose-Headers").ShouldBeFalse($"status {status} from {origin}");
        }

        // What the console reads through the exposed list (FR-062): the request id of every error, the retry delay.
        if (status >= 400)
        {
            response.Headers.GetValues(CorrelationMiddleware.HeaderName).ShouldHaveSingleItem().ShouldNotBeNullOrWhiteSpace($"status {status}");
        }

        if (status is 429 or 503)
        {
            response.Headers.Contains("Retry-After").ShouldBeTrue($"status {status}");
        }
    }

    [Fact]
    public async Task Ok_200_from_a_signed_in_browser() =>
        await ShouldCarryCorsAsync(_admin, origin => Request(HttpMethod.Get, GmModule.MeRoute, origin), 200);

    [Fact]
    public async Task Not_modified_304_on_the_catalog()
    {
        var etag = (await _host.Services.GetRequiredService<GmCatalogDocument>().GetAsync(CancellationToken.None)).ETag;
        await ShouldCarryCorsAsync(
            _admin,
            origin =>
            {
                var request = Request(HttpMethod.Get, GmModule.ConsoleCatalogRoute, origin);
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                return request;
            },
            304);
    }

    [Fact]
    public async Task Bad_request_400_from_the_endpoint() =>
        await ShouldCarryCorsAsync(_admin, origin => Request(HttpMethod.Get, GmPlayerEndpoints.SearchRoute + "?displayName=x", origin), 400, "BAD_REQUEST");

    [Fact]
    public async Task Unauthenticated_401_from_profile_enforcement() =>
        await ShouldCarryCorsAsync(_host.Browser(), origin => Request(HttpMethod.Get, GmModule.MeRoute, origin), 401, "UNAUTHENTICATED");

    [Fact]
    public async Task Forbidden_403_without_the_permission() =>
        await ShouldCarryCorsAsync(
            _host.GmClient("reader", GmPermissions.Access),
            origin => Request(HttpMethod.Get, "/gm/v1/players/p1", origin),
            403,
            "FORBIDDEN");

    [Fact]
    public async Task Csrf_failed_403_on_a_cookie_post_without_token()
    {
        // Same cookies as the signed-in admin, no X-CSRF-Token header.
        var withoutToken = _host.Browser(_jar, https: true);
        await ShouldCarryCorsAsync(withoutToken, origin => Request(HttpMethod.Post, "/gm/v1/staff/admin/sessions/revoke", origin), 403, "CSRF_FAILED");
    }

    [Fact]
    public async Task Not_found_404_from_the_endpoint_and_from_routing()
    {
        await ShouldCarryCorsAsync(_admin, origin => Request(HttpMethod.Get, "/gm/v1/players/does-not-exist", origin), 404, "NOT_FOUND");
        await ShouldCarryCorsAsync(_admin, origin => Request(HttpMethod.Get, "/gm/v1/nope", origin), 404, "NOT_FOUND");
    }

    [Fact]
    public async Task Method_not_allowed_405_on_get_logout() =>
        await ShouldCarryCorsAsync(_admin, origin => Request(HttpMethod.Get, GmModule.LogoutRoute, origin), 405, "METHOD_NOT_ALLOWED");

    [Fact]
    public async Task Unsupported_media_type_415_on_a_command() =>
        await ShouldCarryCorsAsync(
            _admin,
            origin =>
            {
                var request = Request(
                    HttpMethod.Post,
                    IdentityModule.UnbanRoute.Replace("{playerId}", "p1", StringComparison.Ordinal),
                    origin,
                    new StringContent("""{"reason":"x"}""", System.Text.Encoding.UTF8, "text/plain"));
                request.Headers.Add(CorrelationMiddleware.HeaderName, Guid.NewGuid().ToString("D"));
                return request;
            },
            415,
            "UNSUPPORTED_MEDIA_TYPE");

    [Fact]
    public async Task Internal_500_from_the_exception_middleware() =>
        await ShouldCarryCorsAsync(_admin, origin => Request(HttpMethod.Get, CorsProbeModule.ThrowRoute, origin), 500, "INTERNAL");

    [Fact]
    public async Task Rate_limited_429_from_the_gm_limiter()
    {
        await using var host = await StartAsync(values =>
        {
            values["GameServer:Limits:GmConcurrency"] = "1";
            values["GameServer:Limits:QueueFactor"] = "0";
        });
        var gate = host.Services.GetRequiredService<CorsProbeGate>();
        var gm = host.GmClient("waiter", GmPermissions.Access);

        // The first call holds the only permit until released; the next ones meet a full limiter with no queue.
        var holder = gm.SendAsync(Request(HttpMethod.Get, CorsProbeModule.WaitRoute, Allowed));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await ShouldCarryCorsAsync(gm, origin => Request(HttpMethod.Get, CorsProbeModule.WaitRoute, origin), 429, "RATE_LIMITED");

        gate.Release();
        using var released = await holder;
        await ShouldMatchAsync(released, Allowed, 204);
    }

    [Fact]
    public async Task Request_timeout_503_from_the_gm_timeout()
    {
        await using var host = await StartAsync(values => values["GameServer:Limits:GmTimeoutSeconds"] = "1");
        var gm = host.GmClient("waiter", GmPermissions.Access);

        // The gate is never released: each call ends by the 1 s GM request timeout.
        var calls = Origins.Select(async origin => (Origin: origin, Response: await gm.SendAsync(Request(HttpMethod.Get, CorsProbeModule.WaitRoute, origin))));
        foreach (var (origin, response) in await Task.WhenAll(calls))
        {
            using (response)
            {
                await ShouldMatchAsync(response, origin, 503, "REQUEST_TIMEOUT");
            }
        }
    }
}
