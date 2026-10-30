using System.Net;
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
    /// contracts/operations-cli.md §2. Never calls <c>Environment.Exit</c> (WebApplicationFactory runs the same entry point).
    /// </summary>
    public static Task<int> RunGameServerAsync(this WebApplication app, HostMode mode)
    {
        ArgumentNullException.ThrowIfNull(app);
        return mode == HostMode.Provision ? ProvisionAsync(app) : ServeAsync(app, Console.Error);
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
