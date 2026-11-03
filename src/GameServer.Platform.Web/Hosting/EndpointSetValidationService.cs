using Microsoft.Extensions.Hosting;

namespace GameServer.Platform.Web.Hosting;

/// <summary>
/// FR-012 (research R8, VERIFY V1): every endpoint validator again over the <b>final</b> endpoint set — including endpoints mapped
/// after <c>UseGameServer</c> — plus declared routes that are not mapped, before the web host builds its pipeline and binds the
/// server. Registered first by <c>AddGameServer</c>, so its <see cref="StartingAsync"/> also runs before other services' start
/// I/O (key ring). One <see cref="GameServerStartupException"/> carries every line; no setting turns it off (FR-013).
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
        var errors = EndpointValidation.Run(endpoints, services, includeUnmapped: true);
        return errors.Count == 0 ? Task.CompletedTask : throw new GameServerStartupException(errors);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
