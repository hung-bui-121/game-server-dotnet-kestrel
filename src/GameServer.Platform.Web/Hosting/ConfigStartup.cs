using GameServer.Config;
using GameServer.Platform.Config;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Hosting;

/// <summary>
/// The config phase: the first block of <c>UseGameServer</c> (research R10, contracts/startup-report.md §1). It loads the config
/// once, runs the registry checks (required files included), then the module rules and the declared catalogs, so every
/// catalog exists before anything else resolves services. Provision and export modes never run it (D4).
/// </summary>
internal static class ConfigStartup
{
    /// <summary>Adds every line to <paramref name="errors"/>; returns the warnings, logged once with the other startup warnings.</summary>
    public static IReadOnlyList<string> Run(IServiceProvider services, GameServerOptions options, ModuleRegistry registry, List<string> errors)
    {
        IGameConfig? config = null;
        try
        {
            config = services.GetRequiredService<IGameConfig>();
            services.GetRequiredService<PlatformMetrics>().SetConfigVersion(config.ConfigVersion);
        }
        catch (GameServerStartupException ex)
        {
            errors.AddRange(ex.Violations);
        }

        errors.AddRange(registry.Validate(options, config));
        var store = services.GetRequiredService<ConfigCatalogStore>();
        if (config is null)
        {
            // All or nothing (D2): no rule or catalog line cascades from a config that did not load.
            store.ConfigFailed();
            return [];
        }

        var bindings = services.GetRequiredService<DeclaredCatalogBindings>();
        var result = ConfigDeclarationRunner.Run(registry.Modules, config, type => bindings.For(type, services));
        errors.AddRange(result.Errors);
        store.Publish(result);
        return result.Warnings;
    }
}
