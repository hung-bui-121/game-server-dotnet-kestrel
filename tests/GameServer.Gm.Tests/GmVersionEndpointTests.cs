using System.Text;
using GameServer.Modules.Gm;
using GameServer.Platform;
using GameServer.Platform.Observability;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 013 T064 (FR-055, US6-5, contracts/gm-ops-version.md): <c>GET /gm/v1/ops/version</c> answers the identity and versions of
/// the instance to staff with <c>ops.read</c>; health probes, public and player routes disclose no version (OBS-03).
/// </summary>
public sealed class GmVersionEndpointTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await GmTestHost.StartAsync(
            settings: s =>
            {
                s["Observability:InstanceId"] = "host-1";
                s["Observability:Logging:FileEnabled"] = "false";
            },
            extra: b => b.AddObservability());

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Ops_read_returns_the_runtime_identity_and_versions_of_the_instance()
    {
        var runtime = _host.Services.GetRequiredService<GameServerRuntimeInfo>();
        var admin = await _host.GmClientAsync("admin");

        var body = await admin.GetAsync(GmModule.VersionRoute).ShouldBeAsync(200);

        body.AsObject().Select(p => p.Key).ShouldBe(["gameId", "environmentId", "instanceId", "gameVersion", "frameworkVersion"]);
        body["gameId"]!.GetValue<string>().ShouldBe(runtime.GameId);
        body["environmentId"]!.GetValue<string>().ShouldBe(runtime.EnvironmentId);
        body["instanceId"]!.GetValue<string>().ShouldBe("host-1");
        body["gameVersion"]!.GetValue<string>().ShouldBe(runtime.GameVersion);
        body["frameworkVersion"]!.GetValue<string>().ShouldBe(GameServerVersion.FrameworkInformational);
    }

    /// <summary>Slice 017 T032 (FR-036, US3-8, D9): /me and the local login answer carry the versions of ops/version.</summary>
    [Fact]
    public async Task Me_and_the_local_login_answer_carry_the_versions_of_the_instance()
    {
        var runtime = _host.Services.GetRequiredService<GameServerRuntimeInfo>();
        var browser = _host.Browser();

        var login = await (await browser.PostLoginAsync("support")).JsonAsync();
        await browser.RefreshCsrfAsync();
        var me = await browser.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);

        foreach (var answer in new[] { login, me })
        {
            answer.AsObject().Select(p => p.Key).ShouldBe(
                ["staffId", "displayName", "gameId", "environmentId", "permissions", "frameworkVersion", "gameVersion"]);
            answer["frameworkVersion"]!.GetValue<string>().ShouldBe(GameServerVersion.FrameworkInformational);
            answer["gameVersion"]!.GetValue<string>().ShouldBe(runtime.GameVersion);
        }
    }

    [Fact]
    public async Task Without_a_session_it_is_401_and_without_ops_read_403()
    {
        (await _host.Browser().GetAsync(GmModule.VersionRoute).ShouldBeAsync(401))["code"]!.GetValue<string>().ShouldBe("UNAUTHENTICATED");

        var support = await _host.GmClientAsync("support");
        (await support.GetAsync(GmModule.VersionRoute).ShouldBeAsync(403))["code"]!.GetValue<string>().ShouldBe("FORBIDDEN");
    }

    [Fact]
    public async Task Health_public_and_player_routes_carry_no_framework_or_game_version()
    {
        var runtime = _host.Services.GetRequiredService<GameServerRuntimeInfo>();
        string[] secrets = [runtime.FrameworkVersion, runtime.GameVersion, GameServerVersion.Framework];
        var client = _host.Client();
        var responses = new List<(string Route, HttpResponseMessage Response)>
        {
            ("/health/live", await client.GetAsync("/health/live")),
            ("/health/ready", await client.GetAsync("/health/ready")),
            ("/v1/time", await client.GetAsync("/v1/time")),
        };
        var login = await client.PostLoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        responses.Add(("guest login", login));
        responses.Add((GmModule.AuthMethodsRoute, await client.GetAsync(GmModule.AuthMethodsRoute)));
        var player = await _host.NewPlayerAsync();
        responses.Add(("player command", await player.PostAsync("/api/v1/counter/increment", new Sample.Counter.Protocol.V1.IncrementRequest { By = 1 })));

        foreach (var (route, response) in responses)
        {
            ((int)response.StatusCode).ShouldBe(200, route);
            var body = Encoding.Latin1.GetString(await response.Content.ReadAsByteArrayAsync());
            var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers).SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));
            foreach (var secret in secrets)
            {
                body.ShouldNotContain(secret, Case.Sensitive, $"{route} body discloses '{secret}'");
                headers.ShouldNotContain(secret, Case.Sensitive, $"{route} headers disclose '{secret}'");
            }
        }
    }
}
