using GameServer.Platform.Registry;

namespace GameServer.Modules.Identity;

/// <summary>
/// The scopes a ban may name (slice 017, A9): <c>login</c> plus the command group (first segment of the operation) of every
/// registered command. One instance per host, built once from the module registry on first use (every module is registered by
/// then), used by the ban command's validation and by the option route <c>identity.ban_scopes</c>, so the list a console shows
/// and the values the server accepts cannot differ.
/// </summary>
internal sealed class BanScopes(ModuleRegistry registry)
{
    private readonly Lazy<(string[] Sorted, HashSet<string> Set)> _scopes = new(() => Build(registry));

    /// <summary>Every scope, ordinal order.</summary>
    public IReadOnlyList<string> Sorted => _scopes.Value.Sorted;

    public bool Contains(string scope) => _scopes.Value.Set.Contains(scope);

    private static (string[] Sorted, HashSet<string> Set) Build(ModuleRegistry registry)
    {
        var sorted = registry.Commands.Keys
            .Select(operation => operation.Split('.', 2)[0])
            .Append(BanInfo.LoginScope)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return (sorted, new HashSet<string>(sorted, StringComparer.Ordinal));
    }
}
