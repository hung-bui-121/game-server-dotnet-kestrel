extern alias sdk;

using GameServer.Config;
using GameServer.Tests.Shared;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using sdk::GameServer.Client;
using Shouldly;
using SdkCore = sdk::GameServer.Protocol.Core.V1;
using SdkIdentity = sdk::GameServer.Modules.Identity.Protocol.V1;

namespace GameServer.Client.Tests;

/// <summary>FR-046, SC-004, BC-4 (slice 015 T039): the SDK keys its config cache on the client config version.</summary>
public sealed class ClientConfigVersionTests
{
    [Fact]
    public async Task The_client_tracks_both_versions_and_stores_the_files_under_the_client_version()
    {
        await using var harness = await SdkHarness.StartAsync();
        var host = harness.Host.Services.GetRequiredService<IGameConfig>();
        var (client, transport, _) = harness.NewClient();
        var announced = new List<string>();
        client.ConfigVersionChanged += announced.Add;

        await LoginAsync(client);

        client.ServerConfigVersion.ShouldBe(host.ConfigVersion);
        client.ClientConfigVersion.ShouldBe(host.ClientConfigVersion);
        client.ClientConfigVersion.ShouldNotBe(client.ServerConfigVersion);
        announced.ShouldBe([host.ClientConfigVersion]);
        client.ConfigOutdated.ShouldBeTrue();

        (await client.RefreshConfigAsync()).ShouldBeTrue();
        client.Config.Version.ShouldBe(host.ClientConfigVersion);
        client.ConfigOutdated.ShouldBeFalse();

        (await client.RefreshConfigAsync()).ShouldBeFalse();
        var second = SdkCore.GetConfigRequest.Parser.ParseFrom(transport.RequestsTo(GameServerClient.ConfigRoute).Last().Body!);
        second.ConfigVersion.ShouldBe(host.ClientConfigVersion);
        await client.RefreshSectionsAsync();
        announced.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Against_a_server_without_the_client_version_the_sdk_falls_back_to_the_server_version()
    {
        await using var harness = await SdkHarness.StartAsync();
        var host = harness.Host.Services.GetRequiredService<IGameConfig>();
        var (client, transport, _) = harness.NewClient();
        transport.After = (request, _, response) => WithoutClientVersion(request, response);
        var announced = new List<string>();
        client.ConfigVersionChanged += announced.Add;

        await LoginAsync(client);

        client.ClientConfigVersion.ShouldBe(host.ConfigVersion);
        announced.ShouldBe([host.ConfigVersion]);
        (await client.RefreshConfigAsync()).ShouldBeTrue();
        client.Config.Version.ShouldBe(host.ConfigVersion);
        client.ConfigOutdated.ShouldBeFalse();

        // The 015 server also answers not_modified for its server version (SDKs before 0.7.0 hold it).
        (await client.RefreshConfigAsync()).ShouldBeFalse();
        await client.RefreshSectionsAsync();
        announced.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_cache_from_one_host_needs_nothing_from_a_host_that_changed_a_server_only_file()
    {
        var cache = new InMemoryConfigCache();
        await using (var first = await SdkHarness.StartAsync())
        {
            var (a, _, _) = first.NewClient(config: cache);
            await LoginAsync(a);
            (await a.RefreshConfigAsync()).ShouldBeTrue();
        }

        using var seeded = new SeededConfig();
        seeded.With("iap.json", File.ReadAllText(Path.Combine(seeded.BasePath, "iap.json")) + "\n");
        await using var second = await SdkHarness.StartAsync(seeded.Apply);
        var (b, _, _) = second.NewClient(config: cache);
        var announced = new List<string>();
        b.ConfigVersionChanged += announced.Add;

        await LoginAsync(b);

        b.ServerConfigVersion.ShouldBe(second.Host.Services.GetRequiredService<IGameConfig>().ConfigVersion);
        b.ClientConfigVersion.ShouldBe(cache.Version);
        b.ConfigOutdated.ShouldBeFalse();
        announced.ShouldBeEmpty();
        (await b.RefreshConfigAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cache_held_under_the_server_version_is_keyed_by_the_client_version_without_a_download()
    {
        await using var harness = await SdkHarness.StartAsync();
        var host = harness.Host.Services.GetRequiredService<IGameConfig>();
        var files = host.ClientVisibleFiles.ToDictionary(name => name, name => host.GetFileBytes(name).ToArray(), StringComparer.Ordinal);
        var cache = new InMemoryConfigCache();
        cache.Store(host.ConfigVersion, files);
        var (client, _, _) = harness.NewClient(config: cache);
        await LoginAsync(client);

        (await client.RefreshConfigAsync()).ShouldBeFalse();

        cache.Version.ShouldBe(host.ClientConfigVersion);
        cache.Files.ShouldBeSameAs(files);
        client.ConfigOutdated.ShouldBeFalse();
    }

    private static Task LoginAsync(GameServerClient client)
    {
        var (localId, secret) = SdkHarness.NewDevice();
        return client.LoginGuestAsync(localId, secret);
    }

    /// <summary>What a server before 0.7.0 sends: the same responses without <c>client_config_version</c>.</summary>
    private static TransportResponse WithoutClientVersion(TransportRequest request, TransportResponse response)
    {
        if (response.Status != 200)
        {
            return response;
        }

        var route = request.Url.AbsolutePath.TrimStart('/');
        IMessage? message = route switch
        {
            GameServerClient.LoginRoute => Strip(SdkIdentity.LoginResponse.Parser.ParseFrom(response.Body), m => m.ClientConfigVersion = string.Empty),
            GameServerClient.ConfigRoute => Strip(SdkCore.GetConfigResponse.Parser.ParseFrom(response.Body), m => m.ClientConfigVersion = string.Empty),
            GameServerClient.SnapshotRoute => Strip(SdkCore.SnapshotResponse.Parser.ParseFrom(response.Body), m => m.ClientConfigVersion = string.Empty),
            _ => null,
        };
        return message is null ? response : new TransportResponse(response.Status, response.ContentType, message.ToByteArray());
    }

    private static T Strip<T>(T message, Action<T> clear)
        where T : IMessage
    {
        clear(message);
        return message;
    }
}
