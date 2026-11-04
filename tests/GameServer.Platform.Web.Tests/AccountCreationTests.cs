using System.Net;
using GameServer.Modules.Economy;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T027 (FR-011, FR-012, FR-014, FR-016, SC-002, US2-1..US2-4): a first login seeds the account in the transaction that
/// creates it — profile, initializer aggregates, ledger entry and one receipt, or nothing at all — on the in-memory store and,
/// with <c>GAMESERVER_TEST_STORE=File</c>, on the File store.
/// </summary>
public sealed class AccountCreationTests
{
    private const long StartingGold = 100;

    private static readonly string[] SeededCollections =
    [
        IdentityAggregates.ProfileDescriptor.Collection,
        "pa_" + EconomyAggregates.Wallet,
        "pa_" + StartingStateAggregates.Starter,
        ReceiptDocument.Collection,
        EconomyOperations.LedgerCollection,
        StartingStateModule.ClaimCollection,
    ];

    [Fact]
    public async Task A_granting_initializer_commits_profile_aggregates_ledger_and_one_receipt_with_the_first_login()
    {
        await using var host = await StartAsync(StartingStateBehaviour.Grant(StartingGold));
        var before = Counts(host);

        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        login.IsNewPlayer.ShouldBeTrue();
        Delta(before, Counts(host)).ShouldBe([1, 1, 1, 1, 1, 0]);
        Balance(host, login.PlayerId).ShouldBe(StartingGold);
        login.Sections.Single(s => s.Id == EconomyAggregates.Wallet).Rev.ShouldBe(1);
        login.Sections.Single(s => s.Id == StartingStateAggregates.Starter).Rev.ShouldBe(1);

        var receiptId = CreatedReceiptId(host, login.PlayerId);
        var receipt = ReceiptDocument.FromJson(host.Store.Peek(ReceiptDocument.KeyFor(receiptId)).ShouldNotBeNull());
        receipt.Operation.ShouldBe(AccountCreation.Operation);
        receipt.ActorType.ShouldBe("system");
        receipt.ActorId.ShouldBe(login.PlayerId);
        var ledger = host.Store.All(EconomyOperations.LedgerCollection).Single().Content;
        ledger["receiptId"]!.GetValue<string>().ShouldBe(receiptId);
        ledger["operation"]!.GetValue<string>().ShouldBe(AccountCreation.Operation);
        ledger["actorId"]!.GetValue<string>().ShouldBe(login.PlayerId);
        ledger["payload"]!["sourceType"]!.GetValue<string>().ShouldBe(StartingStateBehaviour.LedgerSourceType);
        ledger["payload"]!["actorId"]!.GetValue<string>().ShouldBe(login.PlayerId);
        ledger["payload"]!["delta"]!.GetValue<long>().ShouldBe(StartingGold);

        // The player reads the creation receipt through the receipt lookup (CMD-09: actorId = player id, plan N3).
        var lookup = await host.BearerClient(login.SessionToken).GetAsync($"/api/v1/player/receipts/{receiptId}");
        lookup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await lookup.ReadProtobufAsync<ReceiptView>();
        view.Operation.ShouldBe(AccountCreation.Operation);
        view.Aggregates.Select(a => a.AggregateId).ShouldBe([EconomyAggregates.Wallet, StartingStateAggregates.Starter], ignoreOrder: true);
    }

    [Fact]
    public async Task An_initializer_that_writes_nothing_leaves_the_profile_alone_without_a_receipt()
    {
        await using var host = await StartAsync(StartingStateBehaviour.Nothing());
        var before = Counts(host);

        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        login.IsNewPlayer.ShouldBeTrue();
        Delta(before, Counts(host)).ShouldBe([1, 0, 0, 0, 0, 0]);
        login.Sections.Single(s => s.Id == EconomyAggregates.Wallet).Rev.ShouldBe(0);
        login.Sections.Single(s => s.Id == StartingStateAggregates.Starter).Rev.ShouldBe(0);
        host.LogEntries.Lines(AccountWriter.LogCategory).ShouldBe([$"Account {login.PlayerId} created: 1 initializers, receipt none."]);
    }

    [Fact]
    public async Task A_returning_login_never_runs_the_initializer()
    {
        await using var host = await StartAsync(StartingStateBehaviour.Grant(StartingGold));
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        await host.Client().LoginAsync(localId, secret);
        var behaviour = host.Services.GetRequiredService<StartingStateBehaviour>();
        var invocations = behaviour.Invocations;

        var again = await host.Client().LoginAsync(localId, secret);

        again.IsNewPlayer.ShouldBeFalse();
        behaviour.Invocations.ShouldBe(invocations);
        Balance(host, again.PlayerId).ShouldBe(StartingGold);
    }

    [Fact]
    public async Task Twenty_racing_first_logins_and_five_lost_response_retries_create_one_account_once()
    {
        await using var host = await StartAsync(StartingStateBehaviour.Grant(StartingGold));
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        var racing = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => host.Client().PostLoginAsync(localId, secret))));
        var answers = new List<LoginResponse>();
        foreach (var response in racing)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            answers.Add(await response.ReadProtobufAsync<LoginResponse>());
        }

        // Five retries of a client whose response was lost: each is a normal login.
        for (var i = 0; i < 5; i++)
        {
            answers.Add(await host.Client().LoginAsync(localId, secret));
        }

        answers.Count(a => a.IsNewPlayer).ShouldBe(1);
        answers.Select(a => a.PlayerId).Distinct().ShouldHaveSingleItem();
        host.Store.Count(ReceiptDocument.Collection).ShouldBe(1);
        host.Store.Count(EconomyOperations.LedgerCollection).ShouldBe(1);
        Balance(host, answers[0].PlayerId).ShouldBe(StartingGold);
    }

    [Theory]
    [InlineData(StoreFault.FailBeforeCommit, HttpStatusCode.InternalServerError, "INTERNAL")]
    [InlineData(StoreFault.AmbiguousBeforeCommit, HttpStatusCode.ServiceUnavailable, "COMMIT_UNKNOWN_RETRY")]
    public async Task A_store_fault_of_the_creation_leaves_nothing_and_the_next_login_creates_everything(StoreFault fault, HttpStatusCode status, string code)
    {
        await using var host = await StartAsync(StartingStateBehaviour.Grant(StartingGold));
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var before = Counts(host);
        host.Store.Faults.OnlyWhenWritingCollection = IdentityAggregates.ProfileDescriptor.Collection;
        host.Store.Faults.Enqueue(fault);

        var refused = await host.Client().PostLoginAsync(localId, secret);

        refused.StatusCode.ShouldBe(status);
        (await refused.ReadErrorAsync()).Code.ShouldBe(code);
        Delta(before, Counts(host)).ShouldBe([0, 0, 0, 0, 0, 0]);
        await NextLoginCreatesEverythingAsync(host, localId, secret, before);
    }

    [Fact]
    public async Task A_conflict_during_the_creation_is_retried_and_creates_everything_once()
    {
        await using var host = await StartAsync(StartingStateBehaviour.Grant(StartingGold));
        var before = Counts(host);
        host.Store.Faults.OnlyWhenWritingCollection = IdentityAggregates.ProfileDescriptor.Collection;
        host.Store.Faults.Enqueue(StoreFault.ConflictOnce);

        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        login.IsNewPlayer.ShouldBeTrue();
        Delta(before, Counts(host)).ShouldBe([1, 1, 1, 1, 1, 0]);
        Balance(host, login.PlayerId).ShouldBe(StartingGold);
    }

    [Fact]
    public async Task An_anchor_removed_before_the_creation_creates_nothing_and_answers_account_deleted()
    {
        await using var host = await StartAsync(StartingStateBehaviour.Grant(StartingGold));
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var before = Counts(host);
        var anchor = new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(localId));
        var completion = host.Services.GetRequiredService<LoginCompletion>();
        completion.AfterMappingRead = async _ =>
        {
            completion.AfterMappingRead = null;
            await ((IKeyValueStore)host.Store).RemoveAsync(anchor, CancellationToken.None);
        };

        var refused = await host.Client().PostLoginAsync(localId, secret);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await refused.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_DELETED");
        Delta(before, Counts(host)).ShouldBe([0, 0, 0, 0, 0, 0]);
        await NextLoginCreatesEverythingAsync(host, localId, secret, before);
    }

    [Theory]
    [InlineData(StartingStateMode.Reject)]
    [InlineData(StartingStateMode.Throw)]
    [InlineData(StartingStateMode.WriteUndeclared)]
    [InlineData(StartingStateMode.AddAudit)]
    [InlineData(StartingStateMode.OversizeAggregate)]
    [InlineData(StartingStateMode.FloodOutbox)]
    [InlineData(StartingStateMode.Claim)]
    public async Task A_failing_initializer_answers_internal_leaves_nothing_and_the_next_login_creates_everything(StartingStateMode mode)
    {
        var behaviour = mode switch
        {
            StartingStateMode.Reject => StartingStateBehaviour.Reject(StartingStateErrors.Rejected),
            StartingStateMode.Throw => StartingStateBehaviour.Throw(),
            StartingStateMode.WriteUndeclared => StartingStateBehaviour.WriteUndeclared(),
            StartingStateMode.AddAudit => StartingStateBehaviour.AddAudit(),
            StartingStateMode.OversizeAggregate => StartingStateBehaviour.OversizeAggregate(),
            StartingStateMode.FloodOutbox => StartingStateBehaviour.FloodOutbox(40),
            _ => StartingStateBehaviour.Claim(3),
        };
        await using var host = await StartAsync(behaviour);
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var before = Counts(host);

        var refused = await host.Client().PostLoginAsync(localId, secret);

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await refused.ReadErrorAsync()).Code.ShouldBe("INTERNAL");
        Delta(before, Counts(host)).ShouldBe([0, 0, 0, 0, 0, 0]);
        var failures = host.LogEntries.Where(e => e.Category == AccountWriter.LogCategory && e.Level == LogLevel.Error).ToArray();
        failures.ShouldHaveSingleItem().Message.ShouldStartWith("Account creation of player ");
        failures[0].Message.ShouldEndWith(" Nothing was created; the next login retries.");
        failures[0].Exception.ShouldBeNull();
        host.LogEntries.ShouldNotContain(e => e.Message.Contains("must never be logged", StringComparison.Ordinal));

        behaviour.Become(StartingStateBehaviour.Grant(StartingGold));
        await NextLoginCreatesEverythingAsync(host, localId, secret, before);
    }

    private static async Task NextLoginCreatesEverythingAsync(TestHost host, string localId, byte[] secret, int[] before)
    {
        var login = await host.Client().LoginAsync(localId, secret);

        login.IsNewPlayer.ShouldBeTrue();
        Delta(before, Counts(host)).ShouldBe([1, 1, 1, 1, 1, 0]);
        Balance(host, login.PlayerId).ShouldBe(StartingGold);
    }

    private static Task<TestHost> StartAsync(StartingStateBehaviour behaviour) =>
        SampleTestHost.StartAsync(
            modules: b => b.AddModule<IdentityModule>().AddModule<EconomyModule>().AddModule(new StartingStateModule()),
            settings: s => s["Identity:LoginPerLocalIdPerMinute"] = "100",
            services: s => s.AddSingleton(behaviour));

    private static int[] Counts(TestHost host) => SeededCollections.Select(host.Store.Count).ToArray();

    private static int[] Delta(int[] before, int[] after) => after.Select((n, i) => n - before[i]).ToArray();

    private static long Balance(TestHost host, string playerId) =>
        host.Store.Peek(new DocumentKey("pa_" + EconomyAggregates.Wallet, playerId)).ShouldNotBeNull()["state"]!["balances"]![StartingStateBehaviour.Resource]!
            .GetValue<long>();

    /// <summary>The receipt id of the Information line <c>Account {PlayerId} created: … receipt {ReceiptId}.</c> (plan N3).</summary>
    private static string CreatedReceiptId(TestHost host, string playerId)
    {
        var line = host.LogEntries.Lines(AccountWriter.LogCategory).Single(l => l.StartsWith($"Account {playerId} created:", StringComparison.Ordinal));
        return line[(line.LastIndexOf(' ') + 1)..^1];
    }
}
