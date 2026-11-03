using System.Net;
using System.Text.Json;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Web.Requests;
using GameServer.Platform.Web.Security;
using GameServer.Protocol.Core.V1;
using Google.Protobuf;
using GameServer.Tests.Shared;
using Shouldly;
using PlayGames = GameServer.Modules.Identity.Protocol.V1.PlayGamesCredential;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 013 T042 (FR-082, US3-7, SC-007): no secret, token, cookie, CSRF token, password, body byte, header value or query
/// string reaches a request event; actor ids from test-authentication headers are cleaned and cut.
/// </summary>
public sealed class RequestLogPrivacyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task No_secret_reaches_a_request_event()
    {
        await using var session = await RequestLogHost.StartAsync(modules: GameHttp.FullGame);
        var host = session.Host;
        var scan = new List<(string Name, string Value)>();

        // Guest login: local id + secret.
        var secret = IdentityHttp.NewSecret();
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), secret);
        scan.Add(("guest secret (base64)", Convert.ToBase64String(secret)));
        scan.Add(("guest secret (hex)", Convert.ToHexString(secret)));
        scan.Add(("session token", login.SessionToken));

        // Provider login with a fake provider token.
        var providerSubject = "g" + Guid.NewGuid().ToString("N")[..20];
        var provider = await host.Client().PostProtobufAsync(
            IdentityModule.ProviderLoginRoute,
            new ProviderLoginRequest { Credential = new ProviderCredential { PlayGames = new PlayGames { ServerAuthCode = FakeIdentityAdapter.Prefix + providerSubject } } });
        provider.StatusCode.ShouldBe(HttpStatusCode.OK);
        scan.Add(("provider token", FakeIdentityAdapter.Prefix + providerSubject));
        scan.Add(("provider subject", providerSubject));
        scan.Add(("provider session token", (await provider.ReadProtobufAsync<LoginResponse>()).SessionToken));

        // Bearer requests: a command whose body carries a marker, a fake purchase with its purchase token.
        var player = new GamePlayer(host.BearerClient(login.SessionToken), login.PlayerId);
        var bodyMarker = "body-" + Guid.NewGuid().ToString("N")[..12];
        (await player.CommandAsync("/api/v1/counter/increment", new Wire.IncrementRequest { By = 1, Note = bodyMarker })).Code.ShouldBe("OK");
        scan.Add(("body bytes", bodyMarker));
        var purchaseToken = "valid:" + Guid.NewGuid().ToString("N");
        (await player.IapAsync("google", "gems_100", purchaseToken)).Code.ShouldBe("OK");
        scan.Add(("purchase token", purchaseToken));

        // Config request with a query string, header values and a forwarded client address.
        var queryMarker = "query-" + Guid.NewGuid().ToString("N")[..12];
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/config/get?q=" + queryMarker)
               {
                   Content = new ByteArrayContent(new GetConfigRequest().ToByteArray()),
               })
        {
            request.Content.Headers.ContentType = new("application/x-protobuf");
            request.Headers.Add("X-Custom-Marker", "header-value-marker");
            request.Headers.Add("X-Forwarded-For", "203.0.113.77");
            (await player.Client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        scan.Add(("query string", queryMarker));
        scan.Add(("header value", "header-value-marker"));
        scan.Add(("forwarded IP address", "203.0.113.77"));

        // Test-authentication requests (the header value is the actor id itself, which the event may carry).
        (await host.PlayerClient("p_testauth").PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).StatusCode.ShouldBe(HttpStatusCode.OK);

        // GM login: password, session cookie, antiforgery cookie, CSRF token; then a cookie-authenticated mutation.
        var jar = new CookieContainer();
        var gm = await host.GmLoginAsync(RequestLogHost.AdminId, RequestLogHost.StaffPassword, jar);
        scan.Add(("GM password", RequestLogHost.StaffPassword));
        scan.Add(("CSRF token", gm.DefaultRequestHeaders.GetValues(CsrfOptions.HeaderName).Single()));
        foreach (Cookie cookie in jar.GetAllCookies())
        {
            scan.Add(($"cookie {cookie.Name}", cookie.Value));
        }

        scan.ShouldContain(entry => entry.Name == "cookie " + GameServer.Modules.Gm.GmCookieAuthenticationHandler.CookieName);
        (await gm.GetAsync(GameServer.Modules.Gm.GmModule.MeRoute)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await gm.PostAsync(GameServer.Modules.Gm.GmModule.LogoutRoute, null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A failed GM login with another password.
        (await host.Browser().PostAsync(
            GameServer.Modules.Gm.GmModule.LoginRoute,
            System.Net.Http.Json.JsonContent.Create(new { staffId = RequestLogHost.AdminId, password = "wrong-" + RequestLogHost.StaffPassword }))).IsSuccessStatusCode.ShouldBeFalse();

        await Task.Delay(100);
        var events = session.Events.ToList();
        events.Count.ShouldBeGreaterThanOrEqualTo(10);
        var serialized = string.Join('\n', events.Select(e => e.Message + " " + JsonSerializer.Serialize(e.Properties)));

        output.WriteLine($"Scanned {events.Count} request events for {scan.Count} values:");
        foreach (var (name, value) in scan)
        {
            value.Length.ShouldBeGreaterThan(5, name);
            output.WriteLine($"  {name} ({value.Length} chars)");
            serialized.Contains(value, StringComparison.Ordinal).ShouldBeFalse($"{name} found in a request event");
        }
    }

    [Fact]
    public async Task A_test_authentication_actor_id_is_cleaned_and_cut_at_64()
    {
        await using var session = await RequestLogHost.StartAsync();
        var client = session.Host.Client();
        var actorId = "p\u0001\u001b[31m" + new string('x', 1000);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Test-Player", actorId).ShouldBeTrue();

        var response = await client.PostProtobufAsync("/api/v1/config/get", new GetConfigRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var logged = (string)(await session.EventAsync(response)).Properties["ActorId"]!;
        logged.Length.ShouldBe(RequestLogMiddleware.MaxActorIdLength);
        logged.ShouldStartWith("p??[31m");
        logged.ShouldAllBe(c => !char.IsControl(c));
    }
}
