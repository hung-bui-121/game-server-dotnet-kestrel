using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Commands;

namespace GameServer.Platform.Privacy;

/// <summary>Pure document rewrites of the account purge (research R6 anonymize rule, R8 receipt and audit rules, plan D1).</summary>
public static class PurgeRewrites
{
    /// <summary>Claim key replacement for entries pointing into a Remove-treated collection: <c>{collection}/redacted</c>.</summary>
    public const string RedactedClaimKey = "redacted";

    /// <summary>JSON name of <see cref="AuditEntry.CorrelationId"/> inside an audit payload.</summary>
    private const string AuditCorrelationField = "correlationId";

    /// <summary>
    /// Replaces every JSON string anywhere in <paramref name="node"/> that equals <paramref name="playerId"/> exactly with
    /// <paramref name="anonymousId"/> (in place). Property names and partial matches are left alone.
    /// </summary>
    public static void Anonymize(JsonNode? node, string playerId, string anonymousId)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToArray())
                {
                    if (IsString(child, playerId))
                    {
                        obj[name] = anonymousId;
                    }
                    else
                    {
                        Anonymize(child, playerId, anonymousId);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (IsString(array[i], playerId))
                    {
                        array[i] = anonymousId;
                    }
                    else
                    {
                        Anonymize(array[i], playerId, anonymousId);
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Receipt rules beyond the generic anonymize (D1, review B1):
    /// <list type="bullet">
    /// <item><c>resultCompact</c>, <c>bodyHash</c> → <c>""</c>: personal data in results, dictionary-attackable hashes.</item>
    /// <item><c>requestId</c>, <c>correlationId</c> → <c>""</c>: the receipt key is <c>SHA-256(actorId | playerId | operation |
    /// requestId)</c> without a secret, so a request id kept next to the key lets anyone test a candidate player id against it
    /// and undo the anonymization (the correlation id of a command is the same UUID).</item>
    /// <item><c>claimKeys</c> entries into a Remove-treated collection → <c>{collection}/redacted</c> (provider ids).</item>
    /// <item>audit outbox entries lose their correlation id (<see cref="ApplyAuditRules"/>).</item>
    /// </list>
    /// All of them only serve the 24 h replay window and request tracing, long over at purge time.
    /// </summary>
    public static void ApplyReceiptRules(JsonObject receipt, IReadOnlySet<string> removedCollections)
    {
        receipt["resultCompact"] = string.Empty;
        receipt["bodyHash"] = string.Empty;
        receipt["requestId"] = string.Empty;
        receipt["correlationId"] = string.Empty;
        if (receipt["outbox"] is JsonArray outbox)
        {
            foreach (var entry in outbox.OfType<JsonObject>().Where(e => IsString(e["kind"], AuditExtensions.OutboxKind)))
            {
                ClearAuditCorrelation(entry["payload"]);
            }
        }

        if (receipt["claimKeys"] is not JsonArray claimKeys)
        {
            return;
        }

        for (var i = 0; i < claimKeys.Count; i++)
        {
            if (claimKeys[i] is JsonValue value && value.GetValueKind() == JsonValueKind.String
                && value.GetValue<string>().Split('/', 2) is [var collection, _]
                && removedCollections.Contains(collection))
            {
                claimKeys[i] = $"{collection}/{RedactedClaimKey}";
            }
        }
    }

    /// <summary>
    /// Audit rule (review B1): the audit entry of a GM command carries the GM request's correlation id, which is the request
    /// id of the command's receipt; next to the projection's <c>receiptId</c> it would re-identify the player exactly like the
    /// receipt's own <c>requestId</c>. Applied to the purged player's <c>gm_audit</c> projections.
    /// </summary>
    public static void ApplyAuditRules(JsonObject audit) => ClearAuditCorrelation(audit["payload"]);

    private static void ClearAuditCorrelation(JsonNode? payload)
    {
        if (payload is JsonObject entry && entry.ContainsKey(AuditCorrelationField))
        {
            entry[AuditCorrelationField] = string.Empty;
        }
    }

    private static bool IsString(JsonNode? node, string value) =>
        node is JsonValue json && json.GetValueKind() == JsonValueKind.String && json.GetValue<string>() == value;
}
