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
using Microsoft.Extensions.Logging;

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
    IEnumerable<ICommandGuard> guards,
    IServiceProvider services,
    ILogger<CommandRunner> logger) : ICommandRunner
{
    public async Task<CommandExecution> ExecuteAsync(CommandInvocation invocation, CancellationToken requestAborted)
    {
        try
        {
            var execution = await ExecuteCoreAsync(invocation, requestAborted);
            metrics.CommandCompleted(invocation.Operation, execution.Code.Name, execution.Replayed);
            return execution;
        }
        catch (GameServerException ex)
        {
            metrics.CommandCompleted(invocation.Operation, ex.Code.Name, replayed: false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metrics.CommandCompleted(invocation.Operation, CoreErrorCodes.Internal.Name, replayed: false);
            throw;
        }
    }

    private async Task<CommandExecution> ExecuteCoreAsync(CommandInvocation invocation, CancellationToken requestAborted)
    {
        var registration = registry.GetCommand(invocation.Operation);
        var descriptor = registration.Descriptor;
        var actor = invocation.Actor;
        var playerId = actor.PlayerId
            ?? throw new GameServerException(CoreErrorCodes.BadRequest, "Command has no target player.");
        EnsureActorAllowed(descriptor, actor);

        using var activity = PlatformMetrics.ActivitySource.StartActivity("command " + descriptor.Operation);
        activity?.SetTag("gs.operation", descriptor.Operation);
        activity?.SetTag("gs.request_id", invocation.RequestId);
        activity?.SetTag("gs.player_id", playerId);

        using var lease = await limiter.AcquireAsync(playerId, requestAborted);

        var receiptId = ReceiptIds.Compute(actor.ActorId, playerId, descriptor.Operation, invocation.RequestId);
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

            // Guards (e.g. command-scope bans) apply to new executions only; a committed retry still replays (CMD-04).
            foreach (var guard in guards)
            {
                if (await guard.CheckAsync(actor, descriptor, requestAborted) is { } rejection)
                {
                    throw new GameServerException(rejection, $"Command {descriptor.Operation} rejected by {guard.GetType().Name}.");
                }
            }

            try
            {
                return await ExecuteTransactionAsync(registration, invocation, playerId, receiptId, receiptKey, requestAborted);
            }
            catch (Exception ex) when (ex is DocumentExistsException or StaleReadException)
            {
                // Another instance committed the same receipt/claim between our read and insert (Couchbase reports
                // "document exists" instead of retrying), or a document changed after the bundle read.
                // Re-run: the receipt check above replays a committed duplicate.
                if (round >= MaxInsertRaceRounds)
                {
                    throw new GameServerException(CoreErrorCodes.ConcurrentUpdate, $"Write race for {descriptor.Operation}.", ex) { RetryAfterMs = 200 };
                }
            }
        }
    }

    private const int MaxInsertRaceRounds = 3;

    public async Task<CommandExecution?> TryReplayAsync(string operation, ActorContext actor, string requestId, string bodyHash, CancellationToken requestAborted)
    {
        var descriptor = registry.GetCommand(operation).Descriptor;
        var playerId = actor.PlayerId
            ?? throw new GameServerException(CoreErrorCodes.BadRequest, "Command has no target player.");
        EnsureActorAllowed(descriptor, actor);

        var receiptId = ReceiptIds.Compute(actor.ActorId, playerId, operation, requestId);
        var existing = await store.GetAsync(ReceiptDocument.KeyFor(receiptId), requestAborted);
        if (existing is null)
        {
            return null;
        }

        var committed = ReceiptDocument.FromJson(existing.Content);
        if (!IsInReplayWindow(committed, clock.UtcNow))
        {
            return null;
        }

        var execution = await ReplayAsync(committed, bodyHash, requestId, requestAborted);
        metrics.CommandCompleted(operation, execution.Code.Name, replayed: true);
        return execution;
    }

    /// <summary>
    /// Defense in depth behind endpoint metadata (SEC-07, SEC-08): Player commands run for players only, GM commands
    /// for GM actors holding the command's permission; Dev commands accept either (OPS-09 keeps them out of Production).
    /// </summary>
    private static void EnsureActorAllowed(GameServer.Commands.CommandDescriptor descriptor, ActorContext actor)
    {
        var allowed = descriptor.Profile switch
        {
            SecurityProfile.Player => actor.ActorType == ActorType.Player,
            SecurityProfile.Gm => actor.ActorType == ActorType.Gm && descriptor.Permission is not null && actor.HasPermission(descriptor.Permission),
            SecurityProfile.Dev => true,
            _ => false,
        };
        if (!allowed)
        {
            throw new GameServerException(CoreErrorCodes.Forbidden, $"Actor {actor.ActorType} may not run {descriptor.Operation} ({descriptor.Profile}).");
        }
    }

    private async Task<CommandExecution> ExecuteTransactionAsync(
        GameServer.Commands.CommandRegistration registration,
        CommandInvocation invocation,
        string playerId,
        string receiptId,
        DocumentKey receiptKey,
        CancellationToken requestAborted)
    {
        var descriptor = registration.Descriptor;
        var descriptors = descriptor.Touches.Concat(descriptor.Reads).Distinct(StringComparer.Ordinal).Select(registry.GetAggregate).ToArray();
        var keys = descriptors.Select(d => AggregateDocumentSerializer.KeyFor(d, playerId)).Append(receiptKey).ToArray();
        var attempts = 0;
        var stopwatch = Stopwatch.StartNew();

        AttemptResult result;
        try
        {
            // No request token below this line: a client disconnect must not abort the commit (PERF-02).
            result = await store.RunTransactionAsync(
                tx => RunAttemptAsync(tx, attempts++, registration, invocation, playerId, receiptId, receiptKey, descriptors, keys),
                new TransactionRunOptions(TimeSpan.FromSeconds(options.Transactions.ExpirationSeconds)));
        }
        catch (CommandRejectedException rejected)
        {
            // Only business codes travel in a command response (HTTP 200); e.g. IDENTITY_CONFLICT is a 409 envelope.
            if (rejected.Code.HttpStatus != 200)
            {
                throw new GameServerException(rejected.Code, $"Command {descriptor.Operation} rejected with {rejected.Code.Name}.") { Details = rejected.Details };
            }

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
        var code = registry.Errors.FindByName(receipt.ResultCode) ?? CoreErrorCodes.Ok;
        return new CommandExecution(code, receiptId, updates, result.ResultBytes, false, config.ConfigVersion);
    }

    private async Task<AttemptResult> RunAttemptAsync(
        ITransactionContext tx,
        int attempt,
        GameServer.Commands.CommandRegistration registration,
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
        var handler = ActivatorUtilities.CreateInstance(services, registration.HandlerType);
        var outcome = await registration.InvokeAsync(handler, invocation.Request, context);
        if (!outcome.IsOk)
        {
            // Throwing rolls back claim markers and anything staged (research R5: no receipt for rejections).
            throw new CommandRejectedException(outcome.Error ?? CoreErrorCodes.Internal, outcome.Details);
        }

        EnsureCommittableCode(registration, outcome.Error);
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

        EnsureAudit(descriptor, context);
        var resultCode = outcome.Error ?? CoreErrorCodes.Ok;
        var receipt = BuildReceipt(invocation, descriptor.Operation, playerId, receiptId, resultCode, resultBytes, loaded, writes, context, now);
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

    /// <summary>
    /// SEC-14: every committed GM command carries exactly one audit entry in its receipt; other commands carry none, so
    /// the audit trail cannot be forged by Player or Dev commands. A violation is a module bug (INTERNAL, nothing commits).
    /// </summary>
    private static void EnsureAudit(GameServer.Commands.CommandDescriptor descriptor, CommandContext context)
    {
        var audits = context.Outbox.Count(e => e.Kind == GameServer.Commands.AuditExtensions.OutboxKind);
        var expected = descriptor.Profile == SecurityProfile.Gm ? 1 : 0;
        if (audits != expected)
        {
            throw new InvalidOperationException(
                $"Command '{descriptor.Operation}' ({descriptor.Profile}) wrote {audits} audit entries; expected {expected} (SEC-14). " +
                "Fix: GM commands call ctx.AddAudit(...) exactly once; other commands never do.");
        }
    }

    /// <summary>
    /// API-11: a committed receipt stores the code by name and a response resolves it through the catalog, so the code of a
    /// commit must be the catalogued instance (else the client gets OK or another code's number) and a business code (HTTP 200:
    /// a commit always answers 200). A violation is a module bug (INTERNAL, nothing commits).
    /// </summary>
    private void EnsureCommittableCode(GameServer.Commands.CommandRegistration registration, ErrorCode? code)
    {
        if (code is null)
        {
            return;
        }

        var catalogued = registry.Errors.FindByName(code.Name);
        string problem;
        string fix;
        if (catalogued is null)
        {
            problem = "which is not in the error-code catalog; the client would receive OK";
            fix = "list the code in the module's ErrorCodeSet (ModuleDescriptor.ErrorCodes)";
        }
        else if (catalogued != code)
        {
            problem = $"which has the name of the catalogued {catalogued} but differs from it (number, HTTP status, retryable flag or message)";
            fix = "commit with the catalogued ErrorCode instance from the module's ErrorCodeSet";
        }
        else if (code.HttpStatus != 200)
        {
            problem = $"whose HTTP status is {code.HttpStatus}; a commit always answers HTTP 200";
            fix = "use CommandOutcome.Reject for non-200 codes (nothing is committed)";
        }
        else
        {
            return;
        }

        throw new InvalidOperationException(
            $"Command '{registration.Descriptor.Operation}' ({DescribeOwner(registration)}) committed with error code {code}, {problem}. Fix: {fix} (API-11).");
    }

    /// <summary>Owning module of a command, and the replacing handler when <c>ReplaceHandler</c> swapped it (EXT-02).</summary>
    private string DescribeOwner(GameServer.Commands.CommandRegistration registration)
    {
        var operation = registration.Descriptor.Operation;
        var (module, declared) = registry.Modules
            .SelectMany(m => m.Descriptor.Commands.Select(c => (Module: m.Descriptor.Name, Registration: c)))
            .FirstOrDefault(x => x.Registration.Descriptor.Operation == operation);
        return declared is not null && declared.HandlerType != registration.HandlerType
            ? $"module '{module}', handler replaced by {registration.HandlerType.Name} through ReplaceHandler"
            : $"module '{module}'";
    }

    private ReceiptDocument BuildReceipt(
        CommandInvocation invocation,
        string operation,
        string playerId,
        string receiptId,
        GameServer.Errors.ErrorCode resultCode,
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
            ResultCode = resultCode.Name,
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

    private Task<CommandExecution> ReplayAsync(ReceiptDocument receipt, CommandInvocation invocation, CancellationToken cancellationToken) =>
        ReplayAsync(receipt, invocation.BodyHash, invocation.RequestId, cancellationToken);

    private async Task<CommandExecution> ReplayAsync(ReceiptDocument receipt, string bodyHash, string requestId, CancellationToken cancellationToken)
    {
        if (!string.Equals(receipt.BodyHash, bodyHash, StringComparison.Ordinal))
        {
            throw new GameServerException(
                CoreErrorCodes.IdempotencyPayloadMismatch,
                $"requestId {requestId} reused with a different body for {receipt.Operation}.");
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

        var code = registry.Errors.FindByName(receipt.ResultCode);
        if (code is null)
        {
            // A receipt committed before the catalog check (or a code since removed from its module): the commit stands, so
            // the replay still answers, with OK as before.
            logger.LogWarning(
                "Replay of {Operation} receipt {ReceiptId}: committed error code '{ResultCode}' is not in the error-code catalog; replying OK. " +
                "Fix: keep the code in its module's ErrorCodeSet (API-11).",
                receipt.Operation,
                receipt.ReceiptId,
                receipt.ResultCode);
            code = CoreErrorCodes.Ok;
        }

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
    private sealed class CommandRejectedException(ErrorCode code, IReadOnlyDictionary<string, string>? details = null) : Exception(code.Name)
    {
        public ErrorCode Code { get; } = code;

        public IReadOnlyDictionary<string, string>? Details { get; } = details;
    }
}
