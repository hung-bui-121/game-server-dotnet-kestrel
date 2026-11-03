using GameServer.Config;
using GameServer.Modules;
using GameServer.Platform.Config;
using GameServer.Platform.Registry;

namespace GameServer.TestKit;

/// <summary>
/// Runs the config declarations (required files, rules, catalog builds) of modules over a config without a host or store
/// (FG-018, FR-018). Lines are exactly the ones the host reports.
/// </summary>
public static class ConfigDeclarationCheck
{
    public static ConfigDeclarationReport Check(IGameConfig config, params IGameModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(modules);
        var registry = new ModuleRegistry();
        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module, nameof(modules));
            registry.Add(module, serviceOnly: false);
        }

        var required = registry.ValidateConfig(config);
        var errors = new List<string>(required);
        var result = ConfigDeclarationRunner.Run(registry.Modules, config, _ => CatalogBinding.Declared);
        result.Errors.AddRange(ConfigDeclarationRunner.UnexplainedFailures(result, required));
        errors.AddRange(result.Errors);
        return new ConfigDeclarationReport(config, errors, result.Warnings, result.Catalogs);
    }
}
