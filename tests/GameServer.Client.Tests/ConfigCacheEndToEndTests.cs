extern alias sdk;

using GameServer.Config;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using sdk::GameServer.Client;
using Shouldly;
using SdkCore = sdk::GameServer.Protocol.Core.V1;

namespace GameServer.Client.Tests;

/// <summary>
/// FR-061, FR-062, FR-067, SC-006 (slice 015 T044): a second client on the same cache folder downloads nothing while the client
/// config version is unchanged, also across a server-only config edit.
/// </summary>
public sealed class ConfigCacheEndToEndTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gs-cache-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_second_client_on_the_same_folder_starts_with_the_config_and_downloads_nothing()
    {
        await using var harness = await SdkHarness.StartAsync();
        var host = harness.Host.Services.GetRequiredService<IGameConfig>();
        var (a, transportA, _) = harness.NewClient(config: new PersistentConfigCache(_folder));
        await LoginAsync(a);
        (await a.RefreshConfigAsync()).ShouldBeTrue();
        FilesReceived(transportA).ShouldBe(host.ClientVisibleFiles.Count);

        var cache = new PersistentConfigCache(_folder);
        cache.Version.ShouldBe(host.ClientConfigVersion);
        cache.Files.Keys.ShouldBe(a.Config.Files.Keys, ignoreOrder: true);
        var (b, transportB, _) = harness.NewClient(config: cache);
        var announced = new List<string>();
        b.ConfigVersionChanged += announced.Add;

        await LoginAsync(b);

        b.ConfigOutdated.ShouldBeFalse();
        announced.ShouldBeEmpty();
        (await b.RefreshConfigAsync()).ShouldBeFalse();
        FilesReceived(transportB).ShouldBe(0);
    }

    [Fact]
    public async Task A_server_only_edit_downloads_nothing_and_a_client_visible_edit_downloads_once()
    {
        await using (var first = await SdkHarness.StartAsync())
        {
            var (a, _, _) = first.NewClient(config: new PersistentConfigCache(_folder));
            await LoginAsync(a);
            (await a.RefreshConfigAsync()).ShouldBeTrue();
        }

        using var serverOnly = new SeededConfig();
        serverOnly.With("iap.json", File.ReadAllText(Path.Combine(serverOnly.BasePath, "iap.json")) + "\n");
        await using (var second = await SdkHarness.StartAsync(serverOnly.Apply))
        {
            var (b, transport, _) = second.NewClient(config: new PersistentConfigCache(_folder));
            await LoginAsync(b);

            b.ConfigOutdated.ShouldBeFalse();
            (await b.RefreshConfigAsync()).ShouldBeFalse();
            FilesReceived(transport).ShouldBe(0);
        }

        using var clientVisible = new SeededConfig();
        clientVisible.With("shop.json", File.ReadAllText(Path.Combine(clientVisible.BasePath, "shop.json")) + "\n");
        await using var third = await SdkHarness.StartAsync(clientVisible.Apply);
        var (c, transportC, _) = third.NewClient(config: new PersistentConfigCache(_folder));
        await LoginAsync(c);

        c.ConfigOutdated.ShouldBeTrue();
        (await c.RefreshConfigAsync()).ShouldBeTrue();
        FilesReceived(transportC).ShouldBeGreaterThan(0);
        var stored = new PersistentConfigCache(_folder);
        stored.Version.ShouldBe(third.Host.Services.GetRequiredService<IGameConfig>().ClientConfigVersion);
        Directory.GetFileSystemEntries(_folder).Select(Path.GetFileName).ShouldBe([PersistentConfigCache.FileName]);
    }

    [Fact]
    public async Task An_unwritable_folder_still_gives_the_client_the_new_files()
    {
        await using var harness = await SdkHarness.StartAsync();
        var cache = new PersistentConfigCache(_folder);
        var (client, _, _) = harness.NewClient(config: cache);
        await LoginAsync(client);

        bool updated;
        using (ReadOnlyFolder.DenyWrites(_folder))
        {
            updated = await client.RefreshConfigAsync();
        }

        updated.ShouldBeTrue();
        client.ConfigOutdated.ShouldBeFalse();
        client.Config.Files.ShouldNotBeEmpty();
        cache.WriteProblem.ShouldNotBeNull();
        new PersistentConfigCache(_folder).Version.ShouldBeNull();
    }

    private static Task LoginAsync(GameServerClient client)
    {
        var (localId, secret) = SdkHarness.NewDevice();
        return client.LoginGuestAsync(localId, secret);
    }

    /// <summary>Config files the transport carried from the server (GetConfig responses).</summary>
    private static int FilesReceived(ScriptedTransport transport) =>
        transport.Exchanges
            .Where(e => e.Request.Url.AbsolutePath.TrimStart('/') == GameServerClient.ConfigRoute && e.Response is { Status: 200 })
            .Sum(e => SdkCore.GetConfigResponse.Parser.ParseFrom(e.Response!.Body).Files.Count);
}
