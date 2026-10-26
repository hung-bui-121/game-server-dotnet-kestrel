using System.Threading.RateLimiting;
using GameServer.Errors;

namespace GameServer.Platform.Commands;

/// <summary>
/// Serializes commands of one player inside an instance (CMD-06): concurrency 1, bounded queue.
/// Across instances ordering comes from transaction conflicts.
/// </summary>
public sealed class PerPlayerCommandLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public PerPlayerCommandLimiter(GameServerOptions options)
    {
        var queue = options.Commands.PerPlayerQueueLimit;
        _limiter = PartitionedRateLimiter.Create<string, string>(playerId =>
            RateLimitPartition.GetConcurrencyLimiter(playerId, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 1,
                QueueLimit = queue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));
    }

    public async ValueTask<RateLimitLease> AcquireAsync(string playerId, CancellationToken cancellationToken)
    {
        var lease = await _limiter.AcquireAsync(playerId, 1, cancellationToken);
        if (!lease.IsAcquired)
        {
            lease.Dispose();
            throw new GameServerException(CoreErrorCodes.RateLimited, $"Per-player queue full for {playerId}.") { RetryAfterMs = 500 };
        }

        return lease;
    }

    public void Dispose() => _limiter.Dispose();
}
