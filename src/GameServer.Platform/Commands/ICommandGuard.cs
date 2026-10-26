using GameServer.Actors;
using GameServer.Commands;
using GameServer.Errors;

namespace GameServer.Platform.Commands;

/// <summary>
/// Check run before every command inside the common pipeline (EXT-04), e.g. command-scope bans (BAN-01).
/// Returning an error code rejects the command before any read or write.
/// </summary>
public interface ICommandGuard
{
    ValueTask<ErrorCode?> CheckAsync(ActorContext actor, CommandDescriptor descriptor, CancellationToken cancellationToken);
}
