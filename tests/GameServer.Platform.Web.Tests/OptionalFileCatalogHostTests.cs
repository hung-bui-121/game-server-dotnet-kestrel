using System.Net;
using GameServer.Commands;
using GameServer.Config;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Web.Commands;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 015 T071 M1: a catalog declared over an optional file that is absent is built before traffic (its build returns the empty
/// catalog), so a handler taking it gets that instance; a build that cannot cope stops the host with one line. Never a lazy 500.
/// </summary>
public sealed class OptionalFileCatalogHostTests
{
    private const string Route = "/api/v1/test/optional-catalog";
    private const string ProbeFile = "probe.json";

    private const string ReadsLine =
        "[module optprobe] catalog ProbeCatalog: the build read optional config file 'probe.json', which is absent. " +
        "Fix: build an empty catalog when Config.HasFile(\"probe.json\") is false, or declare the file Required (FG-018).";

    private static readonly RouteDeclaration Probe = RouteDeclaration.Command(ProbeHandler.Descriptor, Route);

    [Fact]
    public async Task A_catalog_over_an_absent_optional_file_is_the_empty_instance_handlers_get()
    {
        await using var host = await StartAsync(c => c.Config.HasFile(ProbeFile) ? new ProbeCatalog(c.Json(ProbeFile).GetArrayLength()) : ProbeCatalog.Empty);
        var (player, _) = await host.NewPlayerAsync();

        var response = await player.PostAsync(Route, new Wire.IncrementRequest { By = 1 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Services.GetRequiredService<ProbeCatalog>().ShouldBeSameAs(ProbeCatalog.Empty);
    }

    [Fact]
    public async Task A_build_that_reads_the_absent_optional_file_stops_the_host_with_one_line()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await StartAsync(c => new ProbeCatalog(c.Json(ProbeFile).GetArrayLength()));
        });

        ex.Violations.ShouldBe([ReadsLine]);
    }

    [Fact]
    public void The_kit_gives_the_host_lines_for_both_builds()
    {
        var config = TestGameConfig.FromDirectory(Path.Combine(AppContext.BaseDirectory, "config"));
        var empty = ConfigDeclarationCheck.Check(config, new OptionalProbeModule(c => c.Config.HasFile(ProbeFile) ? new ProbeCatalog(1) : ProbeCatalog.Empty));
        var reads = ConfigDeclarationCheck.Check(config, new OptionalProbeModule(c => new ProbeCatalog(c.Json(ProbeFile).GetArrayLength())));

        empty.Errors.ShouldBeEmpty();
        empty.Catalog<ProbeCatalog>().ShouldBeSameAs(ProbeCatalog.Empty);
        reads.Errors.ShouldBe([ReadsLine]);
    }

    private static Task<TestHost> StartAsync(Func<ConfigContext, ProbeCatalog?> build) =>
        SampleTestHost.StartAsync(
            modules: b =>
            {
                SampleTestHost.DefaultModules(b);
                b.AddModule(new OptionalProbeModule(build));
                b.DeclareRoutes(Probe);
            },
            afterUseGameServer: app => app.MapPlayerCommand<Wire.IncrementRequest, ProbeCommand, ProbeResult>(
                Probe,
                request => new ProbeCommand(request.By),
                result => new Wire.IncrementResult { Value = result.Rows }));

    public sealed record ProbeCommand(long By);

    public sealed record ProbeResult(long Rows);

    /// <summary>The catalog of the optional probe file: its row count; <see cref="Empty"/> when the file is absent.</summary>
    public sealed class ProbeCatalog(int rows)
    {
        public static ProbeCatalog Empty { get; } = new(0);

        public int Rows { get; } = rows;
    }

    /// <summary>A command whose handler takes the catalog from DI, as every handler does.</summary>
    public sealed class ProbeHandler(ProbeCatalog catalog) : ICommandHandler<ProbeCommand, ProbeResult>
    {
        public static CommandDescriptor Descriptor { get; } = new("optprobe.read", touches: [CounterAggregates.Counter]);

        public ValueTask<CommandOutcome<ProbeResult>> HandleAsync(ProbeCommand request, ICommandContext context) =>
            ValueTask.FromResult(CommandOutcome<ProbeResult>.Ok(new ProbeResult(catalog.Rows)));
    }

    private sealed class OptionalProbeModule(Func<ConfigContext, ProbeCatalog?> build) : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "optprobe",
            DependsOn = [CounterModuleDefinition.ModuleName],
            Commands = [Command.Define<ProbeCommand, ProbeResult>(ProbeHandler.Descriptor).HandledBy<ProbeHandler>()],
            ErrorCodes = new ErrorCodeSet(21100, 21199),
            ConfigFiles = [ConfigFileDeclaration.Optional(ProbeFile)],
            ConfigCatalogs = [ConfigCatalogDeclaration.Of(build, ProbeFile)],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }
}
