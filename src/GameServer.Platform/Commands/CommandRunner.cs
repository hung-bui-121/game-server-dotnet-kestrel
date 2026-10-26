using System.Diagnostics;
using System.Text.Json.Nodes;
using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Config;
using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Time;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Commands;

/// <summary>
/// Runs every state-changing command in exactly one transaction: bundle-read the aggregates and the receipt,
/// run the handler, write changed aggregates + claim markers + receipt, commit; replay committed receipts
/// (DAT-04, DAT-09, DAT-11, CMD-01..07). This is the only writer of <c>pa_*</c> and <c>receipts</c>.
/// </summary>
public sealed class CommandRunner(
    ModuleRegistry registry,
    ITransactionalDocumentStore store,
    AggregateDocumentSerializer serializer,
    IGameConfig config,
    IClock clock,
    GameServerOptions options,
    PerPlayerCommandLimiter limiter,
    IOutboxRelay relay,
    PlatformMetrics metrics,
    IServiceProvider services) : ICommandRunner
{
    public async Task<CommandExecution> ExecuteAsync(CommandInvocation invocation, CancellationToken requestAborted)
    {
        var registration = registry.GetCommand(invocation.Operation);
        var descriptor = registration.Descriptor;
        var actor = invocation.Actor;
        var playerId = actor.PlayerId
            ?? throw new GameServerException(CoreErrorCodes.BadRequest, "Command has no target player.");

        using var activity = PlatformMetrics.ActivitySource.StartActivity("command " + descriptor.Operation);
        activity?.SetTag("gs.operation", descriptor.Operation);
        activity?.SetTag("gs.request_id", invocation.RequestId);
        activity?.SetTag("gs.player_id", playerId);

        using var lease = await limiter.AcquireAsync(playerId, requestAborted);

        var receiptId = ReceiptIds.Compute(actor.ActorId, descriptor.Operation, invocation.RequestId);
        var receiptKey = ReceiptDocument.KeyFor(receiptId);

        for (var round = 1; ; round++)
        {
            // Fast replay path outside the transaction (Read Committed is valid here, DAT-09).
            var existing = await store.GetAsync(receiptKey, requestAborted);
            if (existing is not null)
            {
                var committed = ReceiptDocument.FromJson(existing.Content);
                if (IsInReplayWindow(committed, clock.UtcNow))
                {
                    return await ReplayAsync(committed, invocation, requestAborted);
                }
            }

            requestAborted.ThrowIfCancellationRequested();
            try
            {
                return await ExecuteTransactionAsync(registration, invocation, playerId, receiptId, receiptKey, requestAborted);
            }
            catch (DocumentExistsException ex)
            {
                // Another instance committed the same receipt (or claim) between our read and insert. Couchbase
                // reports this as "document exists" instead of retrying, so re-run: the receipt check above replays it.
                if (round >= MaxInsertRaceRounds)
                {
                    throw new GameServerException(CoreErrorCodes.ConcurrentUpdate, $"Insert race on {ex.Key} for {descriptor.Operation}.", ex) { RetryAfterMs = 200 };
                }
            }
        }
    }

    private const int MaxInsertRaceRounds = 3;

    private async Task<CommandExecution> ExecuteTransactionAsync(
        GameServer.Commands.CommandRegistration registration,
        CommandInvocation invocation,
        string playerId,
        string receiptId,
        DocumentKey receiptKey,
        CancellationToken requestAborted)
    {
        var descriptor = registration.Descriptor;
        var handler = ActivatorUtilities.CreateInstance(services, registration.HandlerType);
        var descriptors = descriptor.Touches.Concat(descriptor.Reads).Distinct(StringComparer.Ordinal).Select(registry.GetAggregate).ToArray();
        var keys = descriptors.Select(d => AggregateDocumentSerializer.KeyFor(d, playerId)).Append(receiptKey).ToArray();
        var attempts = 0;
        var stopwatch = Stopwatch.StartNew();

        AttemptResult result;
        try
        {
            // No request token below this line: a client disconnect must not abort the commit (PERF-02).
            result = await store.RunTransactionAsync(
                tx => RunAttemptAsync(tx, attempts++, registration, handler, invocation, playerId, receiptId, receiptKey, descriptors, keys),
                new TransactionRunOptions(TimeSpan.FromSeconds(options.Transactions.ExpirationSeconds)));
        }
        catch (CommandRejectedException rejected)
        {
            return new CommandExecution(rejected.Code, null, [], [], false, config.ConfigVersion);
        }
        catch (TransactionCommitAmbiguousException ex)
        {
            metrics.TransactionAmbiguous(descriptor.Operation);
            throw new GameServerException(CoreErrorCodes.CommitUnknownRetry, $"Commit ambiguous for {descriptor.Operation}.", ex) { RetryAfterMs = 1000 };
        }
        catch (TransactionConflictExpiredException ex)
        {
            metrics.TransactionExpired(descriptor.Operation);
            throw new GameServerException(CoreErrorCodes.ConcurrentUpdate, $"Transaction expired for {descriptor.Operation}.", ex) { RetryAfterMs = 200 };
        }
        catch (TransactionFailedException ex)
        {
            throw new GameServerException(CoreErrorCodes.Internal, $"Transaction failed for {descriptor.Operation}.", ex);
        }
        finally
        {
            metrics.TransactionDuration(descriptor.Operation, stopwatch.Elapsed);
        }

        if (result.AlreadyCommitted is { } raced)
        {
            return await ReplayAsync(raced, invocation, requestAborted);
        }

        var receipt = result.Receipt!;
        metrics.ReceiptWritten(result.ReceiptSize);
        foreach (var write in result.Writes)
        {
            metrics.AggregateWritten(write.Before.Descriptor.AggregateId, write.SizeBytes, write.SizeBytes >= write.Before.Descriptor.WarnBytes);
        }

        if (receipt.Outbox.Count > 0 && options.Outbox.InlineRelayEnabled)
        {
            await relay.TryRelayAsync(receiptKey, receipt, CancellationToken.None);
        }

        var updates = result.Writes
            .Where(w => w.Before.Descriptor.ClientVisible)
            .Select(w => new AggregateChange(
                w.Before.Descriptor.AggregateId,
                w.Before.Rev,
                w.NewRev,
                AggregateDocumentSerializer.DeserializeState(w.NewStateJson, w.Before.Descriptor.StateType)))
            .ToArray();
        return new CommandExecution(CoreErrorCodes.Ok, receiptId, updates, result.ResultBytes, false, config.ConfigVersion);
    }

    private async Task<AttemptResult> RunAttemptAsync(
        ITransactionContext tx,
        int attempt,
        GameServer.Commands.CommandRegistration registration,
        object handler,
        CommandInvocation invocation,
        string playerId,
        string receiptId,
        DocumentKey receiptKey,
        PlayerAggregateDescriptor[] descriptors,
        DocumentKey[] keys)
    {
        var descriptor = registration.Descriptor;
        metrics.TransactionAttempt(descriptor.Operation, attempt > 0);

        var documents = await tx.GetManyAsync(keys);
        var now = clock.UtcNow;
        var receiptDocument = documents[^1];
        if (receiptDocument is not null)
        {
            var committed = ReceiptDocument.FromJson(receiptDocument.Content);
            if (IsInReplayWindow(committed, now))
            {
                return AttemptResult.Replay(committed);
            }
        }

        var loaded = new Dictionary<string, LoadedAggregate>(StringComparer.Ordinal);
        for (var i = 0; i < descriptors.Length; i++)
        {
            loaded[descriptors[i].AggregateId] = serializer.Load(descriptors[i], playerId, documents[i]);
        }

        var context = new CommandContext(invocation.Actor, now, config, loaded, descriptor, tx, receiptId);
        var outcome = await registration.InvokeAsync(handler, invocation.Request, context);
        if (!outcome.IsOk)
        {
            // Throwing rolls back claim markers and anything staged (research R5: no receipt for rejections).
            throw new CommandRejectedException(outcome.Error ?? CoreErrorCodes.Internal);
        }

        var resultBytes = invocation.EncodeResult(outcome.Result!);
        if (resultBytes.Length > options.Commands.ResultCompactMaxBytes)
        {
            throw new InvalidOperationException(
                $"Result of '{descriptor.Operation}' is {resultBytes.Length} bytes; resultCompact max is {options.Commands.ResultCompactMaxBytes} (CMD-03).");
        }

        var writes = new List<AggregateWrite>();
        foreach (var aggregateId in descriptor.Touches)
        {
            var before = loaded[aggregateId];
            if (!context.TryGetNewState(aggregateId, out var newJson) || newJson.AsSpan().SequenceEqual(before.StateJson))
            {
                continue;
            }

            var newRev = before.Rev + 1;
            var (document, size) = serializer.BuildDocument(before, newJson, newRev, now);
            if (size > before.Descriptor.HardBytes)
            {
                throw new CommandRejectedException(CoreErrorCodes.AggregateTooLarge);
            }

            writes.Add(new AggregateWrite(before, document, size, newRev, newJson));
        }

        foreach (var write in writes)
        {
            if (write.Before.Document is null)
            {
                await tx.InsertAsync(AggregateDocumentSerializer.KeyFor(write.Before.Descriptor, playerId), write.Document);
            }
            else
            {
                await tx.ReplaceAsync(write.Before.Document, write.Document);
            }
        }

        foreach (var (claimKey, claim) in context.Claims)
        {
            await tx.InsertAsync(claimKey, claim);
        }

        var receipt = BuildReceipt(invocation, descriptor.Operation, playerId, receiptId, resultBytes, loaded, writes, context, now);
        var receiptJson = receipt.ToJson();
        var receiptSize = ReceiptDocument.SizeOf(receiptJson);
        if (receiptSize > options.Commands.ReceiptMaxBytes)
        {
            throw new InvalidOperationException(
                $"Receipt of '{descriptor.Operation}' is {receiptSize} bytes; max is {options.Commands.ReceiptMaxBytes} (CMD-03).");
        }

        if (receiptDocument is null)
        {
            await tx.InsertAsync(receiptKey, receiptJson);
        }
        else
        {
            // An expired receipt (outside the replay window) is superseded by the new execution (CMD-07).
            await tx.ReplaceAsync(receiptDocument, receiptJson);
        }

        return AttemptResult.Commit(receipt, receiptSize, writes, resultBytes);
    }

    private ReceiptDocument BuildReceipt(
        CommandInvocation invocation,
        string operation,
        string playerId,
        string receiptId,
        byte[] resultBytes,
        Dictionary<string, LoadedAggregate> loaded,
        List<AggregateWrite> writes,
        CommandContext context,
        DateTimeOffset now) =>
        new()
        {
            ReceiptId = receiptId,
            GameId = options.GameId,
            EnvironmentId = options.EnvironmentId,
            ActorId = invocation.Actor.ActorId,
            ActorType = invocation.Actor.ActorType switch
            {
                ActorType.Player => "player",
                ActorType.Gm => "gm",
                _ => "system",
            },
            PlayerId = playerId,
            Operation = operation,
            RequestId = invocation.RequestId,
            BodyHash = invocation.BodyHash,
            ResultCode = CoreErrorCodes.Ok.Name,
            ResultCompact = Convert.ToBase64String(resultBytes),
            ConfigVersion = config.ConfigVersion,
            SchemaVersions = loaded.Values.ToDictionary(l => l.Descriptor.AggregateId, l => l.Descriptor.SchemaVersion, StringComparer.Ordinal),
            Aggregates = writes.Select(w => new ReceiptAggregate(w.Before.Descriptor.AggregateId, w.NewRev)).ToList(),
            ClaimKeys = context.Claims.Keys.Select(k => $"{k.Collection}/{k.Key}").ToList(),
            CommittedAtUtc = AggregateDocumentSerializer.FormatTime(now),
            Outbox = context.Outbox.ToList(),
            OutboxPending = context.Outbox.Count > 0,
            CorrelationId = invocation.Actor.CorrelationId,
        };

    private async Task<CommandExecution> ReplayAsync(ReceiptDocument receipt, CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (!string.Equals(receipt.BodyHash, invocation.BodyHash, StringComparison.Ordinal))
        {
            throw new GameServerException(
                CoreErrorCodes.IdempotencyPayloadMismatch,
                $"requestId {invocation.RequestId} reused with a different body for {receipt.Operation}.");
        }

        var visible = receipt.Aggregates
            .Select(a => (Committed: a, Descriptor: registry.Aggregates.GetValueOrDefault(a.AggregateId)))
            .Where(x => x.Descriptor is { ClientVisible: true })
            .ToArray();
        var documents = await store.GetManyAsync(
            visible.Select(x => AggregateDocumentSerializer.KeyFor(x.Descriptor!, receipt.PlayerId)).ToArray(),
            cancellationToken);

        // Current state with rev >= committed newRev, full = true (CMD-04).
        var updates = visible
            .Select((x, i) =>
            {
                var current = serializer.Load(x.Descriptor!, receipt.PlayerId, documents[i]);
                return new AggregateChange(x.Committed.AggregateId, x.Committed.NewRev - 1, current.Rev, current.State);
            })
            .ToArray();

        var code = registry.Errors.FindByName(receipt.ResultCode) ?? CoreErrorCodes.Ok;
        return new CommandExecution(code, receipt.ReceiptId, updates, Convert.FromBase64String(receipt.ResultCompact), true, config.ConfigVersion);
    }

    private bool IsInReplayWindow(ReceiptDocument receipt, DateTimeOffset now) =>
        receipt.CommittedAt + TimeSpan.FromHours(options.Commands.ReplayWindowHours) > now;

    private sealed record AggregateWrite(LoadedAggregate Before, JsonObject Document, int SizeBytes, long NewRev, byte[] NewStateJson);

    private sealed class AttemptResult
    {
        public ReceiptDocument? AlreadyCommitted { get; private init; }

        public ReceiptDocument? Receipt { get; private init; }

        public int ReceiptSize { get; private init; }

        public IReadOnlyList<AggregateWrite> Writes { get; private init; } = [];

        public byte[] ResultBytes { get; private init; } = [];

        public static AttemptResult Replay(ReceiptDocument committed) => new() { AlreadyCommitted = committed };

        public static AttemptResult Commit(ReceiptDocument receipt, int size, IReadOnlyList<AggregateWrite> writes, byte[] result) =>
            new() { Receipt = receipt, ReceiptSize = size, Writes = writes, ResultBytes = result };
    }

    /// <summary>Business rejection: thrown inside the attempt so the transaction rolls back.</summary>
    private sealed class CommandRejectedException(ErrorCode code) : Exception(code.Name)
    {
        public ErrorCode Code { get; } = code;
    }
}
