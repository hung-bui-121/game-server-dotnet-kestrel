using System.Globalization;
using System.Text;
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

    /// <summary>The player already has another identity of this provider (one per provider, IDN-04).</summary>
    public static readonly ErrorCode ProviderAlreadyLinked = new(14002, "PROVIDER_ALREADY_LINKED", 200, false,
        "Another account of this provider is already linked.");

    /// <summary>The provider rejected the credential: bad signature, expired, wrong app, single-use code reused (IDN-03).</summary>
    public static readonly ErrorCode IdentityProofInvalid = new(14003, "IDENTITY_PROOF_INVALID", 401, false,
        "The sign-in proof could not be verified.");

    /// <summary>The provider could not be reached or failed; nothing was written.</summary>
    public static readonly ErrorCode ProviderUnavailable = new(14004, "PROVIDER_UNAVAILABLE", 503, true,
        "The sign-in provider is unavailable. Retry later.");

    /// <summary>GM cancel of an account deletion for a player without a pending deletion (PRIV-01).</summary>
    public static readonly ErrorCode DeletionNotPending = new(14005, "DELETION_NOT_PENDING", 409, false,
        "The player has no pending account deletion.");

    /// <summary>GM cancel after the grace period ended or the purge started (PRIV-01, FR-011).</summary>
    public static readonly ErrorCode DeletionGraceOver = new(14006, "DELETION_GRACE_OVER", 409, false,
        "The grace period is over; the account deletion can no longer be cancelled.");

    public static ErrorCodeSet All { get; } = new(
        14000,
        14099,
        DisplayNameInvalid,
        DisplayNameRejected,
        ProviderAlreadyLinked,
        IdentityProofInvalid,
        ProviderUnavailable,
        DeletionNotPending,
        DeletionGraceOver);
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
/// command (no I/O, CMD-03). It receives the NFKC-folded name. The default accepts every name that passes
/// <see cref="DisplayNameRules"/>.
/// </summary>
public interface IDisplayNamePolicy
{
    bool IsAllowed(string displayName);
}

public sealed class AcceptAllDisplayNamePolicy : IDisplayNamePolicy
{
    public bool IsAllowed(string displayName) => true;
}

/// <summary>
/// Format of display names (PRF-01, PCR identity): 2–20 characters (graphemes) of letters, digits, single inner spaces
/// and <c>_ - .</c>. Names are stored NFC (one byte form for the same Vietnamese name); policies see the NFKC form so
/// fullwidth or compatibility look-alikes cannot bypass a word list. Invisible fillers, variation selectors and
/// stacked combining marks (Zalgo) are refused (review M3).
/// </summary>
public static partial class DisplayNameRules
{
    public const int MinLength = 2;
    public const int MaxLength = 20;

    /// <summary>A grapheme may carry a base character and up to 3 combining marks (Vietnamese needs at most 2).</summary>
    public const int MaxUnitsPerGrapheme = 4;

    /// <summary>Normalized form to store (NFC), or null when the input is not a valid display name.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null || input.Length > MaxLength * MaxUnitsPerGrapheme)
        {
            return null;
        }

        string name;
        try
        {
            name = input.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Invalid UTF-16 (lone surrogates).
            return null;
        }

        if (!Pattern().IsMatch(name) || Invisible().IsMatch(name))
        {
            return null;
        }

        var graphemes = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(name);
        while (enumerator.MoveNext())
        {
            if (((string)enumerator.Current).Length > MaxUnitsPerGrapheme)
            {
                return null;
            }

            graphemes++;
        }

        return graphemes is >= MinLength and <= MaxLength ? name : null;
    }

    public static bool IsValid(string? name) => Normalize(name) is not null;

    /// <summary>Compatibility-folded form given to <see cref="IDisplayNamePolicy"/> (e.g. fullwidth → ASCII).</summary>
    public static string ForPolicy(string normalized) => normalized.Normalize(NormalizationForm.FormKC);

    // Words start with a letter or digit (no leading mark); marks only follow a base character; single inner spaces.
    [GeneratedRegex(@"^[\p{L}\p{Nd}_.\-][\p{L}\p{Mn}\p{Nd}_.\-]*( [\p{L}\p{Nd}_.\-][\p{L}\p{Mn}\p{Nd}_.\-]*)*$")]
    private static partial Regex Pattern();

    // Hangul fillers, combining grapheme joiner, variation selectors, Mongolian free variation selectors, Khmer
    // inherent vowels: letters/marks that render as nothing.
    [GeneratedRegex(@"[\u115F\u1160\u3164\uFFA0\u034F\uFE00-\uFE0F\u180B-\u180D\u17B4\u17B5]")]
    private static partial Regex Invisible();
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
        var name = DisplayNameRules.Normalize(request.DisplayName);
        if (name is null)
        {
            return ValueTask.FromResult(CommandOutcome<SetDisplayNameResult>.Reject(IdentityErrors.DisplayNameInvalid));
        }

        if (!policy.IsAllowed(DisplayNameRules.ForPolicy(name)))
        {
            return ValueTask.FromResult(CommandOutcome<SetDisplayNameResult>.Reject(IdentityErrors.DisplayNameRejected));
        }

        var profile = context.Get<ProfileState>(IdentityAggregates.Profile);
        context.Set(IdentityAggregates.Profile, profile with { DisplayName = name });
        return ValueTask.FromResult(CommandOutcome<SetDisplayNameResult>.Ok(new SetDisplayNameResult(name)));
    }
}
