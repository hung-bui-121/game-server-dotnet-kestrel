namespace GameServer.Platform.Config;

/// <summary>
/// How a declared catalog type is provided in this host (research R6, R7): <see cref="Declared"/> = the platform builds it
/// from its declaration; <see cref="Provided"/> = the host or a test registered its own, which is resolved once in the phase.
/// </summary>
internal sealed class CatalogBinding
{
    private CatalogBinding(Func<object>? resolve) => Resolve = resolve;

    public static CatalogBinding Declared { get; } = new(null);

    /// <summary>Resolves the host's registration; null for <see cref="Declared"/>.</summary>
    public Func<object>? Resolve { get; }

    public static CatalogBinding Provided(Func<object> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        return new CatalogBinding(resolve);
    }
}
