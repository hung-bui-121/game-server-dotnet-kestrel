using GameServer.Modules.Gm;
using GameServer.Platform;
using Microsoft.Extensions.Options;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 018 FR-022..FR-025 (SC-003): SSO redirect targets with a console (<c>Gm:Console:BaseUrl</c>, root and sub-path) and
/// without one (legacy <c>/gm/</c> rule, defaults per environment). Unit matrix; <see cref="SsoTests"/> drives the real flow.
/// </summary>
public sealed class GmConsoleUrlsTests
{
    private const string Root = "https://gm.example.com/";
    private const string SubPath = "https://cdn.example.com/gm/";

    public static TheoryData<string> Hostile =>
    [
        "https://evil.example/x",
        "//evil.example",
        "/\\evil",
        "/a/../b",
        "/é",
        "/" + new string('a', 512),
        "",
        "javascript:alert(1)",
        "/a b",
        "/\tx",
        "evil.example/#/x",

        // Review T024 M1: percent-encoded dots resolve as ".." in browsers; a query reaches the console's origin.
        "/%2e%2e/x",
        "/.%2e/x",
        "/%2E./x",
        "/gm/%2E%2E/x",
        "/gm/v1/.%2e/x",
        "/?x=1",
    ];

    /// <summary>
    /// Review T024 M1: with a console only <c>/</c> or <c>/#&lt;fragment&gt;</c> (the console's hash route) is accepted — no path
    /// segment (a sub-path console would leave its path), no query, no encoded dot even inside the fragment.
    /// </summary>
    [Theory]
    [InlineData("/other/")]
    [InlineData("/gm/v1/me")]
    [InlineData("/players/abc")]
    [InlineData("/gm/#/x")]
    [InlineData("/?#/x")]
    [InlineData("/#/players/%2e%2e")]
    public void A_console_accepts_only_its_root_and_hash_routes(string value)
    {
        Urls(SubPath).AcceptReturnPath(value).ShouldBeNull();
        Urls(SubPath).SuccessTarget(value).ShouldBe(SubPath);
    }

    [Fact]
    public void Without_a_console_a_gm_path_keeps_its_query() =>
        Urls(null).SuccessTarget("/gm/v1/me?x=1").ShouldBe("/gm/v1/me?x=1");

    private static GmConsoleUrls Urls(string? baseUrl, string environment = "Development")
    {
        var options = new GmOptions();
        options.Console.BaseUrl = baseUrl;
        return new GmConsoleUrls(Options.Create(options), new GameServerOptions { DeploymentEnvironment = environment });
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/#/players/abc")]
    [InlineData("/#/x?y=1")]
    [InlineData("/#/players/a%20b")]
    public void A_console_accepts_relative_console_paths(string path)
    {
        Urls(Root).AcceptReturnPath(path).ShouldBe(path);
        Urls(Root).SuccessTarget(path).ShouldBe(Root + path[1..]);
        Urls(SubPath).SuccessTarget(path).ShouldBe(SubPath + path[1..]);
    }

    [Fact]
    public void The_longest_accepted_path_has_512_characters()
    {
        var path = "/#/" + new string('a', 509);
        Urls(Root).AcceptReturnPath(path).ShouldBe(path);
        Urls(Root).AcceptReturnPath(path + "a").ShouldBeNull();
    }

    [Fact]
    public void A_missing_value_lands_on_the_console_root()
    {
        Urls(Root).AcceptReturnPath(null).ShouldBeNull();
        Urls(Root).SuccessTarget(null).ShouldBe(Root);
        Urls(null).AcceptReturnPath(null).ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void A_console_refuses_hostile_values_and_lands_on_its_root(string value)
    {
        Urls(Root).AcceptReturnPath(value).ShouldBeNull();
        Urls(Root).SuccessTarget(value).ShouldBe(Root);
        Urls(SubPath).SuccessTarget(value).ShouldBe(SubPath);
    }

    [Fact]
    public void A_console_lands_denials_on_its_login_route()
    {
        Urls(Root).DeniedTarget().ShouldBe("https://gm.example.com/#/login?error=sso_denied");
        Urls(SubPath).DeniedTarget().ShouldBe("https://cdn.example.com/gm/#/login?error=sso_denied");
    }

    [Theory]
    [InlineData("Development", "/gm/v1/ui")]
    [InlineData("Test", "/gm/v1/ui")]
    [InlineData("Staging", "/gm/v1/me")]
    [InlineData("Production", "/gm/v1/me")]
    public void Without_a_console_the_legacy_rule_and_the_environment_default_apply(string environment, string fallback)
    {
        var urls = Urls(null, environment);
        urls.AcceptReturnPath("/gm/v1/me").ShouldBe("/gm/v1/me");
        urls.SuccessTarget("/gm/v1/players/abc").ShouldBe("/gm/v1/players/abc");

        // Outside /gm/ (a console path included) or hostile: the default.
        urls.AcceptReturnPath("/").ShouldBeNull();
        urls.SuccessTarget("/#/players/abc").ShouldBe(fallback);
        urls.SuccessTarget("/gm/v1/é").ShouldBe(fallback);
        urls.SuccessTarget("/gm/../x").ShouldBe(fallback);
        urls.SuccessTarget(null).ShouldBe(fallback);
        urls.DeniedTarget().ShouldBe(fallback + "?error=sso_denied");
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Without_a_console_hostile_values_are_refused(string value) =>
        Urls(null).AcceptReturnPath(value).ShouldBeNull();

    [Fact]
    public void An_empty_console_url_means_no_console() =>
        Urls("").DeniedTarget().ShouldBe("/gm/v1/ui?error=sso_denied");
}
