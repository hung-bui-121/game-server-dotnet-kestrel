using GameServer.Config;

namespace GameServer.Platform.Config;

/// <summary>
/// Texts of the config report lines (data-model §5.3–§5.5, contracts/startup-report.md §4–§7). <c>{problem}</c> and
/// <c>{fix}</c> come from rules and builds; one trailing period is stripped from each so every line ends exactly once.
/// </summary>
internal static class ConfigReportLines
{
    private const string ThrowFix = "Fix: report problems through ConfigProblems instead of throwing (FG-018).";

    /// <summary><c>{file}</c>, plus <c> (config/env/{env})</c> when the effective bytes came from the overlay.</summary>
    public static string FileRef(string file, string? overlayEnvironment) =>
        overlayEnvironment is null ? file : $"{file} (config/env/{overlayEnvironment})";

    /// <summary>" row 3", " key 'k'", " at $.a", or "" for the whole file.</summary>
    public static string Location(ConfigLocation at) => at.Kind switch
    {
        ConfigLocationKind.Row => $" row {at.Value}",
        ConfigLocationKind.Key => $" key '{at.Value}'",
        ConfigLocationKind.Path => $" at {at.Value}",
        _ => string.Empty,
    };

    /// <summary><c>[module {module}] config {fileRef}{location}: {problem}. Fix: {fix}</c> (also the warning text).</summary>
    public static string Rule(string module, string fileRef, ConfigLocation at, string problem, string fix) =>
        $"[module {module}] config {fileRef}{Location(at)}: {Trim(problem)}. Fix: {Trim(fix)}.";

    public static string RuleThrew(string module, string fileRef, string rule, Exception exception) =>
        $"[module {module}] config {fileRef}: rule '{rule}' threw {TypeName(exception)}: {exception.Message}. {ThrowFix}";

    /// <summary><c>[module {module}] catalog {Type}[ ({file}{location})]: {problem}. Fix: {fix}</c>.</summary>
    public static string Catalog(string module, Type catalog, string? fileRef, ConfigLocation at, string problem, string fix)
    {
        var where = (fileRef ?? string.Empty) + Location(at);
        var place = where.Length == 0 ? string.Empty : $" ({where.TrimStart()})";
        return $"[module {module}] catalog {catalog.Name}{place}: {Trim(problem)}. Fix: {Trim(fix)}.";
    }

    public static string CatalogThrew(string module, Type catalog, Exception exception) =>
        $"[module {module}] catalog {catalog.Name}: the build threw {TypeName(exception)}: {exception.Message}. {ThrowFix}";

    /// <summary>
    /// The build read <paramref name="file"/>, which is absent (M1; T071 round 2 m-1: only for an <see cref="AbsentConfigFileException"/>,
    /// naming the file it carries). Every other throw is <see cref="CatalogThrew"/>.
    /// </summary>
    public static string CatalogReadAbsentOptionalFile(string module, Type catalog, string file) =>
        $"[module {module}] catalog {catalog.Name}: the build read optional config file '{file}', which is absent. " +
        $"Fix: build an empty catalog when Config.HasFile(\"{file}\") is false, or declare the file Required (FG-018).";

    /// <summary>The backstop line (M1): a catalog is not built and no other line explains why.</summary>
    public static string CatalogUnexplained(Type catalog) =>
        $"[catalog {catalog.Name}] was not built and no other line explains why (framework fault). " +
        "Fix: report it with this config; the catalog would fail at its first use (FG-018).";

    public static string CatalogNull(string module, Type catalog) =>
        $"[module {module}] catalog {catalog.Name}: the build returned no catalog and reported no problem. Fix: return the catalog or report why (FG-018).";

    public static string CatalogDeclaredTwice(Type catalog, string firstModule, string secondModule) =>
        $"[catalog {catalog.Name}] is declared by {firstModule} and {secondModule}. Fix: declare it in one module; the other takes it from DI (FG-018).";

    /// <summary>A host's or a test's registration (instance, type or factory) of a declared catalog threw when resolved.</summary>
    public static string CatalogHostFailed(string module, Type catalog, Exception exception) =>
        $"[catalog {catalog.Name}] the registration that replaces the declaration of module {module} could not be resolved: " +
        $"{exception.GetType().Name}: {exception.Message}. " +
        "Fix: correct that registration (host or test), or remove it so the platform builds the declared catalog (FG-018).";

    public static string CheckNotCreated(string check, Exception exception) =>
        $"[{check}] the startup check could not be created: {exception.GetType().Name}: {exception.Message}. " +
        "Fix: register its dependencies; a check that reads config takes GameConfigStatus (FG-023).";

    public static string CheckNotRunConfig(string check) =>
        $"[{check}] the startup check did not run: the game config did not load (see the [config] lines). " +
        "Fix: take GameConfigStatus instead of IGameConfig and skip when it is not Loaded (FG-023).";

    public static string CheckNotRunCatalog(string check, string catalogTypeName) =>
        $"[{check}] the startup check did not run: catalog {catalogTypeName} was not built (see the lines above). " +
        "Fix: take GameConfigStatus and read the catalog with TryGetCatalog (FG-023).";

    /// <summary>A warning, not an error: provision mode does not validate config (D4, BC-9).</summary>
    public static string CheckNotRunProvision(string check) =>
        $"[{check}] the startup check did not run: provision mode does not load the game config. " +
        "Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023).";

    /// <summary>The existing line of a check whose <c>Check</c> threw (unchanged text).</summary>
    public static string CheckFailed(string check, Exception exception) =>
        $"[{check}] the startup check failed: {exception.GetType().Name}: {exception.Message}";

    public static string BundleTooLarge(long size, int files, long limit) =>
        $"[config] the client-visible bundle needs a GetConfig response of up to {size} bytes ({files} files), over " +
        $"GameServer:Limits:ConfigBundleBytes = {limit}. Fix: raise the limit or remove files from 'clientVisible' (CFG-03, API-06).";

    private static string Trim(string text) => text.EndsWith('.') ? text[..^1] : text;

    /// <summary>The thrown type as a rule author knows it: an absent-file read is the <see cref="KeyNotFoundException"/> <c>ConfigContext.Json</c> documents.</summary>
    private static string TypeName(Exception exception) =>
        exception is AbsentConfigFileException ? nameof(KeyNotFoundException) : exception.GetType().Name;
}
