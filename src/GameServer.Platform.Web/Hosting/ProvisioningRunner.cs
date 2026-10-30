using System.Globalization;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Hosting;

/// <summary>
/// Provision mode of the host binary (research R10, contracts/operations-cli.md §2): reduced validation (registry rules, secret
/// sources and <see cref="IStartupCheck"/>s; no GM SSO, IAP, pepper or master key), then <see cref="IStoreProvisioner"/> with a progress
/// report on stdout. Starts no hosted service and no web server.
/// </summary>
internal static class ProvisioningRunner
{
    /// <summary>Default of <c>Couchbase:ProvisioningTimeoutSeconds</c>; read here because Platform.Web cannot reference CouchbaseOptions.</summary>
    internal const int DefaultTimeoutSeconds = 600;

    private const string Prefix = "provision: ";

    public static async Task<int> RunAsync(WebApplication app, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var services = app.Services;
        var options = services.GetRequiredService<GameServerOptions>();
        var errors = services.GetRequiredService<ModuleRegistry>().Validate(options, config: null).ToList();

        // The provisioning credential is a secret as well: refused from JSON files outside Development/Test (OPS-08).
        DeploymentChecks.ValidateSecretSources(app.Configuration, options, errors);
        var checks = GameServerApplicationExtensions.CheckAll(services);
        errors.AddRange(checks.Errors);
        foreach (var warning in checks.Warnings)
        {
            await error.WriteLineAsync("warning: " + warning);
        }

        var provisioner = services.GetService<IStoreProvisioner>();
        if (errors.Count == 0 && provisioner is null)
        {
            errors.Add(
                $"[provision] {GameServerCommandLine.ProvisionSwitch} needs GameServer:Store=Couchbase; the store '{options.Store}' has no schema " +
                "to provision (D13). Fix: run it with the flavor of a Couchbase deployment.");
        }

        if (errors.Count > 0)
        {
            await error.WriteLineAsync(new GameServerStartupException(errors).Message);
            return HostExitCodes.InvalidConfiguration;
        }

        var bucket = app.Configuration["Couchbase:BucketName"];
        await output.WriteLineAsync(
            $"{Prefix}game {options.GameId}, environment {options.EnvironmentId} ({options.DeploymentEnvironment}), bucket {bucket}, scope g_{options.GameId}");

        var seconds = TimeoutSeconds(app.Configuration["Couchbase:ProvisioningTimeoutSeconds"]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var report = new ProgressReport(output);
        try
        {
            await provisioner!.ProvisionAsync(report, timeout.Token);
        }
        catch (ProvisioningFailedException ex)
        {
            report.Failed($"{ex.Kind} {ex.Name}: {ex.Cause}");
            return HostExitCodes.ProvisioningFailed;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            report.Failed(cancellationToken.IsCancellationRequested
                ? "interrupted (Ctrl+C)"
                : $"timed out after {seconds} s (Couchbase:ProvisioningTimeoutSeconds)");
            return HostExitCodes.ProvisioningFailed;
        }

        await output.WriteLineAsync($"{Prefix}done: {report.Created} created, {report.Present} present");
        return HostExitCodes.Ok;
    }

    private static int TimeoutSeconds(string? configured) =>
        int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : DefaultTimeoutSeconds;

    /// <summary>Writes one line per item as it is reported (synchronously: <c>Progress&lt;T&gt;</c> would post to a thread pool).</summary>
    private sealed class ProgressReport(TextWriter output) : IProgress<ProvisioningItem>
    {
        private readonly Lock _lock = new();

        public int Created { get; private set; }

        public int Present { get; private set; }

        public void Report(ProvisioningItem value)
        {
            lock (_lock)
            {
                if (value.Outcome == ProvisioningOutcome.Created)
                {
                    Created++;
                }
                else
                {
                    Present++;
                }

                output.WriteLine(Prefix + Format(value));
            }
        }

        public void Failed(string cause)
        {
            lock (_lock)
            {
                output.WriteLine($"{Prefix}FAILED {cause}");
                output.WriteLine($"{Prefix}stopped after {Created + Present} items; fix the cause and run again (idempotent)");
            }
        }

        private static string Format(ProvisioningItem item)
        {
            if (item.Outcome == ProvisioningOutcome.AlreadyPresent)
            {
                return $"{item.Kind} {item.Name} present";
            }

            return item.Kind == "index"
                ? $"{item.Kind} {item.Name} created (online after {(int)item.Elapsed.TotalSeconds} s)"
                : $"{item.Kind} {item.Name} created";
        }
    }
}
