# Public API — 014 wire-contract

Signatures only (no bodies). Everything here is additive except the `[Obsolete]` mark (BC-3) and the behaviour changes
listed as BC-1..BC-6 in the plan. Internal types are named where a reviewer needs them; they are not consumer API.

## 1. Core (`GameServer.Core`)

```csharp
namespace GameServer.Routing;

/// <summary>Where a route is mapped from (FR-001): decides whether an unmapped declaration is a fault and what Unity gets.</summary>
public enum RouteSurface
{
    /// <summary>Mapped by <c>IPlayerApiModule.MapPlayerApi</c> or the platform when <c>Hosting:MapPlayerApi</c> is on.</summary>
    PlayerApi,

    /// <summary>Mapped by <c>IGmApiModule.MapGmApi</c> or the platform when <c>Hosting:MapGmApi</c> is on. Never generated for Unity.</summary>
    GmApi,

    /// <summary>Mapped by every host (health probes, server time, host-only endpoints).</summary>
    Host,
}

/// <summary>Well-known hosting condition names; modules register their own (Platform.Web <c>AddRouteCondition</c>).</summary>
public static class RouteConditions
{
    /// <summary>Holds where development tools are allowed (Development/Test).</summary>
    public const string DevelopmentTools = "development_tools";
}

/// <summary>
/// One HTTP route as data (FG-026): written once next to the module's commands, read by mapping code, the endpoint check,
/// the request log and code generation. Constructing never validates; the host reports invalid declarations at startup.
/// </summary>
public sealed class RouteDeclaration
{
    public RouteDeclaration(
        string name,
        IReadOnlyList<string> methods,
        string template,
        SecurityProfile profile,
        RouteSurface surface,
        string? operation = null,
        string? permission = null,
        string? condition = null);

    /// <summary>Stable route name, <c>&lt;area&gt;.&lt;route&gt;</c> (rule in PCR platform §20); renaming is a breaking change.</summary>
    public string Name { get; }

    /// <summary>Upper-case HTTP methods, ordinal-sorted; empty = any method.</summary>
    public IReadOnlyList<string> Methods { get; }

    /// <summary>Route template with exactly one leading <c>/</c>.</summary>
    public string Template { get; }

    public SecurityProfile Profile { get; }

    public RouteSurface Surface { get; }

    /// <summary>Command operation run by the route; null for non-command routes.</summary>
    public string? Operation { get; }

    /// <summary>GM permission (profile Gm only).</summary>
    public string? Permission { get; }

    /// <summary>Hosting condition name; null = mapped whenever its surface is mapped.</summary>
    public string? Condition { get; }

    public static RouteDeclaration Get(string name, string template, SecurityProfile profile, RouteSurface surface = RouteSurface.PlayerApi, string? permission = null, string? condition = null);

    public static RouteDeclaration Post(string name, string template, SecurityProfile profile, RouteSurface surface = RouteSurface.PlayerApi, string? permission = null, string? condition = null);

    public static RouteDeclaration Put(string name, string template, SecurityProfile profile, RouteSurface surface = RouteSurface.PlayerApi, string? permission = null, string? condition = null);

    public static RouteDeclaration Delete(string name, string template, SecurityProfile profile, RouteSurface surface = RouteSurface.PlayerApi, string? permission = null, string? condition = null);

    /// <summary>Any HTTP method (e.g. a health probe mapped with <c>MapHealthChecks</c>).</summary>
    public static RouteDeclaration AnyMethod(string name, string template, SecurityProfile profile, RouteSurface surface, string? condition = null);

    /// <summary>
    /// <c>POST {template}</c> running <paramref name="command"/>: name = operation unless given, profile and permission from the
    /// descriptor, surface <see cref="RouteSurface.GmApi"/> for GM commands, else <see cref="RouteSurface.PlayerApi"/>.
    /// </summary>
    public static RouteDeclaration Command(GameServer.Commands.CommandDescriptor command, string template, string? name = null, string? condition = null);

    public override string ToString();   // "POST /api/v1/battle/start (battle.start)"
}
```

```csharp
namespace GameServer.Modules;

public sealed class ModuleDescriptor
{
    // … existing members …

    /// <summary>HTTP routes of the module (FG-026). Public declarations also count as public endpoints (SEC-09).</summary>
    public IReadOnlyList<GameServer.Routing.RouteDeclaration> Routes { get; init; } = [];

    /// <summary>Still accepted (SEC-09); a listed route whose declaration has another profile fails startup (FR-006).</summary>
    public IReadOnlyList<string> PublicEndpoints { get; init; } = [];
}
```

Framework route classes (static, one `public static RouteDeclaration X { get; }` per row of data-model §1.3; existing
`public const string …Route` constants stay):

| Class | Assembly / namespace |
| --- | --- |
| `IdentityRoutes` | `GameServer.Modules.Identity.Adapters` |
| `IapRoutes` (+ `const string PurchaseTemplate`; `IapModule.PurchaseRoute` becomes an alias) | `GameServer.Modules.Iap` |
| `EconomyRoutes`, `CheatRoutes` (+ template consts; `EconomyModule.AdjustRoute/LedgerRoute`, `CheatModule.GrantRoute` become aliases) | `GameServer.Modules.Economy` |
| `GmRoutes` | `GameServer.Modules.Gm.Adapters` |
| `ObservabilityRoutes` (`Live`, `Ready`) | `GameServer.Platform.Observability` |

## 2. Platform.Web (`GameServer.Platform.Web`)

### 2.1 Platform routes and declared mapping

```csharp
namespace GameServer.Platform.Web.Routing;

/// <summary>Routes the platform maps itself (owner "platform"). Consumers read them instead of copying strings (FG-026 (4)).</summary>
public static class PlatformRoutes
{
    public static RouteDeclaration Time { get; }
    public static RouteDeclaration Snapshot { get; }
    public static RouteDeclaration Config { get; }
    public static RouteDeclaration Receipt { get; }
    public static RouteDeclaration GmOpenApi { get; }
    public static IReadOnlyList<RouteDeclaration> All { get; }
}

/// <summary>The declaration an endpoint was mapped from.</summary>
public sealed record RouteDeclarationMetadata(RouteDeclaration Declaration);

public static class DeclaredRouteExtensions
{
    /// <summary>Maps <paramref name="route"/>'s methods and template to <paramref name="handler"/> and tags the endpoint. The profile helper (AsPlayerEndpoint …) is still called by the caller; the startup check compares it with the declaration.</summary>
    public static RouteHandlerBuilder MapDeclared(this IEndpointRouteBuilder routes, RouteDeclaration route, Delegate handler);

    /// <summary>Tags an endpoint mapped by a library helper (MapHealthChecks, MapOpenApi) with its declaration.</summary>
    public static TBuilder WithDeclaration<TBuilder>(this TBuilder builder, RouteDeclaration route)
        where TBuilder : IEndpointConventionBuilder;
}
```

### 2.2 Command mapping overloads (existing string overloads unchanged)

```csharp
namespace GameServer.Platform.Web.Commands;

public static class CommandEndpointFactory
{
    public static IEndpointConventionBuilder MapPlayerCommand<TWireRequest, TRequest, TResult>(this IEndpointRouteBuilder routes, RouteDeclaration route, Func<TWireRequest, TRequest> toDomain, Func<TResult, IMessage> toWire)
        where TWireRequest : IMessage<TWireRequest>, new() where TRequest : notnull;

    public static IEndpointConventionBuilder MapPlayerCommand<TWireRequest, TRequest, TResult>(this IEndpointRouteBuilder routes, RouteDeclaration route, Func<TWireRequest, HttpContext, Task<TRequest>> toDomainAsync, Func<TResult, IMessage> toWire)
        where TWireRequest : IMessage<TWireRequest>, new() where TRequest : notnull;

    public static IEndpointConventionBuilder MapPlayerCommand<TWireRequest, TRequest, TResult>(this IEndpointRouteBuilder routes, RouteDeclaration route, Func<TWireRequest, TRequest> toDomain, Func<TResult, IMessage> toWire, Func<CommandExecution, HttpContext, Task> afterExecute)
        where TWireRequest : IMessage<TWireRequest>, new() where TRequest : notnull;

    public static IEndpointConventionBuilder MapDevCommand<TWireRequest, TRequest, TResult>(this IEndpointRouteBuilder routes, RouteDeclaration route, Func<TWireRequest, TRequest> toDomain, Func<TResult, IMessage> toWire)
        where TWireRequest : IMessage<TWireRequest>, new() where TRequest : notnull;
}

public static class GmCommandEndpointFactory
{
    /// <summary>Operation and permission from <paramref name="route"/>; the template must contain {playerId}.</summary>
    public static IEndpointConventionBuilder MapGmCommand<TBody, TRequest, TResult>(this IEndpointRouteBuilder routes, RouteDeclaration route, Func<TBody, TRequest> toDomain)
        where TBody : IGmCommandBody where TRequest : notnull;

    public static IEndpointConventionBuilder MapGmCommand<TBody, TRequest, TResult>(this IEndpointRouteBuilder routes, RouteDeclaration route, Func<TBody, TRequest> toDomain, Func<CommandExecution, HttpContext, Task> afterExecute)
        where TBody : IGmCommandBody where TRequest : notnull;
}
```

All six throw `ArgumentException` when `route.Operation` is null (GM: also when `route.Permission` is null).

### 2.3 Catalog

```csharp
namespace GameServer.Platform.Web.Catalog;

public enum CatalogOrigin { Framework, Game }

public sealed record RouteEntry(RouteDeclaration Declaration, string Owner, CatalogOrigin Origin, bool ServiceOnly)
{
    public const string PlatformOwner = "platform";
    public const string HostOwner = "host";
}

public sealed record SectionEntry(
    string Id,
    string Owner,
    CatalogOrigin Origin,
    string? MessageFullName,
    IReadOnlyList<string> HiddenMembers,
    IReadOnlyList<string> MayStayDefaultFields);

/// <summary>Read-only route and section catalog of a host (FG-026, FG-028). DI singleton, available after Build() without starting.</summary>
public sealed class WireCatalog
{
    public IReadOnlyList<RouteEntry> Routes { get; }          // ordinal by name
    public IReadOnlyList<SectionEntry> Sections { get; }      // ordinal by id
    public bool TryGetRoute(string name, [NotNullWhen(true)] out RouteEntry? entry);
    public bool TryGetSection(string id, [NotNullWhen(true)] out SectionEntry? entry);

    /// <summary>Name of the declaration for a method and template (any-method declarations match every method); null if none.</summary>
    public string? FindRouteName(string method, string template);

    /// <summary>Declaration faults (data-model §2.1) as report lines; empty when valid.</summary>
    public IReadOnlyList<string> Validate(GameServerOptions options);

    /// <summary>Deterministic export, format <see cref="WireCatalogExport.Format"/> (data-model §3).</summary>
    public string ToExportJson();
}

public static class WireCatalogExport
{
    public const string Format = "gameserver.wire-contract/1";
}

/// <summary>A named hosting condition (FR-001); declarations reference it by name.</summary>
public sealed record RouteConditionRegistration(string Name, Func<IServiceProvider, bool> Holds);

public static class RouteConditionServiceCollectionExtensions
{
    public static IServiceCollection AddRouteCondition(this IServiceCollection services, string name, Func<IServiceProvider, bool> holds);
}
```

Internal: `WireCatalogBuilder`, `WireCatalogRules`, `HostRouteDeclarations`.

### 2.4 Validation and hosting

```csharp
namespace GameServer.Platform.Web.Validation;

/// <summary>Mapped endpoints ↔ catalog (FR-010): one report line per fault (data-model §2.2).</summary>
public static class EndpointCatalogValidator
{
    public static IReadOnlyList<string> Validate(IEnumerable<Endpoint> endpoints, WireCatalog catalog, EndpointCheckContext context, bool includeUnmapped);
}

/// <summary>What the check needs to know about the host: the mapped surfaces and whether a hosting condition holds (service-only owners come from RouteEntry.ServiceOnly, T086).</summary>
public sealed record EndpointCheckContext(bool MapPlayerApi, bool MapGmApi, Func<string, bool> ConditionHolds);
```

```csharp
namespace GameServer.Platform.Web.Hosting;

public sealed class GameServerBuilder
{
    // … existing members …

    /// <summary>Routes the host maps itself outside any module (owner "host"). Public host routes still need GameServer:PublicEndpointAllowlist.</summary>
    public GameServerBuilder DeclareRoutes(params RouteDeclaration[] routes);
}

public sealed class ExtensionRegistry
{
    // … ReplacePolicy, ReplaceHandler …

    /// <summary>
    /// Replaces the wire mapper of <paramref name="aggregateId"/> (the documented way since two mappers fail startup, BC-4). Must keep the
    /// message of the typed mapper it replaces (startup line otherwise, FG-021, T086): a new payload needs a new aggregate.
    /// </summary>
    public ExtensionRegistry ReplaceWireMapper<TState, TWire>(string aggregateId, Func<TState, TWire> map, Action<WireMapperOptions>? configure = null)
        where TState : class
        where TWire : class, IMessage<TWire>, new();
}

public enum HostMode
{
    Serve,
    Provision,

    /// <summary>Write the catalog export and exit (<c>--export-wire-contract &lt;path&gt;</c>); never serves or opens the store.</summary>
    ExportWireContract,
}

public sealed record GameServerCommandLine(HostMode Mode, string[] HostArgs)
{
    public const string ProvisionSwitch = "--provision";
    public const string ExportWireContractSwitch = "--export-wire-contract";

    /// <summary>Target file of <see cref="HostMode.ExportWireContract"/>; null otherwise.</summary>
    public string? ExportPath { get; init; }

    public static GameServerCommandLine Parse(string[] args);
}
```

Internal: `EndpointValidation` (runs `EndpointSecurityValidator`, `CommandRouteValidator`, `EndpointCatalogValidator` over a
set), `EndpointValidationState`, `EndpointSetValidationService : IHostedLifecycleService`.

### 2.5 Wire mappers

```csharp
namespace GameServer.Platform.Web.Aggregates;

/// <summary>Static wire contract of a typed mapper (FG-028): what the catalog and the conformance check read.</summary>
public interface IAggregateWireContract
{
    string AggregateId { get; }
    Type StateType { get; }
    Google.Protobuf.Reflection.MessageDescriptor Message { get; }
    MessageParser Parser { get; }
    IReadOnlyList<string> HiddenMembers { get; }
    IReadOnlyList<string> MayStayDefaultFields { get; }
}

public sealed class AggregateWireMapper<TState, TWire> : IAggregateWireMapper, IAggregateWireContract
    where TState : class
    where TWire : class, IMessage<TWire>, new()
{
    public AggregateWireMapper(string aggregateId, Func<TState, TWire> map, WireMapperOptions? options = null);
    // IAggregateWireMapper + IAggregateWireContract members
    public TWire Map(TState state);
}

/// <summary>Declarations next to the mapper registration (FR-060).</summary>
public sealed class WireMapperOptions
{
    /// <summary>State members kept on the server on purpose: C# member names joined by '.', collection elements transparent.</summary>
    public WireMapperOptions Hidden(params string[] memberPaths);

    /// <summary>Proto fields whose meaningful value may be the default (enum zero): proto field names joined by '.'.</summary>
    public WireMapperOptions MayStayDefault(params string[] protoFieldPaths);
}

public sealed class AggregateWireMapperRegistry
{
    public AggregateWireMapperRegistry(IEnumerable<IAggregateWireMapper> mappers);   // keeps the first per id, records duplicates
    public bool Has(string aggregateId);
    public ByteString ToPayload(string aggregateId, object state);
    public bool TryGetContract(string aggregateId, [NotNullWhen(true)] out IAggregateWireContract? contract);
}

public static class AggregateWireMapperServiceCollectionExtensions
{
    /// <summary>Registers the wire mapping of a client-visible aggregate with its message type.</summary>
    public static IServiceCollection AddAggregateWireMapper<TState, TWire>(this IServiceCollection services, string aggregateId, Func<TState, TWire> map, Action<WireMapperOptions>? configure = null)
        where TState : class
        where TWire : class, IMessage<TWire>, new();

    [Obsolete("Use AddAggregateWireMapper<TState, TWire>(aggregateId, map) so the catalog and Unity know the section's message (FG-028). Kept through v0.7.x.")]
    public static IServiceCollection AddAggregateWireMapper<TState>(this IServiceCollection services, string aggregateId, Func<TState, IMessage> map);
}
```

Internal: `StateMembers` (member tree from `GameServerJson.Options`, data-model §7.1), visible to `GameServer.TestKit`.

### 2.6 Request outcome (FR-015)

```csharp
namespace GameServer.Platform.Web.Requests;

public interface IRequestOutcomeFeature
{
    // … existing members …

    /// <summary>Name of the matched catalog route (FG-026); null for unmatched paths.</summary>
    string? RouteName => null;
}
```

The framework's `RequestOutcome` implements it; request event property `RouteName`.

## 3. Module mapping example (consumer)

```csharp
// Business project (no ASP.NET, no Protobuf):
public static class GameRoutes
{
    public static RouteDeclaration InitAccount { get; } = RouteDeclaration.Command(InitAccountHandler.Descriptor, "/api/v1/account/init");
}

public static class GameModuleDefinition
{
    public static ModuleDescriptor Describe() => new()
    {
        Name = "zhpvp.game",
        Routes = [GameRoutes.InitAccount],
        // … aggregates, commands, error codes …
    };
}

// Adapter:
services.AddAggregateWireMapper<ProgressState, Wire.ProgressState>(
    GameAggregates.Progress,
    GameWireMapping.ToWire,
    o => o.Hidden("ActiveBattle.WinCash", "ActiveBattle.TrophyWin", "ActiveBattle.IsProtect", "ActiveBattle.ConfigVersion"));

routes.MapPlayerCommand<Wire.InitAccountRequest, InitAccountCommand, InitAccountResult>(
    GameRoutes.InitAccount,
    _ => new InitAccountCommand(),
    result => new Wire.InitAccountResult { StartingCash = result.StartingCash, EquippedGunId = result.EquippedGunId });
```

## 4. Consumer test kit (`GameServer.TestKit`)

```csharp
namespace GameServer.TestKit;

/// <summary>Wire-mapper conformance (FG-027): no unit-test framework, no host, no store (FR-066).</summary>
public static class WireConformance
{
    /// <summary>One client-visible aggregate of <paramref name="module"/>.</summary>
    public static WireConformanceReport Check(IGameModule module, string aggregateId, WireConformanceOptions? options = null);

    /// <summary>Every client-visible aggregate declared by <paramref name="modules"/> (their ConfigureServices run against a bare service collection).</summary>
    public static WireConformanceReport CheckModules(IEnumerable<IGameModule> modules, WireConformanceOptions? options = null);

    /// <summary>Every client-visible aggregate of a built host composition (e.g. TestHost.BuildApp(...).Services, LocalFlavorFactory.Services).</summary>
    public static WireConformanceReport CheckAll(IServiceProvider services, WireConformanceOptions? options = null);
}

public sealed class WireConformanceOptions
{
    /// <summary>Use <paramref name="state"/> as the sample (perturbations still apply to its JSON).</summary>
    public WireConformanceOptions WithSampleState(string aggregateId, object state);

    /// <summary>Sample value for a member the kit cannot fill (data-model §7.2).</summary>
    public WireConformanceOptions WithMemberValue(string aggregateId, string memberPath, object? value);
}

public enum WireConformanceProblem
{
    FieldAtDefault,
    MemberNotOnWire,
    HiddenMemberOnWire,
    MemberNotFillable,
    SampleNotSerializable,
    UntypedMapper,
    NoMapper,
}

public sealed record WireConformanceFinding(string AggregateId, WireConformanceProblem Problem, string Path, string Message);

public sealed class WireConformanceReport
{
    public IReadOnlyList<string> CheckedAggregates { get; }
    public IReadOnlyList<WireConformanceFinding> Findings { get; }
    public bool Passed { get; }

    /// <summary>Throws <see cref="WireConformanceException"/> with one line per finding when not passed.</summary>
    public void ThrowIfFailed();

    public override string ToString();
}

public sealed class WireConformanceException : Exception
{
    public WireConformanceException(WireConformanceReport report);
    public WireConformanceReport Report { get; }
}
```

Usage in a game test: `WireConformance.CheckModules([new EconomyModule(), new GameModule()]).ThrowIfFailed();`

## 5. Unity SDK (`sdk/unity/com.gameserver.client`)

```csharp
namespace GameServer.Client
{
    /// <summary>A section id bound to the parser of its message (generated code creates these; FG-028).</summary>
    public abstract class SectionKey
    {
        protected SectionKey(string id);
        public string Id { get; }
        public abstract MessageParser Parser { get; }
    }

    public sealed class SectionKey<T> : SectionKey
        where T : class, IMessage<T>
    {
        public SectionKey(string id, MessageParser<T> parser);
        public MessageParser<T> TypedParser { get; }
        public override MessageParser Parser { get; }
    }

    /// <summary>Section id → key; generated per origin (FrameworkSections.Table, GameSections.Table).</summary>
    public sealed class SectionTable
    {
        public SectionTable(params SectionKey[] keys);                  // duplicate id → ArgumentException
        public IReadOnlyCollection<SectionKey> Keys { get; }
        public bool TryGet(string id, out SectionKey key);
    }

    public enum SectionRead { Absent, Unknown, Parsed }

    public sealed class SectionStore
    {
        // … existing: SectionChanged, HandlerFailed, All, TryGet, RevOf, Get<T>(id, parser), Set, Apply, Clear …

        /// <summary>Adds a generated table; FrameworkSections.Table is registered by the constructor. Same id with another message type → ArgumentException.</summary>
        public void Register(SectionTable table);

        public bool IsKnown(string id);

        /// <summary>The section parsed with its key's parser; null when the client does not have it.</summary>
        public T? Get<T>(SectionKey<T> key) where T : class, IMessage<T>;

        /// <summary>Absent (not received), Unknown (no registered table knows the id: kept raw, not parsed) or Parsed.</summary>
        public SectionRead TryRead(string id, out IMessage? message);
    }

    public sealed class GameServerClient
    {
        public const string LoginRoute = FrameworkRoutes.IdentityLoginGuest;
        public const string ProviderLoginRoute = FrameworkRoutes.IdentityLoginProvider;
        public const string LinkRoute = FrameworkRoutes.IdentityLink;
        public const string LogoutRoute = FrameworkRoutes.IdentityLogout;
        public const string DeleteAccountRoute = FrameworkRoutes.IdentityRequestDeletion;
        public const string SnapshotRoute = FrameworkRoutes.PlatformSnapshot;
        public const string ConfigRoute = FrameworkRoutes.PlatformConfig;
        public const string TimeRoute = FrameworkRoutes.PlatformTime;
        // … everything else unchanged …
    }
}
```

`SectionKey<T>.TypedParser` has its own name because one class cannot both override the abstract non-generic `Parser`
and declare a same-named typed member; `Get<T>(SectionKey<T>)` uses it.
Generated (in `Runtime/Generated`, format in [generated-files.md](generated-files.md)): `FrameworkRoutes`,
`FrameworkSections`.

## 6. WireGen (tool, not a library)

No public .NET API; the contract is the CLI, the config file and the export format
([wiregen-cli.md](wiregen-cli.md)). `GameServer.WireGen.Tests` uses its internals through `InternalsVisibleTo`.
