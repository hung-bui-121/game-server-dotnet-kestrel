namespace GameServer.Commands;

/// <summary>
/// Audit record of a GM mutation (SEC-14), committed as an outbox entry of the command's receipt. With the projection
/// wrapper (receiptId, committedAtUtc) it carries the 7 required fields: actor, permission/action, target, reason,
/// before/after or the receipt reference, UTC time, correlation.
/// </summary>
public sealed record AuditEntry(
    string ActorId,
    string Permission,
    string Action,
    string TargetPlayerId,
    string Reason,
    object? Before,
    object? After,
    string CorrelationId);

public static class AuditExtensions
{
    /// <summary>Outbox kind projected into collection <c>gm_audit</c> by the GM module.</summary>
    public const string OutboxKind = "gm_audit";

    /// <summary>
    /// Records an audit entry inside the current command. Resource adjustments reference the receipt (no copied delta);
    /// edits without a ledger (progression) pass <paramref name="before"/>/<paramref name="after"/>.
    /// </summary>
    public static void AddAudit(this ICommandContext context, string permission, string action, string reason, object? before = null, object? after = null) =>
        context.AddOutbox(OutboxKind, new AuditEntry(
            context.Actor.ActorId,
            permission,
            action,
            context.Actor.PlayerId ?? string.Empty,
            reason,
            before,
            after,
            context.Actor.CorrelationId));
}
