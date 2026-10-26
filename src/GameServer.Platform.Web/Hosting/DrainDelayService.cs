using Microsoft.Extensions.Hosting;

namespace GameServer.Platform.Web.Hosting;

/// <summary>Set when the host starts stopping, before servers stop accepting (readiness reads it, OPS-03).</summary>
public sealed class ShutdownSignal
{
    private volatile bool _stopping;

    public bool IsStopping => _stopping;

    internal void MarkStopping() => _stopping = true;
}

/// <summary>
/// OPS-03: <c>ApplicationStopping</c> has already flipped readiness to 503; wait <c>DrainDelaySeconds</c> before the web
/// server stops accepting connections so the load balancer drains this instance without refused requests.
/// </summary>
internal sealed class DrainDelayService(GameServerOptions options, ShutdownSignal signal) : IHostedLifecycleService
{
    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        // Lifecycle StoppingAsync runs before ApplicationStopping is signalled: flip readiness here first.
        signal.MarkStopping();
        return options.Hosting.DrainDelaySeconds > 0
            ? Task.Delay(TimeSpan.FromSeconds(options.Hosting.DrainDelaySeconds), cancellationToken)
            : Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
