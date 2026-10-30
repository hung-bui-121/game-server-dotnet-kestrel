using System.Text.Json.Nodes;
using GameServer.Aggregates;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Testing;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sample.Counter;
using Sample.Counter.Protocol.V1;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 011 T021: the file store in a host. Refused outside Development/Test before the folder is touched (US2-8, FR-007,
/// FR-027); commands are on disk before the response (FR-019, US2-1); a restarted host on the same folder sees the same data
/// and accepts the old bearer token (FR-028).
/// </summary>
public sealed class FileStoreHostTests : IDisposable
{
    private readonly string _folder = TestStores.NewFolder();
    private readonly FakeClock _clock = new();

    public void Dispose() => TestStores.DeleteFolder(_folder);

    private FileDocumentStore NewStore() => TestStores.CreateFileStore(_folder, "test", _clock, NullLogger<FileDocumentStore>.Instance);

    private Task<TestHost> StartAsync(FileDocumentStore fileStore, Action<Dictionary<string, string?>>? settings = null) =>
        TestHost.StartAsync(
            settings: s =>
            {
                s["GameServer:Store"] = "File";
                settings?.Invoke(s);
            },
            clock: _clock,
            services: s => s.AddFileDocumentStore(fileStore),
            storeKind: TestStoreKind.InMemory);

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Outside_development_the_file_store_is_refused_before_the_folder_is_touched(string environment)
    {
        using var fileStore = NewStore();

        var ex = await Should.ThrowAsync<GameServerStartupException>(() => StartAsync(fileStore, s => s["GameServer:DeploymentEnvironment"] = environment));

        ex.Message.ShouldContain(
            $"[deployment] the File store is configured in '{environment}'. Fix: set GameServer:Store=Couchbase and the Couchbase settings; " +
            "the InMemory and File stores are for Development/Test only (FR-007).");
        Directory.Exists(_folder).ShouldBeFalse();
    }

    [Fact]
    public async Task A_command_is_on_disk_before_its_response_and_survives_a_restart_with_the_same_token()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        LoginResponse login;
        using (var fileStore = NewStore())
        {
            await using var host = await StartAsync(fileStore);
            login = await host.Client().LoginAsync(localId, secret);
            var session = Path.Combine(_folder, "g_test", "sessions");
            Directory.EnumerateFiles(session).ShouldNotBeEmpty();

            var response = await host.BearerClient(login.SessionToken)
                .PostProtobufAsync("/api/v1/counter/increment", new IncrementRequest { By = 3 }, ProtobufHttp.NewRequestId());
            response.EnsureSuccessStatusCode();

            CounterFileKeys().ShouldBe([login.PlayerId]);
            Directory.EnumerateFiles(Path.Combine(_folder, "g_test", "receipts")).ShouldNotBeEmpty();
        }

        using var restarted = NewStore();
        await using var second = await StartAsync(restarted);

        var counter = restarted.Store.Peek(new(PlayerAggregateDescriptor.CollectionFor(CounterAggregates.Counter), login.PlayerId)).ShouldNotBeNull();
        counter["state"]!["value"]!.GetValue<long>().ShouldBe(3);
        var snapshot = await second.BearerClient(login.SessionToken).PostProtobufAsync("/api/v1/player/snapshot", new GameServer.Protocol.Core.V1.SnapshotRequest());
        snapshot.EnsureSuccessStatusCode();
        (await second.Client().LoginAsync(localId, secret)).PlayerId.ShouldBe(login.PlayerId);
    }

    [Fact]
    public async Task The_test_host_file_switch_runs_on_a_temporary_folder_deleted_with_the_host()
    {
        string folder;
        await using (var host = await TestHost.StartAsync(storeKind: TestStoreKind.File))
        {
            var fileStore = host.Services.GetRequiredService<FileDocumentStore>();
            host.Store.ShouldBeSameAs(fileStore.Store);
            folder = fileStore.Folder;
            File.Exists(Path.Combine(folder, "store.json")).ShouldBeTrue();
        }

        Directory.Exists(folder).ShouldBeFalse();
    }

    /// <summary>The <c>key</c> of every counter document file (one file per document).</summary>
    private string[] CounterFileKeys() =>
        Directory.EnumerateFiles(Path.Combine(_folder, "g_test", PlayerAggregateDescriptor.CollectionFor(CounterAggregates.Counter)), "*.json")
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!["key"]!.GetValue<string>())
            .ToArray();
}
