using System.Net;
using System.Text.Json.Nodes;
using GameServer.Modules.Economy;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using GameServer.Platform.Tests.Infrastructure;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using ProfileState = GameServer.Modules.Identity.ProfileState;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>
/// Slice 016 T029 (FR-012, FR-092, SC-002, VERIFY V2, V3, V4) on a real Couchbase with transactions: racing first logins of one
/// credential seed the account exactly once, a failing initializer leaves nothing, and a creation racing the removal of its
/// anchor commits everything or nothing.
/// </summary>
public sealed class CouchbaseAccountCreationTests(CouchbaseGameFixture fixture) : IClassFixture<CouchbaseGameFixture>
{
    private const long StartingGold = 100;
    private const int RaceRounds = 4;

    /// <summary>Per round of the anchor race: positive = the removal waits, negative = the login waits (both orders occur).</summary>
    private static readonly int[] AnchorStaggerMilliseconds = [0, 20, -20, -40];

    private static readonly FakeClock Clock = new();

    [Fact]
    public async Task Twenty_racing_first_logins_and_five_lost_response_retries_seed_one_account_once()
    {
        var sink = new CapturedLogSink();
        await using var host = await StartHostAsync(StartingStateBehaviour.Grant(StartingGold), sink);
        using var metrics = new MetricCollector(host.Services.GetRequiredService<PlatformMetrics>());
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        var racing = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => host.Client().PostLoginAsync(localId, secret))));
        var answers = new List<LoginResponse>();
        foreach (var response in racing)
        {
            // CONCURRENT_UPDATE here would mean the 3 race rounds of the writer ran out (V3).
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            answers.Add(await response.ReadProtobufAsync<LoginResponse>());
        }

        for (var i = 0; i < 5; i++)
        {
            answers.Add(await host.Client().LoginAsync(localId, secret));
        }

        answers.Count(a => a.IsNewPlayer).ShouldBe(1);
        var playerId = answers.Select(a => a.PlayerId).Distinct().ShouldHaveSingleItem();
        var created = sink.Lines("GameServer.Accounts").Where(l => l.StartsWith($"Account {playerId} created:", StringComparison.Ordinal)).ShouldHaveSingleItem();
        var receiptId = created[(created.LastIndexOf(' ') + 1)..^1];
        var store = host.Services.GetRequiredService<ITransactionalDocumentStore>();
        (await store.GetAsync(ReceiptDocument.KeyFor(receiptId), CancellationToken.None)).ShouldNotBeNull();
        (await store.GetAsync(LedgerKey(receiptId, 0), CancellationToken.None)).ShouldNotBeNull();
        (await store.GetAsync(LedgerKey(receiptId, 1), CancellationToken.None)).ShouldBeNull();
        (await BalanceAsync(store, playerId)).ShouldBe(StartingGold);
        metrics.SumTagged("account_creations_total", ("result", "created")).ShouldBe(1);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"creation attempts {metrics.SumTagged("txn_attempts_total", ("operation", AccountCreation.Operation))}, " +
            $"retries {metrics.SumTagged("txn_retries_total", ("operation", AccountCreation.Operation))}");
    }

    [Theory]
    [InlineData(StartingStateMode.Reject)]
    [InlineData(StartingStateMode.FloodOutbox)]
    public async Task A_rejecting_initializer_or_a_receipt_over_the_limit_writes_nothing_and_answers_internal(StartingStateMode mode)
    {
        var behaviour = mode == StartingStateMode.Reject ? StartingStateBehaviour.Reject(StartingStateErrors.Rejected) : StartingStateBehaviour.FloodOutbox(40);
        await using var host = await StartHostAsync(behaviour);
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        var refused = await host.Client().PostLoginAsync(localId, secret);

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await refused.ReadErrorAsync()).Code.ShouldBe("INTERNAL");
        var store = host.Services.GetRequiredService<ITransactionalDocumentStore>();
        var mapping = (await store.GetAsync(new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(localId)), CancellationToken.None)).ShouldNotBeNull();
        var playerId = mapping.Content["playerId"]!.GetValue<string>();
        foreach (var collection in new[] { IdentityAggregates.ProfileDescriptor.Collection, "pa_" + EconomyAggregates.Wallet, "pa_" + StartingStateAggregates.Starter })
        {
            (await store.GetAsync(new DocumentKey(collection, playerId), CancellationToken.None)).ShouldBeNull(collection);
        }

        behaviour.Become(StartingStateBehaviour.Grant(StartingGold));
        var login = await host.Client().LoginAsync(localId, secret);
        login.IsNewPlayer.ShouldBeTrue();
        (await BalanceAsync(store, login.PlayerId)).ShouldBe(StartingGold);
    }

    [Fact]
    public async Task A_creation_racing_the_removal_of_its_anchor_commits_everything_or_nothing()
    {
        await using var host = await StartHostAsync(StartingStateBehaviour.Grant(StartingGold));
        var writer = host.Services.GetRequiredService<IAccountWriter>();
        var store = host.Services.GetRequiredService<ITransactionalDocumentStore>();
        var kv = host.Services.GetRequiredService<IKeyValueStore>();
        var outcomes = new List<string>();
        for (var round = 0; round < RaceRounds; round++)
        {
            var playerId = "p" + Guid.NewGuid().ToString("N")[..24];
            var anchor = new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(IdentityHttp.NewLocalId()));
            (await kv.InsertAsync(anchor, new JsonObject { ["mappingKey"] = anchor.Key, ["playerId"] = playerId }, null, CancellationToken.None)).ShouldBeTrue();
            var stagger = AnchorStaggerMilliseconds[round];
            var login = Task.Run(async () =>
            {
                await Task.Delay(Math.Max(0, -stagger));
                return await writer.WriteLoginAsync(
                    new AccountLoginWrite<ProfileState>(playerId, p => p with { SessionEpoch = p.SessionEpoch + 1 }, anchor, "corr"),
                    CancellationToken.None);
            });
            var removal = Task.Run(async () =>
            {
                await Task.Delay(Math.Max(0, stagger));
                return await store.RunTransactionAsync(
                    async tx =>
                    {
                        if ((await tx.GetManyAsync([anchor]))[0] is not { } mapping)
                        {
                            return false;
                        }

                        await tx.RemoveAsync(mapping);
                        return true;
                    },
                    new TransactionRunOptions(TimeSpan.FromSeconds(10)));
            });

            bool created;
            try
            {
                created = (await login).Created;
            }
            catch (AggregateAnchorMissingException)
            {
                created = false;
            }

            (await removal).ShouldBeTrue();
            foreach (var collection in new[] { IdentityAggregates.ProfileDescriptor.Collection, "pa_" + EconomyAggregates.Wallet, "pa_" + StartingStateAggregates.Starter })
            {
                (await store.GetAsync(new DocumentKey(collection, playerId), CancellationToken.None) is not null).ShouldBe(created, collection);
            }

            outcomes.Add(created ? "login" : "removal");
        }

        TestContext.Current.TestOutputHelper?.WriteLine("Winners per round: " + string.Join(", ", outcomes));
    }

    [Fact]
    public async Task A_mapping_removed_before_the_creation_answers_account_deleted_and_seeds_nothing()
    {
        var behaviour = StartingStateBehaviour.Grant(StartingGold);
        await using var host = await StartHostAsync(behaviour);
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var anchor = new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(localId));
        var kv = host.Services.GetRequiredService<IKeyValueStore>();
        var completion = host.Services.GetRequiredService<LoginCompletion>();
        completion.AfterMappingRead = async _ =>
        {
            completion.AfterMappingRead = null;
            await kv.RemoveAsync(anchor, CancellationToken.None);
        };

        var refused = await host.Client().PostLoginAsync(localId, secret);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await refused.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_DELETED");
        behaviour.Invocations.ShouldBe(0);
    }

    private Task<CouchbaseGameHost> StartHostAsync(StartingStateBehaviour behaviour, CapturedLogSink? sink = null)
    {
        fixture.RequireCouchbase();
        return CouchbaseGameHost.StartAsync(
            fixture,
            Clock,
            s => s["Identity:LoginPerLocalIdPerMinute"] = "100",
            b => b.AddModule(new StartingStateModule()),
            s =>
            {
                s.AddSingleton(behaviour);
                if (sink is not null)
                {
                    s.AddSingleton<ILoggerProvider>(sink);
                }
            });
    }

    private static DocumentKey LedgerKey(string receiptId, int index) =>
        new(EconomyOperations.LedgerCollection, $"{EconomyOperations.LedgerKind}::{receiptId}:{index}");

    private static async Task<long> BalanceAsync(ITransactionalDocumentStore store, string playerId)
    {
        var wallet = (await store.GetAsync(new DocumentKey("pa_" + EconomyAggregates.Wallet, playerId), CancellationToken.None)).ShouldNotBeNull();
        return wallet.Content["state"]!["balances"]![StartingStateBehaviour.Resource]!.GetValue<long>();
    }
}
