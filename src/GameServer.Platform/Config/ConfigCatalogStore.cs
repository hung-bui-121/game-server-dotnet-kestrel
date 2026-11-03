namespace GameServer.Platform.Config;

/// <summary>
/// The declared catalogs of this host (research R7, data-model §3.3), filled once by the config phase. A catalog that was not
/// built is unavailable with a marked exception, so dependents are reported as "not run" instead of adding a second line.
/// </summary>
internal sealed class ConfigCatalogStore
{
    private volatile State? _state;

    /// <summary>The phase ran but the config did not load: every declared catalog is unavailable for that reason.</summary>
    public void ConfigFailed() => Publish(new State(null));

    public void Publish(ConfigDeclarationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Publish(new State(result));
    }

    public object Get(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var state = _state ?? throw new InvalidOperationException(
            $"{type.Name} is a declared config catalog: the platform builds it in UseGameServer. Fix: start the host with " +
            "UseGameServer/RunGameServerAsync/TestHost, or in a test without a host build it with ConfigDeclarationCheck.Check(config, modules) " +
            "and call report.AddCatalogsTo(services) (FG-018).");
        if (state.Result is null)
        {
            throw Unavailable.Mark(
                new GameServerStartupException($"[catalog {type.Name}] was not built: the game config did not load (see the [config] lines)."),
                Unavailable.GameConfig);
        }

        if (state.Result.Catalogs.TryGetValue(type, out var catalog))
        {
            return catalog;
        }

        throw Unavailable.Mark(
            new GameServerStartupException(
                $"[catalog {type.Name}] was not built: its config files are missing or have errors (see the [module …] lines). " +
                "Fix: correct the lines reported for its files (FG-018)."),
            Unavailable.Catalog(type));
    }

    private void Publish(State state)
    {
        if (Interlocked.CompareExchange(ref _state, state, null) is not null)
        {
            throw new InvalidOperationException("The config phase publishes the declared catalogs once per host.");
        }
    }

    private sealed record State(ConfigDeclarationResult? Result);
}
