using System.Net;
using GameServer.Config;
using GameServer.Modules.Economy;
using GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;
using GameServer.Platform.Observability;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.OpenApi;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>
/// Slice 011 US3 independent test (SC-004, FR-001, FR-008..FR-010): each committed Couchbase flavor file of the sample host,
/// with only deployment settings from outside (environment-style), becomes ready on the scope provisioned by
/// <c>--provision</c>; beta/uat run without development tools and with the Staging config overlay; live needs TLS.
/// </summary>
[Collection(SampleScopeCollection.Name)]
public sealed class SampleHostFlavorTests(ProvisionedSampleScope scope)
{
    private const string OverlayLabel = "staging-overlay";

    [Fact]
    public async Task Dev_is_ready_with_development_tools_on()
    {
        ProvisionedSampleScope.RequireCouchbase();
        var settings = scope.HostSettings(IntegrationCluster.AdminUser, IntegrationCluster.AdminPassword);

        await using var factory = SampleHostFactory.Create("Dev", settings);
        var client = factory.CreateClient();

        (await client.GetAsync(ObservabilityModule.ReadyRoute)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(GmOpenApi.DocumentPath)).StatusCode.ShouldBe(HttpStatusCode.OK);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.PlayerHeader, "flavor-dev");
        (await client.PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Beta")]
    [InlineData("Uat")]
    public async Task Staging_flavors_are_ready_without_development_tools_and_use_the_staging_overlay(string flavor)
    {
        ProvisionedSampleScope.RequireCouchbase();
        var user = await scope.DataOnlyUserAsync();
        var configRoot = Path.Combine(Path.GetTempPath(), "gs-flavor-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(Path.Combine(AppContext.BaseDirectory, "config", "base"), Path.Combine(configRoot, "base"));
            var overlay = Directory.CreateDirectory(Path.Combine(configRoot, "env", "Staging")).FullName;
            await File.WriteAllTextAsync(Path.Combine(overlay, "manifest.json"), $$"""{"label":"{{OverlayLabel}}","files":[],"clientVisible":[]}""");
            var settings = StagingSettings(user);
            settings["GameServer:ConfigPath"] = configRoot;

            await using var factory = SampleHostFactory.Create(flavor, settings);
            var client = factory.CreateClient();

            (await client.GetAsync(ObservabilityModule.ReadyRoute)).StatusCode.ShouldBe(HttpStatusCode.OK);
            factory.Services.GetRequiredService<GameServerOptions>().DeploymentEnvironment.ShouldBe("Staging");
            factory.Services.GetRequiredService<GameServerOptions>().EnvironmentId.ShouldBe(flavor.ToLowerInvariant());
            factory.Services.GetRequiredService<IGameConfig>().Label.ShouldBe(OverlayLabel);

            // No development tools (R13): no cheat route, no GM OpenAPI, and the test authentication header is ignored.
            (await client.PostAsync(CheatModule.GrantRoute, new ByteArrayContent([]))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await client.GetAsync(GmOpenApi.DocumentPath)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.PlayerHeader, "flavor-staging");
            (await client.PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            TestStores.DeleteFolder(configRoot);
        }
    }

    [Fact]
    public async Task Live_requires_tls_and_reports_it_as_the_only_error()
    {
        ProvisionedSampleScope.RequireCouchbase();
        var user = await scope.DataOnlyUserAsync();
        var tls = Environment.GetEnvironmentVariable("COUCHBASE_TLS_CONNSTR");
        var settings = StagingSettings(user);
        if (!string.IsNullOrWhiteSpace(tls))
        {
            settings["Couchbase:ConnectionString"] = tls;
            settings["Couchbase:TrustCertificatePath"] = Environment.GetEnvironmentVariable("COUCHBASE_CA_PATH");
            await using var factory = SampleHostFactory.Create("Live", settings);
            (await factory.CreateClient().GetAsync(ObservabilityModule.ReadyRoute)).StatusCode.ShouldBe(HttpStatusCode.OK);
            return;
        }

        var run = await SampleHostProcess.RunAsync("Live", settings, ["--urls", "http://127.0.0.1:0"], TimeSpan.FromMinutes(2));

        run.ExitCode.ShouldBe(1, run.ToString());
        var violations = Violations(run.Error);
        violations.Length.ShouldBe(1, run.Error);
        violations[0].ShouldContain("couchbases://");
    }

    [Fact]
    public async Task Beta_without_couchbase_settings_lists_every_missing_key_in_one_start()
    {
        ProvisionedSampleScope.RequireCouchbase();
        var settings = StagingSecrets.Create();
        settings["GameServer:GameId"] = scope.GameId;

        var run = await SampleHostProcess.RunAsync("Beta", settings, ["--urls", "http://127.0.0.1:0"], TimeSpan.FromMinutes(2));

        run.ExitCode.ShouldBe(1, run.ToString());
        foreach (var variable in new[] { "Couchbase__ConnectionString", "Couchbase__Username", "Couchbase__Password" })
        {
            run.Error.ShouldContain(variable);
        }

        // The bucket comes from the committed flavor file; nothing else is reported.
        Violations(run.Error).Length.ShouldBe(3, run.Error);
    }

    private Dictionary<string, string?> StagingSettings(DataOnlyUser user)
    {
        var settings = scope.HostSettings(user.Name, user.Password);
        foreach (var (key, value) in StagingSecrets.Create())
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>The <c> - violation</c> lines of a <see cref="GameServerStartupException"/> report.</summary>
    private static string[] Violations(string error) =>
        error.Split('\n').Where(l => l.StartsWith(" - ", StringComparison.Ordinal)).ToArray();

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }
}
