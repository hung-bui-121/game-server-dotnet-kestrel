namespace GameServer.Platform;

/// <summary>
/// Contributes startup violations (fail before traffic) and warnings (logged once) to the one startup report (FR-009).
/// Registered by store packages, modules and the host (<c>services.AddSingleton&lt;IStartupCheck, MyCheck&gt;()</c>).
/// </summary>
/// <remarks>
/// <para>
/// Each check is created from its registration and run in its own fault isolation, in serve and provision mode (FG-023): a
/// constructor or factory that throws adds one line naming the check and never hides the other checks. Checks run after the
/// config load, the module config rules and the catalog builds of the same start (FR-033). A check is created once per start;
/// it is not resolved as a container singleton, and it is never disposed: keep it stateless and not disposable.
/// </para>
/// <para>
/// A check must not throw and must not do I/O beyond reading local files named by configuration. To read the game config, take
/// <see cref="GameConfigStatus"/> and skip when it is not <see cref="GameConfigState.Loaded"/> (read declared catalogs only when
/// <see cref="GameConfigStatus.DeclarationsPassed"/>). A check taking <c>IGameConfig</c> or a catalog is safe, but it is reported
/// as "did not run" when the config fails, and provision mode (which never loads config) skips it with a warning.
/// </para>
/// </remarks>
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
