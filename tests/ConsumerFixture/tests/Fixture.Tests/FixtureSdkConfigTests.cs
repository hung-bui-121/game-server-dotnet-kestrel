extern alias sdk;

using Fixture.Host;
using GameServer.Config;
using GameServer.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using sdk::GameServer.Client;

namespace Fixture.Tests;

/// <summary>
/// A game test that calls the SDK (slice 015 T071 G4, the T066 path): the SDK is referenced with <c>Aliases="sdk"</c> because it
/// compiles the same generated protocol types as the server, and talks to the game host through <see cref="HttpClientTransport"/>
/// over <c>factory.CreateClient()</c>. Consumer guide §12.4 takes its example from here.
/// </summary>
public sealed class FixtureSdkConfigTests
{
    [Fact]
    public async Task A_guest_login_announces_the_client_version_and_refresh_downloads_the_client_visible_files()
    {
        await using var factory = new LocalFlavorFactory<Program>();
        var http = factory.CreateClient();
        var client = new GameServerClient(new GameServerClientOptions(http.BaseAddress!, "1.2.3", "editor", "fixture-test"), new HttpClientTransport(http));
        var config = factory.Services.GetRequiredService<IGameConfig>();

        await client.LoginGuestAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        client.ClientConfigVersion.ShouldBe(config.ClientConfigVersion);
        client.ConfigOutdated.ShouldBeTrue();

        (await client.RefreshConfigAsync()).ShouldBeTrue();

        client.Config.Version.ShouldBe(config.ClientConfigVersion);
        client.ConfigOutdated.ShouldBeFalse();
        client.Config.Files.Keys.ShouldBe(config.ClientVisibleFiles, ignoreOrder: true);
        foreach (var name in config.ClientVisibleFiles)
        {
            client.Config.Files[name].ShouldBe(config.GetFileBytes(name).ToArray());
        }

        (await client.RefreshConfigAsync()).ShouldBeFalse();
    }
}
