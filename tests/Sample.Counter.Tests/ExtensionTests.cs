using System.Net;
using System.Reflection;
using GameServer.Commands;
using GameServer.Modules.Identity;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Adapters;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace Sample.Counter.Tests;

/// <summary>US2 / ACC-13: add a module, replace a policy or handler, use a module as a service, without touching Core.</summary>
public sealed class ExtensionTests
{
    private const string Increment = "/api/v1/counter/increment";

    private static async Task<CommandResponse> IncrementAsync(HttpClient client, long by, string? requestId = null) =>
        await (await client.PostProtobufAsync(Increment, new Wire.IncrementRequest { By = by }, requestId ?? ProtobufHttp.NewRequestId()))
            .ReadProtobufAsync<CommandResponse>();

    [Fact]
    public async Task Default_policy_limits_increment_to_ten()
    {
        await using var host = await TestHost.StartAsync();

        (await IncrementAsync(host.PlayerClient("p1"), 50)).Code.ShouldBe("COUNTER_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task Game_can_replace_the_policy()
    {
        await using var host = await TestHost.StartAsync(b => b
            .AddModule<IdentityModule>()
            .AddModule<CounterModule>()
            .ConfigureExtensions(x => x.ReplacePolicy<ICounterLimitPolicy, GenerousPolicy>()));

        var response = await IncrementAsync(host.PlayerClient("p1"), 50);

        response.Code.ShouldBe("OK");
        Wire.IncrementResult.Parser.ParseFrom(response.Result).Value.ShouldBe(50);
    }

    [Fact]
    public async Task Game_can_replace_the_handler_and_keeps_idempotency()
    {
        await using var host = await TestHost.StartAsync(b => b
            .AddModule<IdentityModule>()
            .AddModule<CounterModule>()
            .ConfigureExtensions(x => x.ReplaceHandler<IncrementCommand, IncrementResult, DoublingHandler>(IncrementHandler.Operation)));
        var player = host.PlayerClient("p1");
        var requestId = ProtobufHttp.NewRequestId();

        var first = await IncrementAsync(player, 3, requestId);
        var replay = await IncrementAsync(player, 3, requestId);

        Wire.IncrementResult.Parser.ParseFrom(first.Result).Value.ShouldBe(6);
        replay.Replayed.ShouldBeTrue();
        Wire.IncrementResult.Parser.ParseFrom(replay.Result).Value.ShouldBe(6);
    }

    [Fact]
    public async Task Service_only_module_maps_no_endpoint_but_its_service_works()
    {
        await using var host = await TestHost.StartAsync(b => b
            .AddModule<IdentityModule>()
            .AddModule<CounterModule>(m => m.ServiceOnly()));

        var response = await host.PlayerClient("p1").PostProtobufAsync(Increment, new Wire.IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId());
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var reading = await host.Services.GetRequiredService<ICounterService>().GetAsync("p1", CancellationToken.None);
        reading.ShouldBe(new CounterReading(0, 0));
    }

    [Fact]
    public void Core_does_not_reference_modules_or_infrastructure()
    {
        var core = typeof(ICommandHandler<,>).Assembly;
        var references = core.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        references.ShouldNotContain(n => n.StartsWith("Sample.", StringComparison.Ordinal));
        references.ShouldNotContain(n => n.StartsWith("GameServer.Platform", StringComparison.Ordinal));
        references.ShouldNotContain(n => n.StartsWith("Couchbase", StringComparison.Ordinal));
        references.ShouldNotContain(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        references.ShouldNotContain(n => n.StartsWith("Google.Protobuf", StringComparison.Ordinal));
    }

    [Fact]
    public void Business_project_does_not_reference_platform_or_wire_types()
    {
        var business = typeof(IncrementHandler).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        business.ShouldNotContain(n => n.StartsWith("GameServer.Platform", StringComparison.Ordinal));
        business.ShouldNotContain(n => n.StartsWith("Google.Protobuf", StringComparison.Ordinal));
        business.ShouldNotContain(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    private sealed class GenerousPolicy : ICounterLimitPolicy
    {
        public long MaxPerIncrement => 100;
    }

    private sealed class DoublingHandler : ICommandHandler<IncrementCommand, IncrementResult>
    {
        public ValueTask<CommandOutcome<IncrementResult>> HandleAsync(IncrementCommand request, ICommandContext context)
        {
            var state = context.Get<CounterState>(CounterAggregates.Counter);
            var next = state with { Value = state.Value + (request.By * 2), Increments = state.Increments + 1 };
            context.Set(CounterAggregates.Counter, next);
            return ValueTask.FromResult(CommandOutcome<IncrementResult>.Ok(new IncrementResult(next.Value)));
        }
    }
}
