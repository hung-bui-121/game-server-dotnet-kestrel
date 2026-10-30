using GameServer.Platform.Analytics;
using Shouldly;

namespace GameServer.Platform.Tests.Analytics;

/// <summary>Startup validation of <c>GameServer:Analytics</c> (T016, FR-021, FR-031, SD5).</summary>
public sealed class AnalyticsOptionsValidatorTests
{
    private static IReadOnlyList<string> Validate(AnalyticsOptions options, string environment = "Development", int sinks = 1)
    {
        var gameServer = new GameServerOptions { GameId = "g", EnvironmentId = "live", DeploymentEnvironment = environment };
        var result = new AnalyticsOptionsValidator(new AnalyticsActivation(sinks), gameServer).Validate(null, options);
        return result.Failed ? [.. result.Failures] : [];
    }

    private static AnalyticsOptions Valid() => new() { TimeZone = "Asia/Ho_Chi_Minh" };

    [Fact]
    public void Defaults_with_a_zone_are_valid() => Validate(Valid(), "Production").ShouldBeEmpty();

    [Theory]
    [InlineData("QueueCapacity", 100, true)]
    [InlineData("QueueCapacity", 99, false)]
    [InlineData("QueueCapacity", 1_000_000, true)]
    [InlineData("QueueCapacity", 1_000_001, false)]
    [InlineData("DeliveryTimeoutMs", 50, true)]
    [InlineData("DeliveryTimeoutMs", 49, false)]
    [InlineData("DeliveryTimeoutMs", 5_000, true)]
    [InlineData("DeliveryTimeoutMs", 5_001, false)]
    [InlineData("ResumeTrackerCapacity", 1_000, true)]
    [InlineData("ResumeTrackerCapacity", 999, false)]
    [InlineData("ResumeTrackerCapacity", 10_000_000, true)]
    [InlineData("ResumeTrackerCapacity", 10_000_001, false)]
    [InlineData("ErrorWindowSeconds", 10, true)]
    [InlineData("ErrorWindowSeconds", 9, false)]
    [InlineData("ErrorWindowSeconds", 86_400, true)]
    [InlineData("ErrorWindowSeconds", 86_401, false)]
    [InlineData("RetainLocalDays", 1, true)]
    [InlineData("RetainLocalDays", 0, false)]
    [InlineData("LowDiskBytes", 0, true)]
    [InlineData("LowDiskBytes", -1, false)]
    public void Range_boundaries_name_the_key(string key, int value, bool valid)
    {
        var options = Valid();
        typeof(AnalyticsOptions).GetProperty(key)!.SetValue(options, key == "LowDiskBytes" ? (object)(long)value : value);

        var errors = Validate(options);

        if (valid)
        {
            errors.ShouldBeEmpty();
        }
        else
        {
            errors.ShouldHaveSingleItem().ShouldContain("GameServer:Analytics:" + key);
        }
    }

    [Fact]
    public void An_empty_directory_is_refused() =>
        Validate(new AnalyticsOptions { TimeZone = "UTC", Directory = " " }).ShouldHaveSingleItem().ShouldContain("GameServer:Analytics:Directory");

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void A_missing_zone_with_a_sink_fails_in_staging_and_production(string environment)
    {
        var error = Validate(new AnalyticsOptions(), environment).ShouldHaveSingleItem();

        error.ShouldContain("GameServer:Analytics:TimeZone");
        error.ShouldContain(environment);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public void A_missing_zone_defaults_in_development_and_test(string environment) => Validate(new AnalyticsOptions(), environment).ShouldBeEmpty();

    [Theory]
    [InlineData("Asia/Hanoi_typo")]
    [InlineData("SE Asia Standard Time")]
    public void An_unknown_or_windows_zone_fails(string zone)
    {
        var error = Validate(new AnalyticsOptions { TimeZone = zone }).ShouldHaveSingleItem();

        error.ShouldContain("GameServer:Analytics:TimeZone");
        error.ShouldContain(zone);
    }

    [Fact]
    public void Disabled_analytics_needs_no_zone_and_no_sink_in_production()
    {
        Validate(new AnalyticsOptions { Enabled = false }, "Production").ShouldBeEmpty();
        Validate(new AnalyticsOptions { Enabled = false }, "Production", sinks: 0).ShouldBeEmpty();
        Validate(new AnalyticsOptions { Enabled = false, TimeZone = "Asia/Hanoi_typo" }, "Staging", sinks: 0).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void Enabled_without_a_sink_fails_outside_development_naming_the_fix(string environment)
    {
        // Only the sink error: without a sink the zone is never checked (SD5).
        var error = Validate(new AnalyticsOptions { TimeZone = "Asia/Hanoi_typo" }, environment, sinks: 0).ShouldHaveSingleItem();

        error.ShouldContain("GameServer:Analytics:Enabled is true in '" + environment + "'");
        error.ShouldContain("no analytics sink is registered");
        error.ShouldContain("AddAnalyticsFiles()");
        error.ShouldContain("GameServer:Analytics:Enabled=false");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public void Enabled_without_a_sink_is_allowed_in_development_and_test(string environment) =>
        Validate(new AnalyticsOptions(), environment, sinks: 0).ShouldBeEmpty();
}
