using GameServer.Platform.Diagnostics;
using GameServer.Platform.Tests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Tests.Privacy;

/// <summary>
/// Slice 010 review minor 5: <c>account_purge_overdue_seconds</c> is reported only for a limited time after the lease holder's
/// run set it, so an instance that no longer runs the job stops reporting a stale value.
/// </summary>
public sealed class PurgeOverdueGaugeTests
{
    private const string Gauge = "account_purge_overdue_seconds";

    [Fact]
    public void The_gauge_reports_the_last_value_while_valid_and_nothing_once_expired()
    {
        using var collector = new MetricCollector();
        using var metrics = new PlatformMetrics();

        // Values unique to this test: other tests' PlatformMetrics instances report through the same meter name.
        metrics.SetAccountPurgeOverdue(TimeSpan.FromSeconds(123_456.25), TimeSpan.FromHours(1));
        collector.Observe();
        collector.Values(Gauge).ShouldContain(123_456.25);

        metrics.SetAccountPurgeOverdue(TimeSpan.FromSeconds(234_567.75), TimeSpan.Zero);
        collector.Observe();
        collector.Values(Gauge).ShouldNotContain(234_567.75);
        collector.Values(Gauge).Count(v => v == 123_456.25).ShouldBe(1); // replaced, not reported again
    }
}
