using GameServer.Platform.Analytics;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Zone and settings validation at host level (T034, US3-3, US3-4, SC-007, SD5). A test host cannot start in Staging or
/// Production (in-memory store, test authentication): there the tests assert what the one startup report says about analytics.
/// </summary>
public sealed class AnalyticsStartupTests
{
    private const string NoSinkWarning = "Analytics enabled but no sink registered (AddAnalyticsFiles)";
    private const string DefaultedWarning = "analytics records use UTC (Development/Test default)";

    private static async Task<GameServerStartupException> StartupFails(Action<Dictionary<string, string?>> settings, Action<IServiceCollection>? services = null) =>
        await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(settings: settings, services: services);
        });

    private static void NoZone(Dictionary<string, string?> s) => s.Remove("GameServer:Analytics:TimeZone");

    private static void NoSink(IServiceCollection services) => services.RemoveAll<IAnalyticsSink>();

    [Theory]
    [InlineData("Asia/Hanoi_typo", "not a known time zone")]
    [InlineData("SE Asia Standard Time", "Windows zone id")]
    public async Task An_unknown_or_windows_zone_fails_startup_naming_key_value_and_fix(string zone, string reason)
    {
        var ex = await StartupFails(s => s["GameServer:Analytics:TimeZone"] = zone);

        var violation = ex.Violations.ShouldHaveSingleItem();
        violation.ShouldContain("GameServer:Analytics:TimeZone");
        violation.ShouldContain(zone);
        violation.ShouldContain(reason);
        violation.ShouldContain("IANA zone name (e.g. Asia/Ho_Chi_Minh)");
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task A_missing_zone_with_a_sink_fails_in_staging_and_production(string environment)
    {
        var ex = await StartupFails(s =>
        {
            NoZone(s);
            s["GameServer:DeploymentEnvironment"] = environment;
        });

        ex.Violations.ShouldContain(v => v.Contains("GameServer:Analytics:TimeZone is not set in '" + environment + "'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Enabled_without_a_sink_fails_naming_the_fix_and_needs_no_zone(string environment)
    {
        var ex = await StartupFails(
            s =>
            {
                NoZone(s);
                s["GameServer:DeploymentEnvironment"] = environment;
            },
            NoSink);

        var analytics = ex.Violations.Where(v => v.Contains("Analytics", StringComparison.Ordinal)).ShouldHaveSingleItem();
        analytics.ShouldContain("GameServer:Analytics:Enabled is true in '" + environment + "'");
        analytics.ShouldContain("AddAnalyticsFiles()");
        analytics.ShouldContain("GameServer:Analytics:Enabled=false");
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Disabled_without_a_sink_needs_nothing(string environment)
    {
        var ex = await StartupFails(
            s =>
            {
                NoZone(s);
                s["GameServer:DeploymentEnvironment"] = environment;
                s["GameServer:Analytics:Enabled"] = "false";
            },
            NoSink);

        // The start fails for the test host's own reasons only (test auth, in-memory store), never for analytics.
        ex.Violations.ShouldNotBeEmpty();
        ex.Violations.ShouldNotContain(v => v.Contains("Analytics", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public async Task Development_and_test_default_to_utc_with_one_warning(string environment)
    {
        await using var host = await TestHost.StartAsync(settings: s =>
        {
            NoZone(s);
            s["GameServer:DeploymentEnvironment"] = environment;
        });
        await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        await host.FlushAnalyticsAsync();

        host.Analytics.OfEvent("login").ShouldHaveSingleItem()["timeZone"]!.GetValue<string>().ShouldBe("UTC");
        host.Logs.Count(l => l.Contains(DefaultedWarning, StringComparison.Ordinal)).ShouldBe(1);
        host.Logs.ShouldNotContain(l => l.Contains(NoSinkWarning, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public async Task Enabled_without_a_sink_starts_in_development_and_test_with_one_warning_and_writes_nothing(string environment)
    {
        await using var host = await TestHost.StartAsync(
            settings: s =>
            {
                NoZone(s);
                s["GameServer:DeploymentEnvironment"] = environment;
            },
            services: NoSink);
        await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        host.Logs.Count(l => l.Contains(NoSinkWarning, StringComparison.Ordinal)).ShouldBe(1);
        host.Services.GetRequiredService<AnalyticsPipeline>().IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task Disabled_analytics_logs_no_warning()
    {
        await using var host = await TestHost.StartAsync(settings: s =>
        {
            NoZone(s);
            s["GameServer:Analytics:Enabled"] = "false";
        });

        host.Logs.ShouldNotContain(l => l.Contains(NoSinkWarning, StringComparison.Ordinal) || l.Contains(DefaultedWarning, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_range_violation_joins_the_one_startup_report()
    {
        var ex = await StartupFails(s =>
        {
            s["GameServer:Analytics:QueueCapacity"] = "5";
            s["GameServer:DeploymentEnvironment"] = "Production";
        });

        ex.Violations.ShouldContain(v => v.Contains("GameServer:Analytics:QueueCapacity is 5; it must be 100..1000000", StringComparison.Ordinal));
        ex.Violations.ShouldContain(v => v.Contains("test authentication is enabled in 'Production'", StringComparison.Ordinal));
        ex.Violations.Count(v => v.Contains("QueueCapacity", StringComparison.Ordinal)).ShouldBe(1);
    }
}
