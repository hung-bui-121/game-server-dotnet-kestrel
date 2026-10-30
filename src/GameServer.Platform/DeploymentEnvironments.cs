namespace GameServer.Platform;

/// <summary>The deployment environments the framework knows (OPS-01); <c>GameServer:DeploymentEnvironment</c> must be one of them.</summary>
public static class DeploymentEnvironments
{
    public const string Development = "Development";
    public const string Staging = "Staging";
    public const string Production = "Production";
    public const string Test = "Test";

    public static IReadOnlyList<string> All { get; } = [Development, Staging, Production, Test];

    /// <summary>Case-insensitive match against <see cref="All"/>; returns the canonical spelling.</summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
            {
                normalized = candidate;
                return true;
            }
        }

        normalized = string.Empty;
        return false;
    }
}

/// <summary>The five standard flavors (FR-002); games may add others with an explicit deployment environment.</summary>
public static class StandardFlavors
{
    /// <summary>local→Development, dev→Development, beta→Staging, uat→Staging, live→Production (keys = EnvironmentId, ordinal).</summary>
    public static IReadOnlyDictionary<string, string> DeploymentEnvironmentById { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["local"] = DeploymentEnvironments.Development,
        ["dev"] = DeploymentEnvironments.Development,
        ["beta"] = DeploymentEnvironments.Staging,
        ["uat"] = DeploymentEnvironments.Staging,
        ["live"] = DeploymentEnvironments.Production,
    };

    /// <summary>"local->Development, dev->Development, beta->Staging, uat->Staging, live->Production" for messages (ASCII for any console).</summary>
    public static string Describe() => string.Join(", ", DeploymentEnvironmentById.Select(f => $"{f.Key}->{f.Value}"));
}
