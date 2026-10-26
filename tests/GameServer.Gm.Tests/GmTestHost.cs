using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GameServer.Modules.Gm;
using GameServer.Platform.Web.Auth;
using GameServer.Tests.Shared;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>Full SampleGame host with seeded GM staff (password <see cref="Password"/>).</summary>
public static class GmTestHost
{
    public const string Password = "dev-password";

    /// <summary>Staff seeded by <see cref="StartAsync"/>: id → (roles, extra permissions, scope override, disabled).</summary>
    public static Task<TestHost> StartAsync(Action<Dictionary<string, string?>>? settings = null) =>
        TestHost.StartAsync(
            GameHttp.FullGame,
            values =>
            {
                Seed(values, 0, "support", ["Support"]);
                Seed(values, 1, "operator", ["Operator"]);
                Seed(values, 2, "econ", ["EconomyOperator"]);
                Seed(values, 3, "admin", ["Administrator"]);
                Seed(values, 4, "pii", ["Support"], permissions: [GmPermissions.PlayerReadPii]);
                Seed(values, 5, "other-game", ["Administrator"], gameId: "other");
                Seed(values, 6, "other-env", ["Administrator"], environmentId: "prod");
                Seed(values, 7, "disabled", ["Administrator"], disabled: true);
                settings?.Invoke(values);
            });

    private static void Seed(
        Dictionary<string, string?> values,
        int index,
        string staffId,
        string[] roles,
        string[]? permissions = null,
        string? gameId = null,
        string? environmentId = null,
        bool disabled = false)
    {
        var prefix = $"Gm:Bootstrap:Staff:{index}:";
        values[prefix + "StaffId"] = staffId;
        values[prefix + "DisplayName"] = "Staff " + staffId;
        values[prefix + "Password"] = Password;
        values[prefix + "Disabled"] = disabled ? "true" : "false";
        values[prefix + "GameId"] = gameId;
        values[prefix + "EnvironmentId"] = environmentId;
        for (var i = 0; i < roles.Length; i++)
        {
            values[$"{prefix}Roles:{i}"] = roles[i];
        }

        for (var i = 0; i < (permissions?.Length ?? 0); i++)
        {
            values[$"{prefix}Permissions:{i}"] = permissions![i];
        }
    }

    public static Task<HttpResponseMessage> PostLoginAsync(this TestHost host, string staffId, string password = Password) =>
        host.Client().PostAsJsonAsync(GmModule.LoginRoute, new { staffId, password });

    /// <summary>Logs in and returns a client that sends the <c>gs_gm</c> cookie (TestServer has no cookie jar).</summary>
    public static async Task<HttpClient> GmClientAsync(this TestHost host, string staffId)
    {
        var response = await host.PostLoginAsync(staffId);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var cookie = SessionCookie(response) ?? throw new InvalidOperationException("No gs_gm cookie.");
        var client = host.Client();
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        return client;
    }

    public static string? SessionCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(GmCookieAuthenticationHandler.CookieName + "=", StringComparison.Ordinal))
            : null;

    public static async Task<JsonNode> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty JSON body.");
    }

    /// <summary>Asserts the status and returns the JSON body (Problem Details for errors).</summary>
    public static async Task<JsonNode> ShouldBeAsync(this Task<HttpResponseMessage> call, int status)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).ShouldBe(status, text);
        var json = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true;
        return json && text.Length > 0 ? JsonNode.Parse(text)! : new JsonObject();
    }

    public static Task<HttpResponseMessage> PostCommandAsync(this HttpClient client, string route, object body, string? requestId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Request-Id", requestId ?? Guid.NewGuid().ToString("D"));
        return client.SendAsync(request);
    }

    /// <summary>Player client authenticated by the test scheme (X-Test-Player).</summary>
    public static HttpClient Player(this TestHost host, string playerId) => host.PlayerClient(playerId);

    public static string TestPlayerHeader => TestAuthenticationHandler.PlayerHeader;
}
