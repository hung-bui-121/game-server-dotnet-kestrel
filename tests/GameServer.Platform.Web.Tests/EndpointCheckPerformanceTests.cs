using System.Diagnostics;
using System.Globalization;
using GameServer.Actors;
using GameServer.Modules;
using GameServer.Platform.Web.Hosting;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 014 T035 (SC-007, research V16): the endpoint set of a passing configuration is built once before serving (ASP.NET Core
/// caches no built endpoint, so every enumeration of <c>DataSources</c> builds each endpoint again), and the endpoint check over
/// it stays within budget. The build count runs always; the 200-endpoint measurement is opt-in (<c>GAMESERVER_PERF=1</c>).
/// </summary>
public sealed class EndpointCheckPerformanceTests
{
    private const string OptIn = "Opt-in: set GAMESERVER_PERF=1 to measure the pre-serving endpoint check (SC-007: validation <= 50 ms for 200 endpoints).";
    private const int RouteCount = 200;
    private const int Samples = 5;
    private const double BudgetMs = 50;

    [Fact]
    public async Task Each_endpoint_is_built_once_before_serving()
    {
        var module = new SyntheticModule(3);

        await using var host = await SampleTestHost.StartAsync(gs =>
        {
            SampleTestHost.DefaultModules(gs);
            gs.AddModule(module);
        });

        // Started and validated, no request yet: one build per endpoint (the pre-serving check), not one more in UseGameServer.
        module.Builds.ShouldBe(3);
    }

    [Fact]
    public async Task The_pre_serving_check_of_200_endpoints_validates_in_at_most_50_ms()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GAMESERVER_PERF") == "1", OptIn);
        var module = new SyntheticModule(RouteCount);
        await using var host = await SampleTestHost.StartAsync(gs =>
        {
            SampleTestHost.DefaultModules(gs);
            gs.AddModule(module);
        });
        var buildsBeforeServing = module.Builds;
        var routes = (IEndpointRouteBuilder)host.App;

        Measure(routes, host.Services);
        var samples = Enumerable.Range(0, Samples).Select(_ => Measure(routes, host.Services)).ToArray();
        var validation = Median(samples.Select(s => s.Validate));

        // The build of the endpoint set (RequestDelegateFactory per endpoint) is paid once before serving, as in 013: information only.
        var report = string.Create(
            CultureInfo.InvariantCulture,
            $"pre-serving endpoint check, {samples[0].Endpoints} endpoints ({buildsBeforeServing} synthetic built before serving): validation median " +
            $"{validation:0.0} ms; one build of the set {Median(samples.Select(s => s.Materialize)):0.0} ms");
        TestContext.Current.SendDiagnosticMessage(report);
        buildsBeforeServing.ShouldBe(RouteCount, report);
        validation.ShouldBeLessThanOrEqualTo(BudgetMs, report);
    }

    /// <summary>What <see cref="EndpointSetValidationService"/> does: materialize the final endpoint set, then every validator over it.</summary>
    private static (int Endpoints, double Materialize, double Validate) Measure(IEndpointRouteBuilder routes, IServiceProvider services)
    {
        var watch = Stopwatch.StartNew();
        var endpoints = routes.DataSources.SelectMany(source => source.Endpoints).ToArray();
        var materialize = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var errors = EndpointValidation.Run(endpoints, services, includeUnmapped: true);
        var validate = watch.Elapsed.TotalMilliseconds;
        errors.ShouldBeEmpty();
        return (endpoints.Length, materialize, validate);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    /// <summary>Player routes declared in the descriptor and mapped from the declarations, each counting how often it is built.</summary>
    private sealed class SyntheticModule(int count) : IGameModule, IPlayerApiModule
    {
        private readonly RouteDeclaration[] _routes = Enumerable.Range(0, count)
            .Select(i => RouteDeclaration.Post($"synthetic.route_{i:000}", $"/api/v1/synthetic/route-{i:000}", SecurityProfile.Player))
            .ToArray();

        private int _builds;

        public int Builds => Volatile.Read(ref _builds);

        public ModuleDescriptor Describe() => new() { Name = "synthetic", Routes = _routes };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }

        public void MapPlayerApi(IEndpointRouteBuilder routes)
        {
            foreach (var route in _routes)
            {
                IEndpointConventionBuilder builder = routes.MapDeclared(route, () => "ok").AsPlayerEndpoint();
                builder.Add(_ => Interlocked.Increment(ref _builds));
            }
        }
    }
}
