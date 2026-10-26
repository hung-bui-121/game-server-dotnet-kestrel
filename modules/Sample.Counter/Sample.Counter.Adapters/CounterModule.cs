using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Protobuf;
using GameServer.Platform.Web.Routing;
using GameServer.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wire = Sample.Counter.Protocol.V1;

namespace Sample.Counter.Adapters;

/// <summary>
/// Sample module (ACC-13, EXT-05): one command, one query, one replaceable policy, one GM endpoint,
/// one outbox projection. Registered with <c>AddModule&lt;CounterModule&gt;()</c>.
/// </summary>
public sealed class CounterModule : IGameModule, IPlayerApiModule, IGmApiModule
{
    public const int MaxNoteLength = 64;

    public ModuleDescriptor Describe() => CounterModuleDefinition.Describe();

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.TryAddSingleton<ICounterLimitPolicy, DefaultCounterLimitPolicy>();
        services.TryAddSingleton<ICounterService, CounterService>();
        services.AddAggregateWireMapper<CounterState>(CounterAggregates.Counter, ToWire);
    }

    public void MapPlayerApi(IEndpointRouteBuilder routes)
    {
        routes.MapPlayerCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(
            "/api/v1/counter/increment",
            IncrementHandler.Operation,
            ToDomain,
            result => new Wire.IncrementResult { Value = result.Value });

        routes.MapPost("/api/v1/counter/get", async (HttpContext http) =>
            {
                var services = http.RequestServices;
                var actor = services.GetRequiredService<ActorContextAccessor>().GetActor(http);
                var reading = await services.GetRequiredService<ICounterService>().GetAsync(actor.PlayerId!, http.RequestAborted);
                await ProtobufBinding.WriteAsync(http, new Wire.GetCounterResponse
                {
                    Value = reading.Value,
                    Rev = reading.Rev,
                    ServerTimeUtcMs = services.GetRequiredService<IClock>().UtcNow.ToUnixTimeMilliseconds(),
                });
            })
            .AsPlayerEndpoint();
    }

    public void MapGmApi(IEndpointRouteBuilder routes) =>
        routes.MapGet("/gm/v1/counter/{playerId}", async (string playerId, ICounterService counters, CancellationToken cancellationToken) =>
            {
                var reading = await counters.GetAsync(playerId, cancellationToken);
                return Results.Json(new { playerId, value = reading.Value, rev = reading.Rev });
            })
            .AsGmEndpoint(CounterModuleDefinition.ReadPermission);

    /// <summary>Wire → domain mapping and validation live in the adapter (ARC-02).</summary>
    private static IncrementCommand ToDomain(Wire.IncrementRequest request)
    {
        if (request.Note.Length > MaxNoteLength)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, $"note longer than {MaxNoteLength} characters.");
        }

        return new IncrementCommand(request.By, string.IsNullOrEmpty(request.Note) ? null : request.Note);
    }

    private static Wire.CounterState ToWire(CounterState state) => new() { Value = state.Value, Increments = state.Increments };
}

/// <summary>Reads the counter outside a transaction (snapshot semantics, PLY-02).</summary>
internal sealed class CounterService(PlayerSnapshotReader reader) : ICounterService
{
    public async Task<CounterReading> GetAsync(string playerId, CancellationToken cancellationToken)
    {
        var loaded = await reader.ReadOneAsync(playerId, CounterAggregates.Counter, cancellationToken);
        return new CounterReading(((CounterState)loaded.State).Value, loaded.Rev);
    }
}
