using GameServer.Config;

namespace GameServer.Platform.Web.Config;

/// <summary>
/// What the config phase produced for the readers after it, published once when the config loaded: the <c>GetConfig</c> bundle
/// for the endpoint and the counts of the startup config line (data-model §5.6). A host whose config did not load never starts,
/// so a reader that finds nothing published is a composition fault, reported as such.
/// </summary>
internal sealed class ConfigPhaseSummary
{
    private volatile Values? _values;

    /// <summary>The published values; throws when the config phase has not published (it runs in <c>UseGameServer</c>).</summary>
    public Values Current => _values ?? throw new InvalidOperationException(
        "The config phase has not run: the GetConfig bundle and the config counts are built in UseGameServer. " +
        "Fix: start the host with UseGameServer/RunGameServerAsync/TestHost (FG-019).");

    /// <summary>The bundle served by <c>GetConfig</c>; throws when the player API is not mapped (no bundle is built then).</summary>
    public ConfigBundle Bundle => Current.Bundle ?? throw new InvalidOperationException(
        "No GetConfig bundle was built: the player API is not mapped (GameServer:Hosting:MapPlayerApi=false).");

    /// <param name="bundle">Null when the player API is not mapped ("bundle not served").</param>
    public void Publish(IGameConfig config, int ruleCount, int catalogCount, ConfigBundle? bundle)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (Interlocked.CompareExchange(ref _values, new Values(config, ruleCount, catalogCount, bundle), null) is not null)
        {
            throw new InvalidOperationException("The config phase publishes its summary once per host.");
        }
    }

    /// <param name="Config">The loaded config (the instance <c>IGameConfig</c> resolves to).</param>
    /// <param name="RuleCount">Rules run for the files present.</param>
    /// <param name="CatalogCount">Declared catalog types.</param>
    /// <param name="Bundle">The served bundle; null when the player API is not mapped.</param>
    internal sealed record Values(IGameConfig Config, int RuleCount, int CatalogCount, ConfigBundle? Bundle);
}
