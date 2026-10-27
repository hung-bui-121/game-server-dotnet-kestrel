using System.Globalization;
using System.Text.RegularExpressions;
using GameServer.Actors;
using GameServer.Commands;
using GameServer.Errors;

namespace GameServer.Modules.Identity;

/// <summary>GM permissions owned by the Identity module (SEC-07 catalog).</summary>
public static class IdentityPermissions
{
    /// <summary>Ban, suspend and unban players (GM-07, BAN-01); also cancels account deletion (PRIV-01, slice 010).</summary>
    public const string PlayerBan = "player.ban";
}

/// <summary>Error codes of the Identity module (range 14000–14099, API-11). Business codes: HTTP 200 + code.</summary>
public static class IdentityErrors
{
    public static readonly ErrorCode DisplayNameInvalid = new(14000, "DISPLAY_NAME_INVALID", 200, false,
        "The display name must be 2-20 letters, digits, single spaces, '_', '-' or '.'.");

    public static readonly ErrorCode DisplayNameRejected = new(14001, "DISPLAY_NAME_REJECTED", 200, false,
        "The display name is not allowed.");

    public static ErrorCodeSet All { get; } = new(14000, 14099, DisplayNameInvalid, DisplayNameRejected);
}

public sealed record BanPlayerCommand(string Reason, DateTimeOffset? UntilUtc, IReadOnlyList<string> Scope);

public sealed record UnbanPlayerCommand(string Reason);

public sealed record BanResult(BanInfo? Before, BanInfo? After);

/// <summary>
/// <c>identity.ban</c> (GM-07, BAN-01): sets <c>profile.ban</c>. A ban with scope <c>login</c> also increments the
/// session epoch, so existing sessions end within the cache window (IDN-08) and stay dead after an unban.
/// Audit records the ban before and after (SEC-14).
/// </summary>
public sealed class BanPlayerHandler : ICommandHandler<BanPlayerCommand, BanResult>
{
    public const string Operation = "identity.ban";

    public static CommandDescriptor Descriptor { get; } = new(
        Operation,
        touches: [IdentityAggregates.Profile],
        profile: SecurityProfile.Gm,
        permission: IdentityPermissions.PlayerBan);

    public ValueTask<CommandOutcome<BanResult>> HandleAsync(BanPlayerCommand request, ICommandContext context)
    {
        if (request.UntilUtc is { } until && until <= context.Now)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, "untilUtc must be in the future (omit it for a permanent ban).");
        }

        var profile = context.Get<ProfileState>(IdentityAggregates.Profile);
        var ban = new BanInfo
        {
            Reason = request.Reason,
            UntilUtc = request.UntilUtc,
            Scope = request.Scope,
            Actor = context.Actor.ActorId,
        };
        var blocksLogin = request.Scope.Contains(BanInfo.LoginScope, StringComparer.Ordinal);
        context.Set(IdentityAggregates.Profile, profile with
        {
            Ban = ban,
            SessionEpoch = blocksLogin ? profile.SessionEpoch + 1 : profile.SessionEpoch,
        });
        context.AddAudit(IdentityPermissions.PlayerBan, Operation, request.Reason, profile.Ban, ban);
        return ValueTask.FromResult(CommandOutcome<BanResult>.Ok(new BanResult(profile.Ban, ban)));
    }
}

/// <summary><c>identity.unban</c> (GM-07): clears the ban; sessions revoked by the ban stay revoked (log in again).</summary>
public sealed class UnbanPlayerHandler : ICommandHandler<UnbanPlayerCommand, BanResult>
{
    public const string Operation = "identity.unban";

    public static CommandDescriptor Descriptor { get; } = new(
        Operation,
        touches: [IdentityAggregates.Profile],
        profile: SecurityProfile.Gm,
        permission: IdentityPermissions.PlayerBan);

    public ValueTask<CommandOutcome<BanResult>> HandleAsync(UnbanPlayerCommand request, ICommandContext context)
    {
        var profile = context.Get<ProfileState>(IdentityAggregates.Profile);
        context.Set(IdentityAggregates.Profile, profile with { Ban = null });
        context.AddAudit(IdentityPermissions.PlayerBan, Operation, request.Reason, profile.Ban, null);
        return ValueTask.FromResult(CommandOutcome<BanResult>.Ok(new BanResult(profile.Ban, null)));
    }
}

/// <summary>
/// Game policy for display names (PRF-01, EXT-02): e.g. a profanity filter. Pure and synchronous — it runs inside the
/// command (no I/O, CMD-03). The default accepts every name that passes <see cref="DisplayNameRules"/>.
/// </summary>
public interface IDisplayNamePolicy
{
    bool IsAllowed(string displayName);
}

public sealed class AcceptAllDisplayNamePolicy : IDisplayNamePolicy
{
    public bool IsAllowed(string displayName) => true;
}

/// <summary>Format of display names (PRF-01, PCR identity): 2–20 characters, letters/digits/single spaces/<c>_ - .</c>.</summary>
public static partial class DisplayNameRules
{
    public const int MinLength = 2;
    public const int MaxLength = 20;

    public static bool IsValid(string? name)
    {
        if (name is null || name.Length > MaxLength * 4)
        {
            return false;
        }

        var length = new StringInfo(name).LengthInTextElements;
        return length is >= MinLength and <= MaxLength && Pattern().IsMatch(name);
    }

    // Unicode letters (incl. combining marks for Vietnamese) and decimal digits, single inner spaces only.
    [GeneratedRegex(@"^[\p{L}\p{Mn}\p{Nd}_.\-]+( [\p{L}\p{Mn}\p{Nd}_.\-]+)*$")]
    private static partial Regex Pattern();
}

public sealed record SetDisplayNameCommand(string DisplayName);

public sealed record SetDisplayNameResult(string DisplayName);

/// <summary><c>identity.set_display_name</c> (PRF-01): the player sets their display name; not unique.</summary>
public sealed class SetDisplayNameHandler(IDisplayNamePolicy policy) : ICommandHandler<SetDisplayNameCommand, SetDisplayNameResult>
{
    public const string Operation = "identity.set_display_name";

    public static CommandDescriptor Descriptor { get; } = new(
        Operation,
        touches: [IdentityAggregates.Profile],
        profile: SecurityProfile.Player);

    public ValueTask<CommandOutcome<SetDisplayNameResult>> HandleAsync(SetDisplayNameCommand request, ICommandContext context)
    {
        var name = request.DisplayName;
        if (!DisplayNameRules.IsValid(name))
        {
            return ValueTask.FromResult(CommandOutcome<SetDisplayNameResult>.Reject(IdentityErrors.DisplayNameInvalid));
        }

        if (!policy.IsAllowed(name))
        {
            return ValueTask.FromResult(CommandOutcome<SetDisplayNameResult>.Reject(IdentityErrors.DisplayNameRejected));
        }

        var profile = context.Get<ProfileState>(IdentityAggregates.Profile);
        context.Set(IdentityAggregates.Profile, profile with { DisplayName = name });
        return ValueTask.FromResult(CommandOutcome<SetDisplayNameResult>.Ok(new SetDisplayNameResult(name)));
    }
}
