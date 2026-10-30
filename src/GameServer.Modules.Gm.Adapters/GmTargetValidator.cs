using GameServer.Errors;
using GameServer.Modules.Identity;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Privacy;
using GameServer.Platform.Web.Commands;

namespace GameServer.Modules.Gm;

/// <summary>
/// GM mutations only target players that exist (have a profile) and are not pending deletion, so a mistyped id cannot
/// create wallets, ledger entries and audits for a player that does not exist. The one exception is
/// <c>identity.cancel_deletion</c>: it targets a pending player, and a purging or purged player answers 409
/// DELETION_GRACE_OVER instead of 404 (PRIV-01, FR-011).
/// </summary>
public sealed class GmTargetValidator(PlayerSnapshotReader snapshots, AccountDeletionRecords deletions) : IGmTargetValidator
{
    public async Task ValidateAsync(string playerId, string operation, CancellationToken cancellationToken)
    {
        var cancel = string.Equals(operation, CancelDeletionHandler.Operation, StringComparison.Ordinal);
        var profile = await snapshots.ReadOneAsync(playerId, IdentityAggregates.Profile, cancellationToken);
        if (!profile.Exists)
        {
            // The purge removes the profile last: a missing profile with a record means purged (or purging).
            if (cancel && await deletions.GetAsync(playerId, cancellationToken) is not null)
            {
                throw new GameServerException(IdentityErrors.DeletionGraceOver, "The account of this player is purged.");
            }

            throw new GameServerException(CoreErrorCodes.NotFound, "Player not found.");
        }

        if (((ProfileState)profile.State).Deleted is not null && !cancel)
        {
            throw new GameServerException(CoreErrorCodes.NotFound, "Player is pending deletion.");
        }
    }
}
