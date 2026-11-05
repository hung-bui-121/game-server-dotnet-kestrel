# Contract: public API of slice 017 (gm-api-contract)

Signatures only (XML docs in the code). Everything is additive; no existing public member changes signature or
meaning. Internals named here are for the plan, not public.

## 1. Core — `GameServer.Errors`

```csharp
public sealed class GameServerException : Exception
{
    // existing: ctor(ErrorCode code, string? detail = null, Exception? inner = null), Code, Detail, RetryAfterMs, Details

    /// <summary>
    /// Field errors of an invalid JSON body (JSON path → messages), written as the Problem Details member <c>errors</c> of a
    /// GM endpoint; ignored by the Protobuf error envelope. Messages must be safe to show (API-04).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors { get; init; }
}
```

## 2. Platform.Web — `GameServer.Platform.Web.Endpoints` (new file `GmPresentation.cs`)

```csharp
public enum GmConfirm
{
    Simple,
    Typed,
}

public sealed record GmConfirmMetadata(GmConfirm Confirm);

public sealed record GmPagedMetadata(Type ItemType);

public sealed record GmQueryParameterMetadata(string Name, bool Required, int? MinLength = null, int? MaxLength = null, string? Description = null);

/// <summary>Marker: the endpoint requires the <c>X-Request-Id</c> UUID header (documented as a required header).</summary>
public sealed class GmRequestIdMetadata
{
    public static GmRequestIdMetadata Instance { get; }
}

/// <summary>The property's values come from the GM GET route named <see cref="RouteName"/> (<c>x-gm-options</c>).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class GmOptionsAttribute : Attribute
{
    public GmOptionsAttribute(string routeName);

    public string RouteName { get; }
}

public sealed record GmOption(string Value, string Label);

public sealed record GmOptionList(IReadOnlyList<GmOption> Items);

public static class GmPresentationExtensions
{
    public static TBuilder WithGmConfirm<TBuilder>(this TBuilder builder, GmConfirm confirm)
        where TBuilder : IEndpointConventionBuilder;

    /// <summary>Produces&lt;GmPage&lt;T&gt;&gt;; documents required <c>pageSize</c> (1..200) and optional <c>cursor</c>; <c>x-gm-paged</c>.</summary>
    public static RouteHandlerBuilder ProducesGmPage<T>(this RouteHandlerBuilder builder);

    /// <summary>Documents a query parameter the handler reads by hand (the handler keeps its own check).</summary>
    public static TBuilder WithGmQueryParameter<TBuilder>(this TBuilder builder, string name, bool required, int? maxLength = null, int? minLength = null)
        where TBuilder : IEndpointConventionBuilder;

    /// <summary>Documents the required <c>X-Request-Id</c> header of a GM mutation mapped outside <c>MapGmCommand</c>.</summary>
    public static TBuilder WithGmRequestId<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder;
}
```

Shape change from design §5.2: `ProducesGmPage<T>` is an extension of `RouteHandlerBuilder` with one type argument
(C# cannot infer `TBuilder` when `T` is explicit; every GM list is mapped with `MapDeclared`, which returns
`RouteHandlerBuilder`). `WithGmRequestId` is new (spec FR-006). `GmQueryParameterMetadata` gains `MinLength`.

`GmHttp` (existing static class, same namespace):

```csharp
/// <summary>Reads a bounded JSON body; since slice 017 also validates its DataAnnotations (400 with <c>errors</c>).</summary>
public static Task<T> ReadJsonAsync<T>(HttpContext context);   // signature unchanged, behaviour extended (FR-024)
```

Internal (not public): `GmBodyContract` (per-type attribute walk over the JSON contract, research R2),
`GmBodyValidator` (R2/R3), `GmOptionsCheck` (startup lines, R12).

## 3. Platform.Web — `GameServer.Platform.Web.Commands`

```csharp
public static class GmCommandEndpointFactory
{
    // existing four MapGmCommand overloads: signatures unchanged. Since slice 017 each endpoint also gets
    // Accepts<TBody>("application/json"), GmRequestIdMetadata, and validation of TBody before toDomain (FR-003, FR-020).
}
```

`IGmCommandBody`, `IGmTargetValidator`, `GmCommandResponse`, `GmAggregateUpdate`, `GmUnavailableAggregate`: unchanged.

## 4. Platform.Web — `GameServer.Platform.Web.OpenApi`

```csharp
public static class GmOpenApi
{
    public const string DocumentName = "gm";                                 // unchanged
    public const string Route = "/gm/v1/openapi/{documentName}.json";        // unchanged (dev route)
    public const string DocumentPath = "/gm/v1/openapi/gm.json";             // unchanged

    /// <summary>Registers the GM document (every environment since slice 017) with the GM transformers.</summary>
    public static void Add(IServiceCollection services);                     // signature unchanged

    /// <summary>Maps the dev route (Development/Test only, Public).</summary>
    public static void Map(IEndpointRouteBuilder routes);                    // unchanged
}

/// <summary>The canonical bytes of the host's GM document, generated once per process (catalog route, test kit).</summary>
public sealed class GmCatalogDocument
{
    public Task<GmCatalogBytes> GetAsync(CancellationToken cancellationToken);
}

public sealed record GmCatalogBytes(ReadOnlyMemory<byte> Bytes, string ETag);
```

`GmCatalogDocument` is public because the test kit (a separate assembly) reads it; it is registered by
`GameServerBuilder` in every environment.

## 5. Gm module — `GameServer.Modules.Gm` (Gm.Adapters)

```csharp
/// <summary>Pre-login answer (Public, allowlisted by declaration, SEC-09): which sign-in buttons the login page shows.</summary>
public sealed record GmAuthMethods(bool Local, bool Sso);

public sealed record GmMeResponse(string StaffId, string DisplayName, string GameId, string EnvironmentId, IReadOnlyList<string> Permissions)
{
    /// <summary>Framework informational version of the answering instance (as <c>GET /gm/v1/ops/version</c>).</summary>
    public string FrameworkVersion { get; init; } = "";

    /// <summary>Game informational version of the answering instance (<c>unknown</c> when unreadable).</summary>
    public string GameVersion { get; init; } = "";
}

public sealed class GmModule
{
    public const string AuthMethodsRoute = "/gm/v1/auth/methods";            // new
    public const string ConsoleCatalogRoute = "/gm/v1/console/openapi.json"; // new
}

public static class GmRoutes
{
    public static RouteDeclaration AuthMethods { get; }      // gm.auth_methods, GET, Public
    public static RouteDeclaration ConsoleCatalog { get; }   // gm.console_catalog, GET, Gm, gm.access
    // All: + AuthMethods, ConsoleCatalog (29 routes)
}
```

## 6. Identity — `GameServer.Modules.Identity` (Identity.Adapters)

```csharp
public static class IdentityRoutes
{
    public static RouteDeclaration BanScopes { get; }        // identity.ban_scopes, GET /gm/v1/players/ban-scopes, Gm, player.ban
}

public sealed record BanBody(
    string? Reason,
    DateTimeOffset? UntilUtc,
    [property: GmOptions("identity.ban_scopes")] List<string>? Scope) : IGmCommandBody;   // attribute added
```

Internal: `BanScopes` (singleton set: `login` + command groups, used by the ban `toDomain` and the option route).

## 7. Economy — `GameServer.Modules.Economy`

```csharp
public static class EconomyRoutes
{
    public const string ResourceOptionsTemplate = "/gm/v1/economy/resources";
    public static RouteDeclaration ResourceOptions { get; }  // economy.resource_options, GET, Gm, player.read
}

public sealed record AdjustResourceBody(
    [property: Required, GmOptions("economy.resource_options")] string? Resource,
    long Delta,
    string? Reason) : IGmCommandBody;                       // attributes added
```

## 8. Test kit — `GameServer.TestKit`

```csharp
/// <summary>CI-01 OpenAPI diff for a game: writes or compares the committed GM document of a started host.</summary>
public static class GmOpenApiSnapshot
{
    /// <summary>The canonical bytes the catalog route serves (UTF-8 text).</summary>
    public static Task<string> GetDocumentAsync(IServiceProvider services, CancellationToken cancellationToken = default);

    /// <summary>Writes the document to <paramref name="committedPath"/> (folder created; LF; final newline).</summary>
    public static Task WriteAsync(IServiceProvider services, string committedPath, CancellationToken cancellationToken = default);

    /// <summary>Empty when equal (line ends ignored); otherwise the first differing line and how to rewrite the file.</summary>
    public static Task<string> DiffAsync(IServiceProvider services, string committedPath, CancellationToken cancellationToken = default);

    public static Task WriteAsync(TestHost host, string committedPath, CancellationToken cancellationToken = default);

    public static Task<string> DiffAsync(TestHost host, string committedPath, CancellationToken cancellationToken = default);
}
```

Shape change from design §5.2: the main overloads take the host's `IServiceProvider`, so a game uses
`LocalFlavorFactory<Program>.Services` (its real host) as well as `TestHost`. Uses only public framework API
(`GmCatalogDocument`), so the kit's internal allow-list does not grow.

## 9. Sample game (`SampleGame.Adapters`) and fixture

```csharp
public sealed record SetProgressionBody(
    [property: Range(1, int.MaxValue)] int? Level,
    [property: Range(0, long.MaxValue)] long? Xp,
    [property: Range(1, int.MaxValue)] int? Stage,
    string? Reason) : IGmCommandBody;
```

Fixture (`tests/ConsumerFixture/src/Fixture.Game`): `SetNoteBody`, `SetNoteHandler` (`fixture.note_set`), `FixtureRoutes.NoteSet`,
`FixtureRoutes.NotePresets`, permission `fixture.note.edit` (research R10).
