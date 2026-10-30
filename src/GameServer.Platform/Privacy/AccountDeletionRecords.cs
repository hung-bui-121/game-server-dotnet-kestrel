using System.Text.Json.Nodes;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;

namespace GameServer.Platform.Privacy;

/// <summary>State of an account purge (data-model.md, <c>account_deletions</c>).</summary>
public enum AccountDeletionState
{
    Purging,
    Purged,
}

/// <summary>Last failed purge attempt: step and exception type only, never a message (it may hold data, LOG-05).</summary>
public sealed record PurgeFailure(string Step, string ErrorType, DateTimeOffset AtUtc);

/// <summary>
/// <c>account_deletions/deletion::{playerId}</c>: progress and fencing anchor while purging, purge record afterwards
/// (FR-017, FR-018). <see cref="AnonymousId"/>, <see cref="CompletedSteps"/>, <see cref="Attempts"/> and
/// <see cref="LastFailure"/> exist only while purging. Written only by the purge writer, inside transactions.
/// </summary>
public sealed record AccountDeletionRecord
{
    public const string KeyPrefix = "deletion::";

    public required string PlayerId { get; init; }

    public required string GameId { get; init; }

    public required string EnvironmentId { get; init; }

    public AccountDeletionState State { get; init; }

    public DateTimeOffset RequestedAtUtc { get; init; }

    public DateTimeOffset PurgeAfterUtc { get; init; }

    public DateTimeOffset PurgeStartedAtUtc { get; init; }

    public DateTimeOffset? PurgedAtUtc { get; init; }

    /// <summary>Replacement id while purging; removed when purged. Never logged or returned by an API.</summary>
    public string? AnonymousId { get; init; }

    /// <summary>Highest job lease token that wrote the record; a smaller token is fenced out (FR-003).</summary>
    public long FencingToken { get; init; }

    public IReadOnlyList<string> CompletedSteps { get; init; } = [];

    public long DocumentsProcessed { get; init; }

    /// <summary>Consecutive failed attempts.</summary>
    public int Attempts { get; init; }

    public PurgeFailure? LastFailure { get; init; }

    /// <summary>When the purge job should next work on this player; null once purged.</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; init; }

    public static DocumentKey KeyOf(string playerId) => new(PlatformCollections.AccountDeletions, KeyPrefix + playerId);

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["playerId"] = PlayerId,
            ["gameId"] = GameId,
            ["environmentId"] = EnvironmentId,
            ["state"] = State == AccountDeletionState.Purged ? "purged" : "purging",
            ["requestedAtUtc"] = AggregateDocumentSerializer.FormatTime(RequestedAtUtc),
            ["purgeAfterUtc"] = AggregateDocumentSerializer.FormatTime(PurgeAfterUtc),
            ["purgeStartedAtUtc"] = AggregateDocumentSerializer.FormatTime(PurgeStartedAtUtc),
            ["purgedAtUtc"] = PurgedAtUtc is { } purged ? AggregateDocumentSerializer.FormatTime(purged) : null,
            ["fencingToken"] = FencingToken,
            ["documentsProcessed"] = DocumentsProcessed,
            ["nextAttemptAtUtc"] = NextAttemptAtUtc is { } next ? AggregateDocumentSerializer.FormatTime(next) : null,
        };

        // Purge record (FR-018): the anonymous id and the progress fields are dropped, not just emptied.
        if (State == AccountDeletionState.Purging)
        {
            json["anonymousId"] = AnonymousId;
            json["completedSteps"] = new JsonArray(CompletedSteps.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            json["attempts"] = Attempts;
            json["lastFailure"] = LastFailure is null
                ? null
                : new JsonObject
                {
                    ["step"] = LastFailure.Step,
                    ["errorType"] = LastFailure.ErrorType,
                    ["atUtc"] = AggregateDocumentSerializer.FormatTime(LastFailure.AtUtc),
                };
        }

        return json;
    }

    public static AccountDeletionRecord FromJson(JsonObject json) => new()
    {
        PlayerId = json["playerId"]!.GetValue<string>(),
        GameId = json["gameId"]?.GetValue<string>() ?? string.Empty,
        EnvironmentId = json["environmentId"]?.GetValue<string>() ?? string.Empty,
        State = json["state"]?.GetValue<string>() == "purged" ? AccountDeletionState.Purged : AccountDeletionState.Purging,
        RequestedAtUtc = Time(json["requestedAtUtc"]) ?? default,
        PurgeAfterUtc = Time(json["purgeAfterUtc"]) ?? default,
        PurgeStartedAtUtc = Time(json["purgeStartedAtUtc"]) ?? default,
        PurgedAtUtc = Time(json["purgedAtUtc"]),
        AnonymousId = json["anonymousId"]?.GetValue<string>(),
        FencingToken = json["fencingToken"]?.GetValue<long>() ?? 0,
        CompletedSteps = json["completedSteps"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [],
        DocumentsProcessed = json["documentsProcessed"]?.GetValue<long>() ?? 0,
        Attempts = json["attempts"]?.GetValue<int>() ?? 0,
        LastFailure = json["lastFailure"] is JsonObject failure
            ? new PurgeFailure(failure["step"]!.GetValue<string>(), failure["errorType"]!.GetValue<string>(), Time(failure["atUtc"]) ?? default)
            : null,
        NextAttemptAtUtc = Time(json["nextAttemptAtUtc"]),
    };

    private static DateTimeOffset? Time(JsonNode? node) =>
        node?.GetValue<string>() is { } value ? AggregateDocumentSerializer.ParseTime(value) : null;
}

/// <summary>
/// Read side of the purge records for GM, the target validator and the purge job (no schema knowledge outside Platform).
/// </summary>
public sealed class AccountDeletionRecords(ITransactionalDocumentStore store, IMaintenanceQueryStore queries)
{
    /// <summary>Due field of purges to resume or retry, served by <c>ix_account_deletions_due</c> (partial: not purged).</summary>
    public const string NextAttemptField = "nextAttemptAtUtc";

    /// <summary>Set once purged; the due index and query leave such records out.</summary>
    public const string PurgedField = "purgedAtUtc";

    /// <summary>The player's purge record or purge in progress; null when the player was never purged.</summary>
    public async Task<AccountDeletionRecord?> GetAsync(string playerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(playerId);
        var document = await store.GetAsync(AccountDeletionRecord.KeyOf(playerId), cancellationToken);
        return document is null ? null : AccountDeletionRecord.FromJson(document.Content);
    }

    /// <summary>Unfinished purges whose <c>nextAttemptAtUtc &lt;= now</c>, oldest attempt first, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<AccountDeletionRecord>> QueryDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var due = await queries.QueryDueAsync(
            new DueQuery(PlatformCollections.AccountDeletions, NextAttemptField, now, limit, PurgedField),
            cancellationToken);
        return due.Select(d => AccountDeletionRecord.FromJson(d.Content)).ToArray();
    }
}
