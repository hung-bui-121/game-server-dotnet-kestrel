using GameServer.Platform;

namespace GameServer.TestKit;

/// <summary>A <see cref="GameConfigStatus"/> for unit tests of a startup check, in the state the host would publish (FG-023).</summary>
public static class TestGameConfigStatus
{
    /// <summary>Provision mode, or before the config phase.</summary>
    public static GameConfigStatus NotLoaded() => new();

    /// <summary>The config did not load.</summary>
    public static GameConfigStatus Failed()
    {
        var status = new GameConfigStatus();
        status.PublishFailed();
        return status;
    }

    /// <summary>
    /// Loaded with the report's config: <see cref="GameConfigStatus.DeclarationsPassed"/> = <see cref="ConfigDeclarationReport.Passed"/>,
    /// catalogs = the catalogs the report built (<see cref="GameConfigStatus.TryGetCatalog{T}"/>). Exactly what the host publishes for
    /// that config and those modules.
    /// </summary>
    public static GameConfigStatus From(ConfigDeclarationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var status = new GameConfigStatus();
        status.PublishLoaded(report.Config, report.Passed, report.Catalogs);
        return status;
    }
}
