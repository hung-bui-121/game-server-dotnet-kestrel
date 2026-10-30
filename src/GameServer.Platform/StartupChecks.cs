namespace GameServer.Platform;

/// <summary>
/// Contributes startup violations (fail before traffic) and warnings (logged once) to the one startup report (FR-009).
/// Registered by store packages and the host; must not throw or do I/O beyond reading local files named by configuration.
/// </summary>
public interface IStartupCheck
{
    void Check(StartupCheckResult result);
}

/// <summary>What the startup checks found; errors fail the start, warnings are logged once (category <c>GameServer.Startup</c>).</summary>
public sealed class StartupCheckResult
{
    public List<string> Errors { get; } = [];

    public List<string> Warnings { get; } = [];
}
