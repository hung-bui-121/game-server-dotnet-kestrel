using System.Text.Json.Nodes;
using GameServer.Platform.Commands;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Time;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Outbox;

/// <summary>Default consumer: inserts <c>&lt;kind&gt;::&lt;entryId&gt;</c> into a collection if absent.</summary>
public sealed class ProjectionOutboxConsumer(string kind, string collection, ITransactionalDocumentStore store, GameServerOptions options)
    : IOutboxConsumer
{
    public string Kind { get; } = kind;

    public string Collection { get; } = collection;

    public Task HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
    {
        var document = new JsonObject
        {
            ["entryId"] = delivery.Entry.EntryId,
            ["kind"] = delivery.Entry.Kind,
            ["gameId"] = options.GameId,
            ["environmentId"] = options.EnvironmentId,
            ["receiptId"] = delivery.ReceiptId,
            ["playerId"] = delivery.PlayerId,
            ["actorId"] = delivery.ActorId,
            ["operation"] = delivery.Operation,
            ["committedAtUtc"] = delivery.CommittedAtUtc,
            ["payload"] = delivery.Entry.Payload?.DeepClone(),
        };
        return store.InsertIfAbsentAsync(new DocumentKey(Collection, $"{Kind}::{delivery.Entry.EntryId}"), document, cancellationToken);
    }
}

/// <summary>Relays the outbox of a committed receipt (JOB-02). Best effort: failures leave the receipt pending for the sweeper.</summary>
public interface IOutboxRelay
{
    /// <summary>Returns true when every entry was delivered and the receipt was marked relayed.</summary>
    Task<bool> TryRelayAsync(DocumentKey receiptKey, ReceiptDocument receipt, CancellationToken cancellationToken);
}

public sealed class OutboxRelay : IOutboxRelay
{
    private readonly Dictionary<string, IOutboxConsumer> _consumers = new(StringComparer.Ordinal);
    private readonly ITransactionalDocumentStore _store;
    private readonly IClock _clock;
    private readonly OutboxSettings _settings;
    private readonly PlatformMetrics _metrics;
    private readonly ILogger<OutboxRelay> _logger;

    public OutboxRelay(
        IEnumerable<IOutboxConsumer> consumers,
        ModuleRegistry registry,
        ITransactionalDocumentStore store,
        IClock clock,
        GameServerOptions options,
        PlatformMetrics metrics,
        ILogger<OutboxRelay> logger)
    {
        _store = store;
        _clock = clock;
        _settings = options.Outbox;
        _metrics = metrics;
        _logger = logger;
        foreach (var projection in registry.OutboxProjections)
        {
            _consumers[projection.Kind] = new ProjectionOutboxConsumer(projection.Kind, projection.Collection, store, options);
        }

        foreach (var consumer in consumers)
        {
            if (!_consumers.TryAdd(consumer.Kind, consumer))
            {
                throw new GameServerStartupException(
                    $"[outbox] two consumers handle kind '{consumer.Kind}'. Fix: keep one projection or IOutboxConsumer per kind.");
            }
        }
    }

    public IReadOnlyCollection<string> Kinds => _consumers.Keys;

    public async Task<bool> TryRelayAsync(DocumentKey receiptKey, ReceiptDocument receipt, CancellationToken cancellationToken)
    {
        var allDelivered = true;
        foreach (var entry in receipt.Outbox)
        {
            if (!_consumers.TryGetValue(entry.Kind, out var consumer))
            {
                allDelivered = false;
                _metrics.OutboxRelayFailed(entry.Kind);
                _logger.LogError("No outbox consumer for kind {Kind} (entry {EntryId}).", entry.Kind, entry.EntryId);
                continue;
            }

            try
            {
                var delivery = new OutboxDelivery(entry, receipt.ReceiptId, receipt.PlayerId, receipt.ActorId, receipt.Operation, receipt.CommittedAtUtc);
                await consumer.HandleAsync(delivery, cancellationToken);
            }
            catch (Exception ex)
            {
                allDelivered = false;
                _metrics.OutboxRelayFailed(entry.Kind);
                _logger.LogWarning(ex, "Outbox relay failed for entry {EntryId} of kind {Kind}; the sweeper will retry.", entry.EntryId, entry.Kind);
            }
        }

        if (!allDelivered)
        {
            await RecordFailureAsync(receiptKey, receipt, cancellationToken);
            return false;
        }

        try
        {
            await _store.MarkOutboxRelayedAsync(receiptKey, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _metrics.OutboxRelayFailed("mark_relayed");
            _logger.LogWarning(ex, "Could not mark receipt {ReceiptId} relayed; the sweeper will retry.", receipt.ReceiptId);
            return false;
        }
    }

    /// <summary>Exponential backoff per receipt so failing receipts never block newer ones (review M1).</summary>
    private async Task RecordFailureAsync(DocumentKey receiptKey, ReceiptDocument receipt, CancellationToken cancellationToken)
    {
        var attempts = receipt.OutboxAttempts + 1;
        var delaySeconds = Math.Min(_settings.MaxRetryDelaySeconds, _settings.RetryDelaySeconds * Math.Pow(2, attempts - 1));
        var deadLetter = attempts >= _settings.MaxRelayAttempts;
        try
        {
            await _store.MarkOutboxFailedAsync(receiptKey, attempts, _clock.UtcNow.AddSeconds(delaySeconds), deadLetter, cancellationToken);
            if (deadLetter)
            {
                _metrics.OutboxDeadLettered();
                _logger.LogError("Receipt {ReceiptId} dead-lettered after {Attempts} relay attempts.", receipt.ReceiptId, attempts);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record relay failure of receipt {ReceiptId}.", receipt.ReceiptId);
        }
    }
}
