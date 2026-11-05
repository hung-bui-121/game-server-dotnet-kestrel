using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using GameServer.Actors;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.OpenApi;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 017 GM document transformers on a host with test routes: enum representation (T011, FR-014, V12).
/// </summary>
public sealed class GmOpenApiTransformerTests
{
    public enum ProbeKind
    {
        FirstKind,
        SecondKind,
    }

    [Flags]
    public enum ProbeRights
    {
        None = 0,
        Read = 1,
        Write = 2,
    }

    public sealed record EnumBody(ProbeKind Kind, ProbeKind? Maybe, ProbeRights Rights);

    private static readonly RouteDeclaration EnumRoute = RouteDeclaration.Post("probe.enum", "/gm/v1/probe/enum", SecurityProfile.Gm, RouteSurface.GmApi, "counter.read");

    [Fact]
    public async Task Enums_are_described_as_the_camel_case_strings_of_the_gm_wire()
    {
        var document = await DocumentAsync(
            app => app.MapDeclared(EnumRoute, () => Results.Ok()).Accepts<EnumBody>(GmHttp.JsonContentType).AsGmEndpoint("counter.read"),
            EnumRoute);

        var kind = document["components"]!["schemas"]!["ProbeKind"]!;
        kind.ToJsonString().ShouldBe("""{"enum":["firstKind","secondKind"],"type":"string"}""");

        // Review r2 m2: a [Flags] enum serializes as comma lists ("read, write"): no closed list, the generator's schema stays.
        var rights = document["components"]!["schemas"]!["ProbeRights"]!;
        rights.AsObject().ContainsKey("enum").ShouldBeFalse();
        rights["type"]!.GetValue<string>().ShouldBe("integer");
    }

    /// <summary>Review r1 m1: the enum description is the schema transformer's; the host's HTTP JSON options are not changed.</summary>
    [Fact]
    public async Task The_host_http_json_options_keep_their_defaults()
    {
        await using var host = await SampleTestHost.StartAsync();

        var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value;

        options.SerializerOptions.Converters.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_only_runtime_user_of_the_http_json_options_keeps_its_output()
    {
        await using var host = await SampleTestHost.StartAsync();

        var text = await host.GmClient("support", CounterModuleDefinition.ReadPermission).GetStringAsync("/gm/v1/counter/p-1");

        text.ShouldBe("""{"playerId":"p-1","value":0,"rev":0}""");
    }

    private const string Permission = CounterModuleDefinition.ReadPermission;

    private static readonly RouteDeclaration Declared = RouteDeclaration.Get("probe.declared", "/gm/v1/probe/declared", SecurityProfile.Gm, RouteSurface.GmApi, Permission);
    private static readonly RouteDeclaration ByTemplate = RouteDeclaration.Post("probe.by_template", "/gm/v1/probe/template", SecurityProfile.Gm, RouteSurface.GmApi, Permission);
    private static readonly RouteDeclaration Tagged = RouteDeclaration.Get("probe.tagged", "/gm/v1/probe/tagged", SecurityProfile.Gm, RouteSurface.GmApi, Permission);
    private static readonly RouteDeclaration PlayerAction = RouteDeclaration.Put("probe.player_action", "/gm/v1/probe/players/{playerId}/x", SecurityProfile.Gm, RouteSurface.GmApi, Permission);
    private static readonly RouteDeclaration StaffTyped = RouteDeclaration.Post("probe.staff_typed", "/gm/v1/probe/staff/{staffId}/x", SecurityProfile.Gm, RouteSurface.GmApi, Permission);
    private static readonly RouteDeclaration Paged = RouteDeclaration.Get("probe.paged", "/gm/v1/probe/paged", SecurityProfile.Gm, RouteSurface.GmApi, Permission);

    /// <summary>T014 (FR-001..FR-009, D1–D4): ids, tags, parameters and x-gm-* keys of operations on a host with test routes.</summary>
    [Fact]
    public async Task Operations_carry_ids_tags_parameters_and_gm_keys()
    {
        var document = await DocumentAsync(
            app =>
            {
                app.MapDeclared(Declared, () => Results.Ok()).AsGmEndpoint(Permission);
                app.MapPost(ByTemplate.Template, () => Results.Ok()).AsGmEndpoint(Permission);
                app.MapDeclared(Tagged, () => Results.Ok()).WithTags("x").AsGmEndpoint(Permission);
                app.MapDeclared(PlayerAction, (string playerId) => Results.Ok()).AsGmEndpoint(Permission);
                app.MapDeclared(StaffTyped, () => Results.Ok()).WithGmConfirm(GmConfirm.Typed).WithGmRequestId().AsGmEndpoint(Permission);
                app.MapDeclared(Paged, () => Results.Ok()).ProducesGmPage<EnumBody>().WithGmQueryParameter("displayName", required: true, maxLength: 64, minLength: 1)
                    .AsGmEndpoint(Permission);
            },
            Declared,
            ByTemplate,
            Tagged,
            PlayerAction,
            StaffTyped,
            Paged);

        var declared = Operation(document, Declared);
        declared["operationId"]!.GetValue<string>().ShouldBe("probe.declared");
        Strings(declared["tags"]).ShouldBe(["probe"]);
        declared["x-gm-kind"]!.GetValue<string>().ShouldBe("query");
        declared["x-gm-target"]!.GetValue<string>().ShouldBe("global");
        declared["x-gm-confirm"].ShouldBeNull();
        declared["x-gm-paged"].ShouldBeNull();
        declared["parameters"].ShouldBeNull();

        var byTemplate = Operation(document, ByTemplate);
        byTemplate["operationId"]!.GetValue<string>().ShouldBe("probe.by_template");
        byTemplate["x-gm-kind"]!.GetValue<string>().ShouldBe("action");
        byTemplate["x-gm-confirm"]!.GetValue<string>().ShouldBe("simple");

        Strings(Operation(document, Tagged)["tags"]).ShouldBe(["x"]);

        var playerAction = Operation(document, PlayerAction);
        playerAction["x-gm-target"]!.GetValue<string>().ShouldBe("player");
        var playerId = playerAction["parameters"]!.AsArray().ShouldHaveSingleItem()!;
        playerId["name"]!.GetValue<string>().ShouldBe("playerId");
        playerId["in"]!.GetValue<string>().ShouldBe("path");
        playerId["required"]!.GetValue<bool>().ShouldBeTrue();
        playerId["schema"]!["pattern"]!.GetValue<string>().ShouldBe("^[A-Za-z0-9_-]{1,64}$");

        var staff = Operation(document, StaffTyped);
        staff["x-gm-confirm"]!.GetValue<string>().ShouldBe("typed");
        staff["x-gm-target"]!.GetValue<string>().ShouldBe("global");
        var parameters = staff["parameters"]!.AsArray();
        parameters.Select(p => $"{p!["in"]} {p["name"]} {p["required"]}").ShouldBe(["path staffId true", "header X-Request-Id true"]);
        parameters[0]!["schema"]!.ToJsonString().ShouldBe("""{"type":"string"}""");
        parameters[1]!["schema"]!.ToJsonString().ShouldBe("""{"type":"string","format":"uuid"}""");

        var paged = Operation(document, Paged);
        paged["x-gm-paged"]!.GetValue<bool>().ShouldBeTrue();
        var query = paged["parameters"]!.AsArray();
        query.Select(p => $"{p!["in"]} {p["name"]} {p["required"]?.GetValue<bool>() ?? false}").ShouldBe(
            ["query pageSize True", "query cursor False", "query displayName True"]);
        query[0]!["schema"]!.ToJsonString().ShouldBe("""{"maximum":200,"minimum":1,"type":"integer","format":"int32"}""");
        query[1]!["schema"]!.ToJsonString().ShouldBe("""{"type":"string"}""");
        query[2]!["schema"]!.ToJsonString().ShouldBe("""{"maxLength":64,"minLength":1,"type":"string"}""");

        // A command of the default modules: kind command and its operation; no servers in the document.
        var ban = document["paths"]!["/gm/v1/players/{playerId}/ban"]!["post"]!;
        ban["x-gm-kind"]!.GetValue<string>().ShouldBe("command");
        ban["x-gm-operation"]!.GetValue<string>().ShouldBe("identity.ban");
        document.AsObject().ContainsKey("servers").ShouldBeFalse();
        document["tags"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ShouldBe(["counter", "identity", "probe", "x"]);
    }

    /// <summary>D1: a name without a dot (refused by the catalog check on every host, so only reachable here) is its own tag.</summary>
    [Theory]
    [InlineData("identity.ban", "identity")]
    [InlineData("sample.game.x", "sample")]
    [InlineData("nodot", "nodot")]
    public void The_area_is_the_segment_before_the_first_dot(string name, string area) => GmOperationTransformer.AreaOf(name).ShouldBe(area);

    public sealed record SharedLine([property: Range(1, 9)] int Quantity, string? Sku);

    public sealed record SchemaProbeBody(
        [property: Range(1, 10)] int PInt,
        [Range(1, 10)] int AInt,
        [Range(0, long.MaxValue)] long? ALong,
        [property: Range(0.5, 9.5)] double PDouble,
        [StringLength(10, MinimumLength = 2)] string? AStrLen,
        [property: MinLength(3), MaxLength(7)] string? PMinMax,
        [Length(1, 4)] List<string>? ALenList,
        [RegularExpression("^[a-z]+$")] string? ARegex,
        [System.ComponentModel.Description("shown")] string? ADesc,
        [Required] string? AReq,
        [property: Required] long Delta,
        [GmOptions("probe.options")] string? Pick,
        [property: GmOptions("probe.options")] List<string>? Picks,
        [property: System.ComponentModel.Description("use site"), Required] SharedLine? Line,
        [Length(1, 3)] List<SharedLine>? Lines,
        ProbeKind Kind,
        string? Reason) : IGmCommandBody;

    public sealed record OtherBody([Length(2, 5)] List<SharedLine>? Lines, string? Reason) : IGmCommandBody;

    /// <summary>Same shape as a request body, used only as a response: keeps the generator's rules.</summary>
    public sealed record SchemaProbeView([property: Range(1, 10)] int PInt, string? Text);

    private static readonly RouteDeclaration SchemaRoute = RouteDeclaration.Post("probe.schema", "/gm/v1/probe/schema", SecurityProfile.Gm, RouteSurface.GmApi, Permission);
    private static readonly RouteDeclaration OtherRoute = RouteDeclaration.Post("probe.other", "/gm/v1/probe/other", SecurityProfile.Gm, RouteSurface.GmApi, Permission);
    private static readonly RouteDeclaration ViewRoute = RouteDeclaration.Get("probe.view", "/gm/v1/probe/view", SecurityProfile.Gm, RouteSurface.GmApi, Permission);
    private static readonly RouteDeclaration OptionsRoute = RouteDeclaration.Get("probe.options", "/gm/v1/probe/options", SecurityProfile.Gm, RouteSurface.GmApi, Permission);

    /// <summary>T021 (FR-010..FR-013, D5, A6): keywords from both targets, real required list, reason rule, options, use sites.</summary>
    [Fact]
    public async Task Request_body_schemas_show_the_rules_the_server_enforces()
    {
        var document = await DocumentAsync(
            app =>
            {
                app.MapDeclared(SchemaRoute, () => Results.Ok()).Accepts<SchemaProbeBody>(GmHttp.JsonContentType).AsGmEndpoint(Permission);
                app.MapDeclared(OtherRoute, () => Results.Ok()).Accepts<OtherBody>(GmHttp.JsonContentType).AsGmEndpoint(Permission);
                app.MapDeclared(ViewRoute, () => Results.Ok()).Produces<SchemaProbeView>().AsGmEndpoint(Permission);
                app.MapDeclared(OptionsRoute, () => Results.Ok()).Produces<GmOptionList>().AsGmEndpoint(Permission);
            },
            SchemaRoute,
            OtherRoute,
            ViewRoute,
            OptionsRoute);
        var schemas = document["components"]!["schemas"]!;
        var body = schemas["SchemaProbeBody"]!;
        var properties = body["properties"]!;

        // Review r1 m3: every non-nullable value type is required (the server refuses its absence).
        Strings(body["required"]).ShouldBe(["pInt", "aInt", "pDouble", "aReq", "delta", "line", "kind", "reason"]);
        Keywords(properties["pInt"]).ShouldBe("minimum=1 maximum=10");
        Keywords(properties["aInt"]).ShouldBe("minimum=1 maximum=10");
        Keywords(properties["aLong"]).ShouldBe("minimum=0 maximum=9223372036854775807");
        Keywords(properties["pDouble"]).ShouldBe("minimum=0.5 maximum=9.5");
        Keywords(properties["aStrLen"]).ShouldBe("minLength=2 maxLength=10");
        Keywords(properties["pMinMax"]).ShouldBe("minLength=3 maxLength=7");
        Keywords(properties["aLenList"]).ShouldBe("minItems=1 maxItems=4");
        properties["aRegex"]!["pattern"]!.GetValue<string>().ShouldBe("^(?:^[a-z]+$)?$");
        properties["aDesc"]!["description"]!.GetValue<string>().ShouldBe("shown");
        properties["aReq"]!["type"]!.GetValue<string>().ShouldBe("string");

        // T063 (review M3): [Required] refuses "" and white space on the server, so the schema says so.
        Keywords(properties["aReq"]).ShouldBe("minLength=1");
        properties["aReq"]!["pattern"]!.GetValue<string>().ShouldBe("\\S");

        // T063 (review m5): a [Required] object property is required and not nullable at its use site.
        properties["line"]!.ToJsonString().ShouldBe("""{"description":"use site","$ref":"#/components/schemas/SharedLine"}""");
        properties["line"]!.AsObject().ContainsKey("oneOf").ShouldBeFalse();
        Strings(properties["delta"]!["type"]).ShouldBe(["integer", "string"]);
        properties["pick"]!["x-gm-options"]!.GetValue<string>().ShouldBe("probe.options");
        properties["picks"]!["items"]!["x-gm-options"]!.GetValue<string>().ShouldBe("probe.options");
        properties["picks"]!["x-gm-options"].ShouldBeNull();
        properties["reason"]!.ToJsonString().ShouldBe("""{"maxLength":500,"minLength":1,"pattern":"\\S","type":"string"}""");
        properties["kind"]!["$ref"]!.GetValue<string>().ShouldBe("#/components/schemas/ProbeKind");

        // Use sites of the shared nested type: list bounds on each list, nothing of a use site on the component.
        Keywords(properties["lines"]).ShouldBe("minItems=1 maxItems=3");
        Keywords(schemas["OtherBody"]!["properties"]!["lines"]).ShouldBe("minItems=2 maxItems=5");
        var shared = schemas["SharedLine"]!.AsObject();
        Keywords(shared).ShouldBe(string.Empty);
        shared.ContainsKey("pattern").ShouldBeFalse();
        shared.ContainsKey("description").ShouldBeFalse();
        Keywords(shared["properties"]!["quantity"]).ShouldBe("minimum=1 maximum=9");
        Strings(shared["required"]).ShouldBe(["quantity"]);

        // A response record keeps the generator's required list (every member is always written).
        Strings(schemas["SchemaProbeView"]!["required"]).ShouldBe(["pInt", "text"]);
    }

    private static string Keywords(JsonNode? schema)
    {
        string[] names = ["minimum", "exclusiveMinimum", "maximum", "exclusiveMaximum", "minLength", "maxLength", "minItems", "maxItems"];
        return string.Join(" ", names.Where(n => schema![n] is not null).Select(n => $"{n}={schema![n]!.ToJsonString().Trim('"')}"));
    }

    private static JsonNode Operation(JsonNode document, RouteDeclaration route) =>
        document["paths"]![route.Template]![route.Methods[0].ToLowerInvariant()]!;

    private static string[] Strings(JsonNode? array) => array!.AsArray().Select(v => v!.GetValue<string>()).ToArray();

    internal static async Task<JsonNode> DocumentAsync(Action<WebApplication> map, params RouteDeclaration[] routes)
    {
        await using var host = await SampleTestHost.StartAsync(SampleTestHost.WithRoutes(routes), beforeUseGameServer: map);
        return await DocumentAsync(host);
    }

    internal static async Task<JsonNode> DocumentAsync(TestHost host)
    {
        var catalog = await host.Services.GetRequiredService<GmCatalogDocument>().GetAsync(CancellationToken.None);
        return JsonNode.Parse(catalog.Bytes.Span)!;
    }
}
