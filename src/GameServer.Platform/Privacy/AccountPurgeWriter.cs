using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Time;

namespace GameServer.Platform.Privacy;

/// <summary>The fenced purge transactions of <see cref="IAccountPurgeWriter"/> (research R6).</summary>
public sealed class AccountPurgeWriter(
    ModuleRegistry registry,
    PurgeTargets targets,
    ITransactionalDocumentStore store,
    AggregateDocumentSerializer serializer,
    IClock clock,
    GameServerOptions options,
    PlatformMetrics metrics) : IAccountPurgeWriter
{
    /// <summary>Re-runs after a lost concurrent insert (Couchbase reports "exists") or a stale bundle read.</summary>
    private const int MaxRaceRounds = 3;

    private const string AnonymizeAction = "anonymize";
    private const string RemoveAction = "remove";

    /// <summary><c>RetryDelayMinutes × 2^(attempts − 1)</c>, capped at <c>MaxRetryDelayMinutes</c> (research R7).</summary>
    public static TimeSpan RetryDelay(PurgeSettings settings, int attempts) =>
        TimeSpan.FromMinutes(Math.Min(settings.MaxRetryDelayMinutes, settings.RetryDelayMinutes * Math.Pow(2, Math.Max(0, attempts - 1))));

    public async Task<PurgeTicket?> BeginAsync<TProfile>(
        string playerId,
        long fencingToken,
        Func<TProfile, PurgeStart<TProfile>?> start,
        CancellationToken cancellationToken)
        where TProfile : class
    {
        ArgumentException.ThrowIfNullOrEmpty(playerId);
        var descriptor = registry.GetAggregate(ModuleRegistry.RequiredProfileAggregate);
        if (descriptor.StateType != typeof(TProfile))
        {
            throw new ArgumentException($"The profile aggregate holds {descriptor.StateType}, not {typeof(TProfile)}.", nameof(start));
        }

        var profileKey = AggregateDocumentSerializer.KeyFor(descriptor, playerId);
        var recordKey = AccountDeletionRecord.KeyOf(playerId);
        var now = clock.UtcNow;
        return await RunAsync<PurgeTicket?>(async tx =>
        {
            var documents = await tx.GetManyAsync([profileKey, recordKey]);
            if (documents[1] is { } recordDocument)
            {
                var existing = AccountDeletionRecord.FromJson(recordDocument.Content);
                if (existing.State == AccountDeletionState.Purged)
                {
                    return null;
                }

                if (existing.FencingToken > fencingToken)
                {
                    throw new PurgeFencedOutException(playerId, fencingToken, existing.FencingToken);
                }

                if (existing.AnonymousId is null)
                {
                    throw new InvalidOperationException($"Deletion record of player {playerId} is purging without an anonymous id.");
                }

                await tx.ReplaceAsync(recordDocument, (existing with { FencingToken = fencingToken }).ToJson());
                return new PurgeTicket(playerId, existing.AnonymousId, fencingToken, existing.CompletedSteps.ToHashSet(StringComparer.Ordinal));
            }

            if (documents[0] is not { } profileDocument)
            {
                return null;
            }

            var loaded = serializer.Load(descriptor, playerId, profileDocument);
            if (start((TProfile)loaded.State) is not { } decision)
            {
                return null;
            }

            // Marks the profile purging in the same transaction that inserts the record: a concurrent cancel writes the same
            // profile document, so exactly one of them commits (US3-6).
            var json = AggregateDocumentSerializer.SerializeState(decision.Profile, descriptor.StateType);
            var (document, size) = serializer.BuildDocument(loaded, json, loaded.Rev + 1, now);
            if (size > descriptor.HardBytes)
            {
                throw new GameServerException(CoreErrorCodes.AggregateTooLarge, $"{descriptor.AggregateId} of {playerId} would be {size} bytes.");
            }

            await tx.ReplaceAsync(profileDocument, document);
            var record = new AccountDeletionRecord
            {
                PlayerId = playerId,
                GameId = options.GameId,
                EnvironmentId = options.EnvironmentId,
                State = AccountDeletionState.Purging,
                RequestedAtUtc = decision.RequestedAtUtc,
                PurgeAfterUtc = decision.PurgeAfterUtc,
                PurgeStartedAtUtc = now,
                AnonymousId = AnonymousIds.New(),
                FencingToken = fencingToken,
                NextAttemptAtUtc = now,
            };
            await tx.InsertAsync(recordKey, record.ToJson());
            metrics.AggregateWritten(descriptor.AggregateId, size, size >= descriptor.WarnBytes);
            return new PurgeTicket(playerId, record.AnonymousId, fencingToken, new HashSet<string>(StringComparer.Ordinal));
        }, cancellationToken);
    }

    public async Task<int> ApplyAsync(PurgeTicket ticket, IReadOnlyList<DocumentKey> keys, PurgeAction action, CancellationToken cancellationToken)
    {
        EnsureBatch(keys);
        var entries = keys.Select(k => targets.EnsureApplicable(k, action, ticket.PlayerId)).ToArray();
        if (keys.Count == 0)
        {
            return 0;
        }

        var written = await RunAsync(async tx =>
        {
            var (record, _, documents) = await FencedReadAsync(tx, ticket, keys);
            var count = 0;
            for (var i = 0; i < keys.Count; i++)
            {
                if (documents[i] is not { } document)
                {
                    continue; // Never created or already purged.
                }

                if (action == PurgeAction.Remove)
                {
                    // Only documents that still reference the player (removal by key for pa_*).
                    if (entries[i].IsPlayerAggregate || PageCursor.StringAt(document.Content, entries[i].PlayerField) == ticket.PlayerId)
                    {
                        await tx.RemoveAsync(document);
                        count++;
                    }

                    continue;
                }

                var content = (JsonObject)document.Content.DeepClone();
                if (keys[i].Collection == ReceiptDocument.Collection && OwnedByPlayer(content, ticket))
                {
                    PurgeRewrites.ApplyReceiptRules(content, targets.RemovedCollections);
                }
                else if (keys[i].Collection == AuditedStore.AuditCollection && OwnedByPlayer(content, ticket))
                {
                    PurgeRewrites.ApplyAuditRules(content);
                }

                PurgeRewrites.Anonymize(content, ticket.PlayerId, ticket.AnonymousId);
                if (!JsonNode.DeepEquals(content, document.Content))
                {
                    await tx.ReplaceAsync(document, content);
                    count++;
                }
            }

            await ReplaceRecordAsync(tx, record, ticket, r => r with { DocumentsProcessed = r.DocumentsProcessed + count });
            return count;
        }, cancellationToken);
        metrics.AccountPurgeDocuments(action == PurgeAction.Remove ? RemoveAction : AnonymizeAction, written);
        return written;
    }

    public async Task<int> RewriteAsync(PurgeTicket ticket, IReadOnlyList<DocumentKey> keys, Func<JsonObject, JsonObject?> rewrite, CancellationToken cancellationToken)
    {
        EnsureBatch(keys);
        foreach (var key in keys)
        {
            targets.EnsureModuleOwned(key.Collection);
        }

        if (keys.Count == 0)
        {
            return 0;
        }

        var (rewritten, removed) = await RunAsync(async tx =>
        {
            var (record, _, documents) = await FencedReadAsync(tx, ticket, keys);
            var changed = 0;
            var deleted = 0;
            foreach (var document in documents.OfType<StoredDocument>())
            {
                if (rewrite((JsonObject)document.Content.DeepClone()) is not { } content)
                {
                    await tx.RemoveAsync(document);
                    deleted++;
                }
                else if (!JsonNode.DeepEquals(content, document.Content))
                {
                    await tx.ReplaceAsync(document, content);
                    changed++;
                }
            }

            await ReplaceRecordAsync(tx, record, ticket, r => r with { DocumentsProcessed = r.DocumentsProcessed + changed + deleted });
            return (changed, deleted);
        }, cancellationToken);
        metrics.AccountPurgeDocuments(AnonymizeAction, rewritten);
        metrics.AccountPurgeDocuments(RemoveAction, removed);
        return rewritten + removed;
    }

    public async Task<int> ScrubOutboxAsync(
        PurgeTicket ticket,
        IReadOnlyList<DocumentKey> receiptKeys,
        string kind,
        Func<JsonNode?, JsonNode?> scrub,
        CancellationToken cancellationToken)
    {
        EnsureBatch(receiptKeys);
        if (receiptKeys.Any(k => k.Collection != ReceiptDocument.Collection))
        {
            throw new InvalidOperationException("ScrubOutboxAsync rewrites receipts only.");
        }

        if (receiptKeys.Count == 0)
        {
            return 0;
        }

        var written = await RunAsync(async tx =>
        {
            var (record, _, documents) = await FencedReadAsync(tx, ticket, receiptKeys);
            var count = 0;
            foreach (var document in documents.OfType<StoredDocument>())
            {
                var content = (JsonObject)document.Content.DeepClone();
                if (!OwnedByPlayer(content, ticket) || content["outbox"] is not JsonArray outbox)
                {
                    continue; // Another player's receipt is never touched.
                }

                foreach (var entry in outbox.OfType<JsonObject>().Where(e => e["kind"]?.GetValue<string>() == kind))
                {
                    entry["payload"] = scrub(entry["payload"]?.DeepClone());
                }

                if (!JsonNode.DeepEquals(content, document.Content))
                {
                    await tx.ReplaceAsync(document, content);
                    count++;
                }
            }

            await ReplaceRecordAsync(tx, record, ticket, r => r with { DocumentsProcessed = r.DocumentsProcessed + count });
            return count;
        }, cancellationToken);
        metrics.AccountPurgeDocuments(AnonymizeAction, written);
        return written;
    }

    public Task<PurgeTicket> CompleteStepAsync(PurgeTicket ticket, string step, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(step);
        return RunAsync(async tx =>
        {
            var (record, state, _) = await FencedReadAsync(tx, ticket, []);
            var steps = state.CompletedSteps.Contains(step, StringComparer.Ordinal) ? state.CompletedSteps : [.. state.CompletedSteps, step];

            // Progress ends a run of consecutive failures (attempts drives the backoff).
            await ReplaceRecordAsync(tx, record, ticket, r => r with { CompletedSteps = steps, Attempts = 0 });
            return ticket with { CompletedSteps = steps.ToHashSet(StringComparer.Ordinal) };
        }, cancellationToken);
    }

    public async Task CompleteAsync(PurgeTicket ticket, CancellationToken cancellationToken)
    {
        var profileKey = new DocumentKey(targets.ProfileCollection, ticket.PlayerId);
        var now = clock.UtcNow;
        var removed = await RunAsync(async tx =>
        {
            var (record, _, documents) = await FencedReadAsync(tx, ticket, [profileKey]);
            if (documents[0] is { } profile)
            {
                await tx.RemoveAsync(profile);
            }

            // The purge record keeps no anonymous id and no progress (FR-018; AccountDeletionRecord.ToJson drops them).
            await ReplaceRecordAsync(tx, record, ticket, r => r with
            {
                State = AccountDeletionState.Purged,
                PurgedAtUtc = now,
                NextAttemptAtUtc = null,
                AnonymousId = null,
                CompletedSteps = [],
                Attempts = 0,
                LastFailure = null,
                DocumentsProcessed = r.DocumentsProcessed + (documents[0] is null ? 0 : 1),
            });
            return documents[0] is null ? 0 : 1;
        }, cancellationToken);
        metrics.AccountPurgeDocuments(RemoveAction, removed);
    }

    public Task RecordFailureAsync(PurgeTicket ticket, string step, Exception error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        var now = clock.UtcNow;
        return RunAsync(async tx =>
        {
            var (record, state, _) = await FencedReadAsync(tx, ticket, []);
            var attempts = state.Attempts + 1;
            await ReplaceRecordAsync(tx, record, ticket, r => r with
            {
                Attempts = attempts,

                // Exception type only: a message may hold document content (LOG-05).
                LastFailure = new PurgeFailure(step, error.GetType().FullName ?? error.GetType().Name, now),
                NextAttemptAtUtc = now + RetryDelay(options.Purge, attempts),
            });
            return true;
        }, cancellationToken);
    }

    /// <summary>A receipt or audit projection about the purged player (old id, or anonymous id after a resumed batch).</summary>
    private static bool OwnedByPlayer(JsonObject document, PurgeTicket ticket) =>
        document["playerId"]?.GetValue<string>() is { } owner && (owner == ticket.PlayerId || owner == ticket.AnonymousId);

    private void EnsureBatch(IReadOnlyList<DocumentKey> keys)
    {
        if (keys.Count > options.Purge.BatchSize)
        {
            throw new ArgumentException($"A purge transaction writes at most {options.Purge.BatchSize} documents (DAT-04); got {keys.Count}.", nameof(keys));
        }
    }

    /// <summary>
    /// Bundle read of the deletion record and <paramref name="keys"/>, refusing a stale claim (FR-003) and, separately, a
    /// purge this token already completed (an ambiguous final commit that landed, review minor 1).
    /// </summary>
    private static async Task<(StoredDocument Record, AccountDeletionRecord State, IReadOnlyList<StoredDocument?> Documents)> FencedReadAsync(
        ITransactionContext tx,
        PurgeTicket ticket,
        IReadOnlyList<DocumentKey> keys)
    {
        var all = await tx.GetManyAsync([AccountDeletionRecord.KeyOf(ticket.PlayerId), .. keys]);
        var document = all[0] ?? throw new InvalidOperationException($"Player {ticket.PlayerId} has no deletion record; the purge was never begun.");
        var record = AccountDeletionRecord.FromJson(document.Content);
        if (record.FencingToken > ticket.FencingToken)
        {
            throw new PurgeFencedOutException(ticket.PlayerId, ticket.FencingToken, record.FencingToken);
        }

        if (record.State == AccountDeletionState.Purged)
        {
            throw new PurgeAlreadyCompletedException(ticket.PlayerId);
        }

        return (document, record, all.Skip(1).ToArray());
    }

    private static Task ReplaceRecordAsync(ITransactionContext tx, StoredDocument document, PurgeTicket ticket, Func<AccountDeletionRecord, AccountDeletionRecord> change) =>
        tx.ReplaceAsync(document, change(AccountDeletionRecord.FromJson(document.Content) with { FencingToken = ticket.FencingToken }).ToJson());

    /// <summary>One transaction; the caller's token is checked before and never passed to the commit (PERF-02).</summary>
    private async Task<T> RunAsync<T>(Func<ITransactionContext, Task<T>> body, CancellationToken cancellationToken)
    {
        var run = new TransactionRunOptions(TimeSpan.FromSeconds(options.Transactions.ExpirationSeconds));
        for (var round = 1; ; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await store.RunTransactionAsync(body, run);
            }
            catch (Exception ex) when (round < MaxRaceRounds && ex is DocumentExistsException or StaleReadException)
            {
                // A concurrent begin inserted the record first, or a bundle read went stale: the next round reads again.
            }
        }
    }
}
