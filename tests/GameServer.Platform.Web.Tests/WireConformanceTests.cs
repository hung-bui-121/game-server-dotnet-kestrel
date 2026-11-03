using GameServer.Aggregates;
using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Modules.Identity;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter;
using Sample.Counter.Adapters;
using SampleGame;
using SampleGame.Adapters;
using Shouldly;
using Template.Module.Adapters;
using CounterWire = Sample.Counter.Protocol.V1;
using EconomyWire = GameServer.Modules.Economy.Protocol.V1;
using SampleWire = SampleGame.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>State of a conformance probe with a member the counter message has no field for.</summary>
public sealed record ExtraCounterState
{
    public long Value { get; init; }

    public int Increments { get; init; }

    public string Extra { get; init; } = string.Empty;
}

/// <summary>State of a conformance probe with a secret and a date.</summary>
public sealed record SecretState
{
    public string Secret { get; init; } = string.Empty;
}

public sealed record DatedState
{
    public DateTimeOffset At { get; init; }
}

public sealed record LooseState
{
    public long Value { get; init; }

    public int Increments { get; init; }

    public object? Anything { get; init; }
}

/// <summary>A module with one client-visible aggregate and the given typed mapper (conformance probes of slice 014 T067).</summary>
public sealed class ConformanceProbeModule<TState, TWire>(string id, Func<TState> createDefault, Func<TState, TWire> map, Action<WireMapperOptions>? configure = null)
    : IGameModule
    where TState : class
    where TWire : class, Google.Protobuf.IMessage<TWire>, new()
{
    public ModuleDescriptor Describe() => new()
    {
        Name = "probe." + id,
        Aggregates = [new PlayerAggregateDescriptor<TState>(id, 1, clientVisible: true, createDefault)],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context) =>
        services.AddAggregateWireMapper(id, map, configure);
}

/// <summary>FG-027 (slice 014 T066, T067): wire-mapper conformance of the framework, sample and template mappers, and the kit's behaviour.</summary>
public sealed class WireConformanceTests
{
    private const string Probe = "probe_state";

    private static readonly IGameModule[] FrameworkSampleAndTemplate =
        [new EconomyModule(), new IdentityModule(), new SampleGameModule(), new CounterModule(), new TemplateModule()];

    // T066: the framework, sample and template mappers pass with their hidden declarations (profile: Ban, Deleted, Identities;
    // battle: the captured rewards of the open battle). No may-stay-default field is needed.
    [Fact]
    public void Framework_sample_and_template_mappers_pass_conformance()
    {
        var report = WireConformance.CheckModules(FrameworkSampleAndTemplate);

        report.CheckedAggregates.ShouldBe(["battle", "counter", "inventory", "profile", "progression", "template", "wallet"]);
        report.ThrowIfFailed();
        report.Passed.ShouldBeTrue();
    }

    [Fact]
    public void Check_all_covers_every_client_visible_aggregate_of_a_composition_without_naming_them()
    {
        using var app = SampleTestHost.BuildApp(b =>
        {
            GameHttp.FullGame(b);
            b.AddModule<TemplateModule>();
        });

        var report = WireConformance.CheckAll(app.Services);

        report.CheckedAggregates.ShouldBe(["battle", "counter", "inventory", "profile", "progression", "template", "wallet"]);
        report.ThrowIfFailed();
    }

    [Fact]
    public void Check_one_aggregate_of_a_module()
    {
        WireConformance.Check(new IdentityModule(), IdentityAggregates.Profile).CheckedAggregates.ShouldBe([IdentityAggregates.Profile]);
        Should.Throw<ArgumentException>(() => WireConformance.Check(new IdentityModule(), "nope")).Message.ShouldContain("'nope'");
    }

    // T067 mutation set (FR-064, data-model §7.4).
    [Fact]
    public void An_unmapped_member_is_not_on_the_wire()
    {
        var report = Check(new ConformanceProbeModule<ExtraCounterState, CounterWire.CounterState>(
            Probe, () => new ExtraCounterState(), s => new CounterWire.CounterState { Value = s.Value, Increments = s.Increments }));

        Problems(report).ShouldBe([(WireConformanceProblem.MemberNotOnWire, "Extra")]);
    }

    [Fact]
    public void An_unset_proto_field_is_at_default_and_its_member_not_on_the_wire()
    {
        var report = Check(new ConformanceProbeModule<CounterState, CounterWire.CounterState>(
            Probe, () => new CounterState(), s => new CounterWire.CounterState { Value = s.Value }));

        Problems(report).ShouldBe([(WireConformanceProblem.FieldAtDefault, "increments"), (WireConformanceProblem.MemberNotOnWire, "Increments")]);
    }

    [Fact]
    public void A_nested_unmapped_member_is_named_by_its_path()
    {
        var report = Check(new ConformanceProbeModule<BattleState, SampleWire.BattleState>(
            Probe,
            () => new BattleState(),
            s =>
            {
                var wire = new SampleWire.BattleState { Stats = new SampleWire.BattleStats { Started = s.Stats.Started, Won = s.Stats.Won, Lost = s.Stats.Lost, Abandoned = s.Stats.Abandoned, Expired = s.Stats.Expired } };
                if (s.ActiveBattle is { } active)
                {
                    wire.ActiveBattle = new SampleWire.ActiveBattle
                    {
                        BattleId = active.BattleId,
                        Stage = active.Stage,
                        StartedAtUtcMs = active.StartedAtUtc.ToUnixTimeMilliseconds(),
                        ExpiresAtUtcMs = active.ExpiresAtUtc.ToUnixTimeMilliseconds(),
                    };
                }

                return wire;
            },
            o => o.Hidden("ActiveBattle.ConfigVersion", "ActiveBattle.Xp", "ActiveBattle.Rewards", "ActiveBattle.Drops")));

        Problems(report).ShouldBe([(WireConformanceProblem.FieldAtDefault, "active_battle.seed"), (WireConformanceProblem.MemberNotOnWire, "ActiveBattle.Seed")]);
    }

    [Fact]
    public void A_stale_hidden_declaration_of_a_mapped_member_is_reported()
    {
        var report = Check(new ConformanceProbeModule<CounterState, CounterWire.CounterState>(
            Probe, () => new CounterState(), s => new CounterWire.CounterState { Value = s.Value, Increments = s.Increments }, o => o.Hidden("Increments")));

        Problems(report).ShouldBe([(WireConformanceProblem.HiddenMemberOnWire, "Increments")]);
    }

    [Fact]
    public void A_hidden_member_reaching_the_wire_is_reported()
    {
        var report = Check(new ConformanceProbeModule<SecretState, StringValue>(
            Probe, () => new SecretState(), s => new StringValue { Value = "secret:" + s.Secret }, o => o.Hidden(nameof(SecretState.Secret))));

        Problems(report).ShouldBe([(WireConformanceProblem.HiddenMemberOnWire, "Secret")]);
    }

    [Fact]
    public void A_collection_the_mapper_leaves_empty_is_at_default_and_its_elements_not_on_the_wire()
    {
        var report = Check(new ConformanceProbeModule<WalletState, EconomyWire.WalletState>(Probe, () => new WalletState(), _ => new EconomyWire.WalletState()));

        Problems(report).ShouldBe([(WireConformanceProblem.FieldAtDefault, "balances"), (WireConformanceProblem.MemberNotOnWire, "Balances")]);
    }

    [Fact]
    public void A_dictionary_key_the_mapper_drops_is_not_on_the_wire()
    {
        var report = Check(new ConformanceProbeModule<WalletState, EconomyWire.WalletState>(
            Probe, () => new WalletState(), s => new EconomyWire.WalletState { Balances = { ["fixed"] = s.Balances.Values.Sum() } }));

        Problems(report).ShouldBe([(WireConformanceProblem.MemberNotOnWire, "Balances")]);
    }

    [Fact]
    public void An_object_member_is_not_fillable_unless_a_value_is_supplied()
    {
        var module = new ConformanceProbeModule<LooseState, CounterWire.CounterState>(
            Probe, () => new LooseState(), s => new CounterWire.CounterState { Value = s.Value, Increments = s.Increments }, o => o.Hidden(nameof(LooseState.Anything)));

        var report = Check(module);
        Problems(report).ShouldBe([(WireConformanceProblem.MemberNotFillable, "Anything")]);
        report.Findings.Single().Message.ShouldContain("WithMemberValue(\"probe_state\", \"Anything\", value)");

        var supplied = WireConformance.CheckModules([module], new WireConformanceOptions().WithMemberValue(Probe, nameof(LooseState.Anything), "any"));
        supplied.ThrowIfFailed();
        supplied.ToString().ShouldContain("[aggregate probe_state] note: Anything uses the supplied value and is not perturbed.");
    }

    [Fact]
    public void A_supplied_sample_state_is_used_and_still_perturbed()
    {
        var seen = new List<CounterState>();
        var module = new ConformanceProbeModule<CounterState, CounterWire.CounterState>(Probe, () => new CounterState(), s =>
        {
            seen.Add(s);
            return new CounterWire.CounterState { Value = s.Value };
        });

        var report = WireConformance.CheckModules([module], new WireConformanceOptions().WithSampleState(Probe, new CounterState { Value = 100, Increments = 7 }));

        seen[0].ShouldBe(new CounterState { Value = 100, Increments = 7 });
        seen.ShouldContain(new CounterState { Value = 101, Increments = 7 });
        seen.ShouldContain(new CounterState { Value = 100, Increments = 8 });
        Problems(report).ShouldBe([(WireConformanceProblem.FieldAtDefault, "increments"), (WireConformanceProblem.MemberNotOnWire, "Increments")]);
        Should.Throw<ArgumentException>(() => WireConformance.CheckModules([module], new WireConformanceOptions().WithSampleState(Probe, new WalletState())));
    }

    [Fact]
    public void A_date_mapped_to_utc_milliseconds_is_on_the_wire()
    {
        Check(new ConformanceProbeModule<DatedState, Int64Value>(Probe, () => new DatedState(), s => new Int64Value { Value = s.At.ToUnixTimeMilliseconds() }))
            .ThrowIfFailed();
    }

    [Fact]
    public void A_may_stay_default_field_is_not_reported()
    {
        var report = Check(new ConformanceProbeModule<CounterState, CounterWire.CounterState>(
            Probe, () => new CounterState(), s => new CounterWire.CounterState { Value = s.Value }, o => o.MayStayDefault("increments").Hidden("Increments")));

        report.ThrowIfFailed();
    }

    [Fact]
    public void Untyped_and_missing_mappers_are_findings()
    {
        var report = WireConformance.CheckModules([new UntypedMapperProbeModule(), new CatalogProbeModule(name: "probe.nomapper", card: false), new NoMapperModule()]);

        Problems(report).ShouldBe([(WireConformanceProblem.NoMapper, string.Empty), (WireConformanceProblem.UntypedMapper, string.Empty)]);
        report.Findings.Select(f => f.AggregateId).ShouldBe([NoMapperModule.Id, UntypedMapperProbeModule.Legacy]);
    }

    [Fact]
    public void Throw_if_failed_lists_every_line()
    {
        var report = Check(new ConformanceProbeModule<ExtraCounterState, CounterWire.CounterState>(Probe, () => new ExtraCounterState(), s => new CounterWire.CounterState { Value = s.Value }));

        var ex = Should.Throw<WireConformanceException>(report.ThrowIfFailed);

        ex.Report.ShouldBeSameAs(report);
        report.Findings.Count.ShouldBe(3);
        foreach (var finding in report.Findings)
        {
            ex.Message.ShouldContain(finding.ToString());
        }

        ex.Message.ShouldContain(
            "[aggregate probe_state] MemberNotOnWire: Extra — changing this member does not change the wire message. Fix: map it, or declare " +
            "o.Hidden(\"Extra\") when it stays on the server (FG-027).");
    }

    // Startup lines of data-model §2.1 (the kit needs valid declarations; the host refuses invalid ones).
    [Fact]
    public void An_unknown_hidden_path_fails_the_host_start()
    {
        using var app = SampleTestHost.BuildApp(b =>
        {
            SampleTestHost.DefaultModules(b);
            b.AddModule(new CatalogProbeModule(cardMapper: o => o.Hidden("Note", "Nope")));
        });

        Should.Throw<GameServerStartupException>(() => app.UseGameServer()).Violations.ShouldContain(
            "[aggregate probe_card] hidden member 'Nope' does not exist on ProbeCard. Fix: use C# member names joined by '.' (FG-027).");
    }

    [Fact]
    public void An_unknown_may_stay_default_field_fails_the_host_start()
    {
        using var app = SampleTestHost.BuildApp(b =>
        {
            SampleTestHost.DefaultModules(b);
            b.AddModule(new CatalogProbeModule(cardMapper: o => o.Hidden("Note").MayStayDefault("nope")));
        });

        Should.Throw<GameServerStartupException>(() => app.UseGameServer()).Violations.ShouldContain(
            "[aggregate probe_card] field 'nope' does not exist on sample.counter.v1.CounterState. Fix: use proto field names joined by '.' (FG-027).");
    }

    private static WireConformanceReport Check(IGameModule module) => WireConformance.CheckModules([module]);

    private static (WireConformanceProblem, string)[] Problems(WireConformanceReport report) =>
        report.Findings.Select(f => (f.Problem, f.Path)).ToArray();

    /// <summary>A client-visible aggregate without any wire mapper.</summary>
    private sealed class NoMapperModule : IGameModule
    {
        public const string Id = "probe_bare";

        public ModuleDescriptor Describe() => new()
        {
            Name = "probe.bare",
            Aggregates = [new PlayerAggregateDescriptor<CounterState>(Id, 1, clientVisible: true, () => new CounterState())],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }
}
