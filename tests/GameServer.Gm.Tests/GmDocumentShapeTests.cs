using System.Text.Json.Nodes;
using GameServer.Gm.Tests.Infrastructure;
using GameServer.Modules.Economy;
using GameServer.Modules.Identity;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.OpenApi;
using GameServer.Routing;
using Microsoft.Extensions.DependencyInjection;
using SampleGame;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 017 (SC-001, FR-001..FR-006, US1-1..US1-6): the GM document of the framework test host, as the catalog serves it,
/// describes every GM command (body, path, header, answer, x-gm-* keys) and every operation by its route name and area.
/// </summary>
public sealed class GmDocumentShapeTests
{
    public static TheoryData<string, string, string, string> Commands() => new()
    {
        { IdentityRoutes.Ban.Name, IdentityRoutes.Ban.Template, "BanBody", "typed" },
        { IdentityRoutes.Unban.Name, IdentityRoutes.Unban.Template, "UnbanBody", "simple" },
        { IdentityRoutes.CancelDeletion.Name, IdentityRoutes.CancelDeletion.Template, "CancelDeletionBody", "typed" },
        { EconomyRoutes.Adjust.Name, EconomyRoutes.Adjust.Template, "AdjustResourceBody", "typed" },
        { SampleGameRoutes.ProgressionSet.Name, SampleGameRoutes.ProgressionSet.Template, "SetProgressionBody", "simple" },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task A_gm_command_is_fully_described(string name, string template, string body, string confirm)
    {
        await using var host = await StartAsync();
        var document = await DocumentAsync(host);

        var operation = document["paths"]![template]!["post"]!;
        operation["operationId"]!.GetValue<string>().ShouldBe(name);
        Strings(operation["tags"]).ShouldBe([name[..name.IndexOf('.', StringComparison.Ordinal)]]);

        var request = operation["requestBody"]!;
        request["required"]!.GetValue<bool>().ShouldBeTrue();
        request["content"]!.AsObject().Select(c => c.Key).ShouldBe(["application/json"]);
        request["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>().ShouldBe("#/components/schemas/" + body);

        var parameters = operation["parameters"]!.AsArray();
        parameters.Select(p => $"{p!["in"]} {p["name"]} {p["required"]}").ShouldBe(["path playerId true", "header X-Request-Id true"]);
        parameters[0]!["schema"]!["pattern"]!.GetValue<string>().ShouldBe("^[A-Za-z0-9_-]{1,64}$");
        parameters[1]!["schema"]!["format"]!.GetValue<string>().ShouldBe("uuid");

        operation["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/components/schemas/GmCommandResponse");
        operation["x-gm-kind"]!.GetValue<string>().ShouldBe("command");
        operation["x-gm-operation"]!.GetValue<string>().ShouldBe(name);
        operation["x-gm-target"]!.GetValue<string>().ShouldBe("player");
        operation["x-gm-confirm"]!.GetValue<string>().ShouldBe(confirm);
    }

    [Fact]
    public async Task Every_operation_is_named_by_its_route_and_tagged_by_its_area()
    {
        await using var host = await StartAsync();
        var document = await DocumentAsync(host);
        var catalog = host.Services.GetRequiredService<WireCatalog>();

        var ids = new List<string>();
        foreach (var (path, item) in document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                var label = $"{method.ToUpperInvariant()} {path}";
                var id = operation!["operationId"]?.GetValue<string>();
                id.ShouldNotBeNull(label);
                id.ShouldBe(catalog.FindRouteName(method.ToUpperInvariant(), path), label);
                Strings(operation["tags"]).ShouldBe([id[..id.IndexOf('.', StringComparison.Ordinal)]], label);
                var kind = operation["x-gm-kind"]!.GetValue<string>();
                if (method == "get")
                {
                    kind.ShouldBe("query", label);
                    operation["x-gm-confirm"].ShouldBeNull(label);
                }
                else
                {
                    kind.ShouldBeOneOf(["command", "action"], label);
                    operation["x-gm-confirm"]!.GetValue<string>().ShouldBeOneOf(["simple", "typed"], label);
                }

                operation["x-gm-target"]!.GetValue<string>().ShouldBe(path.Contains("{playerId}", StringComparison.Ordinal) ? "player" : "global", label);
                ids.Add(id);
            }
        }

        ids.Count.ShouldBeGreaterThan(30);
        ids.ShouldBeUnique();
        document.AsObject().ContainsKey("servers").ShouldBeFalse();
    }

    public static TheoryData<string, string> Lists() => new()
    {
        { "/gm/v1/players/search", "GmPlayerSearchItem" },
        { "/gm/v1/players/{playerId}/receipts", "GmReceiptSummary" },
        { "/gm/v1/staff", "StaffView" },
        { "/gm/v1/players/{playerId}/audit", "JsonObject" },
        { "/gm/v1/staff/{staffId}/audit", "JsonObject" },
        { "/gm/v1/players/{playerId}/ledger", "JsonObject" },
    };

    /// <summary>T042 (FR-040..FR-042, D10, D11): the six lists are paged with typed or free-form item schemas.</summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task A_list_is_paged_with_its_item_schema(string path, string item)
    {
        await using var host = await StartAsync();
        var document = await DocumentAsync(host);

        var operation = document["paths"]![path]!["get"]!;
        operation["x-gm-paged"]!.GetValue<bool>().ShouldBeTrue();
        var query = operation["parameters"]!.AsArray().Where(p => p!["in"]!.GetValue<string>() == "query").ToArray();
        query[0]!["name"]!.GetValue<string>().ShouldBe("pageSize");
        query[0]!["required"]!.GetValue<bool>().ShouldBeTrue();
        query[0]!["schema"]!.ToJsonString().ShouldBe("""{"maximum":200,"minimum":1,"type":"integer","format":"int32"}""");
        query[1]!["name"]!.GetValue<string>().ShouldBe("cursor");
        query[1]!["required"].ShouldBeNull();
        var page = operation["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>();
        page.ShouldBe("#/components/schemas/GmPageOf" + item);
        document["components"]!["schemas"]!["GmPageOf" + item]!["properties"]!["items"]!["items"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/components/schemas/" + item);
    }

    /// <summary>T042 (FR-041, FR-043..FR-047, D3, Q7, R14): query parameter, options, confirmations, request ids, summaries.</summary>
    [Fact]
    public async Task Lists_options_actions_and_summaries_are_described()
    {
        await using var host = await StartAsync();
        var document = await DocumentAsync(host);
        var paths = document["paths"]!;
        var schemas = document["components"]!["schemas"]!;

        var displayName = paths["/gm/v1/players/search"]!["get"]!["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "displayName")!;
        displayName["required"]!.GetValue<bool>().ShouldBeTrue();
        displayName["schema"]!.ToJsonString().ShouldBe("""{"maxLength":64,"minLength":1,"type":"string"}""");

        schemas["AdjustResourceBody"]!["properties"]!["resource"]!["x-gm-options"]!.GetValue<string>().ShouldBe("economy.resource_options");
        schemas["BanBody"]!["properties"]!["scope"]!["items"]!["x-gm-options"]!.GetValue<string>().ShouldBe("identity.ban_scopes");
        foreach (var options in new[] { "/gm/v1/economy/resources", "/gm/v1/players/ban-scopes" })
        {
            var operation = paths[options]!["get"]!;
            operation["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>().ShouldBe("#/components/schemas/GmOptionList");
            operation["x-gm-kind"]!.GetValue<string>().ShouldBe("query");
        }

        string[] typed = ["identity.ban", "identity.cancel_deletion", "economy.adjust", "gm.ops_rotate_keys", "gm.ops_gate_maintenance"];
        string[] requestId =
        [
            "gm.staff_create", "gm.staff_grant", "gm.staff_disable", "gm.staff_enable", "gm.staff_revoke_sessions",
            "gm.ops_gate_maintenance", "gm.ops_gate_min_client_version", "gm.ops_rotate_keys",
        ];
        string[] frameworkAreas = ["gm", "identity", "economy", "platform", "sample", "progression", "counter"];
        var seenTyped = new List<string>();
        var seenRequestId = new List<string>();
        foreach (var (path, item) in paths.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                var id = operation!["operationId"]!.GetValue<string>();
                if (operation["x-gm-confirm"]?.GetValue<string>() == "typed")
                {
                    seenTyped.Add(id);
                }

                var hasHeader = operation["parameters"]?.AsArray().Any(p => p!["in"]!.GetValue<string>() == "header" && p["name"]!.GetValue<string>() == "X-Request-Id") == true;
                if (hasHeader && operation["x-gm-kind"]!.GetValue<string>() == "action")
                {
                    seenRequestId.Add(id);
                }

                if (frameworkAreas.Contains(id[..id.IndexOf('.', StringComparison.Ordinal)]))
                {
                    (operation["summary"]?.GetValue<string>()).ShouldNotBeNullOrWhiteSpace($"{method.ToUpperInvariant()} {path} ({id}) has no summary");
                }
            }
        }

        seenTyped.Order(StringComparer.Ordinal).ShouldBe(typed.Order(StringComparer.Ordinal));
        seenRequestId.Order(StringComparer.Ordinal).ShouldBe(requestId.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// T063 (review M2, M3): every mutation body with a <c>reason</c> lists it as required with the server rule (actions refuse a
    /// missing reason too); a <c>[Required]</c> string shows that blank values are refused (<c>minLength: 1</c>, <c>pattern: \S</c>).
    /// </summary>
    [Fact]
    public async Task Reason_and_required_strings_show_the_rules_the_server_applies()
    {
        await using var host = await StartAsync();
        var document = await DocumentAsync(host);
        var schemas = document["components"]!["schemas"]!;

        var bodies = new List<string>();
        foreach (var (_, item) in document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                var reference = operation!["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>();
                if (method == "get" || reference is null || operation["operationId"]!.GetValue<string>() == "gm.auth_login")
                {
                    continue;
                }

                var name = reference[(reference.LastIndexOf('/') + 1)..];
                var schema = schemas[name]!;
                schema["properties"]!["reason"].ShouldNotBeNull(name);
                Strings(schema["required"]).ShouldContain("reason", name);
                schema["properties"]!["reason"]!.ToJsonString().ShouldBe("""{"maxLength":500,"minLength":1,"pattern":"\\S","type":"string"}""", name);
                bodies.Add(name);
            }
        }

        bodies.Distinct().Order(StringComparer.Ordinal).ShouldBe(
        [
            "AdjustResourceBody", "BanBody", "CancelDeletionBody", "CreateStaffBody", "MaintenanceBody", "MinClientVersionBody",
            "SetProgressionBody", "StaffGrantBody", "StaffReasonBody", "UnbanBody",
        ]);
        schemas["AdjustResourceBody"]!["properties"]!["resource"]!.ToJsonString()
            .ShouldBe("""{"minLength":1,"pattern":"\\S","type":"string","x-gm-options":"economy.resource_options"}""");
    }

    /// <summary>T022 (FR-010..FR-012, SC-002): the probe body shows every supported attribute on both targets, nested included.</summary>
    [Fact]
    public async Task The_probe_body_shows_every_supported_attribute_on_both_targets()
    {
        await using var host = await StartAsync(b => b.AddModule<ProbeGmModule>());
        var schemas = (await DocumentAsync(host))["components"]!["schemas"]!;
        var body = schemas["ProbeGmBody"]!;
        var properties = body["properties"]!;

        Strings(body["required"]).ShouldBe(["pRequired", "aRequired", "pRequiredNumber", "reason"]);

        // Review r2 M1: a defaulted parameter and a computed member are in the document but not required.
        properties["weight"].ShouldNotBeNull();
        properties["hasRange"].ShouldNotBeNull();
        foreach (var target in new[] { "p", "a" })
        {
            Keywords(properties[target + "Range"]).ShouldBe("minimum=1 maximum=100", target);
            Keywords(properties[target + "StringLength"]).ShouldBe("minLength=2 maxLength=8", target);
            Keywords(properties[target + "MinLength"]).ShouldBe("minLength=3", target);
            Keywords(properties[target + "MaxLength"]).ShouldBe("maxLength=5", target);
            Keywords(properties[target + "Length"]).ShouldBe("minItems=1 maxItems=3", target);
            properties[target + "Pattern"]!["pattern"]!.GetValue<string>().ShouldBe("^(?:^[a-z]+$)?$", target);
            properties[target + "Required"]!["type"]!.GetValue<string>().ShouldBe("string", target);
            Keywords(properties[target + "Required"]).ShouldBe("minLength=1", target);
            properties[target + "Required"]!["pattern"]!.GetValue<string>().ShouldBe("\\S", target);
        }

        properties["pDescription"]!["description"]!.GetValue<string>().ShouldBe("property description");
        properties["aDescription"]!["description"]!.GetValue<string>().ShouldBe("parameter description");
        Keywords(properties["items"]).ShouldBe("minItems=1 maxItems=2");
        // Review r1 M1: full match, "" allowed (as RegularExpressionAttribute); m3/m6: nested value types are required.
        properties["pLoosePattern"]!["pattern"]!.GetValue<string>().ShouldBe("^(?:[a-z]+)?$");
        Strings(schemas["ProbeNested"]!["required"]).ShouldBe(["inner", "count"]);
        var nested = schemas["ProbeNested"]!["properties"]!;
        Keywords(nested["inner"]).ShouldBe("minimum=1 maximum=9");
        Keywords(nested["tag"]).ShouldBe("maxLength=4");
        properties["reason"]!["pattern"]!.GetValue<string>().ShouldBe("\\S");
        var operation = (await DocumentAsync(host))["paths"]![ProbeGmModule.Template]!["post"]!;
        operation["operationId"]!.GetValue<string>().ShouldBe(ProbeSetHandler.Operation);
    }

    internal static string Keywords(JsonNode? schema)
    {
        string[] names = ["minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems"];
        return string.Join(" ", names.Where(n => schema![n] is not null).Select(n => $"{n}={schema![n]!.ToJsonString()}"));
    }

    internal static Task<TestHost> StartAsync(Action<Platform.Web.Hosting.GameServerBuilder>? extra = null) =>
        GmTestHost.StartAsync(
            s =>
            {
                s["Gm:Oidc:Authority"] = "https://idp.example";
                s["Gm:Oidc:ClientId"] = "gm-console";
                s["Gm:Oidc:ClientSecret"] = "not-used";
            },
            extra: extra);

    internal static async Task<JsonNode> DocumentAsync(TestHost host)
    {
        var catalog = await host.Services.GetRequiredService<GmCatalogDocument>().GetAsync(CancellationToken.None);
        return JsonNode.Parse(catalog.Bytes.Span)!;
    }

    internal static string[] Strings(JsonNode? array) => array!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
}
