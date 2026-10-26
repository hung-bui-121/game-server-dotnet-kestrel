namespace GameServer.Modules.Gm;

/// <summary>
/// Role templates (SEC-15): a role is only a seed. Its permissions are expanded into the grant when the staff is
/// created; modules check permissions, never role names.
/// </summary>
public static class GmRoles
{
    public const string Support = "Support";
    public const string Operator = "Operator";
    public const string EconomyOperator = "EconomyOperator";
    public const string Administrator = "Administrator";

    /// <summary>Permission names used by the templates that other modules own.</summary>
    public const string ResourceAdjust = "resource.adjust";
    public const string ProgressionEdit = "progression.edit";

    public static IReadOnlyList<string> Names { get; } = [Support, Operator, EconomyOperator, Administrator];

    /// <summary>Expands a role into permissions; <see cref="Administrator"/> gets the whole catalog of the deployment.</summary>
    public static IReadOnlyList<string> Expand(string role, IReadOnlyCollection<string> catalog)
    {
        IEnumerable<string> permissions = role switch
        {
            Support => [GmPermissions.Access, GmPermissions.PlayerRead],
            Operator => [GmPermissions.Access, GmPermissions.PlayerRead, ProgressionEdit],
            EconomyOperator => [GmPermissions.Access, GmPermissions.PlayerRead, ResourceAdjust],
            Administrator => catalog,
            _ => throw new ArgumentException($"Unknown GM role '{role}'. Known: {string.Join(", ", Names)}.", nameof(role)),
        };

        // A template never grants a permission the deployment does not have (module not registered).
        return permissions.Where(catalog.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
