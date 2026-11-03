using System.Diagnostics.CodeAnalysis;
using GameServer.Config;

namespace GameServer.Platform;

/// <summary>What <see cref="GameConfigStatus"/> knows about the game config of this start (FG-023).</summary>
public enum GameConfigState
{
    /// <summary>This mode does not load game config (provision), or the config phase has not run yet.</summary>
    NotLoaded,

    /// <summary>The config loaded; <see cref="GameConfigStatus.Config"/> is the instance handlers get.</summary>
    Loaded,

    /// <summary>The config did not load; its faults are already in the startup report.</summary>
    Failed,
}

/// <summary>
/// The game config as seen by startup code (FG-023, FR-032). Never throws. A startup check that reads config takes this
/// instead of <see cref="IGameConfig"/>: a check taking <see cref="IGameConfig"/> is safe, but it is reported as not run when the
/// config fails. Published once per start by the config phase of <c>UseGameServer</c>, before any startup check runs (FR-033);
/// provision mode never loads config, so it stays <see cref="GameConfigState.NotLoaded"/> there.
/// </summary>
public sealed class GameConfigStatus
{
    private static readonly Snapshot NotLoadedSnapshot = new(GameConfigState.NotLoaded, null, false);

    private volatile Snapshot _snapshot = NotLoadedSnapshot;
    private int _published;

    internal GameConfigStatus()
    {
    }

    public GameConfigState State => _snapshot.State;

    /// <summary>The <see cref="IGameConfig"/> instance handlers get; null unless <see cref="State"/> is Loaded.</summary>
    public IGameConfig? Config => _snapshot.Config;

    /// <summary>Loaded, and every required file, module config rule and catalog build of this start passed (warnings do not count).</summary>
    public bool DeclarationsPassed => _snapshot.DeclarationsPassed;

    /// <summary>The loaded config, read in one step (safe while the status is published).</summary>
    public bool TryGetConfig([NotNullWhen(true)] out IGameConfig? config)
    {
        config = _snapshot.Config;
        return config is not null;
    }

    /// <summary>The config phase found the config: <paramref name="declarationsPassed"/> when no required-file, rule or catalog line was reported.</summary>
    internal void PublishLoaded(IGameConfig config, bool declarationsPassed)
    {
        ArgumentNullException.ThrowIfNull(config);
        Publish(new Snapshot(GameConfigState.Loaded, config, declarationsPassed));
    }

    /// <summary>The config phase could not load the config.</summary>
    internal void PublishFailed() => Publish(new Snapshot(GameConfigState.Failed, null, false));

    private void Publish(Snapshot snapshot)
    {
        if (Interlocked.Exchange(ref _published, 1) != 0)
        {
            throw new InvalidOperationException("The game config status is published once per start, by the config phase of UseGameServer.");
        }

        _snapshot = snapshot;
    }

    private sealed record Snapshot(GameConfigState State, IGameConfig? Config, bool DeclarationsPassed);
}
