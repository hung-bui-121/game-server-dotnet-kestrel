using GameServer.Actors;
using GameServer.Commands;
using GameServer.Errors;

namespace GameServer.Modules.Identity;

/// <summary>
/// Proof of a provider identity sent by the client (IDN-03). Never trusted as is: an
/// <see cref="IIdentityProviderAdapter"/> verifies it with the provider and returns the identity.
/// </summary>
public abstract record IdentityCredential
{
    /// <summary>Provider name, also the mapping key prefix (IDN-04), e.g. <c>pgs</c>, <c>gamecenter</c>.</summary>
    public abstract string Provider { get; }
}

/// <summary>Google Play Games Services v2 server auth code (single use).</summary>
public sealed record PlayGamesCredential(string ServerAuthCode) : IdentityCredential
{
    public const string ProviderName = "pgs";

    public override string Provider => ProviderName;
}

/// <summary>
/// Apple Game Center <c>fetchItems(forIdentityVerificationSignature:)</c> output plus the ids and bundle the signature
/// covers. <see cref="Timestamp"/> is milliseconds since the Unix epoch, as returned by GameKit.
/// </summary>
public sealed record GameCenterCredential(
    string PublicKeyUrl,
    byte[] Signature,
    byte[] Salt,
    ulong Timestamp,
    string TeamPlayerId,
    string GamePlayerId,
    string BundleId) : IdentityCredential
{
    public const string ProviderName = "gamecenter";

    public override string Provider => ProviderName;
}

/// <summary>Credential of a provider added by the game (EXT-01); <see cref="Data"/> is opaque to the framework.</summary>
public sealed record CustomCredential(string ProviderId, byte[] Data) : IdentityCredential
{
    public override string Provider => ProviderId;
}

/// <summary>An identity the provider vouched for. <see cref="Attributes"/> are stored in the mapping (IDN-04).</summary>
public sealed record VerifiedIdentity(string Provider, string Subject, IReadOnlyDictionary<string, string> Attributes)
{
    public string MappingKey => ProviderIdentities.MappingKey(Provider, Subject);
}

/// <summary>
/// Verifies a credential with its provider (IDN-03, EXT-01). Runs before any transaction (outbound I/O is never inside
/// a command, CMD-03). Throw <see cref="GameServerException"/> with <see cref="IdentityErrors.IdentityProofInvalid"/>
/// for a credential the provider rejects and <see cref="IdentityErrors.ProviderUnavailable"/> when the provider cannot
/// answer. Register one adapter per provider name; duplicates fail startup.
/// </summary>
public interface IIdentityProviderAdapter
{
    string Provider { get; }

    ValueTask<VerifiedIdentity> VerifyAsync(IdentityCredential credential, CancellationToken cancellationToken);
}

/// <summary>Mapping key rules for provider identities (IDN-04).</summary>
public static class ProviderIdentities
{
    public const int MaxSubjectLength = 128;

    /// <summary>Provider names: lower-case letters/digits, 2–32, not <c>guest</c> (reserved for IDN-02).</summary>
    public static bool IsValidProvider(string? provider) =>
        provider is { Length: >= 2 and <= 32 }
        && provider != "guest"
        && provider.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
        && provider[0] is >= 'a' and <= 'z';

    /// <summary>Subjects: 1–128 printable ASCII characters without spaces (Couchbase keys are ≤ 250 bytes).</summary>
    public static bool IsValidSubject(string? subject) =>
        subject is { Length: >= 1 and <= MaxSubjectLength } && subject.All(c => c is > ' ' and < '\u007f');

    public static string MappingKey(string provider, string subject) =>
        IsValidProvider(provider) && IsValidSubject(subject)
            ? $"{provider}:{subject}"
            : throw new GameServerException(IdentityErrors.IdentityProofInvalid, $"Provider '{provider}' returned an unusable subject.");

    /// <summary>Provider of a mapping key in <see cref="ProfileState.Identities"/>.</summary>
    public static string ProviderOf(string mappingKey)
    {
        var colon = mappingKey.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? mappingKey : mappingKey[..colon];
    }
}

public sealed record LinkIdentityCommand(VerifiedIdentity Identity);

public sealed record LinkIdentityResult(string Provider, bool AlreadyLinked);

/// <summary>
/// <c>identity.link</c> (IDN-05, IDN-06): links a verified provider identity to the calling player in one transaction
/// {insert mapping, <c>profile.identities</c>, receipt}. The mapping is an insert-only claim, so of two players linking
/// the same identity on two instances only one commits; the other gets 409 <c>IDENTITY_CONFLICT</c>. No account is
/// moved or merged. A player holds at most one identity per provider.
/// </summary>
public sealed class LinkIdentityHandler : ICommandHandler<LinkIdentityCommand, LinkIdentityResult>
{
    public const string Operation = "identity.link";

    public static CommandDescriptor Descriptor { get; } = new(
        Operation,
        touches: [IdentityAggregates.Profile],
        maxClaimMarkers: 1,
        profile: SecurityProfile.Player);

    public async ValueTask<CommandOutcome<LinkIdentityResult>> HandleAsync(LinkIdentityCommand request, ICommandContext context)
    {
        var identity = request.Identity;
        var key = identity.MappingKey;
        var profile = context.Get<ProfileState>(IdentityAggregates.Profile);
        if (profile.Identities.Contains(key, StringComparer.Ordinal))
        {
            return CommandOutcome<LinkIdentityResult>.Ok(new LinkIdentityResult(identity.Provider, AlreadyLinked: true));
        }

        if (profile.Identities.Any(existing => ProviderIdentities.ProviderOf(existing) == identity.Provider))
        {
            return CommandOutcome<LinkIdentityResult>.Reject(IdentityErrors.ProviderAlreadyLinked);
        }

        if (!await context.TryClaimAsync(IdentityCollections.Identities, key, identity.Attributes))
        {
            // Mapped to another player: the client may offer to log in to that account instead (TBD-04b).
            return CommandOutcome<LinkIdentityResult>.Reject(
                CoreErrorCodes.IdentityConflict,
                new Dictionary<string, string> { ["provider"] = identity.Provider });
        }

        context.Set(IdentityAggregates.Profile, profile with { Identities = [.. profile.Identities, key] });
        return CommandOutcome<LinkIdentityResult>.Ok(new LinkIdentityResult(identity.Provider, AlreadyLinked: false));
    }
}
