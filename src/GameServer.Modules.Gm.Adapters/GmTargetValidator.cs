using GameServer.Errors;
using GameServer.Modules.Identity;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Web.Commands;

namespace GameServer.Modules.Gm;

/// <summary>
/// GM mutations only target players that exist (have a profile) and are not pending deletion, so a mistyped id cannot
/// create wallets, ledger entries and audits for a player that does not exist.
/// </summary>
public sealed class GmTargetValidator(PlayerSnapshotReader snapshots) : IGmTargetValidator
{
    public async Task ValidateAsync(string playerId, CancellationToken cancellationToken)
    {
        var profile = await snapshots.ReadOneAsync(playerId, IdentityAggregates.Profile, cancellationToken);
        if (!profile.Exists)
        {
            throw new GameServerException(CoreErrorCodes.NotFound, "Player not found.");
        }

        if (((ProfileState)profile.State).Deleted is not null)
        {
            throw new GameServerException(CoreErrorCodes.NotFound, "Player is pending deletion.");
        }
    }
}
