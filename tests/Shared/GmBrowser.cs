using System.Net;
using GameServer.Modules.Gm;
using GameServer.Platform.Web.Security;
using Microsoft.AspNetCore.TestHost;

namespace GameServer.Tests.Shared;

/// <summary>Browser-like GM client helpers: cookie jar and CSRF token (SEC-05) for TestServer hosts.</summary>
public static class GmBrowser
{
    /// <summary>
    /// A client that keeps cookies (TestServer has no cookie jar) so the antiforgery cookie and <c>gs_gm</c> travel
    /// like in a browser. Pass <paramref name="cookies"/> to share one jar between instances (ACC-49).
    /// </summary>
    public static HttpClient Browser(this TestHost host, CookieContainer? cookies = null, bool https = false) =>
        host.App.GetTestServer().Browser(cookies, https);

    /// <summary>The same browser on any TestServer (e.g. <c>WebApplicationFactory.Server</c> of the sample host).</summary>
    public static HttpClient Browser(this TestServer server, CookieContainer? cookies = null, bool https = false) =>
        new(new CookieJarHandler(cookies ?? new CookieContainer()) { InnerHandler = server.CreateHandler() })
        {
            BaseAddress = new Uri(https ? "https://localhost/" : "http://localhost/"),
        };

    /// <summary>Fetches a CSRF token for the client's current identity and sends it on every later request.</summary>
    public static async Task<string> RefreshCsrfAsync(this HttpClient client)
    {
        var response = await client.GetAsync(GmModule.CsrfRoute);
        response.EnsureSuccessStatusCode();
        var token = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
        client.DefaultRequestHeaders.Remove(CsrfOptions.HeaderName);
        client.DefaultRequestHeaders.Add(CsrfOptions.HeaderName, token);
        return token;
    }

    /// <summary>Local GM login (Development/Test) as the GM page does it; returns a client with session + CSRF token.</summary>
    public static Task<HttpClient> GmLoginAsync(this TestHost host, string staffId, string password, CookieContainer? cookies = null) =>
        host.App.GetTestServer().GmLoginAsync(staffId, password, cookies);

    /// <summary>Local GM login on any TestServer.</summary>
    public static async Task<HttpClient> GmLoginAsync(this TestServer server, string staffId, string password, CookieContainer? cookies = null)
    {
        var client = server.Browser(cookies);
        await client.RefreshCsrfAsync();
        var login = await client.PostAsync(
            GmModule.LoginRoute,
            System.Net.Http.Json.JsonContent.Create(new { staffId, password }));
        if (!login.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GM login failed: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");
        }

        await client.RefreshCsrfAsync();
        return client;
    }
}

/// <summary>Stores and sends cookies for TestServer requests.</summary>
public sealed class CookieJarHandler(CookieContainer cookies) : DelegatingHandler
{
    public CookieContainer Cookies => cookies;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var header = cookies.GetCookieHeader(uri);
        if (header.Length > 0)
        {
            request.Headers.Remove("Cookie");
            request.Headers.Add("Cookie", header);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var value in values)
            {
                // Expiry is computed from the host's FakeClock, the jar compares with real time: keep the cookie unless
                // it is a deletion (expires in 1970). The server's own expiry checks are what the tests exercise.
                var deletion = value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
                var kept = string.Join(';', value.Split(';').Where(part => !part.Trim().StartsWith("expires=", StringComparison.OrdinalIgnoreCase)));
                cookies.SetCookies(uri, deletion ? kept + "; max-age=0" : kept);
            }
        }

        return response;
    }
}
