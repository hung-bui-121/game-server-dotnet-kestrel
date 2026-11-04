using System.Net;
using System.Text.Json.Nodes;
using GameServer.Actors;
using GameServer.Modules.Economy;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Tests.Shared;
using SampleGame.Adapters;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// Slice 016 T063 (FR-050, US6-1, research R13): the sample initializer <c>sample.starting_gold</c> grants the gold of the optional,
/// server-only <c>account.json</c> in the transaction that creates the account, and changes nothing without the file (the test
/// config has none, so the full-game suites keep their expectations). The rule lines of <c>account.json</c> are asserted with the
/// other seeded faults (<see cref="ConfigStartupReportTests"/>, host = kit in <see cref="ConfigDeclarationParityTests"/>) and in
/// <see cref="SampleConfigRulesTests"/>.
/// </summary>
public sealed class SampleAccountInitializerTests
{
    private const long StartingGold = 100;
    private const string AccountsCategory = "GameServer.Accounts";

    [Fact]
    public async Task A_first_login_grants_the_starting_gold_with_one_ledger_entry_and_one_creation_receipt()
    {
        using var seeded = WithAccountJson(StartingGold);
        await using var host = await StartAsync(seeded);

        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        login.IsNewPlayer.ShouldBeTrue();
        var wallet = login.Sections.Single(s => s.Id == EconomyAggregates.Wallet);
        wallet.Rev.ShouldBe(1);
        GameServer.Modules.Economy.Protocol.V1.WalletState.Parser.ParseFrom(wallet.Payload).Balances["gold"].ShouldBe(StartingGold);
        var ledger = host.Ledger(login.PlayerId).ShouldHaveSingleItem();
        ledger["operation"]!.GetValue<string>().ShouldBe(AccountCreation.Operation);
        ledger["payload"]!["sourceType"]!.GetValue<string>().ShouldBe(SampleAccountInitializer.LedgerSourceType);
        ledger["payload"]!["delta"]!.GetValue<long>().ShouldBe(StartingGold);
        var receipt = CreationReceipts(host, login.PlayerId).ShouldHaveSingleItem();
        receipt["receiptId"]!.GetValue<string>().ShouldBe(ledger["receiptId"]!.GetValue<string>());
        receipt["actorType"]!.GetValue<string>().ShouldBe("system");
        host.LogEntries.Lines(AccountsCategory).ShouldBe([$"Account {login.PlayerId} created: 1 initializers, receipt {receipt["receiptId"]!.GetValue<string>()}."]);
    }

    [Fact]
    public async Task Concurrent_first_logins_of_one_credential_grant_once()
    {
        using var seeded = WithAccountJson(StartingGold);
        await using var host = await StartAsync(seeded);
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        var racing = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => host.Client().PostLoginAsync(localId, secret))));
        var answers = new List<LoginResponse>();
        foreach (var response in racing)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            answers.Add(await response.ReadProtobufAsync<LoginResponse>());
        }

        answers.Count(a => a.IsNewPlayer).ShouldBe(1);
        var playerId = answers.Select(a => a.PlayerId).Distinct().ShouldHaveSingleItem();
        host.Ledger(playerId).ShouldHaveSingleItem();
        CreationReceipts(host, playerId).ShouldHaveSingleItem();
        host.LedgerSum(playerId, "gold").ShouldBe(StartingGold);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_the_file_or_with_zero_gold_the_creation_writes_the_profile_alone(bool zeroGold)
    {
        using var seeded = zeroGold ? WithAccountJson(0) : new SeededConfig();
        await using var host = await StartAsync(seeded);

        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        login.IsNewPlayer.ShouldBeTrue();
        login.Sections.Single(s => s.Id == EconomyAggregates.Wallet).Rev.ShouldBe(0);
        host.Store.Peek(new DocumentKey("pa_" + EconomyAggregates.Wallet, login.PlayerId)).ShouldBeNull();
        host.Ledger(login.PlayerId).ShouldBeEmpty();
        CreationReceipts(host, login.PlayerId).ShouldBeEmpty();
        host.LogEntries.Lines(AccountsCategory).ShouldBe([$"Account {login.PlayerId} created: 1 initializers, receipt none."]);
    }

    [Fact]
    public async Task The_kit_runs_the_initializer_host_free_with_the_writers_rules()
    {
        var config = TestGameConfig.FromJson(new Dictionary<string, string>
        {
            [ResourceCatalog.ConfigFile] = """{"resources": [{"id": "gold", "kind": "currency", "maxQuantity": 1000, "displayName": "Gold"}]}""",
            [SampleAccountInitializer.ConfigFile] = """{"startingGold": 100}""",
        });
        var context = Double().WithConfig(config);

        (await new SampleAccountInitializer(ResourceCatalog.Load(config)).InitializeAsync(context)).ShouldBeNull();

        context.Actor.ActorType.ShouldBe(ActorType.System);
        context.Written<WalletState>(EconomyAggregates.Wallet)!.Balances["gold"].ShouldBe(StartingGold);
        context.Outbox.Select(e => e.Kind).ShouldBe([EconomyOperations.LedgerKind]);
        context.CheckAccountCreation().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_kit_shows_that_the_initializer_changes_nothing_without_the_file()
    {
        var config = TestGameConfig.FromJson(new Dictionary<string, string>
        {
            [ResourceCatalog.ConfigFile] = """{"resources": [{"id": "gold", "kind": "currency", "maxQuantity": 1000, "displayName": "Gold"}]}""",
        });
        var context = Double().WithConfig(config);

        (await new SampleAccountInitializer(ResourceCatalog.Load(config)).InitializeAsync(context)).ShouldBeNull();

        context.Writes.ShouldBeEmpty();
        context.Outbox.ShouldBeEmpty();
        context.CheckAccountCreation().ShouldBeEmpty();
    }

    /// <summary>The test config plus <c>account.json</c>, listed in the manifest <c>files</c> and not client-visible (as SampleHost).</summary>
    internal static SeededConfig WithAccountJson(long startingGold) =>
        new SeededConfig()
            .With(SampleAccountInitializer.ConfigFile, $$"""{"startingGold": {{startingGold}}}""")
            .Manifest(m =>
            {
                m["files"]!.AsArray().Add(SampleAccountInitializer.ConfigFile);
                return m;
            });

    private static Task<TestHost> StartAsync(SeededConfig seeded) =>
        GameHttp.StartGameAsync(settings: s =>
        {
            seeded.Apply(s);
            s["Identity:LoginPerLocalIdPerMinute"] = "100";
        });

    private static CommandContextDouble Double() =>
        CommandContextDouble.ForAccountInitializer(SampleAccountInitializer.Name, new EconomyModule(), new SampleGameModule());

    private static JsonObject[] CreationReceipts(TestHost host, string playerId) =>
        host.Store.All(ReceiptDocument.Collection)
            .Select(d => d.Content)
            .Where(r => r["playerId"]!.GetValue<string>() == playerId && r["operation"]!.GetValue<string>() == AccountCreation.Operation)
            .ToArray();
}
