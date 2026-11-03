namespace GameServer.Platform.Config;

/// <summary>
/// Marker on the exceptions of things that are unavailable because an earlier startup step already reported why
/// (data-model §3.4): <c>Exception.Data["GameServer.Unavailable"]</c>. Set by <see cref="GameConfigLoad"/> and the catalog
/// store; read by the startup checks and the service validation, anywhere in the <c>InnerException</c> chain.
/// </summary>
internal static class Unavailable
{
    public const string DataKey = "GameServer.Unavailable";

    /// <summary>The game config did not load (its <c>[config]</c> lines are in the report).</summary>
    public const string GameConfig = "game config";

    /// <summary>Provision mode does not load the game config.</summary>
    public const string Provision = "provision";

    private const string CatalogPrefix = "catalog ";

    /// <summary>A declared catalog was not built (its lines are in the report).</summary>
    public static string Catalog(Type catalogType) => CatalogPrefix + catalogType.Name;

    /// <summary>The catalog type name of a <see cref="Catalog"/> marker; null for the other markers.</summary>
    public static string? CatalogTypeName(string marker) =>
        marker.StartsWith(CatalogPrefix, StringComparison.Ordinal) ? marker[CatalogPrefix.Length..] : null;

    public static TException Mark<TException>(TException exception, string value)
        where TException : Exception
    {
        exception.Data[DataKey] = value;
        return exception;
    }

    /// <summary>The marker of <paramref name="exception"/> or of one of its inner exceptions; null when none carries one.</summary>
    public static string? Find(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data[DataKey] is string value)
            {
                return value;
            }
        }

        return null;
    }
}
