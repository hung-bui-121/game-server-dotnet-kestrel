using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

namespace GameServer.Platform.Web.Hosting;

/// <summary>Deployment-level settings applied by <c>UseGameServer</c>: reverse proxy (OPS-02) and secret sources (OPS-08).</summary>
public static class DeploymentChecks
{
    /// <summary>Shutdown settings (OPS-03): positive timeout, drain delay inside it.</summary>
    public static void ValidateShutdown(GameServerOptions options, List<string> errors)
    {
        var hosting = options.Hosting;
        if (hosting.ShutdownTimeoutSeconds < 1 || hosting.DrainDelaySeconds < 0 || hosting.DrainDelaySeconds >= hosting.ShutdownTimeoutSeconds)
        {
            errors.Add(
                $"[deployment] GameServer:Hosting:ShutdownTimeoutSeconds ({hosting.ShutdownTimeoutSeconds}) must be >= 1 and greater than " +
                $"DrainDelaySeconds ({hosting.DrainDelaySeconds}, >= 0) (OPS-03).");
        }
    }

    public static ForwardedHeadersOptions? BuildForwardedHeaders(GameServerOptions options, List<string> errors)
    {
        var settings = options.Hosting.ForwardedHeaders;
        if (!settings.Enabled)
        {
            return null;
        }

        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = settings.ForwardLimit,
        };

        // Only the configured proxies are trusted; the loopback defaults are removed.
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var proxy in settings.KnownProxies)
        {
            if (IPAddress.TryParse(proxy, out var address))
            {
                forwarded.KnownProxies.Add(address);
            }
            else
            {
                errors.Add($"[deployment] GameServer:Hosting:ForwardedHeaders:KnownProxies has an invalid IP '{proxy}'.");
            }
        }

        foreach (var network in settings.KnownNetworks)
        {
            if (System.Net.IPNetwork.TryParse(network, out var parsed) && parsed.PrefixLength == 0)
            {
                errors.Add($"[deployment] GameServer:Hosting:ForwardedHeaders:KnownNetworks '{network}' trusts every client. Fix: list the proxy subnets.");
            }
            else if (System.Net.IPNetwork.TryParse(network, out parsed))
            {
                forwarded.KnownIPNetworks.Add(parsed);
            }
            else
            {
                errors.Add($"[deployment] GameServer:Hosting:ForwardedHeaders:KnownNetworks has an invalid CIDR '{network}'.");
            }
        }

        if (settings.KnownProxies.Length == 0 && settings.KnownNetworks.Length == 0)
        {
            errors.Add(
                "[deployment] ForwardedHeaders is enabled without KnownProxies/KnownNetworks. " +
                "Fix: list the reverse proxies, otherwise any client can spoof its IP for rate limits and audit (OPS-02).");
        }

        if (settings.ForwardLimit < 1)
        {
            errors.Add("[deployment] GameServer:Hosting:ForwardedHeaders:ForwardLimit must be >= 1.");
        }

        return forwarded;
    }

    /// <summary>Secrets must not be read from JSON files outside Development/Test (OPS-08).</summary>
    public static void ValidateSecretSources(IConfiguration configuration, GameServerOptions options, List<string> errors)
    {
        if (options.AllowsDevelopmentTools)
        {
            return;
        }

        if (configuration is not IConfigurationRoot root)
        {
            errors.Add("[deployment] secret sources cannot be checked: the configuration is not an IConfigurationRoot (OPS-08).");
            return;
        }

        foreach (var key in options.SecretKeys)
        {
            // Any file holding the secret is a leak, even when an environment variable overrides it.
            foreach (var file in root.Providers.OfType<FileConfigurationProvider>().Where(p => p.TryGet(key, out var value) && !string.IsNullOrEmpty(value)))
            {
                errors.Add(
                    $"[deployment] secret '{key}' is present in {file.Source.Path}. Fix: remove it from the file and provide it through an " +
                    $"environment variable ({key.Replace(":", "__", StringComparison.Ordinal)}) or a secret provider; never commit secrets (OPS-08).");
            }
        }
    }
}
