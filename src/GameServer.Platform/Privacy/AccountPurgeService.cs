using GameServer.Modules;
using GameServer.Platform.Commands;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Privacy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Privacy;

/// <summary>
/// How an account is purged across every module's data (research R7): receipts → anonymize collections → module steps →
/// verify → other <c>pa_*</c> → remove collections (sessions, identities, …) → profile + purge record. Each step is a
/// query-until-empty loop of fenced writer transactions and is recorded when finished, so a resumed purge skips finished
/// steps and repeats the current one harmlessly. The Remove steps run last (review minor 2): while the identity mappings
/// exist the old credentials keep answering <c>ACCOUNT_DELETED</c>; once they are gone only the final transaction is left.
/// The identity module's job decides <em>when</em> (discovery, start decision, limits).
/// </summary>
public sealed class AccountPurgeService(
    ModuleRegistry registry,
    PurgeTargets targets,
    IAccountPurgeWriter writer,
    IMaintenanceQueryStore queries,
    ITransactionalDocumentStore store,
    IOutboxRelay relay,
    IServiceScopeFactory scopes,
    GameServerOptions options,
    PlatformMetrics metrics,
    ILogger<AccountPurgeService> logger)
{
    public const string ReceiptsStep = "receipts";
    public const string VerifyStep = "verify";
    public const string AggregatesStep = "aggregates";

    /// <summary>Failure step id of the final transaction (it is not recorded as a step).</summary>
    public const string CompleteStep = "complete";

    /// <summary>Verify rounds before late-appearing data is treated as a failure (an in-flight relay settles in one).</summary>
    public const int VerifyRounds = 3;

    private IReadOnlyList<PurgeStep>? _steps;

    public static string AnonymizeStep(string collection) => "anonymize:" + collection;

    public static string ModuleStep(string name) => "step:" + name;

    public static string RemoveStep(string collection) => "remove:" + collection;

    private int BatchSize => options.Purge.BatchSize;

    /// <summary>
    /// Runs the unfinished steps of <paramref name="ticket"/>'s purge within <paramref name="budget"/>. A failure is recorded
    /// (<c>nextAttemptAtUtc</c> = backoff) and reported as <see cref="PurgeOutcome.Failed"/>; fencing and cancellation propagate.
    /// </summary>
    public async Task<PurgeOutcome> RunAsync(PurgeTicket ticket, PurgeBudget budget, CancellationToken cancellationToken)
    {
        using var activity = PlatformMetrics.ActivitySource.StartActivity("purge");
        activity?.SetTag("gs.player_id", ticket.PlayerId);
        var current = CompleteStep;
        try
        {
            foreach (var step in _steps ??= BuildSteps())
            {
                if (ticket.IsCompleted(step.Id))
                {
                    continue;
                }

                current = step.Id;
                if (!await step.Run(ticket, budget, cancellationToken))
                {
                    metrics.AccountPurge("incomplete");
                    return PurgeOutcome.Incomplete;
                }

                ticket = await writer.CompleteStepAsync(ticket, step.Id, cancellationToken);
            }

            current = CompleteStep;
            await writer.CompleteAsync(ticket, cancellationToken);
            return Completed(ticket);
        }
        catch (PurgeAlreadyCompletedException)
        {
            return Completed(ticket);
        }
        catch (Exception ex) when (ex is not PurgeFencedOutException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return await RecordFailureAsync(ticket, current, ex, cancellationToken);
        }
    }

    private PurgeOutcome Completed(PurgeTicket ticket)
    {
        metrics.AccountPurge("completed");
        logger.LogInformation("Account of player {PlayerId} purged.", ticket.PlayerId);
        return PurgeOutcome.Completed;
    }

    /// <summary>
    /// Records the failure. A record that is purged already means the failed call did commit (an ambiguous final transaction
    /// that landed, review minor 1): the purge is complete, not failed, and the job goes on with the next player.
    /// </summary>
    private async Task<PurgeOutcome> RecordFailureAsync(PurgeTicket ticket, string step, Exception error, CancellationToken cancellationToken)
    {
        try
        {
            await writer.RecordFailureAsync(ticket, step, error, cancellationToken);
        }
        catch (PurgeAlreadyCompletedException)
        {
            logger.LogInformation(
                "Purge step {Step} of player {PlayerId} reported {ErrorType} but the purge had completed.", step, ticket.PlayerId, error.GetType().FullName);
            return Completed(ticket);
        }
        catch (Exception ex) when (ex is not PurgeFencedOutException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Not recorded: the record stays due and the next run retries at once.
            LogFailure(ticket, step, error);
            logger.LogWarning("Could not record the purge failure of player {PlayerId} ({ErrorType}).", ticket.PlayerId, ex.GetType().FullName);
            return PurgeOutcome.Failed;
        }
        catch
        {
            // Fenced out or shutting down while recording: the job handles that; the original failure is still reported.
            LogFailure(ticket, step, error);
            throw;
        }

        LogFailure(ticket, step, error);
        return PurgeOutcome.Failed;
    }

    private void LogFailure(PurgeTicket ticket, string step, Exception error)
    {
        // Step and exception type only: the message or stack may carry document content or the anonymous id (LOG-05).
        metrics.AccountPurgeStepFailed(step);
        metrics.AccountPurge("failed");
        logger.LogError("Purge of player {PlayerId} failed at step {Step} with {ErrorType}; it is retried after the backoff.", ticket.PlayerId, step, error.GetType().FullName);
    }

    private List<PurgeStep> BuildSteps()
    {
        var plan = registry.DeletionPlan().Where(d => !d.IsPlayerAggregate).ToArray();
        var receipts = plan.Single(d => d.Collection == ReceiptDocument.Collection);
        var anonymized = plan.Where(d => d.Treatment == DeletionTreatment.Anonymize && d != receipts).ToArray();
        var removed = plan.Where(d => d.Treatment == DeletionTreatment.Remove).ToArray();

        // Verify covers the retained (anonymized) data; the Remove steps run after it and drain whatever landed until then.
        CollectionDeletion[] verified = [receipts, .. anonymized];

        var steps = new List<PurgeStep> { new(ReceiptsStep, (t, b, ct) => DrainStepAsync(t, b, receipts, PurgeAction.Anonymize, ct)) };
        steps.AddRange(anonymized.Select(d => new PurgeStep(AnonymizeStep(d.Collection), (t, b, ct) => DrainStepAsync(t, b, d, PurgeAction.Anonymize, ct))));
        foreach (var module in registry.Modules)
        {
            steps.AddRange(module.Descriptor.DeletionSteps.Select(s => new PurgeStep(ModuleStep(s.Name), (t, b, ct) => RunModuleStepAsync(module.Descriptor, s, t, b, ct))));
        }

        steps.Add(new PurgeStep(VerifyStep, (t, b, ct) => VerifyAsync(t, b, verified, ct)));
        steps.Add(new PurgeStep(AggregatesStep, RemoveAggregatesAsync));

        // Last before the final transaction: removing the identity mappings frees the credentials (a login then creates a new
        // player), so only these steps separate it from the profile removal (review minor 2).
        steps.AddRange(removed.Select(d => new PurgeStep(RemoveStep(d.Collection), (t, b, ct) => DrainStepAsync(t, b, d, PurgeAction.Remove, ct))));
        return steps;
    }

    private async Task<bool> DrainStepAsync(PurgeTicket ticket, PurgeBudget budget, CollectionDeletion target, PurgeAction action, CancellationToken cancellationToken) =>
        (await DrainAsync(ticket, budget, target, action, cancellationToken)).Done;

    /// <summary>Processes every document of <paramref name="target"/> that references the player; false when the budget ran out.</summary>
    private async Task<(bool Done, int Found)> DrainAsync(PurgeTicket ticket, PurgeBudget budget, CollectionDeletion target, PurgeAction action, CancellationToken cancellationToken)
    {
        var found = 0;
        IReadOnlyList<DocumentKey>? unchanged = null;
        while (true)
        {
            var limit = budget.Next(BatchSize);
            if (limit == 0)
            {
                return (false, found);
            }

            var keys = await queries.QueryReferencesAsync(new ReferenceQuery(target.Collection, target.PlayerField, ticket.PlayerId, limit), cancellationToken);
            if (keys.Count == 0)
            {
                return (true, found);
            }

            if (unchanged is not null && keys.SequenceEqual(unchanged))
            {
                throw new InvalidOperationException($"Purge of {target.Collection} made no progress: the same documents still reference the player.");
            }

            var written = await writer.ApplyAsync(ticket, keys, action, cancellationToken);
            budget.Consume(keys.Count);
            found += keys.Count;
            unchanged = written == 0 ? keys : null;
            if (target.Collection == ReceiptDocument.Collection)
            {
                await RelayPendingAsync(keys, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Relays rewritten receipts whose outbox is still pending, so their projections carry the anonymous id (research R7);
    /// a relay failure stays with the sweeper and the verify step.
    /// </summary>
    private async Task RelayPendingAsync(IReadOnlyList<DocumentKey> keys, CancellationToken cancellationToken)
    {
        foreach (var document in (await store.GetManyAsync(keys, cancellationToken)).OfType<StoredDocument>())
        {
            var receipt = ReceiptDocument.FromJson(document.Content);
            if (receipt.OutboxPending && !receipt.OutboxDeadLetter)
            {
                await relay.TryRelayAsync(document.Key, receipt, cancellationToken);
            }
        }
    }

    private async Task<bool> RunModuleStepAsync(ModuleDescriptor module, DeletionStepRegistration registration, PurgeTicket ticket, PurgeBudget budget, CancellationToken cancellationToken)
    {
        if (budget.IsExhausted)
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        var step = (IAccountDeletionStep)ActivatorUtilities.GetServiceOrCreateInstance(scope.ServiceProvider, registration.StepType);
        var kinds = module.OutboxProjections.Select(p => p.Kind).ToHashSet(StringComparer.Ordinal);
        var eraser = new PlayerDataEraser(module.Name, kinds, ticket, writer, queries, targets, BatchSize, budget);
        try
        {
            await step.RunAsync(eraser, cancellationToken);
        }
        catch (PurgeBudgetExhaustedException)
        {
            // The step stopped at an eraser call; it is not recorded and runs again from the start in the next run.
            return false;
        }

        return true;
    }

    /// <summary>Re-queries receipts and the anonymized collections: catches projections an in-flight relay inserted with the old id.</summary>
    private async Task<bool> VerifyAsync(PurgeTicket ticket, PurgeBudget budget, IReadOnlyList<CollectionDeletion> verified, CancellationToken cancellationToken)
    {
        for (var round = 1; round <= VerifyRounds; round++)
        {
            var found = 0;
            foreach (var target in verified)
            {
                var action = target.Treatment == DeletionTreatment.Remove ? PurgeAction.Remove : PurgeAction.Anonymize;
                var (done, count) = await DrainAsync(ticket, budget, target, action, cancellationToken);
                if (!done)
                {
                    return false;
                }

                found += count;
            }

            if (found == 0)
            {
                return true;
            }
        }

        throw new InvalidOperationException($"Documents referencing the player kept appearing for {VerifyRounds} verify rounds.");
    }

    /// <summary>Every player aggregate except the profile, removed by key (the profile goes last, research R9).</summary>
    private async Task<bool> RemoveAggregatesAsync(PurgeTicket ticket, PurgeBudget budget, CancellationToken cancellationToken)
    {
        var keys = registry.Aggregates.Values
            .Select(a => a.Collection)
            .Where(c => c != targets.ProfileCollection)
            .Distinct(StringComparer.Ordinal)
            .Select(c => new DocumentKey(c, ticket.PlayerId))
            .ToArray();
        for (var next = 0; next < keys.Length;)
        {
            var limit = budget.Next(BatchSize);
            if (limit == 0)
            {
                return false;
            }

            var batch = keys[next..Math.Min(keys.Length, next + limit)];
            await writer.ApplyAsync(ticket, batch, PurgeAction.Remove, cancellationToken);
            budget.Consume(batch.Length);
            next += batch.Length;
        }

        return true;
    }

    private sealed record PurgeStep(string Id, Func<PurgeTicket, PurgeBudget, CancellationToken, Task<bool>> Run);
}
