using System.Diagnostics;
using GameServer.Accounts;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>
/// Slice 016 T031 (SC-003), opt-in (<c>GAMESERVER_PERF=1</c> and Couchbase): a first login whose initializer touches three
/// aggregates and grants once costs at most 20 ms p95 more than a first login without initializer; returning logins cost the
/// same with and without an initializer registered (they never run it).
/// </summary>
public sealed class AccountCreationPerformanceTests(CouchbaseGameFixture fixture) : IClassFixture<CouchbaseGameFixture>
{
    private const int WarmUp = 20;
    private const int Logins = 200;
    private const double MaxFirstLoginDeltaMs = 20;

    /// <summary>Returning logins are the same transaction with or without initializers; allowed difference = measurement noise.</summary>
    private const double MaxReturningDeltaMs = 20;

    private static readonly FakeClock Clock = new();

    [Fact]
    public async Task A_seeding_first_login_costs_at_most_20_ms_p95_more_and_returning_logins_cost_the_same()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GAMESERVER_PERF") == "1", "Opt-in: set GAMESERVER_PERF=1 to measure the first-login cost (SC-003).");
        fixture.RequireCouchbase();

        var (plainFirst, plainReturning) = await MeasureAsync(withInitializer: false);
        var (seededFirst, seededReturning) = await MeasureAsync(withInitializer: true);

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"first login, no initializer:      p50 {P(plainFirst, 50):F2} ms, p95 {P(plainFirst, 95):F2} ms");
        output?.WriteLine($"first login, seeding initializer: p50 {P(seededFirst, 50):F2} ms, p95 {P(seededFirst, 95):F2} ms");
        output?.WriteLine($"returning, no initializer:        p50 {P(plainReturning, 50):F2} ms, p95 {P(plainReturning, 95):F2} ms");
        output?.WriteLine($"returning, initializer registered: p50 {P(seededReturning, 50):F2} ms, p95 {P(seededReturning, 95):F2} ms");
        (P(seededFirst, 95) - P(plainFirst, 95)).ShouldBeLessThanOrEqualTo(MaxFirstLoginDeltaMs);
        Math.Abs(P(seededReturning, 95) - P(plainReturning, 95)).ShouldBeLessThanOrEqualTo(MaxReturningDeltaMs);
    }

    /// <summary>Times <see cref="Logins"/> first logins of new guests, then one returning login of each, after a warm-up.</summary>
    private async Task<(List<double> First, List<double> Returning)> MeasureAsync(bool withInitializer)
    {
        await using var host = await CouchbaseGameHost.StartAsync(
            fixture,
            Clock,
            s => s["Identity:LoginPerLocalIdPerMinute"] = "1000",
            withInitializer ? b => b.AddModule(new PerfSeedModule()) : null);
        var guests = Enumerable.Range(0, WarmUp + Logins).Select(_ => (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret())).ToArray();
        var first = new List<double>(Logins);
        var returning = new List<double>(Logins);
        for (var i = 0; i < guests.Length; i++)
        {
            var elapsed = await TimeAsync(host, guests[i]);
            if (i >= WarmUp)
            {
                first.Add(elapsed);
            }
        }

        for (var i = 0; i < guests.Length; i++)
        {
            var elapsed = await TimeAsync(host, guests[i]);
            if (i >= WarmUp)
            {
                returning.Add(elapsed);
            }
        }

        return (first, returning);
    }

    private static async Task<double> TimeAsync(CouchbaseGameHost host, (string LocalId, byte[] Secret) guest)
    {
        var watch = Stopwatch.StartNew();
        await host.Client().LoginAsync(guest.LocalId, guest.Secret);
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double P(List<double> values, int percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1)];
    }

    private sealed record PerfState
    {
        public long Value { get; init; }
    }

    /// <summary>One grant of gold and two seeded aggregates: profile + wallet + perf_a + perf_b = the 4-aggregate budget.</summary>
    private sealed class PerfSeedInitializer(ResourceCatalog resources) : IAccountInitializer
    {
        public ValueTask<ErrorCode?> InitializeAsync(ICommandContext context)
        {
            context.Set(PerfSeedModule.A, new PerfState { Value = 1 });
            context.Set(PerfSeedModule.B, new PerfState { Value = 2 });
            return ValueTask.FromResult(EconomyOperations.Grant(context, resources, "gold", 100, new LedgerSource("account_init")));
        }
    }

    private sealed class PerfSeedModule : IGameModule
    {
        public const string A = "perf_a";
        public const string B = "perf_b";

        public ModuleDescriptor Describe() => new()
        {
            Name = "perf",
            DependsOn = [EconomyModuleDefinition.ModuleName],
            Aggregates =
            [
                new PlayerAggregateDescriptor<PerfState>(A, 1, clientVisible: false, () => new PerfState()),
                new PlayerAggregateDescriptor<PerfState>(B, 1, clientVisible: false, () => new PerfState()),
            ],
            AccountInitializers = [AccountInitializerRegistration.Of<PerfSeedInitializer>("perf.seed", [EconomyAggregates.Wallet, A, B])],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }
}
