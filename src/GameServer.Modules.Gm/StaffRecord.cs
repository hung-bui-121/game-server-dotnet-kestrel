namespace GameServer.Modules.Gm;

/// <summary>Permissions of a staff member for one game and environment (SEC-07 scope).</summary>
public sealed record StaffGrant
{
    public string GameId { get; init; } = string.Empty;

    public string EnvironmentId { get; init; } = string.Empty;

    public IReadOnlyList<string> Permissions { get; init; } = [];
}

/// <summary>Identity of the staff member at the OpenID Connect provider (SEC-03); bound once, then authoritative.</summary>
public sealed record StaffOidcIdentity
{
    public string Issuer { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;
}

/// <summary>Lifecycle of a staff record (data model 007).</summary>
public static class StaffStatus
{
    /// <summary>Created with an email invitation; becomes <see cref="Active"/> at the first SSO sign-in.</summary>
    public const string Invited = "invited";

    public const string Active = "active";
}

/// <summary>Document <c>gm_staff/{staffId}</c>; written only inside transactions together with its audit entry.</summary>
public sealed record StaffRecord
{
    /// <summary>Constant discriminator so the staff list can use the one-field paged query (GM-06).</summary>
    public const string DocumentType = "staff";

    public string Type { get; init; } = DocumentType;

    public string StaffId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Lower-case invitation email; matched once at the first SSO sign-in (email must be verified).</summary>
    public string? Email { get; init; }

    public string Status { get; init; } = StaffStatus.Active;

    public bool Disabled { get; init; }

    /// <summary>Sessions carry the epoch they were created with; disabling or revoking sessions increments it (SEC-10).</summary>
    public long SessionEpoch { get; init; }

    public StaffOidcIdentity? Oidc { get; init; }

    /// <summary>PBKDF2 hash for Development/Test local auth only.</summary>
    public string? PasswordHash { get; init; }

    public IReadOnlyList<StaffGrant> Grants { get; init; } = [];

    public string? UpdatedAtUtc { get; init; }

    /// <summary>Can sign in: active (identity bound or local) and not disabled.</summary>
    public bool CanSignIn => !Disabled && Status == StaffStatus.Active;

    /// <summary>Permissions granted for exactly this game and environment; other scopes grant nothing.</summary>
    public IReadOnlySet<string> EffectivePermissions(string gameId, string environmentId) =>
        Grants
            .Where(g => string.Equals(g.GameId, gameId, StringComparison.Ordinal) && string.Equals(g.EnvironmentId, environmentId, StringComparison.Ordinal))
            .SelectMany(g => g.Permissions)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Replaces the grant of one game/environment, keeping the grants of other scopes.</summary>
    public StaffRecord WithGrant(string gameId, string environmentId, IReadOnlyList<string> permissions) => this with
    {
        Grants =
        [
            .. Grants.Where(g => !(string.Equals(g.GameId, gameId, StringComparison.Ordinal) && string.Equals(g.EnvironmentId, environmentId, StringComparison.Ordinal))),
            new StaffGrant { GameId = gameId, EnvironmentId = environmentId, Permissions = permissions },
        ],
    };
}
