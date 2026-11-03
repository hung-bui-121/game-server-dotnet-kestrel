using Shouldly;

namespace GameServer.WireGen.Tests;

/// <summary>Slice 014 T046 (FR-031, FR-032 rules, US3-1, US3-2, research R13, data-model §3.1, §5): names, templates and selection.</summary>
public sealed class TemplateAndNameTests
{
    [Theory]
    [InlineData("identity.login_guest", "IdentityLoginGuest")]
    [InlineData("platform.health_live", "PlatformHealthLive")]
    [InlineData("gm.ops_gate_min_client_version", "GmOpsGateMinClientVersion")]
    [InlineData("wallet", "Wallet")]
    [InlineData("a.route_2x", "ARoute2x")]
    public void Names_are_pascal_case_of_the_parts(string name, string expected) => Names.Pascal(name).ShouldBe(expected);

    [Theory]
    [InlineData("receiptId", "receiptId")]
    [InlineData("class", "@class")]
    [InlineData("event", "@event")]
    [InlineData("bad-name", null)]
    public void Parameters_keep_their_name_and_escape_keywords(string name, string? expected) => Names.Parameter(name).ShouldBe(expected);

    [Fact]
    public void Templates_split_into_literals_and_parameters()
    {
        TemplateParser.Parse("x.y", "/api/v1/player/receipts/{receiptId}").ShouldBe(
            [new TemplatePart("api/v1/player/receipts/", false), new TemplatePart("receiptId", true)]);
        TemplateParser.Parse("x.y", "/gm/v1/openapi/{documentName}.json").ShouldBe(
            [new TemplatePart("gm/v1/openapi/", false), new TemplatePart("documentName", true), new TemplatePart(".json", false)]);
        TemplateParser.Parse("x.y", "/v1/time").ShouldBe([new TemplatePart("v1/time", false)]);
    }

    [Theory]
    [InlineData("/api/v1/x/{id:int}", "a constraint ':'")]
    [InlineData("/api/v1/x/{id?}", "an optional parameter '?'")]
    [InlineData("/api/v1/x/{id=1}", "a default value '='")]
    [InlineData("/api/v1/x/{*rest}", "a catch-all '*'")]
    [InlineData("/api/v1/x/{{id}}", "an escaped or unclosed '{'")]
    public void Unsupported_template_forms_name_the_route(string template, string what)
    {
        Should.Throw<WireGenException>(() => TemplateParser.Parse("game.thing", template)).Message.ShouldBe(
            $"[route game.thing] template '{template}' uses {what}: the generated Unity route code cannot express it. Fix: declare the player-facing " +
            "route with plain {name} parameters (FG-026).");
    }

    [Fact]
    public void Names_that_collide_fail_naming_both_routes()
    {
        var dotted = Route("a.b_c", "/v1/one");
        var underscored = Route("a_b.c", "/v1/two");
        var plain = Route("game.save", "/v1/save");
        var method = Route("game.save_method", "/v1/save-method");

        Should.Throw<WireGenException>(() => Render(dotted, underscored)).Lines[0]
            .ShouldBe("[route a.b_c] and [route a_b.c] both generate the member GameRoutes.ABC. Fix: rename one of them (FG-026).");
        Should.Throw<WireGenException>(() => Render(plain, method)).Message.ShouldContain(
            "[route game.save] (method constant) and [route game.save_method] both generate the member GameRoutes.GameSaveMethod.");
        Should.Throw<WireGenException>(() => Render(Route("game.routes", "/v1/r"))).Message.ShouldContain(
            "[route game.routes] generates the member GameRoutes, the name of its class.");
    }

    [Fact]
    public void Selection_keeps_player_facing_routes_of_the_origin()
    {
        var contract = new WireContract("0.7.0", "3.36.2",
        [
            Route("gm.auth_login", "/gm/v1/auth/login", profile: "Public", surface: "GmApi", origin: "framework"),
            Route("gm.sso_start", "/gm/v1/auth/sso/start", profile: "Public", surface: "GmApi", origin: "framework"),
            Route("identity.ban", "/gm/v1/players/{playerId}/ban", profile: "Gm", surface: "GmApi", origin: "framework"),
            Route("cheat.grant", "/api/v1/cheat/grant", profile: "Dev", origin: "framework"),
            Route("platform.time", "/v1/time", profile: "Public", surface: "Host", origin: "framework", methods: ["GET"]),
            Route("platform.hook", "/v1/hook", profile: "Public", surface: "Host", origin: "framework", methods: []),
            Route("quiet.thing", "/api/v1/quiet", origin: "framework", serviceOnly: true),
            Route("battle.end", "/api/v1/battle/end"),
        ], []);

        Selection.Routes(contract, "framework").Select(r => r.Name).ShouldBe(["cheat.grant", "platform.hook", "platform.time"]);
        Selection.Routes(contract, "game").Select(r => r.Name).ShouldBe(["battle.end"]);
        RouteRenderer.Render(Selection.Routes(contract, "framework"), "N", "R", "framework", "0.7.0")
            .ShouldContain("public const string PlatformHookMethod = \"*\";");
    }

    // T086 minor 9: the unversioned host routes are the load-balancer health probes, never called by the client.
    [Fact]
    public void Selection_leaves_out_the_unversioned_health_probes()
    {
        var contract = new WireContract("0.7.0", "3.36.2",
        [
            Route("platform.health_live", "/health/live", profile: "Public", surface: "Host", origin: "framework", methods: []),
            Route("platform.health_ready", "/health/ready", profile: "Public", surface: "Host", origin: "framework", methods: ["GET"]),
            Route("platform.time", "/v1/time", profile: "Public", surface: "Host", origin: "framework", methods: ["GET"]),
        ], []);

        Selection.Routes(contract, "framework").Select(r => r.Name).ShouldBe(["platform.time"]);
    }

    [Fact]
    public void A_player_facing_route_with_several_methods_fails()
    {
        var contract = new WireContract("0.7.0", "3.36.2", [Route("game.both", "/api/v1/both", methods: ["GET", "POST"])], []);

        Should.Throw<WireGenException>(() => Selection.Routes(contract, "game")).Message.ShouldBe(
            "[route game.both] declares GET,POST: a player-facing route has one method in the generated code. Fix: declare one route per method " +
            "(FG-026, plan N4).");
    }

    [Fact]
    public void Sections_are_selected_by_origin()
    {
        var contract = new WireContract("0.7.0", "3.36.2", [],
        [
            new ContractSection("wallet", "economy", "framework", "gameserver.economy.v1.WalletState"),
            new ContractSection("card", "fixture", "game", "fixture.v1.CardState"),
        ]);

        Selection.Sections(contract, "game").ShouldHaveSingleItem().Id.ShouldBe("card");
        Selection.Sections(contract, "framework").ShouldHaveSingleItem().Id.ShouldBe("wallet");
    }

    internal static ContractRoute Route(
        string name,
        string template,
        string profile = "Player",
        string surface = "PlayerApi",
        string origin = "game",
        string[]? methods = null,
        bool serviceOnly = false,
        string owner = "game",
        string? operation = null) =>
        new(name, methods ?? ["POST"], template, profile, surface, owner, origin, operation, serviceOnly);

    private static string Render(params ContractRoute[] routes) => RouteRenderer.Render(routes, "Game.Client", "GameRoutes", "game", "0.7.0");
}
