using System.Text.Json;

namespace GameServer.Config;

/// <summary>One config file a module reads (FG-018). A file named by several modules is checked by each one's rules.</summary>
public sealed class ConfigFileDeclaration
{
    private ConfigFileDeclaration(string name, bool isOptional, ConfigRule[] rules)
    {
        ConfigNames.RequirePlainFileName(name, nameof(name));
        ArgumentNullException.ThrowIfNull(rules);
        if (Array.IndexOf(rules, null) >= 0)
        {
            throw new ArgumentException($"A rule of config file '{name}' is null.", nameof(rules));
        }

        Name = name;
        IsOptional = isOptional;
        Rules = [.. rules];
    }

    public string Name { get; }

    public bool IsOptional { get; }

    public IReadOnlyList<ConfigRule> Rules { get; }

    /// <summary>The file must be in the effective config; a missing file fails startup (ARC-06).</summary>
    public static ConfigFileDeclaration Required(string name, params ConfigRule[] rules) => new(name, isOptional: false, rules);

    /// <summary>The rules run only when the file is in the effective config.</summary>
    public static ConfigFileDeclaration Optional(string name, params ConfigRule[] rules) => new(name, isOptional: true, rules);
}

/// <summary>Checks the content of one declared file and reports through <see cref="ConfigContext.Problems"/>; never throws.</summary>
public delegate void ConfigRuleCheck(ConfigContext context);

/// <summary>A named content rule of a config file (FG-018).</summary>
public sealed class ConfigRule
{
    /// <param name="name">Shown when the rule throws, e.g. <c>economy.resources</c>.</param>
    /// <param name="check">Reports every problem through <see cref="ConfigContext.Problems"/>.</param>
    public ConfigRule(string name, ConfigRuleCheck check)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(check);
        Name = name;
        Check = check;
    }

    public string Name { get; }

    public ConfigRuleCheck Check { get; }
}

/// <summary>A config catalog (FG-018): built once at startup, after the rules, and shared by every handler and service.</summary>
public sealed class ConfigCatalogDeclaration
{
    private readonly Func<ConfigContext, object?> _build;

    private ConfigCatalogDeclaration(Type catalogType, Func<ConfigContext, object?> build, string[] files)
    {
        CatalogType = catalogType;
        _build = build;
        Files = files;
    }

    public Type CatalogType { get; }

    public IReadOnlyList<string> Files { get; }

    /// <param name="build">Returns the catalog, or null after reporting why. Reads only the config; cross-file consistency belongs to rules.</param>
    /// <param name="files">Files the build reads: not built when one is missing or has rule errors.</param>
    public static ConfigCatalogDeclaration Of<T>(Func<ConfigContext, T?> build, params string[] files)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Length == 0)
        {
            throw new ArgumentException($"Catalog {typeof(T).Name} must name at least one config file it reads.", nameof(files));
        }

        foreach (var file in files)
        {
            ConfigNames.RequirePlainFileName(file, nameof(files));
        }

        return new ConfigCatalogDeclaration(typeof(T), build, [.. files.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>Runs the build function. Called by the platform once per start (and by the test kit).</summary>
    public object? Build(ConfigContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _build(context);
    }
}

/// <summary>What a rule or a catalog build sees. Valid only during the call.</summary>
public sealed class ConfigContext
{
    private readonly Func<string, JsonElement> _json;

    internal ConfigContext(IGameConfig config, string? file, ConfigProblems problems, Func<string, JsonElement> json)
    {
        Config = config;
        File = file;
        Problems = problems;
        _json = json;
    }

    /// <summary>The whole loaded config (every effective file): cross-file rules read other files here.</summary>
    public IGameConfig Config { get; }

    /// <summary>The declared file of a rule; null in a catalog build.</summary>
    public string? File { get; }

    public ConfigProblems Problems { get; }

    /// <summary>
    /// Root element of a loaded <c>.json</c> file, parsed once per start and shared by every rule and build.
    /// Throws <see cref="KeyNotFoundException"/> when the file is not in the config (check <c>Config.HasFile</c> first for other files).
    /// The element must not be kept after the call.
    /// </summary>
    public JsonElement Json(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return _json(file);
    }
}

/// <summary>Problem sink bound by the framework to the declaring module and its file (rule) or catalog type (build).</summary>
public sealed class ConfigProblems
{
    private readonly List<ConfigProblem> _entries = [];

    internal ConfigProblems()
    {
    }

    public int ErrorCount { get; private set; }

    /// <summary>Every reported problem in report order (formatted by the platform).</summary>
    internal IReadOnlyList<ConfigProblem> Entries => _entries;

    /// <summary>Fails the start: one report line. <paramref name="problem"/> and <paramref name="fix"/> without trailing period.</summary>
    public void Error(ConfigLocation at, string problem, string fix)
    {
        Add(at, problem, fix, isWarning: false);
        ErrorCount++;
    }

    /// <summary>Logged once with the startup warnings; does not fail the start.</summary>
    public void Warning(ConfigLocation at, string problem, string fix) => Add(at, problem, fix, isWarning: true);

    private void Add(ConfigLocation at, string problem, string fix, bool isWarning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        ArgumentException.ThrowIfNullOrWhiteSpace(fix);
        _entries.Add(new ConfigProblem(at, problem, fix, isWarning));
    }
}

/// <summary>One problem reported by a rule or a catalog build.</summary>
internal readonly record struct ConfigProblem(ConfigLocation At, string Problem, string Fix, bool IsWarning);

public enum ConfigLocationKind
{
    WholeFile,
    Row,
    Key,
    Path,
}

/// <summary>Where a problem is, as the line prints it: " row 3", " key 'google:gems_100'", " at $.items.sword.price".</summary>
public readonly struct ConfigLocation : IEquatable<ConfigLocation>
{
    private ConfigLocation(ConfigLocationKind kind, string? value, string? file)
    {
        Kind = kind;
        Value = value;
        File = file;
    }

    public static ConfigLocation WholeFile => default;

    public ConfigLocationKind Kind { get; }

    public string? Value { get; }

    /// <summary>Null: the rule's own file (rules) or no file (catalog builds).</summary>
    public string? File { get; }

    /// <summary>1-based row of the file's row array.</summary>
    public static ConfigLocation Row(int row)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(row, 1);
        return new ConfigLocation(ConfigLocationKind.Row, row.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
    }

    public static ConfigLocation Key(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new ConfigLocation(ConfigLocationKind.Key, key, null);
    }

    /// <summary>JSON path rooted at <c>$</c>.</summary>
    public static ConfigLocation Path(string jsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        if (!jsonPath.StartsWith('$'))
        {
            throw new ArgumentException($"JSON path '{jsonPath}' must start with '$'.", nameof(jsonPath));
        }

        return new ConfigLocation(ConfigLocationKind.Path, jsonPath, null);
    }

    public static bool operator ==(ConfigLocation left, ConfigLocation right) => left.Equals(right);

    public static bool operator !=(ConfigLocation left, ConfigLocation right) => !left.Equals(right);

    /// <summary>Names another file (a catalog build, or a rule pointing into a second file).</summary>
    public ConfigLocation InFile(string file)
    {
        ConfigNames.RequirePlainFileName(file, nameof(file));
        return new ConfigLocation(Kind, Value, file);
    }

    public bool Equals(ConfigLocation other) =>
        Kind == other.Kind && string.Equals(Value, other.Value, StringComparison.Ordinal) && string.Equals(File, other.File, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ConfigLocation other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, Value, File);

    public override string ToString() => Kind switch
    {
        ConfigLocationKind.Row => $"row {Value}",
        ConfigLocationKind.Key => $"key '{Value}'",
        ConfigLocationKind.Path => $"at {Value}",
        _ => "whole file",
    } + (File is null ? string.Empty : $" in {File}");
}

/// <summary>Config file name rules shared by the declarations (the loader applies the same rule to manifest entries).</summary>
internal static class ConfigNames
{
    public static void RequirePlainFileName(string? name, string parameter)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Config file name '{name}' must be a plain file name (no '/', '\\' or '..').", parameter);
        }
    }
}
