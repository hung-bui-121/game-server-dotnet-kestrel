using System.Net;
using System.Text;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Web.Hosting;

public static class GameServerRunExtensions
{
    /// <summary>
    /// Runs the host in the given mode and returns the process exit code (<see cref="HostExitCodes"/>). Serve:
    /// <see cref="GameServerApplicationExtensions.UseGameServer"/> then <c>RunAsync</c>; a <see cref="GameServerStartupException"/>
    /// from validation or from a hosted service's start prints the violations on stderr and returns 1. Provision: see
    /// contracts/operations-cli.md §2. ExportWireContract: see <see cref="ExportAsync"/>. Never calls <c>Environment.Exit</c>
    /// (WebApplicationFactory runs the same entry point).
    /// </summary>
    public static Task<int> RunGameServerAsync(this WebApplication app, HostMode mode)
    {
        ArgumentNullException.ThrowIfNull(app);
        return mode switch
        {
            HostMode.Provision => ProvisionAsync(app),
            HostMode.ExportWireContract => ExportAsync(app, Console.Out, Console.Error),
            _ => ServeAsync(app, Console.Error),
        };
    }

    /// <summary>
    /// Export mode (FR-030, slice 014 contracts/wiregen-cli.md §1): reports the registry rules of the composition (deployment
    /// identity, modules; no game config) and the declaration faults of the <see cref="WireCatalog"/> (exit 1, no file), then writes <see cref="WireCatalog.ToExportJson"/> to the path given with
    /// <see cref="GameServerCommandLine.ExportWireContractSwitch"/> (UTF-8 without BOM, LF; its folder must exist; written to a temp file
    /// in that folder and moved over the path, so the path never holds a partial export) and disposes
    /// the app. Never calls <c>UseGameServer</c>, starts no hosted service and resolves no store, game config or secret.
    /// </summary>
    internal static async Task<int> ExportAsync(WebApplication app, TextWriter output, TextWriter error)
    {
        try
        {
            if (app.Configuration[GameServerCommandLine.ExportPathKey] is not { Length: > 0 } path)
            {
                await error.WriteLineAsync(new GameServerStartupException(
                    $"[export] no output file: export mode needs {GameServerCommandLine.ExportWireContractSwitch} <path> parsed by " +
                    "GameServerCommandLine.Parse(args). Fix: pass commandLine.HostArgs to WebApplication.CreateBuilder (contracts/wiregen-cli.md §1).").Message);
                return HostExitCodes.InvalidConfiguration;
            }

            // The composition the export describes must be valid itself (deployment identity, module rules), as for --provision.
            var errors = app.Services.GetRequiredService<ModuleRegistry>().Validate(app.Services.GetRequiredService<GameServerOptions>(), config: null).ToList();
            var catalog = app.Services.GetRequiredService<WireCatalog>();
            errors.AddRange(catalog.Validate());
            if (errors.Count > 0)
            {
                await error.WriteLineAsync(new GameServerStartupException(errors).Message);
                return HostExitCodes.InvalidConfiguration;
            }

            var file = Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(file);
            if (folder is null || !Directory.Exists(folder))
            {
                await error.WriteLineAsync(new GameServerStartupException(
                    $"[export] the folder of the export file '{path}' does not exist ({folder}). Fix: create the folder or pass another path " +
                    "(contracts/wiregen-cli.md §1).").Message);
                return HostExitCodes.InvalidConfiguration;
            }

            // Next to the target and moved over it: a reader (or a run cut short) sees the old file or the new one, never a part.
            var temp = ExportTempPath(folder, file);
            try
            {
                await File.WriteAllTextAsync(temp, catalog.ToExportJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                await error.WriteLineAsync(new GameServerStartupException(
                    $"[export] the export file '{path}' cannot be written: {ex.Message} Fix: pass a writable file path (contracts/wiregen-cli.md §1).").Message);
                return HostExitCodes.InvalidConfiguration;
            }

            await output.WriteLineAsync($"export: {catalog.Routes.Count} routes, {catalog.Sections.Count} sections written to {path}");
            return HostExitCodes.Ok;
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// The export's temp file in <paramref name="folder"/>, the folder of <paramref name="file"/>: <c>{file name}.{guid}.wire-contract.tmp</c>.
    /// A process killed between the write and the move leaves it behind; the name says what it is and that it may be deleted
    /// (wiregen-cli §1).
    /// </summary>
    internal static string ExportTempPath(string folder, string file) =>
        Path.Combine(folder, $"{Path.GetFileName(file)}.{Guid.NewGuid():N}.wire-contract.tmp");

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The write already failed and is reported; a leftover temp file next to the export must not replace that message.
        }
    }

    internal static async Task<int> ServeAsync(WebApplication app, TextWriter error)
    {
        try
        {
            app.UseGameServer();
        }
        catch (GameServerStartupException ex)
        {
            // Fail fast before serving traffic (ARC-06, ACC-26).
            await error.WriteLineAsync(ex.Message);
            return HostExitCodes.InvalidConfiguration;
        }

        RegisterListenWarning(app);
        try
        {
            await app.RunAsync();
        }
        catch (Exception ex) when (FindStartupException(ex) is { } startup)
        {
            // A store that cannot open (file store locked or malformed, Couchbase scope missing collections) stops the start.
            await error.WriteLineAsync(startup.Message);
            return HostExitCodes.InvalidConfiguration;
        }

        return HostExitCodes.Ok;
    }

    internal static GameServerStartupException? FindStartupException(Exception exception) => exception switch
    {
        GameServerStartupException startup => startup,
        AggregateException aggregate => aggregate.InnerExceptions.Select(FindStartupException).FirstOrDefault(found => found is not null),
        { InnerException: { } inner } => FindStartupException(inner),
        _ => null,
    };

    private static async Task<int> ProvisionAsync(WebApplication app)
    {
        using var interrupt = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            interrupt.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            return await ProvisioningRunner.RunAsync(app, Console.Out, Console.Error, interrupt.Token);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            await app.DisposeAsync();
        }
    }

    /// <summary>FR-038: development tools reachable from the network are worth one warning once the server listens.</summary>
    private static void RegisterListenWarning(WebApplication app)
    {
        var options = app.Services.GetRequiredService<GameServerOptions>();
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var addresses = app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            if (ListenAddresses.Warning(options, addresses) is { } warning)
            {
                app.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(GameServerApplicationExtensions.StartupLogCategory)
                    .LogWarning("{ListenWarning}", warning);
            }
        });
    }
}

/// <summary>Classifies listen addresses for the FR-038 warning (SEC-03).</summary>
internal static class ListenAddresses
{
    /// <summary>True for <c>localhost</c>, loopback IPs and Unix sockets; false for wildcards (<c>0.0.0.0</c>, <c>[::]</c>, <c>*</c>, <c>+</c>), other IPs and host names.</summary>
    public static bool IsLoopback(string address)
    {
        BindingAddress parsed;
        try
        {
            parsed = BindingAddress.Parse(address);
        }
        catch (FormatException)
        {
            return false;
        }

        if (parsed.IsUnixPipe)
        {
            return true;
        }

        return string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(parsed.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>The warning text when development tools are on and any address is reachable from the network; otherwise null.</summary>
    public static string? Warning(GameServerOptions options, IEnumerable<string> addresses)
    {
        if (!options.AllowsDevelopmentTools)
        {
            return null;
        }

        var exposed = addresses.Where(a => !IsLoopback(a)).ToArray();
        return exposed.Length == 0
            ? null
            : $"[deployment] the server listens on {string.Join(", ", exposed)} in '{options.DeploymentEnvironment}': development tools " +
              "(test authentication, cheats, the GM staff seed, the GM OpenAPI document) are reachable from the network. " +
              "Use a loopback address unless this is intended (FR-038, SEC-03).";
    }
}
