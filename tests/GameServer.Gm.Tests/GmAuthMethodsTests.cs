using GameServer.Modules.Gm;
using GameServer.Tests.Shared;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 017 T031 (FR-034, FR-035, SC-007, US3-6, US3-7): <c>GET /gm/v1/auth/methods</c> answers anonymously which sign-in buttons a
/// login page shows, two booleans and nothing else, and needs no allowlist setting. The Beta case is in
/// <c>SampleHostFlavorTests</c> (Couchbase project).
/// </summary>
public sealed class GmAuthMethodsTests
{
    [Fact]
    public async Task Without_oidc_only_the_local_login_is_offered()
    {
        await using var host = await GmTestHost.StartAsync();

        var body = await host.Client().GetAsync(GmModule.AuthMethodsRoute).ShouldBeAsync(200);

        body.ToJsonString().ShouldBe("""{"local":true,"sso":false}""");
    }

    [Fact]
    public async Task With_oidc_both_methods_are_offered()
    {
        await using var host = await GmTestHost.StartAsync(s =>
        {
            s["Gm:Oidc:Authority"] = "https://idp.example";
            s["Gm:Oidc:ClientId"] = "gm-console";
            s["Gm:Oidc:ClientSecret"] = "not-used";
        });

        var response = await host.Client().GetAsync(GmModule.AuthMethodsRoute);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var body = await response.JsonAsync();

        body.AsObject().Select(m => m.Key).ShouldBe(["local", "sso"]);
        body.ToJsonString().ShouldBe("""{"local":true,"sso":true}""");
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task An_explicit_allowlist_that_does_not_list_it_still_starts_and_serves_it()
    {
        await using var host = await GmTestHost.StartAsync(s => s["GameServer:PublicEndpointAllowlist:0"] = "/v1/time");

        (await host.Client().GetAsync(GmModule.AuthMethodsRoute).ShouldBeAsync(200)).ToJsonString().ShouldBe("""{"local":true,"sso":false}""");
    }
}
