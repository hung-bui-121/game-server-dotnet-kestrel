using GameServer.Actors;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Platform.Commands;
using GameServer.Time;

namespace GameServer.Modules.Identity;

/// <summary>
/// Command-scope bans (BAN-01): a ban whose scope lists the command group (operation prefix) rejects new executions of
/// that group without revoking the session. GM commands on a banned player are not blocked. Account deletion is never
/// blocked by a command-group ban, even of group <c>identity</c> (PRIV-01, FR-006); a login ban already fails
/// authentication. A missing profile is no ban (the bearer check refuses such sessions).
/// </summary>
public sealed class BanCommandGuard(ProfileCache profiles, IClock clock) : ICommandGuard
{
    public async ValueTask<ErrorCode?> CheckAsync(ActorContext actor, CommandDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (actor.ActorType != ActorType.Player || actor.PlayerId is null
            || string.Equals(descriptor.Operation, RequestDeletionHandler.Operation, StringComparison.Ordinal))
        {
            return null;
        }

        var profile = await profiles.GetAsync(actor.PlayerId, cancellationToken);
        var group = descriptor.Operation.Split('.', 2)[0];
        return profile?.Ban is { } ban && ban.BlocksGroup(group, clock.UtcNow) ? CoreErrorCodes.AccountSuspended : null;
    }
}
