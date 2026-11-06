using GameServer.Actors;
using GameServer.Modules;
using GameServer.Modules.Gm;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Gm.Tests.Infrastructure;

/// <summary>
/// Lets a test hold GM requests inside the pipeline (slice 018 T004): <see cref="WaitAsync"/> returns when the test calls
/// <see cref="Release"/> or the request is aborted (the GM request timeout cancels it). <see cref="Entered"/> completes once a
/// request holds a limiter permit and waits, so the test knows when a second call meets a full limiter.
/// </summary>
public sealed class CorsProbeGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void Release() => _released.TrySetResult();

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>
/// Test-only GM module (slice 018 T004, architect A3): endpoints whose answers come from the platform's error writers that a
/// framework route cannot trigger on demand — an exception (500 from the exception middleware) and a request that waits (429 from
/// the GM limiter, 503 from the GM request timeout). Added through <c>GmTestHost.StartAsync(extra: …)</c> only, so the committed
/// framework document never contains it.
/// </summary>
public sealed class CorsProbeModule : IGameModule, IGmApiModule
{
    public const string ThrowRoute = "/gm/v1/probe-cors/throw";
    public const string WaitRoute = "/gm/v1/probe-cors/wait";

    public static RouteDeclaration Throw { get; } =
        RouteDeclaration.Get("probe_cors.throw", ThrowRoute, SecurityProfile.Gm, RouteSurface.GmApi, GmPermissions.Access);

    public static RouteDeclaration Wait { get; } =
        RouteDeclaration.Get("probe_cors.wait", WaitRoute, SecurityProfile.Gm, RouteSurface.GmApi, GmPermissions.Access);

    public ModuleDescriptor Describe() => new()
    {
        Name = "probe_cors",
        DependsOn = [GmModule.ModuleName],
        Routes = [Throw, Wait],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context) => services.AddSingleton<CorsProbeGate>();

    public void MapGmApi(IEndpointRouteBuilder routes)
    {
        routes.MapDeclared(Throw, (Func<HttpContext, IResult>)(_ => throw new InvalidOperationException("CORS probe: unhandled exception.")))
            .WithSummary("Throw an unhandled exception (CORS probe).")
            .AsGmEndpoint(GmPermissions.Access);
        routes.MapDeclared(Wait, (Func<HttpContext, Task<IResult>>)(async http =>
            {
                await http.RequestServices.GetRequiredService<CorsProbeGate>().WaitAsync(http.RequestAborted);
                return Results.NoContent();
            }))
            .WithSummary("Wait until the test releases the gate (CORS probe).")
            .AsGmEndpoint(GmPermissions.Access);
    }
}
