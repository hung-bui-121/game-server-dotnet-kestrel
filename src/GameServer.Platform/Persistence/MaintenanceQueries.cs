using System.Text.Json;
using GameServer.Platform.Aggregates;

namespace GameServer.Platform.Persistence;

/// <summary>How the time field of a <see cref="DueQuery"/> was written; the query value is formatted the same way so string order equals time order.</summary>
public enum DueTimeFormat
{
    /// <summary><see cref="AggregateDocumentSerializer.FormatTime"/> (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>): platform documents.</summary>
    Platform,

    /// <summary>System.Text.Json <see cref="DateTimeOffset"/> at offset 0 (<c>2026-11-29T10:00:00+00:00</c>): aggregate state written from <c>IClock.UtcNow</c>.</summary>
    Json,
}

/// <summary>
/// Documents whose <paramref name="DueField"/> (a time string) is &lt;= <paramref name="Now"/> and whose
/// <paramref name="UnsetField"/> (when given) is missing or null; oldest first, then by key; at most <paramref name="Limit"/>;
/// read at RequestPlus. Needs a partial index on <paramref name="DueField"/> with the same unset predicate (PERF-07).
/// </summary>
public sealed record DueQuery(
    string Collection,
    string DueField,
    DateTimeOffset Now,
    int Limit,
    string? UnsetField = null,
    DueTimeFormat Format = DueTimeFormat.Platform)
{
    /// <summary>Query value compared with the field, as a string in the field's own format.</summary>
    public string NowValue => Format switch
    {
        DueTimeFormat.Json => JsonSerializer.Serialize(Now.ToOffset(TimeSpan.Zero), GameServerJson.Options).Trim('"'),
        _ => AggregateDocumentSerializer.FormatTime(Now),
    };

    public void Validate()
    {
        MaintenanceQueryRules.ValidateCollection(Collection);
        MaintenanceQueryRules.ValidateField(DueField);
        if (UnsetField is not null)
        {
            MaintenanceQueryRules.ValidateField(UnsetField);
        }

        MaintenanceQueryRules.ValidateLimit(Limit);
    }
}

/// <summary>
/// Keys of documents whose <paramref name="Field"/> equals <paramref name="Value"/> (a string); ordered by key; keys greater
/// than <paramref name="AfterKey"/> when given (paging over documents a job does not change); at most <paramref name="Limit"/>;
/// RequestPlus.
/// </summary>
public sealed record ReferenceQuery(string Collection, string Field, string Value, int Limit, string? AfterKey = null)
{
    public void Validate()
    {
        MaintenanceQueryRules.ValidateCollection(Collection);
        MaintenanceQueryRules.ValidateField(Field);
        MaintenanceQueryRules.ValidateLimit(Limit);
    }
}

/// <summary>Bounded maintenance queries for jobs (PERF-07: N1QL only outside the hot path). Couchbase and in-memory.</summary>
public interface IMaintenanceQueryStore
{
    /// <summary>Documents of <see cref="DueQuery"/> with their committed content (no write handle).</summary>
    Task<IReadOnlyList<StoredDocument>> QueryDueAsync(DueQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentKey>> QueryReferencesAsync(ReferenceQuery query, CancellationToken cancellationToken);
}

internal static class MaintenanceQueryRules
{
    /// <summary>Upper bound of one maintenance query (a job pages by re-querying after it processed a batch).</summary>
    public const int MaxLimit = 1000;

    public static void ValidateCollection(string collection)
    {
        if (!PagedQuery.IsValidCollection(collection))
        {
            throw new ArgumentException($"Invalid collection name '{collection}'.", nameof(collection));
        }
    }

    public static void ValidateField(string field)
    {
        // Field paths are spliced into N1QL: dotted identifiers only (no query injection).
        if (!PagedQuery.IsValidField(field))
        {
            throw new ArgumentException($"Invalid field path '{field}'.", nameof(field));
        }
    }

    public static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"Limit must be 1..{MaxLimit}.");
        }
    }
}
