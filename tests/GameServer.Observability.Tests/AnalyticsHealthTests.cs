using GameServer.Platform.Observability;
using GameServer.Tests.Shared;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;

namespace GameServer.Observability.Tests;

/// <summary>Readiness check <c>analytics</c> (T041, US4-6, FR-027): Degraded only, per host, on the server clock.</summary>
public sealed class AnalyticsHealthTests
{
    [Fact]
    public async Task A_sink_error_degrades_readiness_until_the_error_window_passed()
    {
        await using var af = await AnalyticsFileHost.StartAsync(s => s["GameServer:Analytics:ErrorWindowSeconds"] = "60");
        (await af.AnalyticsHealthAsync()).Status.ShouldBe(HealthStatus.Healthy);

        af.Host.Analytics.FailWrites = true;
        await af.Host.NewPlayerAsync();
        await af.Host.FlushAnalyticsAsync();
        af.Host.Analytics.FailWrites = false;

        var degraded = await af.AnalyticsHealthAsync();
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Data["sinkErrors"].ShouldBe(1L);
        degraded.Data["lastError"].ToString()!.ShouldContain("IOException");

        af.Host.Clock.Advance(TimeSpan.FromSeconds(59));
        (await af.AnalyticsHealthAsync()).Status.ShouldBe(HealthStatus.Degraded);
        af.Host.Clock.Advance(TimeSpan.FromSeconds(2));
        (await af.AnalyticsHealthAsync()).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Low_disk_degrades_but_never_fails_readiness_or_liveness()
    {
        await using var af = await AnalyticsFileHost.StartAsync(s => s["GameServer:Analytics:LowDiskBytes"] = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var entry = await af.AnalyticsHealthAsync();

        entry.Status.ShouldBe(HealthStatus.Degraded);
        entry.Description.ShouldBe("Analytics disk nearly full.");
        (await af.Host.Client().GetStringAsync(ObservabilityModule.LiveRoute)).ShouldBe("Healthy");
        var ready = await af.Host.Client().GetAsync(ObservabilityModule.ReadyRoute);
        ((int)ready.StatusCode).ShouldBe(200);
        (await ready.Content.ReadAsStringAsync()).ShouldBe("Degraded");
    }

    [Fact]
    public async Task Another_hosts_sink_error_does_not_degrade_this_host()
    {
        await using var failing = await AnalyticsFileHost.StartAsync();
        await using var healthy = await AnalyticsFileHost.StartAsync();
        failing.Host.Analytics.FailWrites = true;
        await failing.Host.NewPlayerAsync();
        await failing.Host.FlushAnalyticsAsync();

        (await failing.AnalyticsHealthAsync()).Status.ShouldBe(HealthStatus.Degraded);
        (await healthy.AnalyticsHealthAsync()).Status.ShouldBe(HealthStatus.Healthy);
    }
}
