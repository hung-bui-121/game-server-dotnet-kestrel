using System.Diagnostics;
using System.Globalization;
using GameServer.Actors;
using GameServer.Modules;
using GameServer.Platform.Web.Hosting;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Opt-in measurement (slice 014 T035, SC-007, VERIFY V2; <c>GAMESERVER_PERF=1</c>): the pre-serving run — materializing the final
/// endpoint set again and every endpoint validator over it — for a host with 200 declared and mapped endpoints.
/// </summary>
public sealed class EndpointCheckPerformanceTests
{
    private const string OptIn = "Opt-in: set GAMESERVER_PERF=1 to measure the pre-serving endpoint check (SC-007: <= 50 ms for 200 endpoints).";
    private const int RouteCount = 200;
    private const int Samples = 5;
    private const double BudgetMs = 50;

    [Fact]
    public async Task The_pre_serving_check_of_200_endpoints_takes_at_most_50_ms()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GAMESERVER_PERF") == "1", OptIn);
        await using var host = await SampleTestHost.StartAsync(gs =>
        {
            SampleTestHost.DefaultModules(gs);
            gs.AddModule(new SyntheticModule());
        });
        var routes = (IEndpointRouteBuilder)host.App;

        Measure(routes, host.Services);
        var samples = Enumerable.Range(0, Samples).Select(_ => Measure(routes, host.Services)).ToArray();
        var median = Median(samples.Select(s => s.Materialize + s.Validate));

        // Both parts are printed: the materialization of the endpoint set (RequestDelegateFactory per endpoint) dominates.
        var report = string.Create(
            CultureInfo.InvariantCulture,
            $"pre-serving endpoint check, {samples[0].Endpoints} endpoints: median {median:0.0} ms = materialization {Median(samples.Select(s => s.Materialize)):0.0} ms + validation {Median(samples.Select(s => s.Validate)):0.0} ms");
        TestContext.Current.SendDiagnosticMessage(report);
        median.ShouldBeLessThanOrEqualTo(BudgetMs, report);
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

    /// <summary>200 Player routes, declared in the descriptor and mapped from the declarations.</summary>
    private sealed class SyntheticModule : IGameModule, IPlayerApiModule
    {
        private static readonly RouteDeclaration[] Routes = Enumerable.Range(0, RouteCount)
            .Select(i => RouteDeclaration.Post($"synthetic.route_{i:000}", $"/api/v1/synthetic/route-{i:000}", SecurityProfile.Player))
            .ToArray();

        public ModuleDescriptor Describe() => new() { Name = "synthetic", Routes = Routes };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }

        public void MapPlayerApi(IEndpointRouteBuilder routes)
        {
            foreach (var route in Routes)
            {
                routes.MapDeclared(route, () => "ok").AsPlayerEndpoint();
            }
        }
    }
}
