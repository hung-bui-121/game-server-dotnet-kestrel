namespace GameServer.Platform.Web.Hosting;

/// <summary>
/// Registered by the kit's <c>LocalFlavorFactory</c> only (slice 015 T071 G3): a startup failure in <c>RunGameServerAsync</c>
/// (the <c>UseGameServer</c> report or a late fault such as a store that cannot open) is rethrown as the
/// <see cref="GameServerStartupException"/> instead of being printed on stderr with exit code 1, so a test sees the report.
/// A real host and a consumer's own <c>WebApplicationFactory</c> never register it.
/// </summary>
internal sealed class StartupFailureMode
{
    private StartupFailureMode()
    {
    }

    public static StartupFailureMode Throw { get; } = new();
}
