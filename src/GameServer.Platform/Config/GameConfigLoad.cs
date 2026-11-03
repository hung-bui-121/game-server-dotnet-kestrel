using GameServer.Config;

namespace GameServer.Platform.Config;

/// <summary>Whether this host loads the game config (serve) or never touches it (provision, D4).</summary>
internal enum GameConfigLoadMode
{
    Serve,
    NotLoaded,
}

/// <summary>
/// The host's one config load (research R8): the loader runs once per host, thread-safe, and keeps either the config or
/// its violations. Every failed resolve throws a new <see cref="GameServerStartupException"/> with the same violations,
/// marked <see cref="Unavailable.GameConfig"/>, so a dependent check or service is reported once as "not run".
/// </summary>
internal sealed class GameConfigLoad
{
    private const string NotLoadedLine =
        "[config] the game config is not loaded in provision mode. Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023).";

    private readonly Lazy<LoadResult> _result;

    public GameConfigLoad(string configRoot, string deploymentEnvironment)
        : this(() => FileGameConfigLoader.Load(configRoot, deploymentEnvironment))
    {
    }

    internal GameConfigLoad(Func<GameConfig> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        _result = new Lazy<LoadResult>(() => Run(load), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Set to <see cref="GameConfigLoadMode.NotLoaded"/> by provision mode before anything resolves the config.</summary>
    public GameConfigLoadMode Mode { get; set; } = GameConfigLoadMode.Serve;

    /// <summary>The loaded config; throws a marked <see cref="GameServerStartupException"/> when it failed or is not loaded in this mode.</summary>
    public IGameConfig GetOrThrow()
    {
        if (Mode == GameConfigLoadMode.NotLoaded)
        {
            throw Unavailable.Mark(new GameServerStartupException(NotLoadedLine), Unavailable.Provision);
        }

        var result = _result.Value;
        return result.Config ?? throw Unavailable.Mark(new GameServerStartupException(result.Violations), Unavailable.GameConfig);
    }

    private static LoadResult Run(Func<GameConfig> load)
    {
        try
        {
            return new LoadResult(load(), []);
        }
        catch (GameServerStartupException ex)
        {
            return new LoadResult(null, ex.Violations);
        }
    }

    private sealed record LoadResult(GameConfig? Config, IReadOnlyList<string> Violations);
}
