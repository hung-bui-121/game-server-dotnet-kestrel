using Microsoft.Extensions.Hosting;

namespace GameServer.Platform.Web.Hosting;

/// <summary>
/// FR-012 (research R8, V1, V16): every endpoint validator over the <b>final</b> endpoint set — including endpoints mapped after
/// <c>UseGameServer</c> — plus declared routes that are not mapped, before the web host builds its pipeline and binds the server.
/// The only endpoint run of a passing configuration (<c>UseGameServer</c> runs the validators only when its other checks already
/// failed, so the endpoint set is built once before serving, T035); on success it logs the startup line (FR-054). Registered
/// first by <c>AddGameServer</c>, so its <see cref="StartingAsync"/> also runs before other services' start I/O (key ring). One
/// <see cref="GameServerStartupException"/> carries every line; no setting turns it off (FR-013).
/// </summary>
internal sealed class EndpointSetValidationService(EndpointValidationState state, IServiceProvider services) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (state.Routes is not { } routes)
        {
            return Task.CompletedTask;
        }

        var endpoints = routes.DataSources.SelectMany(source => source.Endpoints).ToArray();
        // Nothing is reported before this run: a configuration that reached it passed every check of UseGameServer.
        var errors = EndpointValidation.Run(endpoints, services, includeUnmapped: true, reported: []);
        if (errors.Count > 0)
        {
            throw new GameServerStartupException(errors);
        }

        GameServerApplicationExtensions.LogStartup(services);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
