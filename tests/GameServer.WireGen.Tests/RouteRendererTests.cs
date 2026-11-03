using Shouldly;
using static GameServer.WireGen.Tests.TemplateAndNameTests;

namespace GameServer.WireGen.Tests;

/// <summary>Slice 014 T047 (FR-032, FR-023 ordering, US3-1, US3-2, contracts/generated-files.md §3): the routes file.</summary>
public sealed class RouteRendererTests
{
    /// <summary>The player-facing framework routes of data-model §1.3 (inputs of FrameworkRoutes, §5.1).</summary>
    private static readonly ContractRoute[] FrameworkRoutes =
    [
        Route("platform.time", "/v1/time", profile: "Public", surface: "Host", origin: "framework", methods: ["GET"], owner: "platform"),
        Route("platform.snapshot", "/api/v1/player/snapshot", origin: "framework", owner: "platform"),
        Route("platform.config", "/api/v1/config/get", origin: "framework", owner: "platform"),
        Route("platform.receipt", "/api/v1/player/receipts/{receiptId}", origin: "framework", methods: ["GET"], owner: "platform"),
        Route("platform.health_live", "/health/live", profile: "Public", surface: "Host", origin: "framework", methods: [], owner: "platform.observability"),
        Route("platform.health_ready", "/health/ready", profile: "Public", surface: "Host", origin: "framework", methods: ["GET"], owner: "platform.observability"),
        Route("identity.login_guest", "/api/v1/identity/login/guest", profile: "Public", origin: "framework", owner: "identity"),
        Route("identity.login_provider", "/api/v1/identity/login/provider", profile: "Public", origin: "framework", owner: "identity"),
        Route("identity.logout", "/api/v1/identity/logout", origin: "framework", owner: "identity"),
        Route("identity.set_display_name", "/api/v1/identity/display-name", origin: "framework", owner: "identity", operation: "identity.set_display_name"),
        Route("identity.link", "/api/v1/identity/link", origin: "framework", owner: "identity", operation: "identity.link"),
        Route("identity.request_deletion", "/api/v1/identity/delete-account", origin: "framework", owner: "identity", operation: "identity.request_deletion"),
        Route("iap.purchase", "/api/v1/iap/purchase", origin: "framework", owner: "iap", operation: "iap.purchase"),
        Route("cheat.grant", "/api/v1/cheat/grant", profile: "Dev", origin: "framework", owner: "economy.cheat", operation: "cheat.grant"),
    ];

    [Fact]
    public void The_framework_routes_render_to_the_golden_file()
    {
        var text = RouteRenderer.Render(FrameworkRoutes, "GameServer.Client", "FrameworkRoutes", "framework", "0.7.0");

        text.ShouldBe(File.ReadAllText(Repo.TestData("FrameworkRoutes.golden.txt")).ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("IdentityLoginGuest", "api/v1/identity/login/guest")]
    [InlineData("IdentityLoginProvider", "api/v1/identity/login/provider")]
    [InlineData("IdentityLink", "api/v1/identity/link")]
    [InlineData("IdentityLogout", "api/v1/identity/logout")]
    [InlineData("IdentityRequestDeletion", "api/v1/identity/delete-account")]
    [InlineData("PlatformSnapshot", "api/v1/player/snapshot")]
    [InlineData("PlatformConfig", "api/v1/config/get")]
    [InlineData("PlatformTime", "v1/time")]
    [InlineData("IapPurchase", "api/v1/iap/purchase")]
    [InlineData("CheatGrant", "api/v1/cheat/grant")]
    [InlineData("IdentitySetDisplayName", "api/v1/identity/display-name")]
    [InlineData("PlatformHealthReady", "health/ready")]
    public void The_sdk_route_constants_of_data_model_5_1_are_generated(string member, string value)
    {
        RouteRenderer.Render(FrameworkRoutes, "GameServer.Client", "FrameworkRoutes", "framework", "0.7.0")
            .ShouldContain($"public const string {member} = \"{value}\";");
    }

    [Fact]
    public void Parameter_helpers_escape_each_value_in_template_order()
    {
        var text = RouteRenderer.Render(
            [
                Route("platform.receipt", "/api/v1/player/receipts/{receiptId}", methods: ["GET"], owner: "platform"),
                Route("game.doc", "/v1/docs/{id}.json", methods: ["GET"]),
                Route("game.klass", "/v1/{class}/items/{item}"),
            ],
            "Game.Client",
            "GameRoutes",
            "game",
            "0.7.0");

        text.ShouldContain(
            "        public static string PlatformReceipt(string receiptId) =>\n" +
            "            \"api/v1/player/receipts/\" + global::System.Uri.EscapeDataString(receiptId);\n");
        text.ShouldContain("public static string GameDoc(string id) =>\n            \"v1/docs/\" + global::System.Uri.EscapeDataString(id) + \".json\";");
        text.ShouldContain(
            "public static string GameKlass(string @class, string item) =>\n" +
            "            \"v1/\" + global::System.Uri.EscapeDataString(@class) + \"/items/\" + global::System.Uri.EscapeDataString(item);");
        text.ShouldContain("public const string GameKlassMethod = \"POST\";");
        text.Split('\n').Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)).ShouldAllBe(l => !l.Contains('{') || l.Trim() == "{");
    }

    [Fact]
    public void The_output_is_the_same_for_any_input_order()
    {
        var ordered = RouteRenderer.Render(FrameworkRoutes, "GameServer.Client", "FrameworkRoutes", "framework", "0.7.0");
        var shuffled = RouteRenderer.Render(FrameworkRoutes.Reverse().ToArray(), "GameServer.Client", "FrameworkRoutes", "framework", "0.7.0");

        shuffled.ShouldBe(ordered);
    }

    [Fact]
    public void A_parameter_that_is_no_identifier_fails_naming_the_route()
    {
        Should.Throw<WireGenException>(() => RouteRenderer.Render([Route("game.x", "/v1/x/{a-b}")], "G", "R", "game", "0.7.0"))
            .Message.ShouldStartWith("[route game.x] template parameter 'a-b' is not a C# identifier.");
    }
}
