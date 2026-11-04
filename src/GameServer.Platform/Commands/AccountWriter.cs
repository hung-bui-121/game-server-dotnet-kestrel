using System.Diagnostics;
using System.Text.Json.Nodes;
using GameServer.Aggregates;
using GameServer.Config;
using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Time;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Commands;

/// <summary>
/// The login write of the account root (FG-015, FG-013): updates the profile of an existing player, or creates the account
/// (anchor rewrite + profile + account initializers + receipt) when the profile does not exist, in one transaction. The
/// create-or-update decision is taken in each transaction attempt (research R1).
/// </summary>
public interface IAccountWriter
{
    /// <summary>
    /// Writes the profile of a login. Throws <see cref="AggregateAnchorMissingException"/> (nothing written) when the profile is
    /// absent and the creation anchor is missing or names another player; store failures answer the command codes
    /// (<c>COMMIT_UNKNOWN_RETRY</c>, <c>CONCURRENT_UPDATE</c>, <c>INTERNAL</c>); a failed account initializer answers <c>INTERNAL</c>
    /// (logged once with the initializer's name); a <see cref="GameServerException"/> thrown by
    /// <see cref="AccountLoginWrite{TProfile}.Mutate"/> and a typed read failure pass through unchanged.
    /// </summary>
    Task<AccountWriteResult<TProfile>> WriteLoginAsync<TProfile>(AccountLoginWrite<TProfile> write, CancellationToken cancellationToken)
        where TProfile : class;
}

/// <param name="PlayerId">Resolved player.</param>
/// <param name="Mutate">Applied to the stored profile, or to the default profile on creation; may throw GameServerException (late access checks).</param>
/// <param name="CreationAnchor">The identity mapping that must exist and name the player for a creation (010 R9).</param>
/// <param name="CorrelationId">Request correlation id, stored in the creation receipt.</param>
public sealed record AccountLoginWrite<TProfile>(
    string PlayerId,
    Func<TProfile, TProfile> Mutate,
    DocumentKey CreationAnchor,
    string CorrelationId)
    where TProfile : class;

/// <param name="Created">True only when this call committed the creation of the account.</param>
/// <param name="CreationReceiptId">Receipt of the creation; null when not created or when the initializers had no effect.</param>
public sealed record AccountWriteResult<TProfile>(TProfile Profile, long Rev, bool Created, string? CreationReceiptId)
    where TProfile : class;

/// <summary>
/// <see cref="IAccountWriter"/> on the transactional store. A returning login is today's transaction (one read, one replace);
/// only an attempt that finds no profile reads the creation anchor and the initializers' aggregates (a second read in the same
/// attempt, VERIFY V4), runs the initializers and creates the account. The decision is returned from the attempt, never kept
/// across attempts or rounds (trap T2).
/// </summary>
internal sealed partial class AccountWriter : IAccountWriter
{
    /// <summary>Log category of the creation lines (errors-and-report-lines §2).</summary>
    internal const string LogCategory = "GameServer.Accounts";

    /// <summary>Rounds of the outer loop on an insert race or a stale read (as <see cref="PlayerAggregateWriter"/>).</summary>
    private const int MaxRaceRounds = 3;

    /// <summary>Retry delays of the typed store failures (the runner's, research R1).</summary>
    private const int AmbiguousRetryAfterMs = 1000;
    private const int ConflictRetryAfterMs = 200;

    private const string CreatedResult = "created";
    private const string FailedResult = "failed";

    private readonly ModuleRegistry _registry;
    private readonly ITransactionalDocumentStore _store;
    private readonly AggregateDocumentSerializer _serializer;
    private readonly IGameConfig _config;
    private readonly IClock _clock;
    private readonly GameServerOptions _options;
    private readonly IOutboxRelay _relay;
    private readonly PlatformMetrics _metrics;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly Lazy<CreationPlan> _plan;

    public AccountWriter(
        ModuleRegistry registry,
        ITransactionalDocumentStore store,
        AggregateDocumentSerializer serializer,
        IGameConfig config,
        IClock clock,
        GameServerOptions options,
        IOutboxRelay relay,
        PlatformMetrics metrics,
        IServiceProvider services,
        ILoggerFactory loggers)
    {
        _registry = registry;
        _store = store;
        _serializer = serializer;
        _config = config;
        _clock = clock;
        _options = options;
        _relay = relay;
        _metrics = metrics;
        _services = services;
        _logger = loggers.CreateLogger(LogCategory);

        // The registry is complete and validated before the first login; the plan is built once.
        _plan = new Lazy<CreationPlan>(() => CreationPlan.Build(registry));
    }

    public async Task<AccountWriteResult<TProfile>> WriteLoginAsync<TProfile>(AccountLoginWrite<TProfile> write, CancellationToken cancellationToken)
        where TProfile : class
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentException.ThrowIfNullOrEmpty(write.PlayerId);
        ArgumentNullException.ThrowIfNull(write.Mutate);
        var descriptor = _registry.GetAggregate(ModuleRegistry.RequiredProfileAggregate);
        if (descriptor.StateType != typeof(TProfile))
        {
            throw new InvalidOperationException(
                $"The '{descriptor.AggregateId}' aggregate holds {descriptor.StateType.Name}, not {typeof(TProfile).Name}: write the login with its state type.");
        }

        // The request token is used only before the transaction (PERF-02).
        cancellationToken.ThrowIfCancellationRequested();

        // One request id per login call, outside the attempts: every attempt and round has the same receipt id and the same
        // NewId/NewSeed values (research R2).
        var run = new CreationRun(Guid.NewGuid().ToString("D"), write.PlayerId);
        for (var round = 1; ; round++)
        {
            try
            {
                var attempt = await RunTransactionAsync(descriptor, write, run);
                await CompleteAsync(descriptor, attempt, run);
                return new AccountWriteResult<TProfile>(attempt.Profile, attempt.Rev, attempt.Created, attempt.Receipt?.ReceiptId);
            }
            catch (AccountInitializerFailedException failure)
            {
                // Thrown before anything was staged and rethrown unchanged by the store (VERIFY V2): reported once per login.
                throw Report(failure, write.PlayerId);
            }
            catch (Exception ex) when (run.Creating && (ex is AggregateAnchorMissingException || AggregateReadFailure.TryGet(ex, out _)))
            {
                // The creation lost its anchor, or an initializer's aggregate is unreadable (FG-029): a failed creation too (T082 m5).
                // Passed through unchanged; the read failure was already logged where it happened.
                CountFailedCreation(run);
                throw;
            }
            catch (Exception ex) when (ex is DocumentExistsException or StaleReadException)
            {
                // Two first logins raced on the missing profile (Couchbase reports "exists"), or a stale bundle read: the
                // next round reads again and a profile that is now there makes it a normal login.
                if (round >= MaxRaceRounds)
                {
                    CountFailedCreation(run);
                    throw new GameServerException(CoreErrorCodes.ConcurrentUpdate, $"Login write race for player {write.PlayerId}.", ex)
                    {
                        RetryAfterMs = ConflictRetryAfterMs,
                    };
                }
            }
        }
    }

    private async Task<Attempt<TProfile>> RunTransactionAsync<TProfile>(PlayerAggregateDescriptor descriptor, AccountLoginWrite<TProfile> write, CreationRun run)
        where TProfile : class
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await _store.RunTransactionAsync(
                tx => RunAttemptAsync(tx, descriptor, write, run),
                new TransactionRunOptions(TimeSpan.FromSeconds(_options.Transactions.ExpirationSeconds)));
        }
        catch (TransactionCommitAmbiguousException ex)
        {
            if (run.Creating)
            {
                _metrics.TransactionAmbiguous(AccountCreation.Operation);
                CountFailedCreation(run);
            }

            throw new GameServerException(CoreErrorCodes.CommitUnknownRetry, $"Login commit ambiguous for player {write.PlayerId}.", ex)
            {
                RetryAfterMs = AmbiguousRetryAfterMs,
            };
        }
        catch (TransactionConflictExpiredException ex)
        {
            if (run.Creating)
            {
                _metrics.TransactionExpired(AccountCreation.Operation);
                CountFailedCreation(run);
            }

            throw new GameServerException(CoreErrorCodes.ConcurrentUpdate, $"Login transaction expired for player {write.PlayerId}.", ex)
            {
                RetryAfterMs = ConflictRetryAfterMs,
            };
        }
        catch (TransactionFailedException ex)
        {
            CountFailedCreation(run);
            throw new GameServerException(CoreErrorCodes.Internal, $"Login transaction failed for player {write.PlayerId}.", ex);
        }
        finally
        {
            if (run.Creating)
            {
                _metrics.TransactionDuration(AccountCreation.Operation, stopwatch.Elapsed);
            }
        }
    }

    /// <summary>One transaction attempt: everything it decides comes from what it read (research R1).</summary>
    private async Task<Attempt<TProfile>> RunAttemptAsync<TProfile>(
        ITransactionContext tx,
        PlayerAggregateDescriptor descriptor,
        AccountLoginWrite<TProfile> write,
        CreationRun run)
        where TProfile : class
    {
        run.Creating = false;
        var key = AggregateDocumentSerializer.KeyFor(descriptor, write.PlayerId);
        var stored = (await tx.GetManyAsync([key]))[0];
        if (stored is null)
        {
            // No profile in this attempt: create the account (second read of the attempt, VERIFY V4).
            return await CreateAsync(tx, descriptor, key, write, run);
        }

        // Returning player, or the loser of a creation race: today's login write, no anchor read, no initializer.
        var current = _serializer.Load(descriptor, write.PlayerId, stored);
        var (updated, document, size) = Apply(descriptor, current, write);
        if (document is null)
        {
            return new Attempt<TProfile>(updated, current.Rev, Created: false, ProfileBytes: null);
        }

        await tx.ReplaceAsync(stored, document);
        return new Attempt<TProfile>(updated, current.Rev + 1, Created: false, size);
    }

    /// <summary>Runs the login's mutation on <paramref name="before"/>; the document is null when the state did not change.</summary>
    private (TProfile Profile, JsonObject? Document, int Size) Apply<TProfile>(
        PlayerAggregateDescriptor descriptor,
        LoadedAggregate before,
        AccountLoginWrite<TProfile> write)
        where TProfile : class
    {
        var next = write.Mutate((TProfile)AggregateDocumentSerializer.DeserializeState(before.StateJson, descriptor.StateType));
        var json = AggregateDocumentSerializer.SerializeState(next, descriptor.StateType);
        if (!CommitRules.ChangesState(before, json))
        {
            return (next, null, 0);
        }

        var (document, size) = _serializer.BuildDocument(before, json, before.Rev + 1, _clock.UtcNow);
        if (CommitRules.ExceedsHardBytes(descriptor, size))
        {
            throw new GameServerException(CoreErrorCodes.AggregateTooLarge, $"{descriptor.AggregateId} of {write.PlayerId} would be {size} bytes.");
        }

        return (next, document, size);
    }

    /// <summary>After the commit: metrics, and for a creation the inline outbox relay and the Information line (research R1 step 5).</summary>
    private async Task CompleteAsync<TProfile>(PlayerAggregateDescriptor descriptor, Attempt<TProfile> attempt, CreationRun run)
        where TProfile : class
    {
        if (attempt.ProfileBytes is { } size)
        {
            _metrics.AggregateWritten(descriptor.AggregateId, size, size >= descriptor.WarnBytes);
        }

        if (!attempt.Created)
        {
            return;
        }

        foreach (var write in attempt.Writes)
        {
            _metrics.AggregateWritten(write.Descriptor.AggregateId, write.SizeBytes, write.SizeBytes >= write.Descriptor.WarnBytes);
        }

        if (attempt.Receipt is { } receipt)
        {
            _metrics.ReceiptWritten(attempt.ReceiptSize);
            if (receipt.Outbox.Count > 0 && _options.Outbox.InlineRelayEnabled)
            {
                await _relay.TryRelayAsync(ReceiptDocument.KeyFor(receipt.ReceiptId), receipt, CancellationToken.None);
            }
        }

        _metrics.AccountCreation(CreatedResult);
        _logger.LogInformation(
            "Account {PlayerId} created: {InitializerCount} initializers, receipt {ReceiptId}.",
            run.PlayerId,
            _plan.Value.Initializers.Count,
            attempt.Receipt?.ReceiptId ?? "none");
    }

    /// <summary>The one log line and the metrics of a failed creation (errors-and-report-lines §2, §3); the login answers INTERNAL (D15).</summary>
    private GameServerException Report(AccountInitializerFailedException failure, string playerId)
    {
        switch (failure.Kind)
        {
            case AccountInitializerFailureKind.Rejected:
                _logger.LogError(
                    "Account creation of player {PlayerId} failed: initializer {Initializer} (module {Module}) returned {Code}. Nothing was created; the next login retries.",
                    playerId,
                    failure.Initializer,
                    failure.Module,
                    failure.Code!.Name);
                break;
            case AccountInitializerFailureKind.Threw:
                _logger.LogError(
                    "Account creation of player {PlayerId} failed: initializer {Initializer} (module {Module}) threw {ExceptionType}. Nothing was created; the next login retries.",
                    playerId,
                    failure.Initializer,
                    failure.Module,
                    failure.ExceptionType);
                break;
            default:
                _logger.LogError("Account creation of player {PlayerId} failed: {RuleLine} Nothing was created; the next login retries.", playerId, failure.RuleLine);
                break;
        }

        _metrics.AccountInitializerFailed(failure.Initializer, failure.ResultName);
        _metrics.AccountCreation(FailedResult);
        if (failure.OutputPart is { } part)
        {
            _metrics.CommandOutputTooLarge(AccountCreation.Operation, part);
        }

        // No inner exception: the initializer's exception message may carry state content (API-04). Marked as reported: the line
        // above is the only one, the error middleware adds none (T082 m1).
        return ReportedFailure.Mark(new GameServerException(
            CoreErrorCodes.Internal,
            $"Account creation of player {playerId} failed ({failure.ResultName}, initializer {failure.Initializer})."));
    }

    private void CountFailedCreation(CreationRun run)
    {
        if (run.Creating)
        {
            _metrics.AccountCreation(FailedResult);
        }
    }

    /// <summary>The outcome of the committed attempt (the only source of <see cref="AccountWriteResult{TProfile}.Created"/>).</summary>
    private sealed record Attempt<TProfile>(TProfile Profile, long Rev, bool Created, int? ProfileBytes)
        where TProfile : class
    {
        public ReceiptDocument? Receipt { get; init; }

        public int ReceiptSize { get; init; }

        public IReadOnlyList<CreationWrite> Writes { get; init; } = [];
    }

    /// <summary>
    /// Values fixed for one login call: the request id and receipt id of a creation. <see cref="Creating"/> and
    /// <see cref="Attempts"/> only label metrics; no decision is taken from them (trap T2).
    /// </summary>
    private sealed class CreationRun(string requestId, string playerId)
    {
        public string RequestId { get; } = requestId;

        public string PlayerId { get; } = playerId;

        public string ReceiptId { get; } = ReceiptIds.Compute(playerId, playerId, AccountCreation.Operation, requestId);

        public bool Creating { get; set; }

        public int Attempts { get; set; }
    }
}

/// <summary>Why an account initializer failed the creation (the <c>result</c> label of <c>account_initializer_failures_total</c>).</summary>
internal enum AccountInitializerFailureKind
{
    Rejected,
    Threw,
    Rule,
}

/// <summary>
/// An account initializer failed the creation inside the attempt, before anything was staged: it returned a code, threw, or broke a
/// commit rule. Carries names and types only, never an exception message (errors-and-report-lines §2).
/// </summary>
internal sealed class AccountInitializerFailedException : Exception
{
    /// <summary>Label of a rule that belongs to no single initializer (receipt size, Planning note 11).</summary>
    internal const string AnyInitializer = "*";

    private AccountInitializerFailedException(AccountInitializerFailureKind kind, string initializer, string? module)
        : base($"Account initializer {initializer} failed ({kind}).")
    {
        Kind = kind;
        Initializer = initializer;
        Module = module;
    }

    public AccountInitializerFailureKind Kind { get; }

    public string Initializer { get; }

    public string? Module { get; }

    public ErrorCode? Code { get; private init; }

    public string? ExceptionType { get; private init; }

    public string? RuleLine { get; private init; }

    /// <summary><c>receipt</c> for a receipt over its limit (also counted in <c>command_output_too_large_total</c>); null otherwise.</summary>
    public string? OutputPart { get; private init; }

    public string ResultName => Kind switch
    {
        AccountInitializerFailureKind.Rejected => "rejected",
        AccountInitializerFailureKind.Threw => "threw",
        _ => "rule",
    };

    public static AccountInitializerFailedException Rejected(RegisteredAccountInitializer initializer, ErrorCode code) =>
        new(AccountInitializerFailureKind.Rejected, initializer.Registration.Name, initializer.Module) { Code = code };

    public static AccountInitializerFailedException Threw(RegisteredAccountInitializer initializer, Exception exception) =>
        new(AccountInitializerFailureKind.Threw, initializer.Registration.Name, initializer.Module) { ExceptionType = exception.GetType().FullName };

    public static AccountInitializerFailedException Rule(string initializer, string line) =>
        new(AccountInitializerFailureKind.Rule, initializer, module: null) { RuleLine = line };

    public static AccountInitializerFailedException ReceiptTooLarge(string line) =>
        new(AccountInitializerFailureKind.Rule, AnyInitializer, module: null) { RuleLine = line, OutputPart = CommitRules.OutputPartReceipt };
}
