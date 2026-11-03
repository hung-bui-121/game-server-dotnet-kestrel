using GameServer.Actors;
using GameServer.Modules.Identity;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Hosting;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sample.Counter;
using Sample.Counter.Adapters;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FG-026 (1), FG-028: the host's route and section catalog (research R5, data-model §1–§3).</summary>
public sealed class WireCatalogTests
{
    private static readonly RouteDeclaration ProbeSave = RouteDeclaration.Post("probe.save", "/api/v1/probe/save", SecurityProfile.Player);
    private static readonly RouteDeclaration ProbeAny = RouteDeclaration.AnyMethod("probe.any", "/v1/probe/any", SecurityProfile.Public, RouteSurface.Host);
    // Not a literal: the route analyzer (ASP0017) would refuse the deliberately broken template at compile time.
    private static readonly string BrokenTemplate = "/api/v1/{oops";

    private static readonly RouteDeclaration HostHook = RouteDeclaration.Post("host.hook", "/v1/hooks/store", SecurityProfile.Public, RouteSurface.Host);

    private static WireCatalog Catalog(Action<GameServerBuilder> modules, Action<IServiceCollection>? services = null)
    {
        using var app = SampleTestHost.BuildApp(modules, services: services);
        return app.Services.GetRequiredService<WireCatalog>();
    }

    private static void ProbeComposition(GameServerBuilder gs)
    {
        SampleTestHost.DefaultModules(gs);
        gs.AddModule(new CatalogProbeModule(routes: [ProbeSave, ProbeAny]));
        gs.AddModule(new CatalogProbeModule(name: "probe.quiet", routes: [RouteDeclaration.Post("probe.quiet", "/api/v1/probe/quiet", SecurityProfile.Player)], card: false), o => o.ServiceOnly());
        gs.DeclareRoutes(HostHook);
    }

    [Fact]
    public void Entries_carry_owner_origin_and_service_only_flag()
    {
        var catalog = Catalog(ProbeComposition);

        catalog.TryGetRoute("probe.save", out var save).ShouldBeTrue();
        save!.Declaration.ShouldBeSameAs(ProbeSave);
        save.Owner.ShouldBe(CatalogProbeModule.DefaultName);
        save.ServiceOnly.ShouldBeFalse();

        // The origin rule is the assembly prefix "GameServer." (reserved): this test assembly is GameServer.Platform.Web.Tests.
        save.Origin.ShouldBe(CatalogOrigin.Framework);

        catalog.TryGetRoute("probe.quiet", out var quiet).ShouldBeTrue();
        quiet!.ServiceOnly.ShouldBeTrue();

        catalog.TryGetRoute("host.hook", out var hook).ShouldBeTrue();
        hook!.Owner.ShouldBe(RouteEntry.HostOwner);
        hook.Origin.ShouldBe(CatalogOrigin.Game);
    }

    [Fact]
    public void Platform_entries_are_framework_entries_owned_by_the_platform()
    {
        var catalog = Catalog(ProbeComposition);

        foreach (var route in PlatformRoutes.All)
        {
            catalog.TryGetRoute(route.Name, out var entry).ShouldBeTrue(route.Name);
            entry!.Owner.ShouldBe(RouteEntry.PlatformOwner);
            entry.Origin.ShouldBe(CatalogOrigin.Framework);
        }
    }

    [Fact]
    public void Routes_are_ordinal_by_name()
    {
        var names = Catalog(ProbeComposition).Routes.Select(r => r.Declaration.Name).ToArray();

        names.ShouldBe(names.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Sections_are_client_visible_aggregates_with_a_mapper()
    {
        var catalog = Catalog(ProbeComposition);

        catalog.Sections.Select(s => s.Id).ShouldBe([CounterAggregates.Counter, CatalogProbeModule.Card, IdentityAggregates.Profile]);
        catalog.TryGetSection(CatalogProbeModule.Card, out var card).ShouldBeTrue();
        card!.Owner.ShouldBe(CatalogProbeModule.DefaultName);
        card.MessageFullName.ShouldBe("sample.counter.v1.CounterState");
        card.HiddenMembers.ShouldBe([nameof(ProbeCard.Note)]);
        card.MayStayDefaultFields.ShouldBeEmpty();
    }

    [Fact]
    public void A_section_of_an_untyped_mapper_has_no_message()
    {
        var catalog = Catalog(ProbeComposition);

        catalog.TryGetSection(CounterAggregates.Counter, out var counter).ShouldBeTrue();
        counter!.MessageFullName.ShouldBeNull();
        counter.Owner.ShouldBe("sample.counter");
        counter.Origin.ShouldBe(CatalogOrigin.Game);

        catalog.TryGetSection(IdentityAggregates.Profile, out var profile).ShouldBeTrue();
        profile!.Owner.ShouldBe(IdentityModule.ModuleName);
        profile.Origin.ShouldBe(CatalogOrigin.Framework);
    }

    [Fact]
    public void Find_route_name_matches_method_and_canonical_template()
    {
        var catalog = Catalog(ProbeComposition);

        catalog.FindRouteName("POST", "/api/v1/probe/save").ShouldBe("probe.save");
        catalog.FindRouteName("post", "api/v1/probe/save").ShouldBe("probe.save");
        catalog.FindRouteName("GET", "/api/v1/probe/save").ShouldBeNull();
        catalog.FindRouteName("DELETE", "/v1/probe/any").ShouldBe("probe.any");
        catalog.FindRouteName("GET", PlatformRoutes.Time.Template).ShouldBe(PlatformRoutes.Time.Name);
        catalog.FindRouteName("GET", "/v1/unknown").ShouldBeNull();
    }

    [Fact]
    public async Task Resolving_the_catalog_starts_nothing_and_never_touches_the_store()
    {
        var probe = new StartProbe();
        var storeBuilt = false;
        await using var app = SampleTestHost.BuildApp(ProbeComposition, services: services =>
        {
            services.AddSingleton<IHostedService>(probe);
            services.AddSingleton<ITransactionalDocumentStore>(_ =>
            {
                storeBuilt = true;
                throw new InvalidOperationException("the catalog must not open the store");
            });
        });

        app.Services.GetRequiredService<WireCatalog>().Routes.ShouldNotBeEmpty();

        probe.Started.ShouldBeFalse();
        storeBuilt.ShouldBeFalse();
    }

    private sealed class StartProbe : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Validation (T015, data-model §2.1): each fault seeded once in an otherwise valid composition yields exactly one line.
    private static IReadOnlyList<string> Lines(Action<GameServerBuilder> extra, Action<IServiceCollection>? services = null) =>
        Catalog(
                gs =>
                {
                    SampleTestHost.DefaultModules(gs);
                    extra(gs);
                },
                services)
            .Validate();

    private static IReadOnlyList<string> LinesOfProbe(params RouteDeclaration[] routes) =>
        Lines(gs => gs.AddModule(new CatalogProbeModule(routes: routes)));

    [Fact]
    public void A_valid_composition_has_no_line()
    {
        Catalog(ProbeComposition).Validate().ShouldBeEmpty();
        Lines(_ => { }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Probe.save")]
    [InlineData("probe")]
    [InlineData("probe.save-now")]
    [InlineData("probe..save")]
    [InlineData("probe.a23456789012345678901234567890123456789012345678901234567890")]
    public void A_bad_name_is_one_line(string name)
    {
        LinesOfProbe(RouteDeclaration.Post(name, "/api/v1/probe/save", SecurityProfile.Player)).ShouldBe(
        [
            $"[route {name}] (owner probe) name does not match ^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$ (≤ 64). Fix: use <area>.<route> in lower case (FG-026).",
        ]);
    }

    [Fact]
    public void A_name_of_64_characters_is_valid()
    {
        var name = "probe." + new string('a', 58);

        LinesOfProbe(RouteDeclaration.Post(name, "/api/v1/probe/save", SecurityProfile.Player)).ShouldBeEmpty();
    }

    [Fact]
    public void A_duplicate_name_names_both_owners()
    {
        Lines(gs =>
            {
                gs.AddModule(new CatalogProbeModule(routes: [RouteDeclaration.Post("probe.dup", "/api/v1/probe/a", SecurityProfile.Player)]));
                gs.DeclareRoutes(RouteDeclaration.Post("probe.dup", "/v1/hooks/b", SecurityProfile.Public, RouteSurface.Host));
            })
            .ShouldBe(["[route probe.dup] is declared by probe and host. Fix: give each route a unique name (FG-026)."]);
    }

    [Fact]
    public void A_duplicate_method_and_template_names_both_routes_and_owners()
    {
        Lines(gs =>
            {
                gs.AddModule(new CatalogProbeModule(routes: [RouteDeclaration.Post("probe.a", "/api/v1/probe/x", SecurityProfile.Player)]));
                gs.AddModule(new CatalogProbeModule(name: "probe.other", routes: [RouteDeclaration.Post("probe.b", "api/v1/probe/x", SecurityProfile.Player)], card: false));
            })
            .ShouldBe(["[route POST /api/v1/probe/x] is declared twice: probe.a (probe) and probe.b (probe.other). Fix: declare it once (ARC-07)."]);
    }

    [Fact]
    public void An_any_method_declaration_overlaps_every_method_of_its_template()
    {
        LinesOfProbe(
                RouteDeclaration.AnyMethod("probe.any", "/v1/probe/y", SecurityProfile.Public, RouteSurface.Host),
                RouteDeclaration.Get("probe.get", "/v1/probe/y", SecurityProfile.Public, RouteSurface.Host))
            .ShouldBe(["[route GET /v1/probe/y] is declared twice: probe.any (probe) and probe.get (probe). Fix: declare it once (ARC-07)."]);
    }

    [Fact]
    public void Two_methods_on_one_template_are_not_a_duplicate()
    {
        LinesOfProbe(
                RouteDeclaration.Get("probe.list", "/api/v1/probe/items", SecurityProfile.Player),
                RouteDeclaration.Post("probe.create", "/api/v1/probe/items", SecurityProfile.Player))
            .ShouldBeEmpty();
    }

    [Fact]
    public void An_invalid_template_is_one_line_with_the_parser_message()
    {
        var parser = Should.Throw<Microsoft.AspNetCore.Routing.Patterns.RoutePatternException>(
            () => Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(BrokenTemplate));

        LinesOfProbe(RouteDeclaration.Post("probe.bad", BrokenTemplate, SecurityProfile.Player)).ShouldBe(
        [
            $"[route probe.bad] template '/api/v1/{{oops' is not a valid route template: {parser.Message}. Fix: correct the declaration (FG-026).",
        ]);
    }

    [Fact]
    public void A_template_without_version_segment_is_one_line_except_health_probes()
    {
        LinesOfProbe(RouteDeclaration.Post("probe.old", "/api/probe/old", SecurityProfile.Player))
            .ShouldBe(["[route probe.old] template '/api/probe/old' has no version segment. Fix: prefix with /v{n}/ (API-02)."]);
        LinesOfProbe(RouteDeclaration.Get("probe.health", "/health/ready", SecurityProfile.Public, RouteSurface.Host)).ShouldBeEmpty();
    }

    [Fact]
    public void A_gm_route_without_permission_and_a_player_route_with_one_are_one_line_each()
    {
        LinesOfProbe(new RouteDeclaration("probe.gm", ["GET"], "/gm/v1/probe", SecurityProfile.Gm, RouteSurface.GmApi))
            .ShouldBe(["[route probe.gm] has profile Gm and permission ''. Fix: GM routes declare their permission, other routes none (SEC-07)."]);
        LinesOfProbe(RouteDeclaration.Post("probe.player", "/api/v1/probe/p", SecurityProfile.Player, permission: "probe.edit"))
            .ShouldBe(["[route probe.player] has profile Player and permission 'probe.edit'. Fix: GM routes declare their permission, other routes none (SEC-07)."]);
    }

    [Fact]
    public void An_unknown_condition_is_one_line_and_a_registered_one_none()
    {
        var route = RouteDeclaration.Post("probe.flag", "/api/v1/probe/flag", SecurityProfile.Player, condition: "probe.flag_on");

        LinesOfProbe(route).ShouldBe(
        [
            "[route probe.flag] uses hosting condition 'probe.flag_on' which is not registered. Fix: services.AddRouteCondition(\"probe.flag_on\", …) in the module (FG-026).",
        ]);
        Lines(gs => gs.AddModule(new CatalogProbeModule(routes: [route])), services => services.AddRouteCondition("probe.flag_on", _ => true)).ShouldBeEmpty();
    }

    [Fact]
    public void A_public_endpoint_declared_with_another_profile_is_one_line()
    {
        Lines(gs => gs.AddModule(new CatalogProbeModule(routes: [ProbeSave], publicEndpoints: [ProbeSave.Template])))
            .ShouldBe(
            [
                "[module probe] lists /api/v1/probe/save in PublicEndpoints but route probe.save declares profile Player. " +
                "Fix: declare it Public or remove it from PublicEndpoints (SEC-09).",
            ]);
    }

    [Fact]
    public void Two_wire_mappers_for_one_aggregate_are_one_line()
    {
        Lines(
                gs => gs.AddModule(new CatalogProbeModule()),
                services => services.AddAggregateWireMapper<ProbeCard, Sample.Counter.Protocol.V1.CounterState>(CatalogProbeModule.Card, CatalogProbeModule.ToWire))
            .ShouldBe(
            [
                "[aggregate probe_card] has 2 wire mappers. Fix: register one mapper per aggregate; replace a framework mapper with " +
                "ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(…)) (API-10).",
            ]);
    }

    [Fact]
    public void An_unknown_hidden_member_is_one_line()
    {
        Lines(gs => gs.AddModule(new CatalogProbeModule(cardMapper: o => o.Hidden("Note", "Missing.Path"))))
            .ShouldBe(["[aggregate probe_card] hidden member 'Missing.Path' does not exist on ProbeCard. Fix: use C# member names joined by '.' (FG-027)."]);
    }

    [Fact]
    public void An_unknown_may_stay_default_field_is_one_line()
    {
        Lines(gs => gs.AddModule(new CatalogProbeModule(cardMapper: o => o.MayStayDefault("value", "increments.nope"))))
            .ShouldBe(["[aggregate probe_card] field 'increments.nope' does not exist on sample.counter.v1.CounterState. Fix: use proto field names joined by '.' (FG-027)."]);
    }

    [Fact]
    public void An_untyped_mapper_is_a_warning_not_a_line()
    {
        var catalog = Catalog(ProbeComposition);

        catalog.Validate().ShouldBeEmpty();
        catalog.ValidationWarnings.ShouldContain(
            "[aggregate counter] wire mapper registered without its message type: the catalog has no message for this section and Unity gets no parser. " +
            "Fix: AddAggregateWireMapper<TState, TWire>(…) (FG-028).");
        catalog.ValidationWarnings.ShouldNotContain(w => w.StartsWith("[aggregate probe_card]", StringComparison.Ordinal));
    }

    // Export (T016, data-model §3, contracts/generated-files.md §1).
    private static WireCatalog ExampleCatalog() => new(
        [
            new RouteEntry(new RouteDeclaration("platform.receipt", ["GET"], "/api/v1/player/receipts/{receiptId}", SecurityProfile.Player, RouteSurface.PlayerApi), RouteEntry.PlatformOwner, CatalogOrigin.Framework, false),
            new RouteEntry(new RouteDeclaration("battle.end", ["POST"], "/api/v1/battle/end", SecurityProfile.Player, RouteSurface.PlayerApi, "battle.end"), "sample.game", CatalogOrigin.Game, false),
            new RouteEntry(new RouteDeclaration("platform.health_live", [], "/health/live", SecurityProfile.Public, RouteSurface.Host), "platform.observability", CatalogOrigin.Framework, false),
            new RouteEntry(new RouteDeclaration("cheat.grant", ["POST"], "/api/v1/cheat/grant", SecurityProfile.Dev, RouteSurface.PlayerApi, "cheat.grant"), "economy.cheat", CatalogOrigin.Framework, false),
            new RouteEntry(new RouteDeclaration("gm.sso_start", ["GET"], "/gm/v1/auth/sso/start", SecurityProfile.Public, RouteSurface.GmApi, condition: "gm.sso"), "gm", CatalogOrigin.Framework, false),
        ],
        [
            new SectionEntry("wallet", "economy", CatalogOrigin.Framework, "gameserver.economy.v1.WalletState", [], []),
            new SectionEntry("battle", "sample.game", CatalogOrigin.Game, "samplegame.v1.BattleState", [], []),
        ],
        [],
        [],
        [],
        new Dictionary<string, IAggregateWireContract>());

    [Fact]
    public void Export_matches_the_contract_example()
    {
        var expected = $$"""
            {
              "format": "gameserver.wire-contract/1",
              "frameworkVersion": "{{GameServerVersion.Framework}}",
              "protobufVersion": "3.36.2",
              "routes": [
                {
                  "name": "battle.end",
                  "methods": ["POST"],
                  "template": "/api/v1/battle/end",
                  "profile": "Player",
                  "surface": "PlayerApi",
                  "owner": "sample.game",
                  "origin": "game",
                  "operation": "battle.end"
                },
                {
                  "name": "cheat.grant",
                  "methods": ["POST"],
                  "template": "/api/v1/cheat/grant",
                  "profile": "Dev",
                  "surface": "PlayerApi",
                  "owner": "economy.cheat",
                  "origin": "framework",
                  "operation": "cheat.grant"
                },
                {
                  "name": "gm.sso_start",
                  "methods": ["GET"],
                  "template": "/gm/v1/auth/sso/start",
                  "profile": "Public",
                  "surface": "GmApi",
                  "owner": "gm",
                  "origin": "framework",
                  "condition": "gm.sso"
                },
                {
                  "name": "platform.health_live",
                  "template": "/health/live",
                  "profile": "Public",
                  "surface": "Host",
                  "owner": "platform.observability",
                  "origin": "framework"
                },
                {
                  "name": "platform.receipt",
                  "methods": ["GET"],
                  "template": "/api/v1/player/receipts/{receiptId}",
                  "profile": "Player",
                  "surface": "PlayerApi",
                  "owner": "platform",
                  "origin": "framework"
                }
              ],
              "sections": [
                {
                  "id": "battle",
                  "owner": "sample.game",
                  "origin": "game",
                  "message": "samplegame.v1.BattleState"
                },
                {
                  "id": "wallet",
                  "owner": "economy",
                  "origin": "framework",
                  "message": "gameserver.economy.v1.WalletState"
                }
              ]
            }

            """.ReplaceLineEndings("\n");

        ExampleCatalog().ToExportJson().ShouldBe(expected);
    }

    [Fact]
    public void Export_writes_permission_service_only_and_declared_lists()
    {
        var catalog = new WireCatalog(
            [new RouteEntry(new RouteDeclaration("economy.adjust", ["POST"], "/gm/v1/players/{playerId}/resources/adjust", SecurityProfile.Gm, RouteSurface.GmApi, "economy.adjust", "resource.adjust"), "economy", CatalogOrigin.Framework, true)],
            [new SectionEntry("card", "fixture", CatalogOrigin.Game, "fixture.v1.CardState", ["InternalNote", "Slots.UnlockEndUtc"], ["kind"])],
            [],
            [],
            [],
            new Dictionary<string, IAggregateWireContract>());

        var json = catalog.ToExportJson();

        json.ShouldContain("""
                  "operation": "economy.adjust",
                  "permission": "resource.adjust",
                  "serviceOnly": true
                }
            """.ReplaceLineEndings("\n"));
        json.ShouldContain("""
                  "message": "fixture.v1.CardState",
                  "hiddenMembers": ["InternalNote", "Slots.UnlockEndUtc"],
                  "mayStayDefault": ["kind"]
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Export_of_a_host_is_deterministic_lf_without_bom_and_without_machine_values()
    {
        var first = Catalog(ProbeComposition).ToExportJson();
        var second = Catalog(ProbeComposition).ToExportJson();

        second.ShouldBe(first);
        first.ShouldNotContain("\r");
        System.Text.Encoding.UTF8.GetBytes(first).Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF });
        first.ShouldNotContain(Environment.MachineName);
        first.ShouldNotContain(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar).Replace("\\", "\\\\", StringComparison.Ordinal));
        first.ShouldNotContain(DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        first.ShouldNotContain("\"environment");
        first.ShouldContain("\"hiddenMembers\": [\"Note\"]");
    }
}
