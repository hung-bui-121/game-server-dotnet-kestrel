using System.Text.Json;
using GameServer.Config;
using GameServer.Modules;
using GameServer.Platform.Registry;

namespace GameServer.Platform.Config;

/// <summary>
/// Runs the config declarations of the modules over a loaded config (research R6), for the host's config phase and for the
/// test kit, so both report the same lines (FR-018): rules first (module order, declaration order), then catalogs. Every
/// <c>.json</c> file is parsed at most once per run. A missing required file is the registry's line, not this one's.
/// </summary>
internal static class ConfigDeclarationRunner
{
    public static ConfigDeclarationResult Run(IReadOnlyList<RegisteredModule> modules, IGameConfig config, Func<Type, CatalogBinding> binding)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(binding);
        var result = new ConfigDeclarationResult();
        using var cache = new ParseCache(config);
        var fileErrors = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            RunRules(module.Descriptor, config, cache, fileErrors, result);
        }

        var declaredBy = new Dictionary<Type, string>();
        foreach (var module in modules)
        {
            if (module.Descriptor.ConfigCatalogs.Count == 0)
            {
                continue;
            }

            var required = ModuleRegistry.RequiredConfigFiles(module.Descriptor).ToHashSet(StringComparer.Ordinal);
            foreach (var catalog in module.Descriptor.ConfigCatalogs)
            {
                BuildCatalog(module.Descriptor.Name, catalog, required, config, cache, fileErrors, declaredBy, binding, result);
            }
        }

        return result;
    }

    /// <summary>
    /// The backstop (slice 015 T071 M1): one line per catalog that was not built when no other line of this start explains it —
    /// neither the runner's lines nor <paramref name="otherLines"/> (the registry's required-file lines; the loader's lines when the
    /// config failed). Coarse on purpose: any line fails the start anyway; this one only keeps a lazy failure from coming back silently.
    /// </summary>
    public static IReadOnlyList<string> UnexplainedFailures(ConfigDeclarationResult result, IReadOnlyCollection<string> otherLines)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(otherLines);
        if (result.FailedCatalogs.Count == 0 || result.Errors.Count > 0 || otherLines.Count > 0)
        {
            return [];
        }

        return [.. result.FailedCatalogs.Select(ConfigReportLines.CatalogUnexplained).Order(StringComparer.Ordinal)];
    }

    private static void RunRules(ModuleDescriptor module, IGameConfig config, ParseCache cache, Dictionary<string, int> fileErrors, ConfigDeclarationResult result)
    {
        foreach (var declaration in module.ConfigFiles.Where(d => config.HasFile(d.Name)))
        {
            foreach (var rule in declaration.Rules)
            {
                result.RuleCount++;
                var problems = new ConfigProblems();
                Exception? thrown = null;
                try
                {
                    rule.Check(new ConfigContext(config, declaration.Name, problems, cache.Json));
                }
                catch (Exception ex)
                {
                    // A rule must report instead of throwing; its own line keeps the report complete (FR-013).
                    thrown = ex;
                }

                foreach (var entry in problems.Entries)
                {
                    var file = entry.At.File ?? declaration.Name;
                    var line = ConfigReportLines.Rule(module.Name, FileRef(config, file), entry.At, entry.Problem, entry.Fix);
                    if (entry.IsWarning)
                    {
                        result.Warnings.Add(line);
                    }
                    else
                    {
                        result.Errors.Add(line);
                        fileErrors[file] = fileErrors.GetValueOrDefault(file) + 1;
                    }
                }

                if (thrown is not null)
                {
                    result.Errors.Add(ConfigReportLines.RuleThrew(module.Name, FileRef(config, declaration.Name), rule.Name, thrown));
                    fileErrors[declaration.Name] = fileErrors.GetValueOrDefault(declaration.Name) + 1;
                }
            }
        }
    }

    private static void BuildCatalog(
        string module,
        ConfigCatalogDeclaration catalog,
        HashSet<string> requiredFiles,
        IGameConfig config,
        ParseCache cache,
        Dictionary<string, int> fileErrors,
        Dictionary<Type, string> declaredBy,
        Func<Type, CatalogBinding> binding,
        ConfigDeclarationResult result)
    {
        var type = catalog.CatalogType;
        if (!declaredBy.TryAdd(type, module))
        {
            result.Errors.Add(ConfigReportLines.CatalogDeclaredTwice(type, declaredBy[type], module));
            return;
        }

        // Missing required files are the registry's lines and rule errors are the rules' lines: no second line (FR-022). An absent
        // optional file does not skip the build (M1): the build decides what absence means, so no declared catalog is ever lazy.
        if (catalog.Files.Any(f => fileErrors.GetValueOrDefault(f) > 0 || (!config.HasFile(f) && requiredFiles.Contains(f))))
        {
            result.FailedCatalogs.Add(type);
            return;
        }

        var bound = binding(type);
        if (bound.Resolve is { } resolve)
        {
            try
            {
                result.Catalogs[type] = resolve();
            }
            catch (Exception ex)
            {
                result.Errors.Add(ConfigReportLines.CatalogHostFailed(module, type, ex));
                result.FailedCatalogs.Add(type);
            }

            return;
        }

        var problems = new ConfigProblems();
        object? built;
        try
        {
            built = catalog.Build(new ConfigContext(config, null, problems, cache.Json));
        }
        catch (Exception ex)
        {
            AddCatalogProblems(module, type, config, problems, result);
            var absent = catalog.Files.FirstOrDefault(f => !config.HasFile(f));
            result.Errors.Add(absent is null
                ? ConfigReportLines.CatalogThrew(module, type, ex)
                : ConfigReportLines.CatalogThrewOptionalAbsent(module, type, ex, absent));
            result.FailedCatalogs.Add(type);
            return;
        }

        AddCatalogProblems(module, type, config, problems, result);
        if (problems.ErrorCount > 0)
        {
            result.FailedCatalogs.Add(type);
        }
        else if (built is null)
        {
            result.Errors.Add(ConfigReportLines.CatalogNull(module, type));
            result.FailedCatalogs.Add(type);
        }
        else
        {
            result.Catalogs[type] = built;
        }
    }

    private static void AddCatalogProblems(string module, Type type, IGameConfig config, ConfigProblems problems, ConfigDeclarationResult result)
    {
        foreach (var entry in problems.Entries)
        {
            var fileRef = entry.At.File is { } file ? FileRef(config, file) : null;
            var line = ConfigReportLines.Catalog(module, type, fileRef, entry.At, entry.Problem, entry.Fix);
            (entry.IsWarning ? result.Warnings : result.Errors).Add(line);
        }
    }

    private static string FileRef(IGameConfig config, string file) =>
        ConfigReportLines.FileRef(file, config is GameConfig loaded && loaded.OverlaidFiles.Contains(file) ? loaded.OverlayEnvironment : null);

    /// <summary>One <see cref="JsonDocument"/> per file, parsed on first use and disposed at the end of the run.</summary>
    private sealed class ParseCache(IGameConfig config) : IDisposable
    {
        private readonly Dictionary<string, JsonDocument> _documents = new(StringComparer.Ordinal);

        public JsonElement Json(string file)
        {
            if (_documents.TryGetValue(file, out var document))
            {
                return document.RootElement;
            }

            if (!config.HasFile(file))
            {
                throw new KeyNotFoundException($"Config file '{file}' is not in the effective config. Fix: check Config.HasFile(\"{file}\") first.");
            }

            document = JsonDocument.Parse(config.GetFileBytes(file));
            _documents[file] = document;
            return document.RootElement;
        }

        public void Dispose()
        {
            foreach (var document in _documents.Values)
            {
                document.Dispose();
            }

            _documents.Clear();
        }
    }
}
