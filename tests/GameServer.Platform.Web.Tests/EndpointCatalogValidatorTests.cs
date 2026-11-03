using GameServer.Actors;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Routing;
using GameServer.Platform.Web.Validation;
using GameServer.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FR-010, FR-011, FR-014: mapped endpoints ↔ catalog, one line per fault (research R7, data-model §2.2).</summary>
public sealed class EndpointCatalogValidatorTests
{
    private static readonly RouteDeclaration Save = RouteDeclaration.Post("probe.save", "/api/v1/probe/save", SecurityProfile.Player);
    private static readonly RouteDeclaration Item = RouteDeclaration.Get("probe.item", "/api/v1/probe/items/{id}", SecurityProfile.Player);
    private static readonly RouteDeclaration Edit = new("probe.edit", ["POST"], "/gm/v1/probe/{playerId}", SecurityProfile.Gm, RouteSurface.GmApi, "probe.edit", "probe.write");
    private static readonly RouteDeclaration Live = RouteDeclaration.AnyMethod("probe.live", "/health/live", SecurityProfile.Public, RouteSurface.Host);

    private static readonly EndpointCheckContext AllMapped = new(true, true, new HashSet<string>(), _ => true);

    private static WireCatalog Catalog(params RouteDeclaration[] routes) => Catalog(routes.Select(r => new RouteEntry(r, "probe", CatalogOrigin.Game, false)));

    private static WireCatalog Catalog(IEnumerable<RouteEntry> entries) =>
        new(entries, [], [], [], [], new Dictionary<string, IAggregateWireContract>());

    private static RouteEndpoint Endpoint(string template, string[]? methods, params object[] metadata)
    {
        var items = new List<object>(metadata);
        if (methods is not null)
        {
            items.Add(new HttpMethodMetadata(methods));
        }

        return new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0, new EndpointMetadataCollection(items), template);
    }

    private static RouteEndpoint Player(string template, params string[] methods) =>
        Endpoint(template, methods, new SecurityProfileMetadata(SecurityProfile.Player));

    private static IReadOnlyList<string> Check(WireCatalog catalog, bool includeUnmapped, params Endpoint[] endpoints) =>
        EndpointCatalogValidator.Validate(endpoints, catalog, AllMapped, includeUnmapped);

    [Fact]
    public void Matching_endpoints_have_no_line()
    {
        var catalog = Catalog(Save, Item, Edit, Live);

        Check(
                catalog,
                true,
                Player("/api/v1/probe/save", "POST"),
                Player("api/v1/probe/items/{id}", "GET"),
                Endpoint("/gm/v1/probe/{playerId}", ["POST"], new SecurityProfileMetadata(SecurityProfile.Gm), new RequiredPermissionMetadata("probe.write"), new CommandOperationMetadata("probe.edit")),
                Endpoint("/health/live", null, new SecurityProfileMetadata(SecurityProfile.Public)))
            .ShouldBeEmpty();
    }

    [Fact]
    public void An_undeclared_endpoint_is_one_line()
    {
        Check(Catalog(Save), false, Player("/api/v1/probe/save", "POST"), Player("/api/v1/probe/other", "POST")).ShouldBe(
        [
            "[endpoint POST /api/v1/probe/other] is not declared in the route catalog. Fix: declare it in the module's ModuleDescriptor.Routes " +
            "(or GameServerBuilder.DeclareRoutes for a host endpoint) and map it from the declaration (FG-026).",
        ]);
    }

    [Fact]
    public void A_route_group_prefix_on_a_bound_endpoint_is_one_template_line()
    {
        var grouped = Endpoint("/g/api/v1/probe/save", ["POST"], new SecurityProfileMetadata(SecurityProfile.Player), new RouteDeclarationMetadata(Save));

        Check(Catalog(Save), true, grouped).ShouldBe(
        [
            "[route probe.save] declared at /api/v1/probe/save but mapped at /g/api/v1/probe/save. " +
            "Fix: map it with the declaration and no route group prefix, or correct the declaration (FG-026).",
        ]);
    }

    [Fact]
    public void A_method_the_template_does_not_declare_is_one_line()
    {
        Check(Catalog(Item), false, Player("/api/v1/probe/items/{id}", "DELETE"), Player("/api/v1/probe/items/{id}", "GET")).ShouldBe(
        [
            "[endpoint DELETE /api/v1/probe/items/{id}] maps method DELETE but route probe.item declares GET. " +
            "Fix: declare one route per method or correct the mapping (FG-026).",
        ]);
    }

    [Fact]
    public void A_bound_endpoint_with_other_methods_is_one_line()
    {
        var both = Endpoint("/api/v1/probe/items/{id}", ["GET", "HEAD"], new SecurityProfileMetadata(SecurityProfile.Player), new RouteDeclarationMetadata(Item));

        Check(Catalog(Item), true, both).ShouldBe(
        [
            "[endpoint GET,HEAD /api/v1/probe/items/{id}] maps method GET,HEAD but route probe.item declares GET. " +
            "Fix: declare one route per method or correct the mapping (FG-026).",
        ]);
    }

    [Fact]
    public void A_profile_difference_is_one_line()
    {
        var gm = Endpoint("/api/v1/probe/save", ["POST"], new SecurityProfileMetadata(SecurityProfile.Gm), new RequiredPermissionMetadata("x"));

        Check(Catalog(Save), false, gm).ShouldBe(
        [
            "[endpoint POST /api/v1/probe/save] has profile Gm but route probe.save declares Player. " +
            "Fix: use the As*Endpoint helper of the declared profile or correct the declaration (SEC-08).",
        ]);
    }

    [Fact]
    public void Zero_or_several_profiles_are_left_to_the_security_validator()
    {
        Check(Catalog(Save), false, Endpoint("/api/v1/probe/save", ["POST"])).ShouldBeEmpty();
        Check(
                Catalog(Save),
                false,
                Endpoint("/api/v1/probe/save", ["POST"], new SecurityProfileMetadata(SecurityProfile.Player), new SecurityProfileMetadata(SecurityProfile.Dev)))
            .ShouldBeEmpty();
    }

    [Fact]
    public void An_operation_difference_is_one_line()
    {
        var command = Endpoint("/api/v1/probe/save", ["POST"], new SecurityProfileMetadata(SecurityProfile.Player), new CommandOperationMetadata("probe.other"));

        Check(Catalog(Save), false, command).ShouldBe(
        [
            "[endpoint POST /api/v1/probe/save] runs command 'probe.other' but route probe.save declares ''. " +
            "Fix: map it with MapPlayerCommand/MapDevCommand/MapGmCommand(declaration, …) (ARC-07).",
        ]);
    }

    [Fact]
    public void A_permission_difference_is_one_line()
    {
        var gm = Endpoint(
            "/gm/v1/probe/{playerId}",
            ["POST"],
            new SecurityProfileMetadata(SecurityProfile.Gm),
            new RequiredPermissionMetadata("probe.read"),
            new CommandOperationMetadata("probe.edit"));

        Check(Catalog(Edit), false, gm).ShouldBe(
        [
            "[endpoint POST /gm/v1/probe/{playerId}] requires permission 'probe.read' but route probe.edit declares 'probe.write'. " +
            "Fix: map it with the declaration (SEC-07).",
        ]);
    }

    [Fact]
    public void A_declared_route_that_is_not_mapped_is_one_line_only_when_unmapped_is_checked()
    {
        var catalog = Catalog(Save, Item);
        var endpoints = new Endpoint[] { Player("/api/v1/probe/save", "POST") };

        EndpointCatalogValidator.Validate(endpoints, catalog, AllMapped, includeUnmapped: false).ShouldBeEmpty();
        EndpointCatalogValidator.Validate(endpoints, catalog, AllMapped, includeUnmapped: true).ShouldBe(
        [
            "[route probe.item] (GET /api/v1/probe/items/{id}, owner probe, surface PlayerApi) is declared but not mapped. " +
            "Fix: map it in the module's MapPlayerApi method or remove the declaration (FG-026).",
        ]);
    }

    [Fact]
    public void A_health_probe_without_method_metadata_matches_an_any_method_declaration()
    {
        Check(Catalog(Live), true, Endpoint("/health/live", null, new SecurityProfileMetadata(SecurityProfile.Public))).ShouldBeEmpty();
    }

    [Fact]
    public void One_declaration_with_two_methods_and_two_single_method_declarations_both_match()
    {
        var both = new RouteDeclaration("probe.read", ["GET", "HEAD"], "/api/v1/probe/read", SecurityProfile.Player, RouteSurface.PlayerApi);
        var get = RouteDeclaration.Get("probe.get", "/api/v1/probe/split", SecurityProfile.Player);
        var head = new RouteDeclaration("probe.head", ["HEAD"], "/api/v1/probe/split", SecurityProfile.Player, RouteSurface.PlayerApi);

        Check(Catalog(both, get, head), true, Player("/api/v1/probe/read", "HEAD", "GET"), Player("/api/v1/probe/split", "GET", "HEAD"))
            .ShouldBeEmpty();
    }

    [Fact]
    public void A_typo_in_a_plain_string_is_not_declared_plus_declared_but_not_mapped()
    {
        var lines = Check(Catalog(Save), true, Player("/api/v1/probe/svae", "POST"));

        lines.Count.ShouldBe(2);
        lines[0].ShouldStartWith("[endpoint POST /api/v1/probe/svae] is not declared in the route catalog.");
        lines[1].ShouldStartWith("[route probe.save] (POST /api/v1/probe/save, owner probe, surface PlayerApi) is declared but not mapped.");
    }

    [Fact]
    public void Unmapped_surfaces_service_only_owners_and_conditions_that_do_not_hold_are_not_reported()
    {
        var gmRoute = RouteDeclaration.Get("probe.gm_read", "/gm/v1/probe/read", SecurityProfile.Gm, RouteSurface.GmApi, "probe.read");
        var conditional = RouteDeclaration.Post("probe.flag", "/api/v1/probe/flag", SecurityProfile.Player, condition: "probe.flag_on");
        var quiet = RouteDeclaration.Post("probe.quiet", "/api/v1/probe/quiet", SecurityProfile.Player);
        var hostOnly = RouteDeclaration.Get("probe.host", "/v1/probe/host", SecurityProfile.Public, RouteSurface.Host);
        var catalog = Catalog(
        [
            new RouteEntry(Save, "probe", CatalogOrigin.Game, false),
            new RouteEntry(gmRoute, "probe", CatalogOrigin.Game, false),
            new RouteEntry(conditional, "probe", CatalogOrigin.Game, false),
            new RouteEntry(quiet, "probe.quiet", CatalogOrigin.Game, true),
            new RouteEntry(hostOnly, "probe.listed", CatalogOrigin.Game, false),
        ]);
        var context = new EndpointCheckContext(false, false, new HashSet<string> { "probe.listed" }, condition => condition != "probe.flag_on");

        EndpointCatalogValidator.Validate([], catalog, context, includeUnmapped: true).ShouldBeEmpty();
    }

    [Fact]
    public void Templates_compare_after_leading_slash_normalization_and_case_sensitively()
    {
        Check(Catalog(Save), true, Player("api/v1/probe/save", "POST")).ShouldBeEmpty();

        var lines = Check(Catalog(Save), true, Player("/API/v1/probe/save", "POST"));
        lines.Count.ShouldBe(2);
        lines[0].ShouldStartWith("[endpoint POST /API/v1/probe/save] is not declared");
    }

    [Fact]
    public void Non_route_endpoints_are_ignored()
    {
        var plain = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "not a route");

        Check(Catalog(Save), false, plain).ShouldBeEmpty();
    }
}
