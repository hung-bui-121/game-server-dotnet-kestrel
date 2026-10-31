using System.Net;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Web.Commands;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// API-11: a handler committing with a code missing from every module's ErrorCodeSet is a module bug. The receipt stores the
/// code by name, so it would reach the client as OK; instead the command answers INTERNAL, commits nothing and logs the code.
/// </summary>
public sealed class UncataloguedCommitCodeTests
{
    private const string Route = "/api/v1/test/uncatalogued";

    [Fact]
    public async Task Commit_with_an_uncatalogued_code_is_internal_commits_nothing_and_logs_the_code()
    {
        // Mapped before UseGameServer: the route's operation is registered, so the startup route check accepts it.
        await using var host = await TestHost.StartAsync(
            modules: b =>
            {
                TestHost.DefaultModules(b);
                b.AddModule(new UncataloguedModule());
            },
            beforeUseGameServer: app => app.MapPlayerCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(
                Route,
                UncataloguedHandler.Operation,
                request => new IncrementCommand(request.By, null),
                result => new Wire.IncrementResult { Value = result.Value }));

        var response = await host.PlayerClient("p1").PostProtobufAsync(Route, new Wire.IncrementRequest { By = 2 }, ProtobufHttp.NewRequestId());

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.ReadErrorAsync()).Code.ShouldBe("INTERNAL");
        host.Store.Count("pa_counter").ShouldBe(0);
        host.Store.Count("receipts").ShouldBe(0);
        host.Logs.ShouldContain(log => log.Contains(
            "Command 'uncatalogued.commit' (module 'uncatalogued') committed with error code UNCATALOGUED_NOT_LISTED(22001), " +
            "which is not in the error-code catalog",
            StringComparison.Ordinal));
    }

    public sealed class UncataloguedHandler : ICommandHandler<IncrementCommand, IncrementResult>
    {
        public const string Operation = "uncatalogued.commit";

        /// <summary>In the module's range but not listed in its ErrorCodeSet.</summary>
        public static readonly ErrorCode NotListed = new(22001, "UNCATALOGUED_NOT_LISTED", 200, false, "Not listed.");

        public static CommandDescriptor Descriptor { get; } = new(Operation, touches: [CounterAggregates.Counter]);

        public ValueTask<CommandOutcome<IncrementResult>> HandleAsync(IncrementCommand request, ICommandContext context)
        {
            var counter = context.Get<CounterState>(CounterAggregates.Counter);
            context.Set(CounterAggregates.Counter, counter with { Value = counter.Value + request.By });
            return ValueTask.FromResult(CommandOutcome<IncrementResult>.Commit(new IncrementResult(counter.Value + request.By), NotListed));
        }
    }

    private sealed class UncataloguedModule : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "uncatalogued",
            DependsOn = [CounterModuleDefinition.ModuleName],
            Commands = [Command.Define<IncrementCommand, IncrementResult>(UncataloguedHandler.Descriptor).HandledBy<UncataloguedHandler>()],
            ErrorCodes = new ErrorCodeSet(22000, 22099),
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }
}
