using GameServer.Actors;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Platform.Commands;
using GameServer.Time;

namespace GameServer.Modules.Identity;

/// <summary>
/// Command-scope bans (BAN-01): a ban whose scope lists the command group (operation prefix) rejects new executions of
/// that group without revoking the session. GM commands on a banned player are not blocked.
/// </summary>
public sealed class BanCommandGuard(ProfileCache profiles, IClock clock) : ICommandGuard
{
    public async ValueTask<ErrorCode?> CheckAsync(ActorContext actor, CommandDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (actor.ActorType != ActorType.Player || actor.PlayerId is null)
        {
            return null;
        }

        var profile = await profiles.GetAsync(actor.PlayerId, cancellationToken);
        var group = descriptor.Operation.Split('.', 2)[0];
        return profile.Ban is { } ban && ban.BlocksGroup(group, clock.UtcNow) ? CoreErrorCodes.AccountSuspended : null;
    }
}
