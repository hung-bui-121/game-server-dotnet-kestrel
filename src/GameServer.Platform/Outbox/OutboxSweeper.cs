using GameServer.Platform.Commands;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Persistence;
using GameServer.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Outbox;

/// <summary>
/// Background sweeper (OPS-05): every 30 s relays receipts still marked <c>outboxPending</c>
/// after the inline relay window. Safe on many instances because consumers are idempotent.
/// </summary>
public sealed class OutboxSweeper(
    ITransactionalDocumentStore store,
    IOutboxRelay relay,
    IClock clock,
    GameServerOptions options,
    PlatformMetrics metrics,
    ILogger<OutboxSweeper> logger) : BackgroundService
{
    /// <summary>Runs one sweep; returns the number of receipts fully relayed.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var cutoff = now - TimeSpan.FromSeconds(options.Outbox.MinAgeSeconds);
        var pending = await store.QueryPendingOutboxAsync(cutoff, options.Outbox.BatchSize, cancellationToken);
        var relayed = 0;
        foreach (var document in pending)
        {
            var receipt = ReceiptDocument.FromJson(document.Content);
            metrics.OutboxPendingAge(now - receipt.CommittedAt);
            if (await relay.TryRelayAsync(document.Key, receipt, cancellationToken))
            {
                relayed++;
            }
        }

        return relayed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Outbox.SweeperEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Outbox.SweepIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox sweep failed.");
            }
        }
    }
}
