using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

namespace GameServer.Platform.Web.Hosting;

/// <summary>Deployment-level settings applied by <c>UseGameServer</c>: reverse proxy (OPS-02) and secret sources (OPS-08).</summary>
public static class DeploymentChecks
{
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
            if (System.Net.IPNetwork.TryParse(network, out var parsed))
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
        if (options.AllowsDevelopmentTools || configuration is not IConfigurationRoot root)
        {
            return;
        }

        foreach (var key in options.SecretKeys)
        {
            var source = root.Providers.Reverse().FirstOrDefault(p => p.TryGet(key, out var value) && !string.IsNullOrEmpty(value));
            if (source is FileConfigurationProvider file)
            {
                errors.Add(
                    $"[deployment] secret '{key}' is read from {file.Source.Path}. Fix: provide it through an environment variable " +
                    $"({key.Replace(":", "__", StringComparison.Ordinal)}) or a secret provider; never commit secrets (OPS-08).");
            }
        }
    }
}
