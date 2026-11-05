using System.ComponentModel.DataAnnotations;
using GameServer.Actors;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Routing;
using GameServer.Platform.Web.Validation;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 017 T037 (FR-045, D13, BC-8): every <c>[GmOptions]</c> on the body of a mapped GM endpoint names a GM GET route of the host;
/// otherwise one startup line per (endpoint, property), joining the one startup report.
/// </summary>
public sealed class GmOptionsCheckTests
{
    private const string Permission = "counter.read";

    public sealed record Nested([property: GmOptions("probe.unknown")] string? Pick);

    public sealed record OptionsBody(
        [property: GmOptions("probe.options")] string? Valid,
        [GmOptions("probe.unknown")] string? Unknown,
        [property: GmOptions("probe.post")] string? Post,
        [property: GmOptions("probe.player_get")] List<string>? Player,
        [property: GmOptions("probe.conditional")] string? Conditional,
        List<Nested>? Items);

    public sealed record ValidBody([property: GmOptions("probe.options")] string? Valid);

    private static readonly RouteDeclaration Options = RouteDeclaration.Get("probe.options", "/gm/v1/probe/options", SecurityProfile.Gm, RouteSurface.GmApi, Permission);
    private static readonly RouteDeclaration PostRoute = RouteDeclaration.Post("probe.post", "/gm/v1/probe/post", SecurityProfile.Gm, RouteSurface.GmApi, Permission);
    private static readonly RouteDeclaration PlayerGet = RouteDeclaration.Get("probe.player_get", "/api/v1/probe/get", SecurityProfile.Player);
    private static readonly RouteDeclaration Conditional =
        RouteDeclaration.Get("probe.conditional", "/gm/v1/probe/conditional", SecurityProfile.Gm, RouteSurface.GmApi, Permission, condition: "probe_off");
    private static readonly RouteDeclaration Body = RouteDeclaration.Post("probe.body", "/gm/v1/probe/body", SecurityProfile.Gm, RouteSurface.GmApi, Permission);

    [Fact]
    public async Task Each_bad_option_route_is_one_line_and_valid_ones_none()
    {
        await using var app = SampleTestHost.BuildApp(SampleTestHost.WithRoutes(Options, PostRoute, PlayerGet, Conditional, Body));
        app.MapDeclared(Body, () => Results.Ok()).Accepts<OptionsBody>(GmHttp.JsonContentType).AsGmEndpoint(Permission);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToArray();

        var lines = GmOptionsCheck.Validate(endpoints, app.Services.GetRequiredService<WireCatalog>(), condition => condition != "probe_off");

        lines.ShouldBe(
        [
            Line("unknown", "probe.unknown"),
            Line("post", "probe.post"),
            Line("player", "probe.player_get"),
            Line("conditional", "probe.conditional"),
            Line("items[].pick", "probe.unknown"),
        ]);
    }

    [Fact]
    public async Task Valid_option_routes_give_no_line()
    {
        await using var app = SampleTestHost.BuildApp(SampleTestHost.WithRoutes(Options, Body));
        app.MapDeclared(Body, () => Results.Ok()).Accepts<ValidBody>(GmHttp.JsonContentType).AsGmEndpoint(Permission);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToArray();

        GmOptionsCheck.Validate(endpoints, app.Services.GetRequiredService<WireCatalog>(), _ => true).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bad_option_route_joins_the_one_startup_report()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(
                SampleTestHost.WithRoutes(Body),
                beforeUseGameServer: app =>
                {
                    app.MapDeclared(Body, () => Results.Ok()).Accepts<ValidBody>(GmHttp.JsonContentType).AsGmEndpoint(Permission);
                    app.MapGet("/api/v1/probe/undeclared", () => Results.Ok()).AsPlayerEndpoint();
                });
        });

        ex.Violations.ShouldContain(
            "[endpoint POST /gm/v1/probe/body] body ValidBody property 'valid' names option route 'probe.options', which is not a GM GET route " +
            "of this host. Fix: declare the route (RouteDeclaration.Get(..., RouteSurface.GmApi)) or correct the name in [GmOptions] (FR-045).");
        ex.Violations.ShouldContain(v => v.StartsWith("[endpoint GET /api/v1/probe/undeclared] is not declared in the route catalog.", StringComparison.Ordinal));
    }

    public sealed record PermissionBody(
        [property: GmOptions("probe.options")] string? Same,
        [property: GmOptions("probe.access_options")] string? Access,
        [property: GmOptions("probe.other_options")] string? Other);

    public sealed record BrokenBody(
        [property: RegularExpression("[a-z")] string? BadRegex,
        [property: Range(typeof(DateTime), "not a date", "2030-01-01")] DateTime? BadRange,
        [property: StringLength(5)] string? Fine);

    private static readonly RouteDeclaration AccessOptions =
        RouteDeclaration.Get("probe.access_options", "/gm/v1/probe/access-options", SecurityProfile.Gm, RouteSurface.GmApi, "gm.access");
    private static readonly RouteDeclaration OtherOptions =
        RouteDeclaration.Get("probe.other_options", "/gm/v1/probe/other-options", SecurityProfile.Gm, RouteSurface.GmApi, "other.read");

    /// <summary>Review r1 m2: an option route needs gm.access or the permission of the endpoint whose body uses it.</summary>
    [Fact]
    public async Task An_option_route_with_another_permission_is_one_line()
    {
        await using var app = SampleTestHost.BuildApp(SampleTestHost.WithRoutes(Options, AccessOptions, OtherOptions, Body));
        app.MapDeclared(Body, () => Results.Ok()).Accepts<PermissionBody>(GmHttp.JsonContentType).AsGmEndpoint(Permission);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToArray();

        GmOptionsCheck.Validate(endpoints, app.Services.GetRequiredService<WireCatalog>(), _ => true).ShouldBe(
        [
            "[endpoint POST /gm/v1/probe/body] body PermissionBody property 'other' names option route 'probe.other_options', whose " +
            "permission 'other.read' is neither gm.access nor the endpoint's permission 'counter.read'. Fix: declare the option route " +
            "with 'counter.read' or gm.access (FR-045).",
        ]);
    }

    /// <summary>Review r1 m4: an attribute that cannot run (bad regex, unconvertible operand) fails startup, not the first request.</summary>
    [Fact]
    public async Task A_validation_attribute_that_cannot_run_is_one_line_each()
    {
        await using var app = SampleTestHost.BuildApp(SampleTestHost.WithRoutes(Body));
        app.MapDeclared(Body, () => Results.Ok()).Accepts<BrokenBody>(GmHttp.JsonContentType).AsGmEndpoint(Permission);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToArray();

        var lines = GmOptionsCheck.Validate(endpoints, app.Services.GetRequiredService<WireCatalog>(), _ => true);

        lines.Count.ShouldBe(2);
        lines[0].ShouldStartWith("[endpoint POST /gm/v1/probe/body] body BrokenBody property 'badRegex' has a RegularExpressionAttribute that cannot run (");
        lines[1].ShouldStartWith("[endpoint POST /gm/v1/probe/body] body BrokenBody property 'badRange' has a RangeAttribute that cannot run (");
        lines.ShouldAllBe(l => l.EndsWith("). Fix: correct the attribute's arguments (FR-021).", StringComparison.Ordinal));
    }

    /// <summary>A game attribute that refuses to run on null (legitimately: its property is never null when it matters).</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
    public sealed class NotNullAwareAttribute : ValidationAttribute
    {
        public override bool IsValid(object? value) => ((string)value!).Length > 0;
    }

    public sealed record CustomBody([property: NotNullAware] string? Code);

    /// <summary>Review r2 m1: only the built-in attributes are probed at startup; a game's own attribute never fails it.</summary>
    [Fact]
    public async Task A_custom_attribute_is_not_probed()
    {
        await using var app = SampleTestHost.BuildApp(SampleTestHost.WithRoutes(Body));
        app.MapDeclared(Body, () => Results.Ok()).Accepts<CustomBody>(GmHttp.JsonContentType).AsGmEndpoint(Permission);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToArray();

        GmOptionsCheck.Validate(endpoints, app.Services.GetRequiredService<WireCatalog>(), _ => true).ShouldBeEmpty();
    }

    private static string Line(string path, string route) =>
        $"[endpoint POST /gm/v1/probe/body] body OptionsBody property '{path}' names option route '{route}', which is not a GM GET route of this " +
        "host. Fix: declare the route (RouteDeclaration.Get(..., RouteSurface.GmApi)) or correct the name in [GmOptions] (FR-045).";
}
