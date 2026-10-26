namespace GameServer.Modules.Gm;

/// <summary>Permissions of a staff member for one game and environment (SEC-07 scope).</summary>
public sealed record StaffGrant
{
    public string GameId { get; init; } = string.Empty;

    public string EnvironmentId { get; init; } = string.Empty;

    public IReadOnlyList<string> Permissions { get; init; } = [];
}

/// <summary>Document <c>gm_staff/{staffId}</c>.</summary>
public sealed record StaffRecord
{
    public string StaffId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool Disabled { get; init; }

    /// <summary>PBKDF2 hash for Development local auth only; null for SSO staff (P0.5).</summary>
    public string? PasswordHash { get; init; }

    public IReadOnlyList<StaffGrant> Grants { get; init; } = [];

    /// <summary>Permissions granted for exactly this game and environment; other scopes grant nothing.</summary>
    public IReadOnlySet<string> EffectivePermissions(string gameId, string environmentId) =>
        Grants
            .Where(g => string.Equals(g.GameId, gameId, StringComparison.Ordinal) && string.Equals(g.EnvironmentId, environmentId, StringComparison.Ordinal))
            .SelectMany(g => g.Permissions)
            .ToHashSet(StringComparer.Ordinal);
}
