namespace GameServer.Platform;

/// <summary>
/// Thrown before serving traffic when the host configuration is invalid (ARC-06, ARC-07, SEC-08).
/// The message lists every violation with the module, the item and how to fix it.
/// </summary>
public sealed class GameServerStartupException : Exception
{
    public GameServerStartupException(IReadOnlyList<string> violations)
        : base(BuildMessage(violations))
    {
        Violations = violations;
    }

    public GameServerStartupException(string violation)
        : this([violation])
    {
    }

    public IReadOnlyList<string> Violations { get; }

    private static string BuildMessage(IReadOnlyList<string> violations) =>
        "Game server startup validation failed:" + Environment.NewLine +
        string.Join(Environment.NewLine, violations.Select(v => " - " + v));
}
