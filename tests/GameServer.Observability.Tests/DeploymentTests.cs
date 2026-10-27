using System.Net;
using GameServer.Platform;
using GameServer.Platform.Web.Hosting;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace GameServer.Observability.Tests;

/// <summary>Reverse proxy headers (OPS-02) and secret sources (OPS-08).</summary>
public sealed class DeploymentTests
{
    private const string IpRoute = "/v1/test/ip";

    private static Task<ObservedHost> StartWithProxyAsync(Action<Dictionary<string, string?>> proxy) =>
        ObservedHost.StartAsync(
            settings: s =>
            {
                s["GameServer:PublicEndpointAllowlist:2"] = IpRoute;
                s["GameServer:Hosting:ForwardedHeaders:Enabled"] = "true";
                proxy(s);
            },
            beforeUseGameServer: app => app.MapGet(IpRoute, (HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "none")
                .AsPublicEndpoint(protobuf: false));

    private static async Task<string> RemoteIpAsync(ObservedHost observed, string peer, string forwardedFor)
    {
        var context = await observed.Host.App.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = IpRoute;
            c.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            c.Request.Headers["X-Forwarded-For"] = forwardedFor;
        });
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Forwarded_for_is_honoured_only_from_trusted_proxies()
    {
        await using var observed = await StartWithProxyAsync(s =>
        {
            s["GameServer:Hosting:ForwardedHeaders:KnownProxies:0"] = "10.0.0.1";
            s["GameServer:Hosting:ForwardedHeaders:KnownNetworks:0"] = "192.168.50.0/24";
        });

        (await RemoteIpAsync(observed, "10.0.0.1", "203.0.113.7")).ShouldBe("203.0.113.7");
        (await RemoteIpAsync(observed, "192.168.50.9", "203.0.113.8")).ShouldBe("203.0.113.8");
        (await RemoteIpAsync(observed, "10.0.0.2", "203.0.113.9")).ShouldBe("10.0.0.2");
    }

    [Fact]
    public async Task Forwarded_headers_without_trusted_proxies_fail_startup()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(() => StartWithProxyAsync(_ => { }));
        ex.Message.ShouldContain("ForwardedHeaders is enabled without KnownProxies/KnownNetworks");
    }

    [Fact]
    public async Task Catch_all_proxy_networks_fail_startup()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(() => StartWithProxyAsync(s =>
            s["GameServer:Hosting:ForwardedHeaders:KnownNetworks:0"] = "0.0.0.0/0"));
        ex.Message.ShouldContain("trusts every client");
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("10", "10")]
    [InlineData("10", "-1")]
    public async Task Invalid_shutdown_settings_fail_startup(string timeout, string drain)
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(() => ObservedHost.StartAsync(settings: s =>
        {
            s["GameServer:Hosting:ShutdownTimeoutSeconds"] = timeout;
            s["GameServer:Hosting:DrainDelaySeconds"] = drain;
        }));
        ex.Message.ShouldContain("ShutdownTimeoutSeconds");
    }

    [Theory]
    [InlineData("Observability:Logging:MinimumLevel", "Verbose-ish")]
    [InlineData("Observability:Otlp:Endpoint", "not a uri")]
    [InlineData("Observability:Otlp:Protocol", "udp")]
    [InlineData("Observability:Logging:LevelOverrides:Couchbase", "Loud")]
    [InlineData("Observability:Logging:LevelOverrides:Couchbase", "7")]
    [InlineData("Observability:Logging:MinimumLevel", "Warning,Error")]
    public async Task Invalid_observability_settings_fail_startup_with_a_readable_violation(string key, string value)
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(() => ObservedHost.StartAsync(settings: s => s[key] = value));
        ex.Message.ShouldContain(key);
    }

    [Fact]
    public void Secrets_from_json_files_are_refused_outside_development()
    {
        var json = Path.Combine(Path.GetTempPath(), "gs-appsettings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(json, """{ "Identity": { "GuestPepper": "from-file" }, "Couchbase": { "Password": "from-file" } }""");
        try
        {
            var production = new GameServerOptions { DeploymentEnvironment = "Production" };
            var fromFile = new ConfigurationBuilder().AddJsonFile(json).Build();
            var errors = new List<string>();
            DeploymentChecks.ValidateSecretSources(fromFile, production, errors);
            errors.Count.ShouldBe(2);
            errors[0].ShouldContain("Identity__GuestPepper");

            // Overriding with an environment variable does not help: the secret is still committed in the file.
            var overridden = new ConfigurationBuilder()
                .AddJsonFile(json)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Identity:GuestPepper"] = "env", ["Couchbase:Password"] = "env" })
                .Build();
            errors.Clear();
            DeploymentChecks.ValidateSecretSources(overridden, production, errors);
            errors.Count.ShouldBe(2);

            // Secrets only in environment-style providers pass.
            var fromEnvironment = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Identity:GuestPepper"] = "env" })
                .Build();
            errors.Clear();
            DeploymentChecks.ValidateSecretSources(fromEnvironment, production, errors);
            errors.ShouldBeEmpty();

            errors.Clear();
            DeploymentChecks.ValidateSecretSources(fromFile, new GameServerOptions { DeploymentEnvironment = "Development" }, errors);
            errors.ShouldBeEmpty();
        }
        finally
        {
            File.Delete(json);
        }
    }
}
