using GameServer.Config;
using GameServer.Platform.Config;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Config;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Hosting;

/// <summary>
/// The config phase: the first block of <c>UseGameServer</c> (research R10, contracts/startup-report.md §1). It loads the config
/// once, runs the registry checks (required files included), then the module rules and the declared catalogs, so every
/// catalog exists before anything else resolves services, and publishes <see cref="GameConfigStatus"/> for the startup checks.
/// When the player API is mapped it builds the <c>GetConfig</c> bundle and checks its size (R11). Provision and export modes
/// never run it (D4).
/// </summary>
internal static class ConfigStartup
{
    /// <summary>Adds every line to <paramref name="errors"/>; returns the warnings, logged once with the other startup warnings.</summary>
    public static IReadOnlyList<string> Run(IServiceProvider services, GameServerOptions options, ModuleRegistry registry, List<string> errors)
    {
        var status = services.GetRequiredService<GameConfigStatus>();
        IGameConfig? config = null;
        try
        {
            config = services.GetRequiredService<IGameConfig>();
            var metrics = services.GetRequiredService<PlatformMetrics>();
            metrics.SetConfigVersion(config.ConfigVersion);
            metrics.SetClientConfigVersion(config.ClientConfigVersion);
        }
        catch (GameServerStartupException ex)
        {
            errors.AddRange(ex.Violations);
        }

        // The required-file lines come last of the registry lines, as in ModuleRegistry.Validate(options, config).
        errors.AddRange(registry.Validate(options, config: null));
        var store = services.GetRequiredService<ConfigCatalogStore>();
        if (config is null)
        {
            // All or nothing (D2): no rule or catalog line cascades from a config that did not load.
            store.ConfigFailed();
            status.PublishFailed();
            return [];
        }

        var required = registry.ValidateConfig(config);
        errors.AddRange(required);
        var bindings = services.GetRequiredService<DeclaredCatalogBindings>();
        var result = ConfigDeclarationRunner.Run(registry.Modules, config, type => bindings.For(type, services));
        errors.AddRange(result.Errors);
        store.Publish(result);
        status.PublishLoaded(config, declarationsPassed: required.Count == 0 && result.Errors.Count == 0);

        // Step 4 (FR-053, BC-7): the bundle the endpoint serves, built once; its largest response must fit the uncompressed limit.
        ConfigBundle? bundle = null;
        if (options.Hosting.MapPlayerApi)
        {
            bundle = ConfigBundle.Build(config);
            if (bundle.MaxResponseBytes > options.Limits.ConfigBundleBytes)
            {
                errors.Add(ConfigReportLines.BundleTooLarge(bundle.MaxResponseBytes, bundle.FileCount, options.Limits.ConfigBundleBytes));
            }
        }

        services.GetRequiredService<ConfigPhaseSummary>().Publish(config, result.RuleCount, result.CatalogCount, bundle);
        return result.Warnings;
    }
}
