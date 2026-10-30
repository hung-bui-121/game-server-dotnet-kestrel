using GameServer.Config;
using GameServer.Modules.Gm;
using GameServer.Modules.Identity;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Adapters;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 011 US3: the deployment environment and the environment id are validated against the flavor table before traffic
/// (FR-003, FR-004, FR-007, FR-009; research R2). Cases without the key remove it from the test settings: the shared base file
/// sets none (FR-005) and a test host never loads a flavor file.
/// </summary>
public sealed class FlavorValidationTests
{
    private const string DeploymentKey = "GameServer:DeploymentEnvironment";

    private static async Task<GameServerStartupException> StartupFails(
        string environmentName,
        Action<Dictionary<string, string?>> settings,
        Action<GameServer.Platform.Web.Hosting.GameServerBuilder>? modules = null) =>
        await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(modules: modules, settings: settings, environmentName: environmentName);
        });

    [Fact]
    public async Task Local_without_the_key_names_the_key_the_flavor_file_and_the_table_and_fails_closed()
    {
        var ex = await StartupFails("Local", s => s.Remove(DeploymentKey));

        ex.Message.ShouldContain("GameServer:DeploymentEnvironment is not set and the host environment name 'Local' is not a deployment environment");
        ex.Message.ShouldContain("appsettings.Local.json (found: no");
        ex.Message.ShouldContain(StandardFlavors.Describe());

        // Fail-closed: 'Local' is not Development, so test authentication is refused (US3-2).
        ex.Message.ShouldContain("test authentication is enabled in 'Local'");
    }

    [Fact]
    public async Task An_unknown_value_lists_the_allowed_values()
    {
        var ex = await StartupFails("Development", s => s[DeploymentKey] = "banana");

        ex.Message.ShouldContain("GameServer:DeploymentEnvironment 'banana' is not a deployment environment (allowed: Development, Staging, Production, Test)");
    }

    [Theory]
    [InlineData("live", "Development", "Production")]
    [InlineData("beta", "Production", "Staging")]
    [InlineData("dev", "Test", "Development")]
    public async Task A_standard_environment_id_must_match_its_flavor(string environmentId, string deployment, string expected)
    {
        var ex = await StartupFails("Development", s =>
        {
            s["GameServer:EnvironmentId"] = environmentId;
            s[DeploymentKey] = deployment;
        });

        ex.Message.ShouldContain($"GameServer:EnvironmentId '{environmentId}' is a standard flavor that requires GameServer:DeploymentEnvironment={expected}");
        ex.Message.ShouldContain($"but it is '{deployment}'");
    }

    [Fact]
    public async Task A_game_defined_environment_id_is_not_checked_against_the_table()
    {
        // Staging fails for test authentication and the in-memory store, but not for the environment id.
        var ex = await StartupFails("Development", s =>
        {
            s["GameServer:EnvironmentId"] = "qa2";
            s[DeploymentKey] = "Staging";
        });

        ex.Message.ShouldNotContain("GameServer:EnvironmentId");
        ex.Message.ShouldNotContain("GameServer:DeploymentEnvironment");
    }

    [Fact]
    public async Task A_lowercase_value_is_normalized_and_selects_the_canonical_overlay()
    {
        var configRoot = Path.Combine(Path.GetTempPath(), "gs-flavor-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(Path.Combine(AppContext.BaseDirectory, "config"), configRoot);
            var overlay = Directory.CreateDirectory(Path.Combine(configRoot, "env", "Development")).FullName;
            await File.WriteAllTextAsync(Path.Combine(overlay, "manifest.json"), """{"label":"flavor-overlay","files":[],"clientVisible":[]}""");

            await using var host = await TestHost.StartAsync(settings: s =>
            {
                s[DeploymentKey] = "development";
                s["GameServer:ConfigPath"] = configRoot;
            });

            host.Services.GetRequiredService<GameServerOptions>().DeploymentEnvironment.ShouldBe("Development");
            host.Services.GetRequiredService<IGameConfig>().Label.ShouldBe("flavor-overlay");
        }
        finally
        {
            Directory.Delete(configRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_deployment_environment_host_name_is_a_valid_fallback()
    {
        var ex = await StartupFails("Staging", s => s.Remove(DeploymentKey));

        ex.Message.ShouldNotContain("GameServer:DeploymentEnvironment");
        ex.Message.ShouldContain("test authentication is enabled in 'Staging'");
    }

    [Fact]
    public async Task The_in_memory_store_outside_development_gets_the_generalized_message()
    {
        var ex = await StartupFails("Development", s => s[DeploymentKey] = "Staging");

        ex.Message.ShouldContain(
            "[deployment] the InMemory store is configured in 'Staging'. Fix: set GameServer:Store=Couchbase and the Couchbase settings; " +
            "the InMemory and File stores are for Development/Test only (FR-007).");
    }

    [Fact]
    public async Task Production_with_nothing_configured_lists_module_and_platform_secrets_together()
    {
        var ex = await StartupFails(
            "Production",
            s => s.Remove(DeploymentKey),
            modules: b => b.AddModule<IdentityModule>().AddModule<CounterModule>().AddModule<GmModule>());

        // GM SSO comes from the module's ValidateOnStart validator (IStartupValidator), the others from UseGameServer (US3-7, FR-009).
        ex.Message.ShouldContain("Gm:Oidc:Authority");
        ex.Message.ShouldContain("DataProtection:MasterKey is not set in 'Production'");
        ex.Message.ShouldContain("Identity:GuestPepper is not set");
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, target, StringComparison.Ordinal));
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, target, StringComparison.Ordinal));
        }
    }
}
