using System.Text.Json.Nodes;
using GameServer.Accounts;
using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Commands;

/// <summary>The creation branch of a login attempt (data-model §1.2): initializers, commit rules before staging, one receipt.</summary>
internal sealed partial class AccountWriter
{
    private const string AnchorPlayerField = "playerId";

    /// <summary>Body hash of the creation receipt: SHA-256 of the empty body (research R2).</summary>
    private static readonly string EmptyBodyHash = ReceiptIds.BodyHash([]);

    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Creates the account in this attempt: reads the anchor and the initializers' aggregates, runs the initializers over one
    /// fresh context, checks every commit rule, then stages anchor rewrite, profile, changed aggregates, claims and receipt.
    /// </summary>
    private async Task<Attempt<TProfile>> CreateAsync<TProfile>(
        ITransactionContext tx,
        PlayerAggregateDescriptor profileDescriptor,
        DocumentKey profileKey,
        AccountLoginWrite<TProfile> write,
        CreationRun run)
        where TProfile : class
    {
        var plan = _plan.Value;
        run.Creating = true;
        _metrics.TransactionAttempt(AccountCreation.Operation, run.Attempts++ > 0);

        var keys = new DocumentKey[plan.Touches.Count + 1];
        keys[0] = write.CreationAnchor;
        for (var i = 0; i < plan.Touches.Count; i++)
        {
            keys[i + 1] = AggregateDocumentSerializer.KeyFor(plan.Touches[i], write.PlayerId);
        }

        var documents = await tx.GetManyAsync(keys);
        var anchor = RequireAnchor(documents[0], write.CreationAnchor, write.PlayerId);

        // A broken aggregate of an initializer throws its typed read failure here (FG-029): nothing is staged.
        var loaded = new Dictionary<string, LoadedAggregate>(StringComparer.Ordinal);
        for (var i = 0; i < plan.Touches.Count; i++)
        {
            loaded[plan.Touches[i].AggregateId] = _serializer.Load(plan.Touches[i], write.PlayerId, documents[i + 1]);
        }

        // A fresh context per attempt: no outbox entry, claim or state of a discarded attempt survives (trap T2).
        var now = _clock.UtcNow;
        var actor = new ActorContext(write.PlayerId, ActorType.System, write.PlayerId, _options.GameId, _options.EnvironmentId, NoPermissions, write.CorrelationId);
        var context = new CommandContext(actor, now, _config, loaded, plan.Descriptor, tx, run.ReceiptId, _registry.ProvisionedCollections);
        var writers = new Dictionary<string, string>(StringComparer.Ordinal);
        var views = await RunInitializersAsync(plan, context, writers);

        var initial = _serializer.Load(profileDescriptor, write.PlayerId, document: null);
        var (profile, profileDocument, profileSize) = Apply(profileDescriptor, initial, write);
        if (profileDocument is null)
        {
            // A login that leaves the default profile unchanged creates nothing.
            return new Attempt<TProfile>(profile, initial.Rev, Created: false, ProfileBytes: null);
        }

        // Every commit rule runs before the first staged write (research R8 "Order").
        var writes = CollectWrites(plan, loaded, context, writers, now);
        CheckAudit(views);
        var (receipt, receiptJson, receiptSize) = BuildReceipt(plan, run, actor, writes, context, now);

        // The unchanged rewrite makes the creation write-conflict with a concurrent removal of the anchor (010 R9).
        await tx.ReplaceAsync(anchor, (JsonObject)anchor.Content.DeepClone());
        await tx.InsertAsync(profileKey, profileDocument);
        foreach (var change in writes)
        {
            if (change.Before.Document is null)
            {
                await tx.InsertAsync(AggregateDocumentSerializer.KeyFor(change.Descriptor, write.PlayerId), change.Document);
            }
            else
            {
                await tx.ReplaceAsync(change.Before.Document, change.Document);
            }
        }

        foreach (var (claimKey, claim) in context.Claims)
        {
            await tx.InsertAsync(claimKey, claim);
        }

        if (receiptJson is not null)
        {
            // The request id is new for every login call, so the key is never taken.
            await tx.InsertAsync(ReceiptDocument.KeyFor(run.ReceiptId), receiptJson);
        }

        return new Attempt<TProfile>(profile, initial.Rev + 1, Created: true, profileSize)
        {
            Receipt = receipt,
            ReceiptSize = receiptSize,
            Writes = writes,
        };
    }

    /// <summary>Runs every initializer in registration order through its own view; a code, a throw or a view rule fails the attempt.</summary>
    private async Task<IReadOnlyList<InitializerContext>> RunInitializersAsync(CreationPlan plan, CommandContext context, Dictionary<string, string> writers)
    {
        var views = new List<InitializerContext>(plan.Initializers.Count);
        foreach (var initializer in plan.Initializers)
        {
            var view = new InitializerContext(context, initializer.Registration, writers);
            views.Add(view);
            ErrorCode? code;
            try
            {
                // Created per attempt from the root provider, as command handlers (research R3).
                var instance = (IAccountInitializer)ActivatorUtilities.CreateInstance(_services, initializer.Registration.InitializerType);
                code = await instance.InitializeAsync(view);
            }
            catch (AccountCreationRuleException ex)
            {
                throw AccountInitializerFailedException.Rule(initializer.Registration.Name, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !CommandContext.IsStoreFailure(ex))
            {
                // A store failure of a context call (marked by CommandContext) is not the initializer's: it reaches the store
                // unchanged, which maps it to the login's store codes (T082 M1).
                throw AccountInitializerFailedException.Threw(initializer, ex);
            }

            if (code is not null)
            {
                throw AccountInitializerFailedException.Rejected(initializer, code);
            }
        }

        return views;
    }

    /// <summary>Changed initializer aggregates with their documents; an aggregate over its HardBytes fails the creation.</summary>
    private List<CreationWrite> CollectWrites(
        CreationPlan plan,
        Dictionary<string, LoadedAggregate> loaded,
        CommandContext context,
        Dictionary<string, string> writers,
        DateTimeOffset now)
    {
        var writes = new List<CreationWrite>();
        foreach (var descriptor in plan.Touches)
        {
            var before = loaded[descriptor.AggregateId];
            if (!context.TryGetNewState(descriptor.AggregateId, out var json) || !CommitRules.ChangesState(before, json))
            {
                continue;
            }

            var newRev = before.Rev + 1;
            var (document, size) = _serializer.BuildDocument(before, json, newRev, now);
            if (CommitRules.ExceedsHardBytes(descriptor, size))
            {
                var writer = writers[descriptor.AggregateId];
                var line = CommitRules.CheckCreationAggregateSize(AccountCreation.InitializerOperation(writer), _options.GameId, _options.EnvironmentId, before, json, now)!;
                throw AccountInitializerFailedException.Rule(writer, line);
            }

            writes.Add(new CreationWrite(descriptor, before, document, size, newRev));
        }

        return writes;
    }

    /// <summary>SEC-14: a creation is not a GM command, so no initializer adds an audit entry.</summary>
    private static void CheckAudit(IReadOnlyList<InitializerContext> views)
    {
        foreach (var view in views)
        {
            if (CommitRules.CheckAudit(new CommandDescriptor(view.Operation, [], profile: SecurityProfile.Player), view.Outbox) is { } line)
            {
                throw AccountInitializerFailedException.Rule(view.InitializerName, line);
            }
        }
    }

    /// <summary>The creation receipt (data-model §1.3), only when the initializers had effects (research R2); its size rule.</summary>
    private (ReceiptDocument? Receipt, JsonObject? Json, int Size) BuildReceipt(
        CreationPlan plan,
        CreationRun run,
        ActorContext actor,
        List<CreationWrite> writes,
        CommandContext context,
        DateTimeOffset now)
    {
        if (writes.Count == 0 && context.Claims.Count == 0 && context.Outbox.Count == 0)
        {
            return (null, null, 0);
        }

        var receipt = ReceiptBuilder.Build(new ReceiptInputs(
            run.ReceiptId,
            _options.GameId,
            _options.EnvironmentId,
            actor.ActorId,
            actor.ActorType,
            run.PlayerId,
            AccountCreation.Operation,
            run.RequestId,
            EmptyBodyHash,
            CoreErrorCodes.Ok,
            [],
            _config.ConfigVersion,
            plan.Touches,
            writes.Select(w => new ReceiptAggregate(w.Descriptor.AggregateId, w.NewRev)),
            context.Claims.Keys,
            context.Outbox,
            actor.CorrelationId,
            now));
        var json = receipt.ToJson();
        var size = ReceiptDocument.SizeOf(json);
        if (CommitRules.CheckCreationReceiptSize(plan.ReceiptOperation, size, _options.Commands) is { } violation)
        {
            throw AccountInitializerFailedException.ReceiptTooLarge(violation.Line);
        }

        return (receipt, json, size);
    }

    /// <summary>
    /// The creation anchor must exist and name the player. A read alone is not conflict-detected by Couchbase transactions,
    /// which is why the caller rewrites it unchanged before inserting the profile.
    /// </summary>
    private static StoredDocument RequireAnchor(StoredDocument? anchor, DocumentKey anchorKey, string playerId)
    {
        var owner = anchor?.Content[AnchorPlayerField] is JsonValue value && value.TryGetValue<string>(out var id) ? id : null;
        if (anchor is null || !string.Equals(owner, playerId, StringComparison.Ordinal))
        {
            throw new AggregateAnchorMissingException(anchorKey);
        }

        return anchor;
    }

    /// <summary>A changed initializer aggregate of the creation.</summary>
    private sealed record CreationWrite(PlayerAggregateDescriptor Descriptor, LoadedAggregate Before, JsonObject Document, int SizeBytes, long NewRev);

    /// <summary>The creation descriptor, its aggregates and initializers, built once from the validated registry.</summary>
    private sealed record CreationPlan(
        CommandDescriptor Descriptor,
        IReadOnlyList<PlayerAggregateDescriptor> Touches,
        IReadOnlyList<RegisteredAccountInitializer> Initializers,
        string ReceiptOperation)
    {
        public static CreationPlan Build(ModuleRegistry registry)
        {
            var descriptor = AccountCreation.Describe(registry);
            var initializers = registry.AccountInitializers;

            // The receipt belongs to every initializer of the creation: its rule line names them all.
            var receiptOperation = initializers.Count == 0
                ? AccountCreation.Operation
                : AccountCreation.InitializerOperation(string.Join(", ", initializers.Select(i => i.Registration.Name)));
            return new CreationPlan(descriptor, descriptor.Touches.Select(registry.GetAggregate).ToArray(), initializers, receiptOperation);
        }
    }
}
