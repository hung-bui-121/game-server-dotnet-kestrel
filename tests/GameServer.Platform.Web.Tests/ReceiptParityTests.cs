using System.Globalization;
using System.Text.RegularExpressions;
using GameServer.Actors;
using GameServer.Commands;
using GameServer.Config;
using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Modules.Iap;
using GameServer.Modules.Identity;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Adapters;
using SampleGame;
using SampleGame.Adapters;
using Shouldly;
using Wire = SampleGame.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T052 (FR-034, SC-005, research R14, VERIFY V7): for the same actor, player, request id, receipt id, config version and
/// time, the receipt the host stores and the receipt the kit double measures with the runner's builder have the same size (0 bytes
/// difference): the sample <c>shop.buy</c> and <c>battle.end</c> (full game), a seeding account creation and the sample
/// initializer's creation (T064).
/// </summary>
public sealed partial class ReceiptParityTests
{
    /// <summary>The stored receipt must not change after the commit (the inline relay marks the outbox relayed).</summary>
    private static void NoInlineRelay(Dictionary<string, string?> settings) => settings["GameServer:Outbox:InlineRelayEnabled"] = "false";

    [Fact]
    public async Task The_shop_buy_receipt_measures_the_same_in_the_host_and_the_double()
    {
        await using var host = await GameHttp.StartGameAsync(settings: NoInlineRelay);
        var (player, _) = await host.NewPlayerAsync();
        (await player.CheatAsync("gold", 1000)).Code.ShouldBe("OK");
        var request = new BuyItemCommand("potion_pack", 2);

        var run = await MeasureAsync(host, player, BuyItemHandler.Operation, request, () => player.BuyAsync(request.ShopItemId, request.Quantity, RequestId));

        run.Measured.ShouldBe(run.Stored);
    }

    [Fact]
    public async Task The_battle_end_receipt_measures_the_same_in_the_host_and_the_double()
    {
        await using var host = await GameHttp.StartGameAsync(settings: NoInlineRelay);
        var (player, _) = await host.NewPlayerAsync();
        var battleId = Wire.StartBattleResult.Parser.ParseFrom((await player.StartBattleAsync(1)).Result).BattleId;
        var request = new EndBattleCommand(battleId, Won: true);

        var run = await MeasureAsync(host, player, EndBattleHandler.Operation, request, () => player.EndBattleAsync(battleId, won: true, RequestId));

        run.Stored.ShouldBeGreaterThan(0);
        run.Measured.ShouldBe(run.Stored);
    }

    [Fact]
    public async Task The_creation_receipt_measures_the_same_in_the_host_and_the_double()
    {
        var behaviour = StartingStateBehaviour.Grant(250);
        await using var host = await SampleTestHost.StartAsync(
            modules: b => b.AddModule<IdentityModule>().AddModule<EconomyModule>().AddModule(new StartingStateModule()),
            settings: NoInlineRelay,
            services: s => s.AddSingleton(behaviour));
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        login.IsNewPlayer.ShouldBeTrue();
        var receipt = host.Store.All(ReceiptDocument.Collection).Select(d => d.Content)
            .Single(r => r["playerId"]!.GetValue<string>() == login.PlayerId && r["operation"]!.GetValue<string>() == AccountCreation.Operation);
        var stored = ReceiptDocument.SizeOf(receipt);

        var context = CommandContextDouble.ForAccountInitializer(StartingStateModule.InitializerName, new StartingStateModule(), new EconomyModule());
        Configure(context, host, receipt, new ActorContext(login.PlayerId, ActorType.System, login.PlayerId, Text(receipt, "gameId"), Text(receipt, "environmentId"), new HashSet<string>(), Text(receipt, "correlationId")));
        var initializer = ActivatorUtilities.CreateInstance<StartingStateInitializer>(host.Services);
        (await initializer.InitializeAsync(context)).ShouldBeNull();

        MeasuredBytes(context.CheckAccountCreation()).ShouldBe(stored);
    }

    /// <summary>Slice 016 T064: the sample initializer <c>sample.starting_gold</c> on a config with <c>account.json</c> (SC-005 sample row).</summary>
    [Fact]
    public async Task The_sample_starting_gold_creation_receipt_measures_the_same_in_the_host_and_the_double()
    {
        using var seeded = new SeededConfig()
            .With(SampleAccountInitializer.ConfigFile, """{"startingGold": 100}""")
            .Manifest(m =>
            {
                m["files"]!.AsArray().Add(SampleAccountInitializer.ConfigFile);
                return m;
            });
        await using var host = await GameHttp.StartGameAsync(settings: s =>
        {
            NoInlineRelay(s);
            seeded.Apply(s);
        });
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        login.IsNewPlayer.ShouldBeTrue();
        var receipt = host.Store.All(ReceiptDocument.Collection).Select(d => d.Content)
            .Single(r => r["playerId"]!.GetValue<string>() == login.PlayerId && r["operation"]!.GetValue<string>() == AccountCreation.Operation);
        var stored = ReceiptDocument.SizeOf(receipt);

        var context = CommandContextDouble.ForAccountInitializer(SampleAccountInitializer.Name, new EconomyModule(), new SampleGameModule());
        Configure(context, host, receipt, new ActorContext(login.PlayerId, ActorType.System, login.PlayerId, Text(receipt, "gameId"), Text(receipt, "environmentId"), new HashSet<string>(), Text(receipt, "correlationId")));
        var initializer = ActivatorUtilities.CreateInstance<SampleAccountInitializer>(host.Services);
        (await initializer.InitializeAsync(context)).ShouldBeNull();

        MeasuredBytes(context.CheckAccountCreation()).ShouldBe(stored);
    }

    /// <summary>
    /// The double changes by exactly the length difference of a configured value (here a longer config version; characters the JSON
    /// writer escapes, such as "+", count as their escaped length on both sides).
    /// </summary>
    [Fact]
    public async Task A_longer_config_version_changes_the_measure_by_its_length_difference()
    {
        await using var host = await GameHttp.StartGameAsync(settings: NoInlineRelay);
        var (player, _) = await host.NewPlayerAsync();
        (await player.CheatAsync("gold", 1000)).Code.ShouldBe("OK");
        var request = new BuyItemCommand("potion_pack", 1);
        var run = await MeasureAsync(host, player, BuyItemHandler.Operation, request, () => player.BuyAsync(request.ShopItemId, 1, RequestId));
        run.Measured.ShouldBe(run.Stored);

        var longer = new LongerVersionConfig(host.Services.GetRequiredService<IGameConfig>(), "-longer");
        var remeasured = await MeasureDoubleAsync(host, player.PlayerId, BuyItemHandler.Operation, request, run, longer);

        remeasured.ShouldBe(run.Stored + "-longer".Length);
    }

    private const string RequestId = "3f2a9c1e-7b11-4d2a-9c0d-5e6f7a8b9c0d";

    private static async Task<Run> MeasureAsync(
        TestHost host,
        GamePlayer player,
        string operation,
        object request,
        Func<Task<CommandResponse>> send)
    {
        var before = await LoadAsync(host, player.PlayerId, operation);
        var response = await send();
        response.Code.ShouldBe("OK");
        var receipt = host.Store.Peek(ReceiptDocument.KeyFor(response.ReceiptId)).ShouldNotBeNull();
        var run = new Run(receipt, before, response.Result.ToByteArray(), ReceiptDocument.SizeOf(receipt), 0);
        return run with { Measured = await MeasureDoubleAsync(host, player.PlayerId, operation, request, run, host.Services.GetRequiredService<IGameConfig>()) };
    }

    /// <summary>Runs the host's handler in the double, configured with the stored receipt's inputs and the state before the command.</summary>
    private static async Task<int> MeasureDoubleAsync(
        TestHost host,
        string playerId,
        string operation,
        object request,
        Run run,
        IGameConfig config)
    {
        var receipt = run.Receipt;
        var context = CommandContextDouble.Create(operation, new IdentityModule(), new EconomyModule(), new IapModule(), new SampleGameModule(), new CounterModule());
        Configure(
            context,
            host,
            receipt,
            new ActorContext(playerId, ActorType.Player, playerId, Text(receipt, "gameId"), Text(receipt, "environmentId"), new HashSet<string>(), Text(receipt, "correlationId")));
        context.WithConfig(config);
        foreach (var loaded in run.Before.Values.Where(l => l.Document is not null))
        {
            SeedAt(context, loaded);
        }

        var registration = host.Services.GetRequiredService<ModuleRegistry>().GetCommand(operation);
        var handler = ActivatorUtilities.CreateInstance(host.Services, registration.HandlerType);
        var outcome = await registration.InvokeAsync(handler, request, context);
        return MeasuredBytes(context.CheckCommit(outcome, _ => run.Result));
    }

    private static void Configure(CommandContextDouble context, TestHost host, System.Text.Json.Nodes.JsonObject receipt, ActorContext actor) =>
        context
            .WithActor(actor)
            .WithRequestId(Text(receipt, "requestId"))
            .WithReceiptId(Text(receipt, "receiptId"))
            .WithConfig(host.Services.GetRequiredService<IGameConfig>())
            .At(DateTimeOffset.Parse(Text(receipt, "committedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal))
            .WithCommandSettings(new CommandSettings { ReceiptMaxBytes = 1, ResultCompactMaxBytes = 16 * 1024 });

    private static void SeedAt(CommandContextDouble context, LoadedAggregate loaded) =>
        typeof(CommandContextDouble).GetMethods()
            .Single(m => m.Name == nameof(CommandContextDouble.Seed) && m.GetParameters().Length == 3)
            .MakeGenericMethod(loaded.Descriptor.StateType)
            .Invoke(context, [loaded.Descriptor.AggregateId, loaded.State, loaded.Rev]);

    /// <summary>The aggregates of the command's Touches ∪ Reads as the runner will load them.</summary>
    private static async Task<Dictionary<string, LoadedAggregate>> LoadAsync(TestHost host, string playerId, string operation)
    {
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        var serializer = host.Services.GetRequiredService<AggregateDocumentSerializer>();
        var descriptor = registry.GetCommand(operation).Descriptor;
        var result = new Dictionary<string, LoadedAggregate>(StringComparer.Ordinal);
        foreach (var aggregate in descriptor.Touches.Concat(descriptor.Reads).Distinct(StringComparer.Ordinal).Select(registry.GetAggregate))
        {
            var document = await host.Store.GetAsync(AggregateDocumentSerializer.KeyFor(aggregate, playerId), CancellationToken.None);
            result[aggregate.AggregateId] = serializer.Load(aggregate, playerId, document);
        }

        return result;
    }

    /// <summary>The receipt size of the double's line (the double is configured with a 1-byte limit, so the line is always there).</summary>
    private static int MeasuredBytes(IReadOnlyList<string> lines)
    {
        var line = lines.ShouldHaveSingleItem();
        var match = ReceiptBytes().Match(line);
        match.Success.ShouldBeTrue(line);
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string Text(System.Text.Json.Nodes.JsonObject receipt, string name) => receipt[name]!.GetValue<string>();

    [GeneratedRegex(@"receipt would be (\d+) bytes")]
    private static partial Regex ReceiptBytes();

    /// <summary>One command through the host: its stored receipt, the state before it, the encoded result and both sizes.</summary>
    private sealed record Run(System.Text.Json.Nodes.JsonObject Receipt, Dictionary<string, LoadedAggregate> Before, byte[] Result, int Stored, int Measured);

    /// <summary>The host's config with a longer version label (everything else the same).</summary>
    private sealed class LongerVersionConfig(IGameConfig inner, string suffix) : IGameConfig
    {
        public string ConfigVersion => inner.ConfigVersion + suffix;

        public string ClientConfigVersion => inner.ClientConfigVersion;

        public string Label => inner.Label;

        public IReadOnlyCollection<string> FileNames => inner.FileNames;

        public IReadOnlyCollection<string> ClientVisibleFiles => inner.ClientVisibleFiles;

        public bool HasFile(string name) => inner.HasFile(name);

        public ReadOnlyMemory<byte> GetFileBytes(string name) => inner.GetFileBytes(name);

        public System.Text.Json.Nodes.JsonNode GetFileJson(string name) => inner.GetFileJson(name);

        public T GetFile<T>(string name) => inner.GetFile<T>(name);
    }
}
