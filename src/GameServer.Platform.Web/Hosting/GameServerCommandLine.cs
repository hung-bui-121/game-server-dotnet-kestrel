namespace GameServer.Platform.Web.Hosting;

/// <summary>What the host binary does (contracts/operations-cli.md §1, slice 014 contracts/wiregen-cli.md §1).</summary>
public enum HostMode
{
    /// <summary>Validate, then serve traffic until stopped.</summary>
    Serve,

    /// <summary>Create the store schema and exit (<c>--provision</c>, DAT-07).</summary>
    Provision,

    /// <summary>Write the catalog export and exit (<c>--export-wire-contract &lt;path&gt;</c>); never serves or opens the store.</summary>
    ExportWireContract,
}

/// <summary>The host's own switches, split from the arguments given to <c>WebApplication.CreateBuilder</c>.</summary>
/// <param name="Mode">Serve, Provision when <see cref="ProvisionSwitch"/> was given, ExportWireContract for <see cref="ExportWireContractSwitch"/>.</param>
/// <param name="HostArgs">Every other argument, in order (ordinary command-line configuration).</param>
public sealed record GameServerCommandLine(HostMode Mode, string[] HostArgs)
{
    public const string ProvisionSwitch = "--provision";

    public const string ExportWireContractSwitch = "--export-wire-contract";

    /// <summary>
    /// Configuration key carrying the export path from <see cref="Parse"/> to <c>RunGameServerAsync(mode)</c> through
    /// <see cref="HostArgs"/>: the host passes only the mode, so the path travels with the host's own configuration.
    /// </summary>
    internal const string ExportPathKey = "GameServerCommandLine:ExportWireContract";

    private const string ProvisionName = "provision";

    private const string ExportName = "export-wire-contract";

    private const string ExportContract = "contracts/wiregen-cli.md §1";

    /// <summary>Target file of <see cref="HostMode.ExportWireContract"/>; null otherwise.</summary>
    public string? ExportPath { get; init; }

    /// <summary>
    /// Removes the switches (any letter case) from the arguments passed to <c>WebApplication.CreateBuilder</c>: the command-line
    /// configuration provider would read a bare <c>--provision</c> as a key and swallow the next argument as its value. The
    /// export switch takes the next argument as its path and both are removed. Throws <see cref="GameServerStartupException"/>
    /// (exit 1) for the forms the configuration provider would read as a key instead (<c>--provision=…</c>, <c>-provision</c>,
    /// <c>/provision</c>, <c>--export-wire-contract=…</c>, <c>-export-wire-contract</c>, <c>/export-wire-contract</c>): the host
    /// would otherwise serve traffic when the caller asked for another mode. Also refused: the export switch without a path
    /// (none, or the next argument starts with <c>-</c>), given twice, or combined with <c>--provision</c>.
    /// </summary>
    public static GameServerCommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var refused = new List<string>();
        var hostArgs = new List<string>(args.Length);
        var provision = false;
        var exportSwitches = 0;
        string? exportPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsSwitch(arg, ProvisionSwitch))
            {
                provision = true;
            }
            else if (IsSwitch(arg, ExportWireContractSwitch))
            {
                exportSwitches++;
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    exportPath = args[++i];
                }
                else
                {
                    refused.Add($"[export] {ExportWireContractSwitch} needs the output file as the next argument. Fix: " +
                        $"{ExportWireContractSwitch} <path> ({ExportContract}).");
                }
            }
            else
            {
                if (IsMisspelledSwitch(arg, ProvisionSwitch, ProvisionName))
                {
                    refused.Add($"[provision] '{Redacted(arg)}' is not the provisioning switch. Fix: write {ProvisionSwitch} alone, without a value " +
                        "(contracts/operations-cli.md §1).");
                }
                else if (IsMisspelledSwitch(arg, ExportWireContractSwitch, ExportName))
                {
                    refused.Add($"[export] '{Redacted(arg)}' is not the export switch. Fix: write {ExportWireContractSwitch} <path>, the path as the " +
                        $"next argument ({ExportContract}).");
                }

                hostArgs.Add(arg);
            }
        }

        if (exportSwitches > 1)
        {
            refused.Add($"[export] {ExportWireContractSwitch} is given {exportSwitches} times. Fix: give it once ({ExportContract}).");
        }

        if (provision && exportSwitches > 0)
        {
            refused.Add($"[export] {ExportWireContractSwitch} cannot be combined with {ProvisionSwitch}. Fix: run the host once per mode ({ExportContract}).");
        }

        if (refused.Count > 0)
        {
            throw new GameServerStartupException(refused);
        }

        if (exportPath is not null)
        {
            hostArgs.Add($"--{ExportPathKey}={exportPath}");
            return new GameServerCommandLine(HostMode.ExportWireContract, [.. hostArgs]) { ExportPath = exportPath };
        }

        return new GameServerCommandLine(provision ? HostMode.Provision : HostMode.Serve, [.. hostArgs]);
    }

    private static bool IsSwitch(string arg, string name) => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>A key-shaped argument (prefix <c>--</c>, <c>-</c> or <c>/</c>) named <paramref name="name"/> that is not the bare switch.</summary>
    private static bool IsMisspelledSwitch(string arg, string bareSwitch, string name)
    {
        if (IsSwitch(arg, bareSwitch))
        {
            return false;
        }

        var trimmed = arg.TrimStart('-', '/');
        if (trimmed.Length == arg.Length)
        {
            // No prefix: a value of the previous key, not a switch.
            return false;
        }

        var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
        return string.Equals(separator < 0 ? trimmed : trimmed[..separator], name, StringComparison.OrdinalIgnoreCase);
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
