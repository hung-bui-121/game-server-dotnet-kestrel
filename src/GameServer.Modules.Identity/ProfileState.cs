using GameServer.Aggregates;

namespace GameServer.Modules.Identity;

/// <summary>
/// State of the mandatory <c>profile</c> aggregate (DAT-09): session epoch for single-session takeover (IDN-08),
/// ban (BAN-01), deletion request (PRIV-01) and linked identity mapping keys.
/// </summary>
public sealed record ProfileState
{
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Incremented by every login; sessions with a smaller epoch are replaced (IDN-08, IDN-10).</summary>
    public long SessionEpoch { get; init; }

    public BanInfo? Ban { get; init; }

    public DeletionInfo? Deleted { get; init; }

    /// <summary>Mapping keys linked to this player, e.g. <c>guest:{localId}</c>.</summary>
    public IReadOnlyList<string> Identities { get; init; } = [];
}

/// <summary>Ban of BAN-01. <see cref="Scope"/> holds <c>login</c> and/or command groups (operation prefixes).</summary>
public sealed record BanInfo
{
    public const string LoginScope = "login";

    public string Reason { get; init; } = string.Empty;

    /// <summary>End of the ban; null = permanent.</summary>
    public DateTimeOffset? UntilUtc { get; init; }

    public IReadOnlyList<string> Scope { get; init; } = [LoginScope];

    public string Actor { get; init; } = string.Empty;

    public bool IsActive(DateTimeOffset now) => UntilUtc is null || UntilUtc > now;

    public bool BlocksLogin(DateTimeOffset now) => IsActive(now) && Scope.Contains(LoginScope, StringComparer.Ordinal);

    /// <summary>A login ban also blocks every command; a group ban blocks only that group.</summary>
    public bool BlocksGroup(string group, DateTimeOffset now) =>
        IsActive(now) && (Scope.Contains(LoginScope, StringComparer.Ordinal) || Scope.Contains(group, StringComparer.Ordinal));
}

/// <summary>Account deletion request (PRIV-01, written in P0.5; checked from P0).</summary>
public sealed record DeletionInfo
{
    public DateTimeOffset RequestedAtUtc { get; init; }

    public DateTimeOffset PurgeAfterUtc { get; init; }
}

public static class IdentityAggregates
{
    public const string Profile = "profile";

    public static PlayerAggregateDescriptor<ProfileState> ProfileDescriptor { get; } =
        new(Profile, schemaVersion: 1, clientVisible: true, createDefault: () => new ProfileState());
}
