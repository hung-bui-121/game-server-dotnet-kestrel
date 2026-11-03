using System.Text.Json.Nodes;
using GameServer.Aggregates;
using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Time;

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
    /// (<c>COMMIT_UNKNOWN_RETRY</c>, <c>CONCURRENT_UPDATE</c>, <c>INTERNAL</c>); a <see cref="GameServerException"/> thrown by
    /// <see cref="AccountLoginWrite{TProfile}.Mutate"/> passes through unchanged.
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
/// only an attempt that finds no profile reads the creation anchor (a second read in the same attempt, VERIFY V4) and creates
/// the profile. The decision is returned from the attempt, never kept across attempts or rounds (trap T2).
/// </summary>
internal sealed class AccountWriter(
    ModuleRegistry registry,
    ITransactionalDocumentStore store,
    AggregateDocumentSerializer serializer,
    IClock clock,
    GameServerOptions options,
    PlatformMetrics metrics) : IAccountWriter
{
    /// <summary>Rounds of the outer loop on an insert race or a stale read (as <see cref="PlayerAggregateWriter"/>).</summary>
    private const int MaxRaceRounds = 3;

    private const string AnchorPlayerField = "playerId";

    /// <summary>Retry delays of the typed store failures (the runner's, research R1).</summary>
    private const int AmbiguousRetryAfterMs = 1000;
    private const int ConflictRetryAfterMs = 200;

    public async Task<AccountWriteResult<TProfile>> WriteLoginAsync<TProfile>(AccountLoginWrite<TProfile> write, CancellationToken cancellationToken)
        where TProfile : class
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentException.ThrowIfNullOrEmpty(write.PlayerId);
        ArgumentNullException.ThrowIfNull(write.Mutate);
        var descriptor = registry.GetAggregate(ModuleRegistry.RequiredProfileAggregate);
        if (descriptor.StateType != typeof(TProfile))
        {
            throw new InvalidOperationException(
                $"The '{descriptor.AggregateId}' aggregate holds {descriptor.StateType.Name}, not {typeof(TProfile).Name}: write the login with its state type.");
        }

        // The request token is used only before the transaction (PERF-02).
        cancellationToken.ThrowIfCancellationRequested();
        for (var round = 1; ; round++)
        {
            try
            {
                var attempt = await RunTransactionAsync(descriptor, write);
                if (attempt.WrittenBytes is { } size)
                {
                    metrics.AggregateWritten(descriptor.AggregateId, size, size >= descriptor.WarnBytes);
                }

                return new AccountWriteResult<TProfile>(attempt.Profile, attempt.Rev, attempt.Created, CreationReceiptId: null);
            }
            catch (Exception ex) when (ex is DocumentExistsException or StaleReadException)
            {
                // Two first logins raced on the missing profile (Couchbase reports "exists"), or a stale bundle read: the
                // next round reads again and a profile that is now there makes it a normal login.
                if (round >= MaxRaceRounds)
                {
                    throw new GameServerException(CoreErrorCodes.ConcurrentUpdate, $"Login write race for player {write.PlayerId}.", ex)
                    {
                        RetryAfterMs = ConflictRetryAfterMs,
                    };
                }
            }
        }
    }

    private async Task<Attempt<TProfile>> RunTransactionAsync<TProfile>(PlayerAggregateDescriptor descriptor, AccountLoginWrite<TProfile> write)
        where TProfile : class
    {
        try
        {
            return await store.RunTransactionAsync(
                tx => RunAttemptAsync(tx, descriptor, write),
                new TransactionRunOptions(TimeSpan.FromSeconds(options.Transactions.ExpirationSeconds)));
        }
        catch (TransactionCommitAmbiguousException ex)
        {
            throw new GameServerException(CoreErrorCodes.CommitUnknownRetry, $"Login commit ambiguous for player {write.PlayerId}.", ex)
            {
                RetryAfterMs = AmbiguousRetryAfterMs,
            };
        }
        catch (TransactionConflictExpiredException ex)
        {
            throw new GameServerException(CoreErrorCodes.ConcurrentUpdate, $"Login transaction expired for player {write.PlayerId}.", ex)
            {
                RetryAfterMs = ConflictRetryAfterMs,
            };
        }
        catch (TransactionFailedException ex)
        {
            throw new GameServerException(CoreErrorCodes.Internal, $"Login transaction failed for player {write.PlayerId}.", ex);
        }
    }

    /// <summary>One transaction attempt: everything it decides comes from what it read (research R1).</summary>
    private async Task<Attempt<TProfile>> RunAttemptAsync<TProfile>(
        ITransactionContext tx,
        PlayerAggregateDescriptor descriptor,
        AccountLoginWrite<TProfile> write)
        where TProfile : class
    {
        var key = AggregateDocumentSerializer.KeyFor(descriptor, write.PlayerId);
        var stored = (await tx.GetManyAsync([key]))[0];
        if (stored is not null)
        {
            // Returning player, or the loser of a creation race: today's login write, no anchor read.
            var current = serializer.Load(descriptor, write.PlayerId, stored);
            var (updated, document, size) = Apply(descriptor, current, write);
            if (document is null)
            {
                return new Attempt<TProfile>(updated, current.Rev, Created: false, WrittenBytes: null);
            }

            await tx.ReplaceAsync(stored, document);
            return new Attempt<TProfile>(updated, current.Rev + 1, Created: false, size);
        }

        // No profile in this attempt: the creation needs the anchor (second read of the attempt, VERIFY V4).
        var anchor = await ReadAnchorAsync(tx, write.CreationAnchor, write.PlayerId);
        var initial = serializer.Load(descriptor, write.PlayerId, document: null);
        var (created, profileDocument, profileSize) = Apply(descriptor, initial, write);
        if (profileDocument is null)
        {
            // A login that leaves the default profile unchanged creates nothing.
            return new Attempt<TProfile>(created, initial.Rev, Created: false, WrittenBytes: null);
        }

        // The unchanged rewrite makes the creation write-conflict with a concurrent removal of the anchor (010 R9).
        await tx.ReplaceAsync(anchor, (JsonObject)anchor.Content.DeepClone());
        await tx.InsertAsync(key, profileDocument);
        return new Attempt<TProfile>(created, initial.Rev + 1, Created: true, profileSize);
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

        var (document, size) = serializer.BuildDocument(before, json, before.Rev + 1, clock.UtcNow);
        if (CommitRules.ExceedsHardBytes(descriptor, size))
        {
            throw new GameServerException(CoreErrorCodes.AggregateTooLarge, $"{descriptor.AggregateId} of {write.PlayerId} would be {size} bytes.");
        }

        return (next, document, size);
    }

    /// <summary>
    /// The creation anchor must exist and name the player. A read alone is not conflict-detected by Couchbase transactions,
    /// which is why the caller rewrites it unchanged before inserting the profile.
    /// </summary>
    private static async Task<StoredDocument> ReadAnchorAsync(ITransactionContext tx, DocumentKey anchorKey, string playerId)
    {
        var anchor = (await tx.GetManyAsync([anchorKey]))[0];
        var owner = anchor?.Content[AnchorPlayerField] is JsonValue value && value.TryGetValue<string>(out var id) ? id : null;
        if (anchor is null || !string.Equals(owner, playerId, StringComparison.Ordinal))
        {
            throw new AggregateAnchorMissingException(anchorKey);
        }

        return anchor;
    }

    /// <summary>The outcome of the committed attempt (the only source of <see cref="AccountWriteResult{TProfile}.Created"/>).</summary>
    private sealed record Attempt<TProfile>(TProfile Profile, long Rev, bool Created, int? WrittenBytes)
        where TProfile : class;
}
