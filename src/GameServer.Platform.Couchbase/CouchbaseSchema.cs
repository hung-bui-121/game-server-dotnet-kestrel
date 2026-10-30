using GameServer.Modules;
using GameServer.Platform.Commands;
using GameServer.Platform.Registry;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// What the provisioner creates for the registered modules (DAT-07): the collections, <c>txn_meta</c> first, and the
/// framework, module and purge reference indexes with their <c>CREATE INDEX … IF NOT EXISTS</c> statements (unchanged by
/// slice 011). A pure function of the registry and the keyspace; <see cref="CouchbaseProvisioner"/> does the I/O.
/// </summary>
internal sealed class CouchbaseSchema(ModuleRegistry registry, string bucketName, string scopeName)
{
    /// <summary><c>txn_meta</c> first (DAT-07: transactions need it before any other write), then the others in ordinal order.</summary>
    public IReadOnlyList<string> Collections() =>
    [
        CouchbaseConnection.TransactionMetadataCollection,
        .. registry.RequiredCollections()
            .Where(c => c != CouchbaseConnection.TransactionMetadataCollection)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Reference indexes of the account purge (research R5): <c>ix_{collection}_player_ref</c> on (player field, document id)
    /// for every Anonymize/Remove collection of the deletion plan, so the key-ordered reference query reads index order and
    /// stops at its LIMIT (review minor 4: a history index (player, time, id) made each batch sort the player's whole
    /// history). Skipped only when a declared index has exactly these keys and no WHERE clause (review minor 3: a partial
    /// index cannot serve every reference). <c>pa_*</c> are removed by key and need none.
    /// </summary>
    public IReadOnlyList<ReferenceIndex> ReferenceIndexes()
    {
        var declared = registry.Modules
            .SelectMany(m => m.Descriptor.Collections)
            .SelectMany(c => (c.Indexes ?? []).Where(i => i.Where is null).Select(i => (Collection: c.Name, i.Keys)))
            .ToArray();
        return registry.DeletionPlan()
            .Where(d => d.Treatment is DeletionTreatment.Anonymize or DeletionTreatment.Remove && !d.IsPlayerAggregate)
            .Where(d => !declared.Any(i => i.Collection == d.Collection && IsReferenceShaped(i.Keys, d.PlayerField)))
            .Select(d => new ReferenceIndex(
                $"ix_{d.Collection}_player_ref",
                d.Collection,
                string.Join('.', d.PlayerField.Split('.').Select(s => $"`{s}`")) + ", META().id"))
            .ToArray();
    }

    /// <summary>Framework, module and purge reference indexes, in creation order.</summary>
    public IReadOnlyList<IndexDefinition> Indexes()
    {
        var keyspace = $"`{bucketName}`.`{scopeName}`";
        IndexDefinition Index(string name, string collection, string keys, string? where = null) => new(
            name,
            collection,
            $"CREATE INDEX `{name}` IF NOT EXISTS ON {keyspace}.`{collection}`({keys})" + (where is null ? string.Empty : " WHERE " + where));

        var indexes = new List<IndexDefinition>
        {
            Index(CouchbaseProvisioner.PendingOutboxIndex, "receipts", "committedAtUtc", "outboxPending = true"),
            Index(CouchbaseProvisioner.ReceiptsPlayerIndex, "receipts", "playerId, committedAtUtc DESC, META().id DESC"),

            // (due, id): the due query's ORDER BY reads index order and stops at its LIMIT (no sort of the whole backlog, PERF-07).
            Index(CouchbaseProvisioner.AccountDeletionsDueIndex, PlatformCollections.AccountDeletions, "nextAttemptAtUtc, META().id", "purgedAtUtc IS NOT VALUED"),
        };
        indexes.AddRange(registry.Modules
            .SelectMany(m => m.Descriptor.Collections)
            .SelectMany(spec => (spec.Indexes ?? []).Select(index => Index(index.Name, spec.Name, index.Keys, index.Where))));
        indexes.AddRange(ReferenceIndexes().Select(r => Index(r.Name, r.Collection, r.Keys)));
        return indexes;
    }

    /// <summary>Whether index keys are exactly <c>(playerField ASC, META().id ASC)</c>, ignoring backticks, case of META and blanks.</summary>
    private static bool IsReferenceShaped(string keys, string playerField)
    {
        var parts = keys.Split(',').Select(k => k.Replace("`", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal)).ToArray();
        return parts.Length == 2
            && (parts[0] == playerField || parts[0] == playerField + "ASC")
            && parts[1].ToUpperInvariant() is "META().ID" or "META().IDASC";
    }
}

/// <summary>An index created by the provisioner for the account purge's reference queries.</summary>
public sealed record ReferenceIndex(string Name, string Collection, string Keys);

/// <summary>An index the provisioner creates: name, collection and its <c>CREATE INDEX … IF NOT EXISTS</c> statement.</summary>
public sealed record IndexDefinition(string Name, string Collection, string Statement);
