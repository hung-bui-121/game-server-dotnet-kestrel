using System.Text.Json.Nodes;
using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Modules.Identity;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sample.Counter;
using Shouldly;
using Template.Module.Adapters;
using EconomyWire = GameServer.Modules.Economy.Protocol.V1;
using IdentityWire = GameServer.Modules.Identity.Protocol.V1;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>FG-028: a typed wire mapper carries its message type, parser and hidden members (research R15); BC-3, BC-4.</summary>
public sealed class WireMapperRegistrationTests
{
    private const string UntypedWarning =
        "[aggregate probe_legacy] wire mapper registered without its message type: the catalog has no message for this section and Unity gets no " +
        "parser. Fix: AddAggregateWireMapper<TState, TWire>(…) (FG-028).";

    private static readonly CounterState State = new() { Value = 7, Increments = 3 };

    private static Wire.CounterState ToWire(CounterState state) => new() { Value = state.Value, Increments = state.Increments };

    [Fact]
    public void Typed_mapper_payload_equals_the_untyped_payload_of_the_same_function()
    {
        var typed = new AggregateWireMapper<CounterState, Wire.CounterState>(CounterAggregates.Counter, ToWire);
        var untyped = new AggregateWireMapper<CounterState>(CounterAggregates.Counter, ToWire);

        typed.ToPayload(State).ShouldBe(untyped.ToPayload(State));
        typed.Map(State).ShouldBe(ToWire(State));
    }

    [Fact]
    public void Contract_exposes_message_parser_and_declared_lists_sorted_and_distinct()
    {
        var options = new WireMapperOptions().Hidden("Value", "Increments", "Value").MayStayDefault("value");
        var mapper = new AggregateWireMapper<CounterState, Wire.CounterState>(CounterAggregates.Counter, ToWire, options);
        var registry = new AggregateWireMapperRegistry([mapper]);

        registry.TryGetContract(CounterAggregates.Counter, out var contract).ShouldBeTrue();
        contract!.AggregateId.ShouldBe(CounterAggregates.Counter);
        contract.StateType.ShouldBe(typeof(CounterState));
        contract.Message.FullName.ShouldBe("sample.counter.v1.CounterState");
        contract.Parser.ParseFrom(mapper.ToPayload(State)).ShouldBe(ToWire(State));
        contract.HiddenMembers.ShouldBe(["Increments", "Value"]);
        contract.MayStayDefaultFields.ShouldBe(["value"]);
    }

    [Fact]
    public void An_untyped_mapper_has_no_contract()
    {
        var registry = new AggregateWireMapperRegistry([new AggregateWireMapper<CounterState>(CounterAggregates.Counter, ToWire)]);

        registry.Has(CounterAggregates.Counter).ShouldBeTrue();
        registry.TryGetContract(CounterAggregates.Counter, out var contract).ShouldBeFalse();
        contract.ShouldBeNull();
        registry.TryGetContract("unknown", out _).ShouldBeFalse();
    }

    [Fact]
    public void Duplicates_list_an_id_registered_twice_and_the_first_mapper_serves()
    {
        IAggregateWireMapper first = new AggregateWireMapper<CounterState>(CounterAggregates.Counter, _ => new Wire.CounterState { Value = 1 });
        IAggregateWireMapper last = new AggregateWireMapper<CounterState, Wire.CounterState>(CounterAggregates.Counter, _ => new Wire.CounterState { Value = 2 });
        IAggregateWireMapper other = new AggregateWireMapper<CounterState>("other", ToWire);

        var registry = new AggregateWireMapperRegistry([first, other, last]);

        registry.Duplicates.ShouldHaveSingleItem().ShouldBe(new DuplicateWireMapper(CounterAggregates.Counter, 2));
        Wire.CounterState.Parser.ParseFrom(registry.ToPayload(CounterAggregates.Counter, State)).Value.ShouldBe(1);
        registry.TryGetContract(CounterAggregates.Counter, out _).ShouldBeFalse();
    }

    [Fact]
    public void Service_registration_passes_the_configured_options()
    {
        var services = new ServiceCollection();

        services.AddAggregateWireMapper<CounterState, Wire.CounterState>(CounterAggregates.Counter, ToWire, o => o.Hidden("Increments"));

        var mapper = services.ShouldHaveSingleItem().ImplementationInstance.ShouldBeOfType<AggregateWireMapper<CounterState, Wire.CounterState>>();
        mapper.HiddenMembers.ShouldBe(["Increments"]);
        mapper.ToPayload(State).ShouldBe(ToWire(State).ToByteString());
    }

    // T055: the framework mappers switched to the typed overload with unchanged bodies, so their payload bytes are unchanged.
    [Fact]
    public void Framework_mappers_are_typed_and_keep_their_payload_bytes()
    {
        var mappers = MappersOf(new EconomyModule(), new IdentityModule());

        mappers[EconomyAggregates.Wallet].ToPayload(new WalletState { Balances = new Dictionary<string, long> { ["gold"] = 5, ["gem"] = 2 } })
            .ShouldBe(new EconomyWire.WalletState { Balances = { ["gold"] = 5, ["gem"] = 2 } }.ToByteString());
        mappers[EconomyAggregates.Inventory].ToPayload(new InventoryState { Items = new Dictionary<string, long> { ["sword"] = 1 } })
            .ShouldBe(new EconomyWire.InventoryState { Items = { ["sword"] = 1 } }.ToByteString());
        mappers[IdentityAggregates.Profile].ToPayload(new ProfileState { DisplayName = "Ann", SessionEpoch = 4, Identities = ["guest:x"] })
            .ShouldBe(new IdentityWire.ProfileState { DisplayName = "Ann", SessionEpoch = 4 }.ToByteString());
        mappers.Values.ShouldAllBe(mapper => mapper is IAggregateWireContract);
    }

    // T055, T056: every framework, sample and template section names its message in the catalog.
    [Fact]
    public void Every_framework_sample_and_template_section_names_its_message()
    {
        using var app = SampleTestHost.BuildApp(b =>
        {
            GameHttp.FullGame(b);
            b.AddModule<TemplateModule>();
        });

        var sections = app.Services.GetRequiredService<WireCatalog>().Sections.ToDictionary(s => s.Id, s => s.MessageFullName);

        sections.ShouldBe(
            new Dictionary<string, string?>
            {
                ["battle"] = "samplegame.v1.BattleState",
                [CounterAggregates.Counter] = "sample.counter.v1.CounterState",
                [EconomyAggregates.Inventory] = "gameserver.economy.v1.InventoryState",
                [IdentityAggregates.Profile] = "gameserver.identity.v1.ProfileState",
                ["progression"] = "samplegame.v1.ProgressionState",
                ["template"] = "template.module.v1.TemplateState",
                [EconomyAggregates.Wallet] = "gameserver.economy.v1.WalletState",
            },
            ignoreOrder: true);
    }

    // T057 (BC-3): an untyped mapper still works, its section has no message, and the host warns once at startup.
    [Fact]
    public async Task An_untyped_client_visible_section_logs_one_warning_and_the_host_starts()
    {
        await using var host = await SampleTestHost.StartAsync(modules: b =>
        {
            SampleTestHost.DefaultModules(b);
            b.AddModule(new UntypedMapperProbeModule());
        });

        var catalog = host.Services.GetRequiredService<WireCatalog>();
        catalog.TryGetSection(UntypedMapperProbeModule.Legacy, out var section).ShouldBeTrue();
        section!.MessageFullName.ShouldBeNull();
        StartupWarnings(host).ShouldBe([UntypedWarning]);
    }

    [Fact]
    public async Task Typed_sections_log_no_untyped_warning()
    {
        await using var host = await SampleTestHost.StartAsync(modules: b =>
        {
            SampleTestHost.DefaultModules(b);
            b.AddModule(new CatalogProbeModule());
        });

        StartupWarnings(host).ShouldNotContain(w => w.Contains("wire mapper registered without its message type", StringComparison.Ordinal));
    }

    [Fact]
    public void The_export_of_an_untyped_section_has_no_message()
    {
        using var app = SampleTestHost.BuildApp(b =>
        {
            SampleTestHost.DefaultModules(b);
            b.AddModule(new UntypedMapperProbeModule());
        });

        var sections = JsonNode.Parse(app.Services.GetRequiredService<WireCatalog>().ToExportJson())!["sections"]!.AsArray();

        var legacy = sections.Single(s => (string?)s!["id"] == UntypedMapperProbeModule.Legacy)!.AsObject();
        legacy.ContainsKey("message").ShouldBeFalse();
        sections.Single(s => (string?)s!["id"] == CounterAggregates.Counter)!["message"]!.GetValue<string>().ShouldBe("sample.counter.v1.CounterState");
    }

    // T058 (BC-4): two mappers for one aggregate fail startup; ReplaceWireMapper is the documented replacement.
    [Fact]
    public void Two_mappers_for_the_wallet_fail_startup_naming_the_wallet_and_the_fix()
    {
        using var app = SampleTestHost.BuildApp(
            b => b.AddModule<IdentityModule>().AddModule<EconomyModule>(),
            services: s => s.AddAggregateWireMapper<WalletState, EconomyWire.WalletState>(EconomyAggregates.Wallet, CustomWallet));

        var ex = Should.Throw<GameServerStartupException>(() => app.UseGameServer());

        ex.Violations.ShouldContain(
            "[aggregate wallet] has 2 wire mappers. Fix: register one mapper per aggregate; replace a framework mapper with " +
            "ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(…)) (API-10).");
    }

    [Fact]
    public async Task Replace_wire_mapper_serves_the_custom_payload_in_the_snapshot()
    {
        await using var host = await SampleTestHost.StartAsync(modules: b => b
            .AddModule<IdentityModule>()
            .AddModule<EconomyModule>()
            .ConfigureExtensions(e => e.ReplaceWireMapper<WalletState, EconomyWire.WalletState>(EconomyAggregates.Wallet, CustomWallet, o => o.Hidden("Balances"))));
        var (player, _) = await host.NewPlayerAsync();

        var wallet = await player.WalletAsync();

        wallet.Balances.ShouldBe(new Dictionary<string, long> { ["replaced"] = 42 });
        host.Services.GetRequiredService<WireCatalog>().TryGetSection(EconomyAggregates.Wallet, out var section).ShouldBeTrue();
        section!.HiddenMembers.ShouldBe(["Balances"]);
    }

    [Fact]
    public void Replace_wire_mapper_without_an_earlier_mapper_registers_it()
    {
        var services = new ServiceCollection();
        new GameServer.Platform.Web.Hosting.ExtensionRegistry(services, new GameServer.Platform.Registry.ModuleRegistry())
            .ReplaceWireMapper<WalletState, EconomyWire.WalletState>(EconomyAggregates.Wallet, CustomWallet);

        var mapper = services.ShouldHaveSingleItem().ImplementationInstance.ShouldBeAssignableTo<IAggregateWireMapper>()!;
        mapper.AggregateId.ShouldBe(EconomyAggregates.Wallet);
        mapper.ToPayload(new WalletState()).ShouldBe(CustomWallet(new WalletState()).ToByteString());
    }

    [Fact]
    public void Replace_wire_mapper_removes_only_the_mappers_of_its_aggregate()
    {
        var services = new ServiceCollection();
        foreach (var mapper in MappersOf(new EconomyModule()).Values)
        {
            services.AddSingleton(mapper);
        }

        new GameServer.Platform.Web.Hosting.ExtensionRegistry(services, new GameServer.Platform.Registry.ModuleRegistry())
            .ReplaceWireMapper<WalletState, EconomyWire.WalletState>(EconomyAggregates.Wallet, CustomWallet);

        var registry = new AggregateWireMapperRegistry(services.Select(d => (IAggregateWireMapper)d.ImplementationInstance!));
        registry.Duplicates.ShouldBeEmpty();
        registry.Has(EconomyAggregates.Inventory).ShouldBeTrue();
        registry.ToPayload(EconomyAggregates.Wallet, new WalletState()).ShouldBe(CustomWallet(new WalletState()).ToByteString());
    }

    // T086 M1: the client parses a framework section with the framework parser, so an override must keep the message.
    [Fact]
    public void Replace_wire_mapper_with_another_message_fails_startup()
    {
        using var app = SampleTestHost.BuildApp(b => b
            .AddModule<IdentityModule>()
            .AddModule<EconomyModule>()
            .ConfigureExtensions(e => e.ReplaceWireMapper<WalletState, EconomyWire.InventoryState>(EconomyAggregates.Wallet, _ => new EconomyWire.InventoryState())));

        var ex = Should.Throw<GameServerStartupException>(() => app.UseGameServer());

        ex.Violations.ShouldContain("[aggregate wallet] ReplaceWireMapper must keep gameserver.economy.v1.WalletState; a new payload needs a new aggregate (FG-021).");
        ex.Violations.ShouldNotContain(v => v.Contains("wire mapper takes", StringComparison.Ordinal));
    }

    [Fact]
    public void A_second_replacement_still_compares_with_the_original_message()
    {
        var services = new ServiceCollection();
        foreach (var mapper in MappersOf(new EconomyModule()).Values)
        {
            services.AddSingleton(mapper);
        }

        new GameServer.Platform.Web.Hosting.ExtensionRegistry(services, new GameServer.Platform.Registry.ModuleRegistry())
            .ReplaceWireMapper<WalletState, EconomyWire.InventoryState>(EconomyAggregates.Wallet, _ => new EconomyWire.InventoryState())
            .ReplaceWireMapper<WalletState, EconomyWire.InventoryState>(EconomyAggregates.Wallet, _ => new EconomyWire.InventoryState());

        var registry = new AggregateWireMapperRegistry(services.Select(d => (IAggregateWireMapper)d.ImplementationInstance!));
        registry.TryGetContract(EconomyAggregates.Wallet, out var contract).ShouldBeTrue();
        ((IWireMapperReplacement)contract!).ReplacedMessage.ShouldBe("gameserver.economy.v1.WalletState");
    }

    [Fact]
    public void Replace_wire_mapper_keeping_the_message_records_no_fault()
    {
        using var app = SampleTestHost.BuildApp(b => b
            .AddModule<IdentityModule>()
            .AddModule<EconomyModule>()
            .ConfigureExtensions(e => e.ReplaceWireMapper<WalletState, EconomyWire.WalletState>(EconomyAggregates.Wallet, CustomWallet)));

        app.Services.GetRequiredService<WireCatalog>().Validate().ShouldBeEmpty();
    }

    // T086 M2: a mapper whose TState the aggregate does not hold threw InvalidCastException (500) on every snapshot.
    [Fact]
    public void A_mapper_typed_for_another_state_fails_startup_naming_both_types()
    {
        using var app = SampleTestHost.BuildApp(b =>
        {
            SampleTestHost.DefaultModules(b);
            b.AddModule(new MistypedMapperModule());
        });

        var ex = Should.Throw<GameServerStartupException>(() => app.UseGameServer());

        ex.Violations.ShouldBe(
        [
            "[aggregate probe_card] wire mapper takes CounterState but the aggregate holds ProbeCard. " +
            "Fix: AddAggregateWireMapper<ProbeCard, TWire>(…) (API-10).",
        ]);
    }

    [Fact]
    public void A_replacement_typed_for_another_state_fails_startup_too()
    {
        using var app = SampleTestHost.BuildApp(b => b
            .AddModule<IdentityModule>()
            .AddModule<EconomyModule>()
            .ConfigureExtensions(e => e.ReplaceWireMapper<ProfileState, EconomyWire.WalletState>(EconomyAggregates.Wallet, _ => new EconomyWire.WalletState())));

        var ex = Should.Throw<GameServerStartupException>(() => app.UseGameServer());

        ex.Violations.ShouldContain(
            "[aggregate wallet] wire mapper takes ProfileState but the aggregate holds WalletState. Fix: AddAggregateWireMapper<WalletState, TWire>(…) (API-10).");
    }

    private static EconomyWire.WalletState CustomWallet(WalletState state) => new() { Balances = { ["replaced"] = 42 } };

    private static string[] StartupWarnings(TestHost host) =>
        host.LogEntries
            .Where(e => e.Category == GameServerApplicationExtensions.StartupLogCategory && e.Level == LogLevel.Warning)
            .Select(e => e.Message)
            .ToArray();

    /// <summary>The wire mappers the modules register, from their ConfigureServices against a bare collection.</summary>
    private static Dictionary<string, IAggregateWireMapper> MappersOf(params IGameModule[] modules)
    {
        var services = new ServiceCollection();
        foreach (var module in modules)
        {
            module.ConfigureServices(services, new ModuleContext("test", "test", "Development"));
        }

        return services
            .Where(d => d.ServiceType == typeof(IAggregateWireMapper))
            .Select(d => (IAggregateWireMapper)d.ImplementationInstance!)
            .ToDictionary(m => m.AggregateId, StringComparer.Ordinal);
    }

    /// <summary>Declares the probe card (state <see cref="ProbeCard"/>) but maps it from <see cref="CounterState"/>.</summary>
    private sealed class MistypedMapperModule : IGameModule
    {
        public ModuleDescriptor Describe() => new() { Name = "probe.mistyped", Aggregates = [CatalogProbeModule.CardDescriptor] };

        public void ConfigureServices(IServiceCollection services, ModuleContext context) =>
            services.AddAggregateWireMapper<CounterState, Wire.CounterState>(CatalogProbeModule.Card, ToWire);
    }
}
