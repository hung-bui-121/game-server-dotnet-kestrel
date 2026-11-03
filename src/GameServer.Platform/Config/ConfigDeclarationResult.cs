namespace GameServer.Platform.Config;

/// <summary>What one run of the module config declarations produced (research R6): report lines and built catalogs.</summary>
internal sealed class ConfigDeclarationResult
{
    /// <summary>Rule, rule-throw and catalog lines in report order (module order, declaration order, reported order).</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Rule and catalog warnings: logged once, they do not fail the start.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Built (declared) or resolved (provided) catalogs by type.</summary>
    public Dictionary<Type, object> Catalogs { get; } = [];

    /// <summary>Declared catalog types that are not available: skipped (files missing or with rule errors) or failed.</summary>
    public HashSet<Type> FailedCatalogs { get; } = [];

    /// <summary>Rules declared for the files present in this config.</summary>
    public int RuleCount { get; set; }

    /// <summary>Catalog types declared by the modules (first declaration of each type).</summary>
    public int CatalogCount => Catalogs.Count + FailedCatalogs.Count;
}
