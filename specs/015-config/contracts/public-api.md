# Public API — 015 config

Signatures only (no bodies). Everything here is additive, except the `[Obsolete]` marks (BC-6) and the behaviour changes
listed as BC-1..BC-9 in the plan. Internal types are named where a reviewer needs them; they are not consumer API.

## 1. Core (`GameServer.Core`)

### 1.1 Descriptor members

```csharp
namespace GameServer.Modules;

public sealed class ModuleDescriptor
{
    // existing members unchanged; RequiredConfigFiles stays (= Required(name) without rules)

    /// <summary>Config files the module reads and the rules for their content (FG-018). Run at every start, before traffic.</summary>
    public IReadOnlyList<GameServer.Config.ConfigFileDeclaration> ConfigFiles { get; init; } = [];

    /// <summary>
    /// Typed objects built once from the config at startup and registered by the platform as singletons (FG-018).
    /// Do not register them in ConfigureServices.
    /// </summary>
    public IReadOnlyList<GameServer.Config.ConfigCatalogDeclaration> ConfigCatalogs { get; init; } = [];
}
```

### 1.2 Config declarations (`GameServer.Config`)

```csharp
namespace GameServer.Config;

public interface IGameConfig
{
    // existing members unchanged

    /// <summary>
    /// <c>"{label}+{hash12}"</c> of what a client receives: the label and the client-visible files (FG-024, FG-019).
    /// Carried as <c>client_config_version</c> on player responses; never equal to <see cref="ConfigVersion"/>.
    /// </summary>
    string ClientConfigVersion { get; }
}

/// <summary>One config file a module reads (FG-018). A file named by several modules is checked by each one's rules.</summary>
public sealed class ConfigFileDeclaration
{
    /// <summary>The file must be in the effective config; a missing file fails startup (ARC-06).</summary>
    public static ConfigFileDeclaration Required(string name, params ConfigRule[] rules);

    /// <summary>The rules run only when the file is in the effective config.</summary>
    public static ConfigFileDeclaration Optional(string name, params ConfigRule[] rules);

    public string Name { get; }

    public bool IsOptional { get; }

    public IReadOnlyList<ConfigRule> Rules { get; }
}

/// <summary>Checks the content of one declared file and reports through <see cref="ConfigContext.Problems"/>; never throws.</summary>
public delegate void ConfigRuleCheck(ConfigContext context);

public sealed class ConfigRule
{
    /// <param name="name">Shown when the rule throws, e.g. <c>economy.resources</c>.</param>
    public ConfigRule(string name, ConfigRuleCheck check);

    public string Name { get; }

    public ConfigRuleCheck Check { get; }
}

/// <summary>A config catalog (FG-018): built once at startup, after the rules, and shared by every handler and service.</summary>
public sealed class ConfigCatalogDeclaration
{
    /// <param name="build">Returns the catalog, or null after reporting why. Reads only the config; cross-file consistency belongs to rules.</param>
    /// <param name="files">Files the build reads: not built when one is missing or has rule errors.</param>
    public static ConfigCatalogDeclaration Of<T>(Func<ConfigContext, T?> build, params string[] files)
        where T : class;

    public Type CatalogType { get; }

    public IReadOnlyList<string> Files { get; }

    /// <summary>Runs the build function. Called by the platform once per start (and by the test kit).</summary>
    public object? Build(ConfigContext context);
}

/// <summary>What a rule or a catalog build sees. Valid only during the call.</summary>
public sealed class ConfigContext
{
    internal ConfigContext(/* framework only */);

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
    public System.Text.Json.JsonElement Json(string file);
}

/// <summary>Problem sink bound by the framework to the declaring module and its file (rule) or catalog type (build).</summary>
public sealed class ConfigProblems
{
    internal ConfigProblems(/* framework only */);

    /// <summary>Fails the start: one report line (data-model §5.3). <paramref name="problem"/> and <paramref name="fix"/> without trailing period.</summary>
    public void Error(ConfigLocation at, string problem, string fix);

    /// <summary>Logged once with the startup warnings; does not fail the start.</summary>
    public void Warning(ConfigLocation at, string problem, string fix);

    public int ErrorCount { get; }
}

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
    public static ConfigLocation WholeFile { get; }

    /// <summary>1-based row of the file's row array.</summary>
    public static ConfigLocation Row(int row);

    public static ConfigLocation Key(string key);

    /// <summary>JSON path rooted at <c>$</c>.</summary>
    public static ConfigLocation Path(string jsonPath);

    /// <summary>Names another file (a catalog build, or a rule pointing into a second file).</summary>
    public ConfigLocation InFile(string file);

    public ConfigLocationKind Kind { get; }

    public string? Value { get; }

    /// <summary>Null: the rule's own file (rules) or no file (catalog builds).</summary>
    public string? File { get; }

    public bool Equals(ConfigLocation other);
}

/// <summary>
/// Small JSON readers with the framework's wording (FR-017): a missing, null or wrongly typed value reports one problem
/// naming the property and returns null/false. Unknown properties are never reported.
/// </summary>
public static class ConfigJson
{
    public static bool TryGetObject(System.Text.Json.JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, out System.Text.Json.JsonElement value);

    public static bool TryGetArray(System.Text.Json.JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, out System.Text.Json.JsonElement value);

    public static string? RequiredString(System.Text.Json.JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, bool allowEmpty = false);

    /// <summary>A JSON number readable as Int64 within [min, max]; 1.0, 1e3 and strings are problems.</summary>
    public static long? RequiredInteger(System.Text.Json.JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, long min = long.MinValue, long max = long.MaxValue);

    /// <summary>Absent → null without a problem; present but wrong → problem.</summary>
    public static long? OptionalInteger(System.Text.Json.JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, long min = long.MinValue, long max = long.MaxValue);

    /// <summary>Requires <paramref name="element"/> to be an object (row of an array); reports at <paramref name="at"/> otherwise.</summary>
    public static bool RequireObject(System.Text.Json.JsonElement element, ConfigLocation at, ConfigProblems problems);

    /// <summary><c>$.a.b</c>, <c>$.items['key with.dot']</c>, <c>$.rows[2]</c> (0-based index, as JSON paths are).</summary>
    public static string PathOf(params object[] segments);
}
```

`GameServer.Core.csproj` gains `<InternalsVisibleTo Include="GameServer.Platform" />`.

### 1.3 Example (economy, business project)

```csharp
public static ModuleDescriptor Describe() => new()
{
    Name = "economy",
    ConfigFiles = [ConfigFileDeclaration.Required(ResourceCatalog.ConfigFile, EconomyConfigRules.Resources)],
    ConfigCatalogs = [ConfigCatalogDeclaration.Of(c => ResourceCatalog.Load(c.Config), ResourceCatalog.ConfigFile)],
    // RequiredConfigFiles removed (redundant)
};

public static class EconomyConfigRules
{
    public static ConfigRule Resources { get; } = new("economy.resources", Check);
    // private static void Check(ConfigContext c) — rows, ids, kind, maxQuantity, displayName (data-model §2.2)
}
```

## 2. Platform (`GameServer.Platform`)

```csharp
namespace GameServer.Platform.Config;

public sealed class GameConfig : IGameConfig
{
    /// <summary>Computes both versions exactly like the host loader (FR-042).</summary>
    public GameConfig(string label, IReadOnlyDictionary<string, byte[]> files, IEnumerable<string> clientVisible);

    [Obsolete("The given configVersion may not hash clientVisible (FG-024). Use new GameConfig(label, files, clientVisible), which computes both versions like the host.")]
    public GameConfig(string label, string configVersion, IReadOnlyDictionary<string, byte[]> files, IReadOnlySet<string> clientVisible);

    public string ClientConfigVersion { get; }

    // existing members unchanged
}

public readonly record struct GameConfigVersionPair(string Server, string Client);

/// <summary>The host's version rules (data-model §1.3); TestGameConfig and GameConfig use them.</summary>
public static class GameConfigVersions
{
    public static GameConfigVersionPair Compute(string label, IReadOnlyDictionary<string, byte[]> files, IEnumerable<string> clientVisible);
}

public static class FileGameConfigLoader
{
    /// <summary>Every fault of the config in one <see cref="GameServerStartupException"/> (BC-3); line texts as before.</summary>
    public static GameConfig Load(string configRoot, string deploymentEnvironment);

    [Obsolete("Does not hash clientVisible (FG-024) and computes no client version. Use GameConfigVersions.Compute(label, files, clientVisible).")]
    public static string ComputeVersion(string label, IReadOnlyDictionary<string, byte[]> files);
}
```

```csharp
namespace GameServer.Platform;

public enum GameConfigState
{
    /// <summary>This mode does not load game config (provision), or the config phase has not run yet.</summary>
    NotLoaded,

    Loaded,

    /// <summary>The config did not load; its faults are already in the startup report.</summary>
    Failed,
}

/// <summary>
/// The game config as seen by startup code (FG-023). Never throws. A startup check that reads config takes this instead of
/// IGameConfig: a check taking IGameConfig is reported as not run when the config fails.
/// </summary>
public sealed class GameConfigStatus
{
    public GameConfigState State { get; }

    /// <summary>The IGameConfig instance handlers get; null unless <see cref="State"/> is Loaded.</summary>
    public IGameConfig? Config { get; }

    /// <summary>Loaded, and every required file, module config rule and catalog build of this start passed.</summary>
    public bool DeclarationsPassed { get; }

    public bool TryGetConfig([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IGameConfig? config);

    /// <summary>
    /// The declared catalog T built in this start (by its declaration or from a host/test registration of T); false when the
    /// config is not Loaded, T is not a declared catalog, or it was not built. Never throws. (T071 G1)
    /// </summary>
    public bool TryGetCatalog<T>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? catalog) where T : class;
}

/// <summary>
/// Contributes startup violations and warnings to the one startup report (FR-009). Each check is created and run in its
/// own fault isolation (FG-023); it must not throw, must not be disposable, and reads config through GameConfigStatus.
/// </summary>
public interface IStartupCheck
{
    void Check(StartupCheckResult result);
}

public sealed class GameServerOptions
{
    // existing members unchanged

    /// <summary>Compress GetConfig responses for clients that accept br/gzip (FG-019). Rollback switch.</summary>
    public bool ConfigCompression { get; set; } = true;
}
```

Internal (Platform): `GameConfigLoad`, `ConfigDeclarationRunner`, `ConfigDeclarationResult`, `ConfigCatalogStore`,
`DeclaredCatalogBindings`, `ConfigReportLines`, `Unavailable` (marker helpers). `ModuleRegistry.ValidateConfig` (internal)
uses the extended required set.

`PlatformMetrics.SetClientConfigVersion(string)` and gauge `client_config_version_info{version}`.

## 3. Platform.Web (`GameServer.Platform.Web`)

No new public type. Internal: `Hosting.ConfigStartup` (the phase), `Config.ConfigBundle`,
`Config.ConfigResponseCompression`, `Hosting.StartupCheckRegistrations`. `CheckAll(IServiceProvider, StartupCheckMode)`
stays internal. Behaviour: `GameServerBuilder.AddModule` registers declared catalogs (R7). `UseGameServer` runs the
phase (R10). The `GetConfig` handler uses `ConfigBundle` + compression. Login, snapshot, command, error envelope and
`GetConfig` set `ClientConfigVersion`.

## 4. Observability (`GameServer.Platform.Observability`)

`ConfigHealthCheck` data: `configVersion`, `clientConfigVersion`.

## 5. Consumer test kit (`GameServer.TestKit`)

```csharp
namespace GameServer.TestKit;

/// <summary>
/// Runs the config declarations (required files, rules, catalog builds) of modules over a config without a host or store
/// (FG-018, FR-018). Lines are exactly the ones the host reports.
/// </summary>
public static class ConfigDeclarationCheck
{
    public static ConfigDeclarationReport Check(IGameConfig config, params IGameModule[] modules);
}

public sealed class ConfigDeclarationReport
{
    public IReadOnlyList<string> Errors { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool Passed { get; }

    /// <summary>The built catalog; throws InvalidOperationException naming the lines when it was not built.</summary>
    public T Catalog<T>() where T : class;

    public bool TryGetCatalog<T>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? catalog) where T : class;

    /// <summary>Throws InvalidOperationException listing every error; returns this report otherwise.</summary>
    public ConfigDeclarationReport ThrowIfFailed();

    /// <summary>Registers every built catalog as a singleton instance (BC-8: services built from modules alone).</summary>
    public IServiceCollection AddCatalogsTo(IServiceCollection services);
}

public static class TestGameConfig
{
    // FromJson / FromDirectory / Empty unchanged in signature; versions now computed with GameConfigVersions (both)
}

/// <summary>A GameConfigStatus for unit tests of a startup check, in the state the host would publish (T071 G2).</summary>
public static class TestGameConfigStatus
{
    /// <summary>Provision mode, or before the config phase.</summary>
    public static GameConfigStatus NotLoaded();

    /// <summary>The config did not load.</summary>
    public static GameConfigStatus Failed();

    /// <summary>
    /// Loaded with the report's config: DeclarationsPassed = report.Passed, catalogs = the catalogs the report built
    /// (TryGetCatalog). Exactly what the host publishes for that config and those modules.
    /// </summary>
    public static GameConfigStatus From(ConfigDeclarationReport report);
}
```

`ConfigDeclarationReport` keeps the config it was made from (internal), so `From(report)` cannot mix a config and a report.
`GameConfigStatus` keeps its internal constructor: production code can never fake a status.
`LocalFlavorFactory<TEntryPoint>` (slice 013) throws the `GameServerStartupException` of a refused start from
`StartServer()`, `CreateClient()` and `Services` (internal marker `StartupFailureMode`; T071 G3).

## 6. Unity SDK (`sdk/unity/com.gameserver.client`, package 0.7.0, C# 9 / netstandard2.1)

```csharp
namespace GameServer.Client
{
    public sealed class GameServerClient   // additions only
    {
        /// <summary>
        /// Last client config version announced by the server (<c>client_config_version</c>), or its <c>config_version</c>
        /// when the server sends no client version (servers before 0.7.0). The config cache is keyed by this value.
        /// </summary>
        public string? ClientConfigVersion { get; }

        // unchanged signatures; documented meaning:
        // ServerConfigVersion  — last config_version (server config version, matches receipts)
        // ConfigOutdated       — ClientConfigVersion != null && ClientConfigVersion != Config.Version
        // ConfigVersionChanged — raised with ClientConfigVersion: "client-visible config changed"
        // RefreshConfigAsync   — stores the files under the response's client config version
    }

    /// <summary>
    /// IConfigCache that keeps the last client config version and its files on disk (FG-019): available before login at the
    /// next app start. One container file in <see cref="Folder"/>; saves are atomic; a damaged file reads as empty.
    /// Not thread-safe: use it from the thread the client runs on (the Unity main thread).
    /// </summary>
    public sealed class PersistentConfigCache : IConfigCache
    {
        public const string FileName = "gameserver-config.bin";

        /// <summary>Creates the folder when missing and loads the stored version. Never throws for file problems.</summary>
        public PersistentConfigCache(string folder);

        public string Folder { get; }

        public string? Version { get; }

        public IReadOnlyDictionary<string, byte[]> Files { get; }

        /// <summary>Why the cache started empty (truncated, corrupt, unknown format, unreadable); null when it loaded or no file existed.</summary>
        public string? LoadProblem { get; }

        /// <summary>The last save failure (disk full, permissions); null after a successful save. The data stays in memory.</summary>
        public string? WriteProblem { get; }

        /// <summary>Uses the new version in memory at once, then writes it atomically; never throws because of the disk.</summary>
        public void Store(string version, IReadOnlyDictionary<string, byte[]> files);
    }
}
```

`Runtime/Unity/UnityConfigCache.cs` (`#if UNITY_5_3_OR_NEWER`):

```csharp
namespace GameServer.Client
{
    public static class UnityConfigCache
    {
        /// <summary>
        /// <c>Application.persistentDataPath/gameserver-config/{environmentName}</c>: one folder per server environment; on iOS the
        /// folder is excluded from iCloud/iTunes backup. <paramref name="environmentName"/>: 1 to 64 characters of <c>a-z</c>,
        /// <c>0-9</c>, <c>.</c>, <c>_</c>, <c>-</c>; not starting or ending with <c>.</c>; the part before the first <c>.</c> is not
        /// a Windows device name (<c>con</c>, <c>prn</c>, <c>aux</c>, <c>nul</c>, <c>com1</c>–<c>com9</c>, <c>lpt1</c>–<c>lpt9</c>);
        /// the same rule on every platform (never <c>Path.GetInvalidFileNameChars()</c>). Null → ArgumentNullException; any other
        /// invalid value → ArgumentException: "'{environmentName}' is not a valid config cache name. Fix: use 1 to 64 lowercase
        /// letters, digits, '.', '_' or '-', not starting or ending with '.' and not a Windows device name (con, nul, com1, …), for
        /// example 'local', 'live' or 'localhost-5080'." (T071 U1, m8)
        /// </summary>
        public static PersistentConfigCache Create(string environmentName);
    }
}
```

Internal: `CacheFileOps` (fault-injection seam), `PersistentConfigCache.FolderNameProblem(name)` (the cache-name rule, tested
outside Unity). New `Runtime/Core/AssemblyInfo.cs` with
`[assembly: InternalsVisibleTo("GameServer.Client.Tests")]`. Generated `Runtime/Generated/gameserver/core/v1/Core.cs` and
`Runtime/Generated/Identity.cs` gain the `ClientConfigVersion` properties (regenerated by WireGen; paths and `.meta`
unchanged). New files: `PersistentConfigCache.cs`, `AssemblyInfo.cs`, `UnityConfigCache.cs`, each with a committed `.meta`.

Consumer setup (first consumer, `OnlineSession.cs:83`):

```csharp
var client = new GameServerClient(options, new UnityWebRequestTransport(), config: UnityConfigCache.Create(environmentName));
```
