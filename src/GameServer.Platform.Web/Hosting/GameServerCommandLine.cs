namespace GameServer.Platform.Web.Hosting;

/// <summary>What the host binary does (contracts/operations-cli.md §1).</summary>
public enum HostMode
{
    /// <summary>Validate, then serve traffic until stopped.</summary>
    Serve,

    /// <summary>Create the store schema and exit (<c>--provision</c>, DAT-07).</summary>
    Provision,
}

/// <summary>The host's own switches, split from the arguments given to <c>WebApplication.CreateBuilder</c>.</summary>
/// <param name="Mode">Serve, or Provision when <see cref="ProvisionSwitch"/> was given.</param>
/// <param name="HostArgs">Every other argument, in order (ordinary command-line configuration).</param>
public sealed record GameServerCommandLine(HostMode Mode, string[] HostArgs)
{
    public const string ProvisionSwitch = "--provision";

    private const string SwitchName = "provision";

    /// <summary>
    /// Removes the switch (any letter case) from the arguments passed to <c>WebApplication.CreateBuilder</c>: the command-line
    /// configuration provider would read a bare <c>--provision</c> as a key and swallow the next argument as its value. Throws
    /// <see cref="GameServerStartupException"/> (exit 1) for the forms the configuration provider would read as a key instead
    /// (<c>--provision=…</c>, <c>-provision</c>, <c>/provision</c>, <c>/provision=…</c>): the host would otherwise serve traffic
    /// when the caller asked for provisioning.
    /// </summary>
    public static GameServerCommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var refused = args.Where(IsMisspelledSwitch).ToArray();
        if (refused.Length > 0)
        {
            throw new GameServerStartupException(refused
                .Select(a => $"[provision] '{Redacted(a)}' is not the provisioning switch. Fix: write {ProvisionSwitch} alone, without a value " +
                    "(contracts/operations-cli.md §1).")
                .ToList());
        }

        var hostArgs = args.Where(a => !IsSwitch(a)).ToArray();
        return new GameServerCommandLine(hostArgs.Length == args.Length ? HostMode.Serve : HostMode.Provision, hostArgs);
    }

    private static bool IsSwitch(string arg) => string.Equals(arg, ProvisionSwitch, StringComparison.OrdinalIgnoreCase);

    /// <summary>A key-shaped argument (prefix <c>--</c>, <c>-</c> or <c>/</c>) named <c>provision</c> that is not the bare switch.</summary>
    private static bool IsMisspelledSwitch(string arg)
    {
        if (IsSwitch(arg))
        {
            return false;
        }

        var name = arg.TrimStart('-', '/');
        if (name.Length == arg.Length)
        {
            // No prefix: a value of the previous key, not a switch.
            return false;
        }

        var separator = name.IndexOf('=', StringComparison.Ordinal);
        return string.Equals(separator < 0 ? name : name[..separator], SwitchName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The argument without its value: never echo what follows <c>=</c>.</summary>
    private static string Redacted(string arg)
    {
        var separator = arg.IndexOf('=', StringComparison.Ordinal);
        return separator < 0 ? arg : arg[..(separator + 1)] + "…";
    }
}

/// <summary>Process exit codes of the host binary (contracts/operations-cli.md §1); changing one is a breaking change (PCR-01).</summary>
public static class HostExitCodes
{
    public const int Ok = 0;

    /// <summary>Invalid configuration or a startup refusal before traffic.</summary>
    public const int InvalidConfiguration = 1;

    /// <summary>Provisioning failed or timed out; a re-run continues (idempotent).</summary>
    public const int ProvisioningFailed = 2;
}
