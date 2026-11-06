using GameServer.Modules.Gm;
using GameServer.Platform;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 018 startup rules of <see cref="GmOptionsValidator"/> (data-model §1): one console origin outside Development
/// (FR-003). Unit cases on the validator; the host cases that prove the report reaches startup live with their story's tests.
/// </summary>
public sealed class GmOptionsValidatorTests
{
    private const string Console = "https://gm.example.com";

    /// <summary>Every failure line of the validator for <paramref name="options"/> in <paramref name="environment"/>.</summary>
    private static string[] Failures(GmOptions options, string environment) =>
        new GmOptionsValidator(new GameServerOptions { DeploymentEnvironment = environment }).Validate(null, options).Failures?.ToArray() ?? [];

    private static GmOptions WithOrigins(params string[] origins)
    {
        var options = new GmOptions();
        options.Cors.AllowedOrigins.AddRange(origins);
        return options;
    }

    [Fact]
    public void Outside_development_one_console_origin_is_allowed()
    {
        Failures(WithOrigins(Console, "https://gm2.example.com"), "Production").ShouldContain(
            "Gm:Cors:AllowedOrigins lists 2 origins in 'Production'; list the one console origin of this environment.");
        Failures(WithOrigins(Console), "Production").ShouldNotContain(f => f.Contains("origins in", StringComparison.Ordinal));
    }

    [Fact]
    public void Development_accepts_several_console_origins() =>
        Failures(WithOrigins(Console, "http://localhost:5173"), "Development").ShouldNotContain(f => f.Contains("origins in", StringComparison.Ordinal));

    /// <summary>FR-010: <c>Lax</c> (default) or <c>None</c>, without case.</summary>
    [Theory]
    [InlineData("Lax")]
    [InlineData("lax")]
    [InlineData("LAX")]
    [InlineData("None")]
    [InlineData("none")]
    public void Lax_and_none_are_the_samesite_values(string value)
    {
        var options = WithOrigins(Console);
        options.Cookies.SameSite = value;
        Failures(options, "Development").ShouldNotContain(f => f.StartsWith("Gm:Cookies:SameSite", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Strict")]
    [InlineData("Unspecified")]
    [InlineData("")]
    [InlineData(" Lax")]
    public void Other_samesite_values_fail(string value)
    {
        var options = WithOrigins(Console);
        options.Cookies.SameSite = value;
        Failures(options, "Development").ShouldContain($"Gm:Cookies:SameSite '{value}' is not supported; use Lax (default) or None.");
    }

    /// <summary>FR-011: where it is allowed (Development/Test), <c>None</c> needs a console origin.</summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public void Samesite_none_needs_a_cors_origin(string environment)
    {
        const string Line = "Gm:Cookies:SameSite=None needs Gm:Cors:AllowedOrigins: SameSite=None is only for a console on another site (case B, unsupported).";
        var without = new GmOptions();
        without.Cookies.SameSite = "None";
        Failures(without, environment).ShouldContain(Line);

        var with = WithOrigins(Console);
        with.Cookies.SameSite = "None";
        with.Console.BaseUrl = Console + "/";
        Failures(with, environment).ShouldBeEmpty();
    }

    /// <summary>Review T024 m2 (FR-011 as decided): case B is not supported, so <c>None</c> fails startup outside Development/Test.</summary>
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void Samesite_none_is_refused_outside_development_and_test(string environment)
    {
        var options = Production(Console, Console + "/");
        options.Cookies.SameSite = "none";
        Failures(options, environment).ShouldBe(
        [
            $"Gm:Cookies:SameSite=None is refused in '{environment}': a console on another site (case B) is not supported; use Lax (default) " +
            "with a console on the same site.",
        ]);

        options.Cookies.SameSite = "Lax";
        Failures(options, environment).ShouldBeEmpty();
    }

    /// <summary>Review T024 m1: an origin is valid only in the exact form a browser sends; CORS and CSRF compare strings.</summary>
    [Theory]
    [InlineData("https://gm.example.com:443")]
    [InlineData("http://localhost:80")]
    [InlineData("https://user@gm.example.com")]
    [InlineData("https://user:pw@gm.example.com")]
    [InlineData("https://gm.example.com?x=1")]
    [InlineData("https://gm.example.com#x")]
    [InlineData("https://gm.example.com/")]
    [InlineData("https://gm.example.com/console")]
    public void An_origin_not_in_its_serialized_form_fails(string origin) =>
        Failures(WithOrigins(origin), "Development").ShouldContain(
            $"Gm:Cors:AllowedOrigins '{origin}' must be an origin (scheme://host[:port]) without default port, user info, path, query or trailing slash.");

    [Theory]
    [InlineData("https://gm.example.com")]
    [InlineData("https://gm.example.com:8443")]
    [InlineData("http://localhost:5173")]
    [InlineData("https://GM.Example.com")]
    public void An_origin_in_its_serialized_form_is_valid(string origin) =>
        Failures(WithOrigins(origin), "Development").ShouldNotContain(f => f.StartsWith($"Gm:Cors:AllowedOrigins '{origin}'", StringComparison.Ordinal));

    [Fact]
    public void The_default_samesite_is_lax() => new GmOptions().Cookies.SameSite.ShouldBe("Lax");

    /// <summary>A Production configuration that passes every pre-018 rule (SSO configured).</summary>
    private static GmOptions Production(string? origin = null, string? baseUrl = null)
    {
        var options = origin is null ? new GmOptions() : WithOrigins(origin);
        options.Oidc.Authority = "https://login.corp.test";
        options.Oidc.ClientId = "gm";
        options.Oidc.ClientSecret = "from-secret-provider";
        options.Console.BaseUrl = baseUrl;
        return options;
    }

    /// <summary>FR-021: no console (neither setting) is valid everywhere — the zh-pvp bump case.</summary>
    [Fact]
    public void A_deployment_without_console_settings_starts()
    {
        Failures(Production(), "Production").ShouldBeEmpty();
        Failures(new GmOptions(), "Development").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Console, "https://gm.example.com/")]
    [InlineData("https://cdn.example.com", "https://cdn.example.com/gm/")]
    [InlineData("https://GM.Example.com", "HTTPS://gm.example.COM/")]
    [InlineData("https://gm.example.com:8443", "https://gm.example.com:8443/console/")]
    public void A_console_url_of_the_cors_origin_is_valid(string origin, string baseUrl) =>
        Failures(Production(origin, baseUrl), "Production").ShouldBeEmpty();

    [Fact]
    public void A_cors_origin_needs_the_console_url() =>
        Failures(Production(Console), "Production").ShouldBe(
            ["Gm:Cors:AllowedOrigins is set but Gm:Console:BaseUrl is not; set the console URL of that origin (SSO returns there)."]);

    [Theory]
    [InlineData("https://gm.example.com")]
    [InlineData("https://gm.example.com/gm")]
    [InlineData("https://gm.example.com/?x")]
    [InlineData("https://gm.example.com/?x=/")]
    [InlineData("https://gm.example.com/#x")]
    [InlineData("https://gm.example.com/#/")]
    [InlineData("https://user:pw@gm.example.com/")]
    [InlineData("/gm/")]
    [InlineData("gm.example.com/")]
    [InlineData("ftp://gm.example.com/")]
    [InlineData("https://gm.example.com/a b/")]
    [InlineData(" https://gm.example.com/")]
    public void A_malformed_console_url_fails(string baseUrl) =>
        Failures(Production(Console, baseUrl), "Production").ShouldContain(
            $"Gm:Console:BaseUrl '{baseUrl}' must be an absolute http(s) URL whose path ends with '/', without query, fragment or user info.");

    /// <summary>An empty value (a configuration layer clearing the setting) is unset, not malformed.</summary>
    [Fact]
    public void An_empty_console_url_is_unset()
    {
        Failures(Production(baseUrl: ""), "Production").ShouldBeEmpty();
        Failures(Production(Console, ""), "Production").ShouldBe(
            ["Gm:Cors:AllowedOrigins is set but Gm:Console:BaseUrl is not; set the console URL of that origin (SSO returns there)."]);
    }

    [Fact]
    public void A_plain_http_console_url_is_development_only()
    {
        Failures(Production("http://gm.example.com", "http://gm.example.com/"), "Production").ShouldContain(
            "Gm:Console:BaseUrl 'http://gm.example.com/' is not https; plain http is allowed only in Development.");
        Failures(WithConsole("http://localhost:5173", "http://localhost:5173/"), "Development").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void The_console_url_must_be_on_the_cors_origin(string environment)
    {
        const string Line = "Gm:Console:BaseUrl origin 'https://other.example.com' is not in Gm:Cors:AllowedOrigins; add it (one console origin per environment).";
        var elsewhere = environment == "Production" ? Production(Console, "https://other.example.com/") : WithConsole(Console, "https://other.example.com/");
        Failures(elsewhere, environment).ShouldContain(Line);

        var withoutOrigin = environment == "Production" ? Production(baseUrl: "https://other.example.com/") : WithConsole(null, "https://other.example.com/");
        Failures(withoutOrigin, environment).ShouldContain(Line);
    }

    private static GmOptions WithConsole(string? origin, string baseUrl)
    {
        var options = origin is null ? new GmOptions() : WithOrigins(origin);
        options.Console.BaseUrl = baseUrl;
        return options;
    }

    /// <summary>The report reaches startup (one line per fault, FR-020).</summary>
    [Fact]
    public async Task A_host_with_a_malformed_console_url_fails_startup()
    {
        var ex = await Should.ThrowAsync<Exception>(() => GmTestHost.StartAsync(s =>
        {
            s["Gm:Cors:AllowedOrigins:0"] = Console;
            s["Gm:Console:BaseUrl"] = Console;
        }));
        ex.ToString().ShouldContain(
            "Gm:Console:BaseUrl 'https://gm.example.com' must be an absolute http(s) URL whose path ends with '/', without query, fragment or user info.");
    }
}
