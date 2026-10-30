using GameServer.Actors;
using GameServer.Commands;
using GameServer.Errors;

namespace GameServer.Modules.Identity;

/// <summary>Grace period between a deletion request and the purge (PRIV-01, <c>Identity:Deletion:GraceDays</c>).</summary>
public sealed record AccountDeletionPolicy(int GraceDays);

public sealed record RequestDeletionCommand;

public sealed record RequestDeletionResult(DateTimeOffset RequestedAtUtc, DateTimeOffset PurgeAfterUtc);

/// <summary>
/// <c>identity.request_deletion</c> (PRIV-01, FR-006): the player asks to delete the account. One transaction marks the
/// profile deleted with the purge date and increments the session epoch, so every session ends (IDN-08). An already
/// deleted account is rejected with ACCOUNT_DELETED and no receipt: a second request id never writes a second receipt.
/// </summary>
public sealed class RequestDeletionHandler(AccountDeletionPolicy policy) : ICommandHandler<RequestDeletionCommand, RequestDeletionResult>
{
    public const string Operation = "identity.request_deletion";

    public static CommandDescriptor Descriptor { get; } = new(
        Operation,
        touches: [IdentityAggregates.Profile],
        profile: SecurityProfile.Player);

    public ValueTask<CommandOutcome<RequestDeletionResult>> HandleAsync(RequestDeletionCommand request, ICommandContext context)
    {
        var profile = context.Get<ProfileState>(IdentityAggregates.Profile);
        if (profile.Deleted is { } deleted)
        {
            return ValueTask.FromResult(CommandOutcome<RequestDeletionResult>.Reject(CoreErrorCodes.AccountDeleted, AccountChecks.DeletionDetails(deleted)));
        }

        var requested = new DeletionInfo
        {
            RequestedAtUtc = context.Now,
            PurgeAfterUtc = context.Now.AddDays(policy.GraceDays),
            PurgeStartedAtUtc = null,
        };
        context.Set(IdentityAggregates.Profile, profile with { Deleted = requested, SessionEpoch = profile.SessionEpoch + 1 });
        return ValueTask.FromResult(CommandOutcome<RequestDeletionResult>.Ok(new RequestDeletionResult(requested.RequestedAtUtc, requested.PurgeAfterUtc)));
    }
}

public sealed record CancelDeletionCommand(string Reason);

/// <summary>Result of a GM cancel: the deletion that was cleared.</summary>
public sealed record CancelDeletionResult(DeletionInfo Before);

/// <summary>
/// <c>identity.cancel_deletion</c> (PRIV-01, FR-011): GM clears a pending deletion while the grace period runs and the
/// purge has not started. Sessions revoked by the request stay revoked (epoch unchanged): the player logs in again.
/// A cancel racing the purge start conflicts on <c>pa_profile</c>; the retry sees the winner's state.
/// </summary>
public sealed class CancelDeletionHandler : ICommandHandler<CancelDeletionCommand, CancelDeletionResult>
{
    public const string Operation = "identity.cancel_deletion";

    public static CommandDescriptor Descriptor { get; } = new(
        Operation,
        touches: [IdentityAggregates.Profile],
        profile: SecurityProfile.Gm,
        permission: IdentityPermissions.PlayerBan);

    public ValueTask<CommandOutcome<CancelDeletionResult>> HandleAsync(CancelDeletionCommand request, ICommandContext context)
    {
        var profile = context.Get<ProfileState>(IdentityAggregates.Profile);
        if (profile.Deleted is not { } deleted)
        {
            return ValueTask.FromResult(CommandOutcome<CancelDeletionResult>.Reject(IdentityErrors.DeletionNotPending));
        }

        if (deleted.PurgeStartedAtUtc is not null || context.Now >= deleted.PurgeAfterUtc)
        {
            return ValueTask.FromResult(CommandOutcome<CancelDeletionResult>.Reject(IdentityErrors.DeletionGraceOver));
        }

        context.Set(IdentityAggregates.Profile, profile with { Deleted = null });
        context.AddAudit(IdentityPermissions.PlayerBan, Operation, request.Reason, deleted, null);
        return ValueTask.FromResult(CommandOutcome<CancelDeletionResult>.Ok(new CancelDeletionResult(deleted)));
    }
}

/// <summary>
/// When the purge of a pending deletion starts (research R7, step 0): deleted, not started yet and the grace period over.
/// Pure, so the purge writer can re-run it inside its transaction; a concurrent cancel conflicts on the same profile write.
/// </summary>
public static class AccountPurgeStart
{
    /// <summary>The profile marked purging at <paramref name="now"/>, or null when the purge must not start.</summary>
    public static ProfileState? Decide(ProfileState profile, DateTimeOffset now) =>
        profile.Deleted is { PurgeStartedAtUtc: null } deleted && deleted.PurgeAfterUtc <= now
            ? profile with { Deleted = deleted with { PurgeStartedAtUtc = now } }
            : null;
}
