# Research — 014 wire-contract

Decisions for [spec.md](spec.md). Requirement IDs refer to `Requirements/Game_Server_System_Requirements_v1.2.md`.
Signatures are contracts for the implementer; bodies are deliberately absent. Items marked **[VERIFY Vn]** are runtime or
tooling facts the implementer confirms on the pinned versions (.NET SDK 10.0.403, ASP.NET Core 10.0.x shared framework,
`Grpc.Tools` 2.84.0, `Google.Protobuf` 3.36.2) **before** coding against them; each has a stated fallback. The list is at
the end.

## Code facts this research relies on (verified 2026-11-03 on `014-wire-contract`, base `2999a20`)

- **Endpoint validation** runs only inside `UseGameServer` over `((IEndpointRouteBuilder)app).DataSources` at that moment
  (`GameServerApplicationExtensions.cs:113-119`): `EndpointSecurityValidator.Validate(endpoints, options, modulePublic ∪
  platformPublic, permissions)` and `CommandRouteValidator.Validate(endpoints, registry.Commands)`. The Public allowlist =
  `GameServer:PublicEndpointAllowlist` (default `["/v1/time"]`, `GameServerOptions.cs:38`) ∪ `ModuleDescriptor.PublicEndpoints`
  (identity `IdentityModule.cs:69`, GM `GmModule.cs:93`, observability `ObservabilityModule.cs:29`) ∪ the GM OpenAPI route
  when it is mapped.
- **Hosting order**: `SampleHost/Program.cs` and `tests/ConsumerFixture/src/Fixture.Host/Program.cs` map/`Use` everything
  before `app.RunGameServerAsync(mode)`; `ServeAsync` calls `UseGameServer` inside `RunGameServerAsync`
  (`GameServerRunExtensions.cs:25-50`), then `app.RunAsync()`; a `GameServerStartupException` thrown from host start is
  unwrapped by `FindStartupException` → stderr + exit 1. So in the common path every endpoint mapped in `Program` is
  visible to `UseGameServer`; "late" endpoints come from hosts calling `UseGameServer` themselves and from the kit
  `TestHost.afterUseGameServer` hook.
- **Existing `IHostedLifecycleService` users**: `KeyRingService` (`StartingAsync` loads the key ring from the store),
  `GmBootstrapper`, `AnalyticsFileSink` (takes file locks), `DrainDelayService`. Registration order: `AddGameServer`
  (KeyRingService via `SharedDataProtection.Add`, `DrainDelayService`), then modules (`GmBootstrapper`), then
  `AddAnalyticsFiles`.
- **Mapping helpers**: `MapPlayerCommand` ×3, `MapDevCommand` ×1 (`CommandEndpointFactory.cs`), `MapGmCommand` ×2
  (`GmCommandEndpointFactory.cs`, requires `{playerId}` and throws `ArgumentException` otherwise). All attach
  `CommandOperationMetadata(operation)`. Profile helpers `AsPlayerEndpoint`, `AsGmEndpoint(permission)`,
  `AsPublicEndpoint(protobuf)`, `AsPublicPlayerEndpoint`, `AsDevEndpoint` (`ApiModules.cs`) also add transport, gate,
  timeout and limiter metadata.
- **Framework routes** (all `public const string` except `CoreEndpoints`, which is `internal static`): see data-model §1.3.
  Conditional mapping: GM local login only `if (options.AllowsDevelopmentTools)` (`GmModule.cs:117`), GM SSO start/callback
  only `if (GmOptions.Oidc.IsConfigured)` (`GmModule.cs:129`), GM OpenAPI only when development tools are allowed and the
  GM API is mapped (`GameServerApplicationExtensions.cs:106-110`). `/health/live` is `MapHealthChecks` (no
  `HttpMethodMetadata`); `/gm/v1/openapi/{documentName}.json` is `MapOpenApi`. Inline literals with no constant:
  `CounterModule.cs:39,44,60`, `TemplateModule.cs:26`. GM staff list and create share `/gm/v1/staff` (GET/POST).
- **Module descriptors**: identity, GM, IAP and observability build `ModuleDescriptor` in the adapter; economy, cheat,
  sample counter, sample game and the template in the business project. Operation names are `<area>.<verb>` with
  underscores (`identity.request_deletion`, `battle.start`); module names may contain dots (`sample.game`,
  `economy.cheat`, `platform.observability`).
- **Wire mappers**: `AddAggregateWireMapper<TState>(id, Func<TState, IMessage>)` registers an instance
  `IAggregateWireMapper` singleton; `AggregateWireMapperRegistry` keeps the last per id (`AggregateWireMapperRegistry.cs:26-29`).
  Registrations: economy `wallet`, `inventory`; identity `profile`; sample `progression`, `battle`, `counter`; template
  `template`. The client-visible-has-mapper check runs only when `Hosting:MapPlayerApi` (`:301-306`).
- **State serialization**: `GameServerJson.Options` = Web defaults (camelCase), `DefaultIgnoreCondition = Never`,
  `JsonStringEnumConverter(camelCase)`, read-only. Game states use `init` properties, `required` members, positional
  records (`PendingChest(string Tier, int Chapter)`), `IReadOnlyDictionary`, `IReadOnlyList`, nullable records and
  `DateTimeOffset?` (zh-pvp `ProgressState.cs`, `ChestsState.cs`).
- **SDK**: `sdk/GameServer.Client/GameServer.Client.csproj` compiles `Runtime/**` except `Runtime/Generated/**` and
  regenerates the four framework protos into `Runtime/Generated` plus `sample_game.proto` into KestrelDemo
  (`CompileOutputs=false`) on every build (file timestamps change, bytes do not). `GameServerClient.cs:58-65` route
  constants (no leading slash); `SectionStore.Get<T>(id, parser)` (`:48-50`). Package files have `.meta` (MonoImporter
  template, e.g. `Iap.cs.meta`); `Runtime/Plugins/Google.Protobuf.dll` = 3.36.2.0. `GameServer.Client.Tests` references the
  SDK with alias `sdk`.
- **KestrelDemo**: `Assets/SampleGame/SampleGameClient.cs` is a `MonoBehaviour` (needs `UnityEngine`; cannot compile
  outside Unity); hand-copied `SampleGameRoutes` (`:164-169`), `"wallet"` (`:157-159`). asmdef `SampleGame.Client`
  references `GameServer.Client`. Generated file `Assets/SampleGame/Generated/SampleGame.cs` (header `source: sample_game.proto`).
- **Repo line endings**: `.gitattributes` `* text=auto eol=lf`. Committed generated files are LF.
- **First consumer generator** (`E:\ProjGits\zh-pvp-server\tools\UnityProtoGen\UnityProtoGen.csproj`): Grpc.Tools from the
  framework CPM, `ProtoRoot` = Protos folder (flat output `ZhpvpGame.cs`), import dirs `proto` + identity/economy/iap
  Protos, client-root guard target, Roslyn inline task LF→CRLF, `NormalizeDirectory` workaround for mixed separators,
  `dotnet clean` deletes the client file.
- **Architecture rule ARC-01** (`ArchitectureTests.cs:76-85`): business assemblies (incl. Core) must not depend on
  `Microsoft.AspNetCore`, `Couchbase`, `Google.Protobuf`, `Serilog`, `GameServer.Platform`. `SecurityProfile` and
  `CommandDescriptor` live in Core.
- **Kit**: `GameServer.TestKit` references Platform.Web, Platform.Testing, Identity and GM adapters; `InternalsVisibleTo
  GameServer.TestKit` exists on Platform only. `TestHost.StartAsync(modules, settings, beforeUseGameServer, …,
  afterUseGameServer, …, services)`. ~18 ad-hoc `Map*` calls in 8 framework test files map endpoints outside modules.

## R1 — `RouteDeclaration` is pure data in Core (FR-001, FR-003, FR-004, FR-008)

**Decision.** `GameServer.Core/Routing/RouteDeclaration.cs`: sealed class with `Name`, `Methods` (upper-case, empty = any),
`Template` (canonical, leading `/`), `Profile` (`SecurityProfile`), `Surface` (`RouteSurface.PlayerApi | GmApi | Host`),
`Operation?`, `Permission?` (GM), `Condition?` (named hosting condition). Factories `Get/Post/Put/Delete/AnyMethod` and
`Command(CommandDescriptor, template, name?, condition?)` (name = operation, method POST, profile/permission from the
descriptor, surface `GmApi` for Gm profile else `PlayerApi`). Constructors only canonicalize (leading slash, upper-case
methods); they never validate (validation produces aggregated report lines, R5). No owner field: the registry knows who
registered the declaration.

**Rationale.** Core already holds `SecurityProfile` and `CommandDescriptor`; strings and enums keep ARC-01 green; business
projects can declare routes next to commands. A `Command` factory makes the route of a command a function of its
descriptor, so profile, permission and operation are written once (FR-005).

**Alternatives.** Record with positional parameters (rejected: a positional record's equality would make two identical
declarations "equal" and hide duplicate-name faults; constructor overloads are clearer); declaration inside
`CommandRegistration` (rejected: non-command routes and FG-014 queries need the same type); template as a parsed object
(rejected: needs ASP.NET in Core).

**Trade-offs.** Permission is an extra field beyond the spec list; it lets `MapGmCommand(declaration, …)` drop its
permission argument and lets the check compare the GM permission (SEC-07).

## R2 — Where declarations live (FR-002, FR-003, FR-006)

**Decision.**
- Module: `ModuleDescriptor.Routes` (new `IReadOnlyList<RouteDeclaration>`, default `[]`).
- Platform: `GameServer.Platform.Web.Routing.PlatformRoutes` (time, snapshot, config, receipt, GM OpenAPI), owner
  `platform`.
- Observability: `ObservabilityRoutes` in `GameServer.Platform.Observability` (owner `platform.observability`).
- Framework modules: `IdentityRoutes`, `IapRoutes`, `EconomyRoutes`, `CheatRoutes`, `GmRoutes` next to each module's
  descriptor (identity and GM: adapter; IAP, economy and cheat: business project, as today). The existing `public const
  string …Route` members stay (public API). Where the descriptor is in the business project, the template string moves
  there as a `const` of the routes class and the adapter constant becomes an alias of it (`public const string
  PurchaseRoute = IapRoutes.PurchaseTemplate;`, same value): one string per route in the server code. Samples and the
  template follow the same rule.
- Host: `GameServerBuilder.DeclareRoutes(params RouteDeclaration[] routes)` → owner `host`.
- `PublicEndpoints` stays accepted; framework descriptors stop using it (their Public declarations count, FR-006).

**Alternatives.** One central framework catalog class (rejected: Core would reference every module; games could not add
to it); attributes on handlers (rejected: discovery, constitution II).

## R3 — Naming rule and canonical template (FR-007, FR-008, edge cases)

**Decision.** Name `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$`, length ≤ 64 (same shape as operations: at least two
segments). Command routes default to the operation. Template canonical = `"/" + template.TrimStart('/')`, compared
ordinally (case-sensitive) with the endpoint's `RoutePattern.RawText` canonicalized the same way. Parameter names and
constraints are part of the text. Valid template = `RoutePatternFactory.Parse` succeeds; version segment rule = the
existing `(^|/)v[0-9]+(/|$)` with the existing probe exceptions.

**Rationale.** Names are the PCR handle and the source of generated constant names (R13). Ordinal comparison keeps one
canonical text even though routing matches case-insensitively.

## R4 — Binding mapping code to declarations (FR-005)

**Decision.**
- `RouteDeclarationMetadata(RouteDeclaration Declaration)` endpoint metadata.
- Command helpers gain overloads replacing `(string pattern, string operation[, string permission])` with
  `(RouteDeclaration route)`; they throw `ArgumentException` when `route.Operation` is null (programming error, same style
  as the `{playerId}` check), map `route.Template`, and attach the metadata. Profile/permission conflicts between the
  declaration and the helper are reported by the check, not thrown.
- Non-command: `MapDeclared(this IEndpointRouteBuilder, RouteDeclaration, Delegate)` → `MapMethods(template, methods, …)`
  or `Map(template, …)` for any-method, + metadata; returns `RouteHandlerBuilder`. `WithDeclaration(this TBuilder,
  RouteDeclaration)` tags builders produced by library helpers (`MapHealthChecks`, `MapOpenApi`).
- Profile conventions (`As*Endpoint`) stay explicit at the call site for non-command routes.

**Alternatives.** `MapDeclared` applying the profile helper from `Profile` (rejected: Public alone has three variants —
Protobuf + gate, JSON, JSON + CSRF — so the helper would need extra switches that duplicate `As*Endpoint`).

**Trade-offs.** For non-command routes the profile appears twice (declaration, `As*Endpoint`); the check makes a mismatch a
startup failure, which is the property FR-005 needs.

## R5 — `WireCatalog`: build, origin, validation, export (FR-001, FR-007, FR-030, FR-050, FR-065)

**Decision.** `GameServer.Platform.Web.Catalog.WireCatalog` (public, immutable): `Routes` (`RouteEntry`: declaration,
owner, origin, owner service-only), `Sections` (`SectionEntry`: id, owner, origin, message full name or null, hidden
members, may-stay-default fields), lookups `TryGetRoute(name, out)`, `FindRouteName(method, template)`, `TryGetSection`.
Built lazily (DI singleton) by an internal builder from `ModuleRegistry` (declarations of every registered module,
service-only included and flagged), `PlatformRoutes.All`, `ObservabilityRoutes` through its module, `HostRouteDeclarations`
and `AggregateWireMapperRegistry` (client-visible aggregates of the registry that have a mapper; owner = module declaring
the aggregate). Origin = `Framework` when the owner is `platform` or the module type's assembly name starts with
`GameServer.`, else `Game` (host declarations are `Game`). `Validate(GameServerOptions)` returns report lines (data-model
§2.1) and is called by `UseGameServer`, `--provision` and the export mode. `ToExportJson()` writes the deterministic export
(data-model §3).

**Rationale.** One type serves the check, the export, codegen, the request log and docs; building after `Build()` from
registrations needs no store, config or secret.

**Alternatives.** A catalog per surface (rejected: names are global); validating while building and throwing (rejected:
one aggregated report, constitution II); origin by an explicit framework attribute (rejected: needs every framework
project to reference an attribute type; the `GameServer.` prefix is already the framework's assembly naming and is
documented as reserved).

## R6 — Hosting conditions (FR-001, FR-010, FR-011)

**Decision.** A condition is a name. Core `RouteConditions.DevelopmentTools = "development_tools"`. Platform.Web:
`services.AddRouteCondition(string name, Func<IServiceProvider, bool> holds)` registering `RouteConditionRegistration`;
`AddGameServer` registers `development_tools` (`GameServerOptions.AllowsDevelopmentTools`); `GmModule.ConfigureServices`
registers `gm.sso` (`GmOptions.Oidc.IsConfigured`). GM OpenAPI and GM local login use `development_tools`. A declaration
naming an unregistered condition fails startup (catalog validation). The check evaluates a condition only in the
pre-serving run.

**Alternatives.** Enum of known conditions (rejected: GM SSO is a module option the platform cannot know); boolean
"optional" (rejected: an absent route whose condition holds would go unreported).

## R7 — The endpoint check (FR-010, FR-011, FR-014, edge cases)

**Decision.** `EndpointCatalogValidator.Validate(IReadOnlyList<RouteEndpoint> endpoints, WireCatalog catalog,
EndpointCheckContext context, bool includeUnmapped)`:
1. Expand endpoints into (method | `*`, canonical template) pairs; `HttpMethodMetadata` absent → `*`.
2. Bind: endpoint with `RouteDeclarationMetadata` → its declaration (template compared); else exact pair → declaration;
   else template declared with other methods → "method differs"; else "not declared".
3. Compare profile (only when exactly one `SecurityProfileMetadata`; zero/several are already reported by
   `EndpointSecurityValidator`), operation (`CommandOperationMetadata` vs `Operation`), permission
   (`RequiredPermissionMetadata` vs `Permission`, Gm only).
4. `includeUnmapped`: every declaration pair without an endpoint pair, when its owner is registered and not service-only,
   its surface is mapped and its condition holds → "declared but not mapped".
5. Duplicate declarations are reported by catalog validation (FR-007), not here.
Exactly one line per fault; a metadata-bound template mismatch is one line (no extra "undeclared"/"unmapped"). A plain
string typo (no metadata) yields "not declared" + "declared but not mapped" (two lines, by construction).

Non-route endpoints are ignored (as the existing validators). Endpoints added after the first request are out of contract.

**Alternatives.** Comparing only by template (rejected: two methods on one template, edge case); requiring metadata on
every endpoint (rejected: FR-005 keeps plain strings possible).

## R8 — Validating the final endpoint set before listening (FR-012, FR-013, BC-2)

**Decision.** `EndpointSetValidationService : IHostedLifecycleService` (internal), registered by `AddGameServer` as the
**first** hosted service. `UseGameServer` runs every check that is not an endpoint check; when they all pass it stores the
application (`IEndpointRouteBuilder`) in `EndpointValidationState` (internal singleton) **without enumerating the
endpoints** (V16: the endpoint set is built once before serving, here). When they do not pass, `UseGameServer` also runs
the endpoint validators over the endpoints mapped so far (`includeUnmapped: false`) so the failing start keeps one complete
report (013 FR-009), and throws. `StartingAsync` enumerates
`state.Routes.DataSources.SelectMany(d => d.Endpoints)` and runs `EndpointValidation.Run(endpoints, services,
includeUnmapped: true)` — `EndpointSecurityValidator`, `CommandRouteValidator`, `EndpointCatalogValidator` with the same
inputs `UseGameServer` uses — and throws one `GameServerStartupException` with every line. `RunGameServerAsync` already turns
it into stderr + exit 1; `TestHost` / `WebApplicationFactory` see the exception from `StartAsync`. When `UseGameServer` was
never called (state empty) the service does nothing. No setting disables it (FR-013). The request-time Dev refusal (013)
stays. The 013 startup line ("Starting {GameId} …") moves from `UseGameServer` to the end of a passing `StartingAsync`, so
it still means "every startup validation passed" (013 FR-054).

**Rationale.** `IHostedLifecycleService.StartingAsync` (Microsoft.Extensions.Hosting 8+) runs for every lifecycle
service before any hosted service's `StartAsync`; `GenericWebHostService.StartAsync` is where ASP.NET Core builds the
request pipeline and starts the server, and `WebApplicationBuilder` registers it after the application's services. By the
time the host starts, `Program` has finished mapping. Registering first puts the check before `KeyRingService` I/O. [VERIFY V1]

**Alternatives and fallbacks.**
1. `IStartupFilter` registered in `AddGameServer` (runs inside `GenericWebHostService.StartAsync` while the pipeline is
   built, before `Server.StartAsync`) — fallback if V1 fails on 10.0.x.
2. First-request refusal: middleware first in the pipeline runs the same validation once; on violations it logs the report,
   answers 503 `INTERNAL` and calls `IHostApplicationLifetime.StopApplication()` (exit code 1 via `ServeAsync`). Never
   silent acceptance — last fallback.
3. `IServer` decoration (rejected: replaces Kestrel/TestServer registration, fragile).
4. `EndpointDataSource` change tokens (rejected: `RouteEndpointDataSource` returns a null change token).

**Trade-offs.** On a passing configuration the endpoint lines surface from the host start (`StartingAsync`), not from
`UseGameServer`: same text, same report, same exit code 1 through `ServeAsync`, still before any port is bound (V1). A caller
that runs `UseGameServer` and never starts the host gets no endpoint check — nothing serves, so nothing is exposed. The
endpoint set is built once before serving, as in 013; the validation itself costs about 1 ms for 217 endpoints (V2, V16).
Rejected for cost: running the endpoint validators in both places (committed first, f2be391) builds every endpoint once more
— about 2 ms each, 440 ms for 217 — and delays the first response by that much (V16). Rejected as fragile: replacing
`app.DataSources` with a snapshot of the built endpoints so routing reuses them (freezes data sources that have change
tokens and relies on the order inside `GenericWebHostService`; see V16, deferred).

## R9 — Host export mode (FR-030, FR-031)

**Decision.** `GameServerCommandLine.Parse` recognizes `--export-wire-contract <path>` (case-insensitive switch, path as the
next argument; both removed from `HostArgs`). Missing path, `--export-wire-contract=…`, or combined with `--provision` →
`GameServerStartupException` (exit 1). `HostMode.ExportWireContract`; `GameServerCommandLine.ExportPath`.
`RunGameServerAsync` → `ExportAsync`: resolve `WireCatalog`, `Validate(options)`; errors → stderr, exit 1; else write
`ToExportJson()` (UTF-8 without BOM, LF) to the path (directory must exist), dispose the app, exit 0. It never calls
`UseGameServer`, starts hosted services, resolves the store, loads game config or reads secrets. [VERIFY V4, V5]

**Rationale.** The real composition (flavor-dependent modules such as cheat, host declarations) is known only to the host;
reusing the `--provision` pattern keeps one host entry point.

**Alternatives.** Tool loads the host assembly and intercepts `Build()` like `dotnet ef` (rejected: dependency-loading
complexity, framework version coupling); catalog file maintained by hand (rejected: direction A).

## R10 — WireGen tool and configuration (FR-020, FR-021, FR-024..FR-027)

**Decision.** Console project `tools/GameServer.WireGen` (net10.0, framework build props, in `GameServer.slnx`), invoked
`dotnet run --project <framework>/tools/GameServer.WireGen -c Release -- generate|verify --config <wiregen.json>
[--output-root <dir>] [--catalog <export.json>] [--no-build] [--configuration <cfg>]`. All inputs live in a committed JSON
config (data-model §4), paths relative to the config file's folder and normalized with `Path.GetFullPath` (mixed
separators accepted). Build-time `AssemblyMetadata`: framework root, `Grpc.Tools` package root
(`GeneratePathProperty="true"` → `$(PkgGrpc_Tools)`), `Grpc.Tools`, `Google.Protobuf` and framework versions.
Framework import roots are always added: `proto/`, `src/GameServer.Modules.{Identity,Economy,Iap}.Adapters/Protos`, and
`Grpc.Tools` `build/native/include` (well-known types).

Write rules: output root, marker and directory must exist (nothing is created except subfolders inside the output
directory for the `by-root` layout); compare bytes and write only changed files (write to a temp file in the same folder,
then replace); never delete; never read, write or delete `.meta`. `verify` writes nothing and exits 2 listing each missing
or different file. Exit 1 for input/configuration/contract errors with the input and the fix.

**Alternatives.** MSBuild targets file to import (rejected as the primary path: the consumer would again own MSBuild
plumbing; a game may still wrap the CLI in an `Exec`); arguments only, no config (rejected: long, error-prone command lines
and no committed record of inputs).

## R11 — Running protoc (FR-021, FR-022, FR-024, edge cases)

**Decision.** The tool locates `protoc` under `$(PkgGrpc_Tools)/tools/<os>_<arch>/` (`windows_x64`, `linux_x64`,
`linux_arm64`, `macosx_x64`), sets the Unix execute bit if missing, and runs two passes in a fresh temp folder:
1. **Descriptor pass**: `protoc -I<roots…> --descriptor_set_out=<tmp>/set.pb --include_imports <consumer protos>
   <all framework protos>` — protoc fails on duplicate symbols ("already defined") before anything is written; the set gives
   every message full name, its file, package and `csharp_namespace`.
2. **C# pass**, one invocation per consumer proto: `protoc -I<roots…> --csharp_out=<tmp>/<layout dir> <proto>` (no gRPC
   plugin). Layout `flat` → `<tmp>`; `by-root` → `<tmp>/<directory of the proto relative to its root>`. Exactly one new
   `.cs` per invocation is expected; zero → exit 2 naming the proto ("expected output missing").
Proto files are passed as paths inside their `-I` root, so descriptors and the `source:` header carry only virtual
(relative) names. Framework protos are never in the C# pass of a game run. C# type of a message: namespace =
`csharp_namespace` or the package converted like protoc (`UnderscoresToPascalCase` per dot segment), class = message
name, nested messages `Outer.Types.Inner`. A game message whose C# full name equals a framework one also fails (protoc
would accept different packages with the same `csharp_namespace`). [VERIFY V6, V7, V8]

**Alternatives.** Grpc.Tools MSBuild `<Protobuf>` items (rejected: FG-021 traps); `protoc --csharp_opt=base_namespace=`
for layout (rejected: layout by namespace would move `Core.cs` and break `.meta` GUIDs).

## R12 — Version guard (FR-022)

**Decision.**
- Export carries `frameworkVersion` (`GameServerVersion.Framework`) and `protobufVersion` (informational version of the
  loaded `Google.Protobuf` assembly in the host). The tool fails when either differs from its own baked values: "catalog
  exported by framework 0.6.0, generator is 0.7.0: build the host against the same framework checkout" / "the host
  resolves Google.Protobuf 3.37.0, the Unity package ships 3.36.2".
- With `catalog.hostProject`, the tool also evaluates `dotnet msbuild <host> -getItem:PackageVersion` and compares the
  `Grpc.Tools` and `Google.Protobuf` items (when present) with its pinned versions, naming both. [VERIFY V10]
- Framework test (`VersionTests`): CPM `Google.Protobuf` == product version of `Runtime/Plugins/Google.Protobuf.dll`.
  [VERIFY V11]
- The consumer guide states the pair: `Grpc.Tools` 2.84.0 (protoc 35.1) generating for `Google.Protobuf` 3.36.2.

**Trade-offs.** Projects other than the host with a `VersionOverride` are not seen (plan N3).

## R13 — Generated route and section files (FR-032, FR-052, edge cases)

**Decision.** Format in [generated-files.md](contracts/generated-files.md).
- **Route selection**: origin = config `origin`; surface `PlayerApi` or `Host`; profile Player, Public or Dev; owner not
  service-only. GM surface never generated.
- **Names**: PascalCase of the route name split on `.` and `_` (`identity.login_guest` → `IdentityLoginGuest`). Members:
  `const string X` (no parameters) or `static string X(string p1, …)` (parameters, in template order, each
  `Uri.EscapeDataString`), plus `const string XMethod` (`"POST"`; `"*"` for any-method). A name collision among all
  generated members (including `…Method`) fails, naming both routes.
- **Templates**: literal segments and `{name}` parameters (also inside a segment, `{id}.json`); constraints (`:`),
  optional (`?`), defaults (`=`) and catch-all (`*`) fail generation naming the route; output never contains `{`.
- **Several methods** in one player-facing declaration fail generation (plan N4).
- **Sections**: `const string XId`, `static readonly SectionKey<TMessage> X`, `static readonly SectionTable Table`;
  untyped section → `XId` only + generator warning; a section whose message is in neither the consumer protos nor the
  framework protos fails generation naming section and message.
- File header names the generator and framework version; no timestamp, path, user or machine. `#nullable enable`, C# 9
  constructs only, `global::`-qualified types, members ordered ordinally by name.

**Alternatives.** Nested class per route (`Routes.X.Path`, `.Method`) (rejected: `GameServerClient.LoginRoute` must stay a
`const string` assigned from a generated `const`); a route table type at runtime (rejected: not needed by SDK-01).

## R14 — SDK typed section reader and route migration (FR-033, FR-034, FR-053)

**Decision.** New `Runtime/Core/SectionKey.cs`: abstract `SectionKey` (`Id`, non-generic `Parser`), sealed
`SectionKey<T>` (`TypedParser`), `SectionTable` (keys by id; duplicate id → `ArgumentException`), enum `SectionRead
{ Absent, Unknown, Parsed }`. `SectionStore` adds `Register(SectionTable)` (same id with the same message type is
idempotent, a different type throws), `IsKnown(id)`, `Get<T>(SectionKey<T>)`, `TryRead(id, out IMessage? message)`. The
constructor registers `FrameworkSections.Table`. `Set`, `Apply`, `Get<T>(id, parser)`, `TryGet`, `SectionChanged` keep
their behaviour; unknown ids are still stored raw. `GameServerClient` route constants become `public const string
LoginRoute = FrameworkRoutes.IdentityLoginGuest;` etc. (same values, test). No public member removed or changed; asmdef
unchanged; package version stays `0.7.0`.

**Alternatives.** `Get<T>(string id)` looking up the parser by id (rejected: the caller still pairs id and `T`; a wrong
`T` throws at run time instead of not compiling).

## R15 — Typed wire mapper, hidden members, duplicates (FR-050, FR-051, FR-054, FR-060)

**Decision.** `AddAggregateWireMapper<TState, TWire>(string aggregateId, Func<TState, TWire> map,
Action<WireMapperOptions>? configure = null) where TState : class where TWire : class, IMessage<TWire>, new()`.
`AggregateWireMapper<TState, TWire>` implements `IAggregateWireMapper` and new `IAggregateWireContract` (`StateType`,
`Message` = `new TWire().Descriptor`, `Parser` = `new MessageParser<TWire>(() => new TWire())`, `HiddenMembers`,
`MayStayDefaultFields`). `WireMapperOptions.Hidden(params string[] memberPaths)` and `MayStayDefault(params string[]
protoFieldPaths)`. The untyped extension gets `[Obsolete]` (message names the typed overload and FG-028); startup logs one
Warning per untyped client-visible section. The registry keeps the first mapper per id and exposes duplicates internally;
catalog validation reports `[aggregate x] has 2 wire mappers` (BC-4). `ExtensionRegistry.ReplaceWireMapper<TState,
TWire>(id, map, configure)` removes earlier mapper registrations for the id and adds the new one (documented replacement).
Validation: each hidden path must resolve on the state member tree (R16), each may-stay-default path on the message
descriptor (proto field names, `.`-nested).

**Alternatives.** Adding members to `IAggregateWireMapper` (rejected: breaks external implementers); declaring the wire
type on `PlayerAggregateDescriptor` (rejected: Core cannot reference Protobuf, FR-050/ARC-01).

## R16 — Conformance check (FR-061..FR-066)

**Decision.** Member tree from the System.Text.Json contract of `TState` under `GameServerJson.Options`: a member is a
`JsonPropertyInfo` with a setter or an associated constructor parameter (computed get-only properties are not state);
path = C# member names joined by `.`, collection/dictionary elements transparent; leaf kinds and sample/perturbation
values in data-model §7. Algorithm: build the sample JSON from the tree (supplied member values and supplied whole state
win), deserialize with `GameServerJson.Options` (failure → finding), map, serialize, parse with the contract's parser,
report every proto field still at its default (recursive; repeated/map = empty or only default elements; oneof = no case
set; presence fields = not present; minus may-stay-default), then for each leaf perturb the JSON once, deserialize, map,
compare bytes: unchanged and not hidden → `MemberNotOnWire`; changed and hidden → `HiddenMemberOnWire`. Unsupported leaf
types without a supplied value → `MemberNotFillable` (never skipped). No unit-test framework, no host, no store: modules'
`ConfigureServices` run against a bare `ServiceCollection` and only `IAggregateWireMapper` instances are resolved.
Platform.Web exposes the member tree internally to the kit (`InternalsVisibleTo`). [VERIFY V9]

**Rationale.** Serializing through the same options as the document store is exactly "the members the serializer
persists", and handles `required`, `init`, positional records and read-only collections without constructor reflection.

**Alternatives.** Reflection on properties and constructors (rejected: re-implements STJ binding rules); generating mappers
(out of scope, FG-027 (3)).

## R17 — The framework's own path and documentation (story 5, FR-070..FR-073)

**Decision.**
- `sdk/GameServer.Client/GameServer.Client.csproj`: no `Grpc.Tools`, no `<Protobuf>`; `Compile Include="$(PackageRoot)Runtime\**\*.cs"`.
- Committed configs: `sdk/GameServer.Client/wiregen.json` (framework origin, `by-root`, LF, catalog host
  `samples/SampleHost`), `samples/SampleGame/wiregen.json` (game origin, flat, LF, protos `sample_game.proto` +
  `sample_counter.proto`), `tests/ConsumerFixture/tools/wiregen.json` (game origin, flat, LF committed; CRLF exercised in temp
  folders because the repo normalizes to LF).
- `tests/GameServer.WireGen.Tests/CommittedOutputTests` runs `verify` for the three configs (`--no-build`, the test's
  configuration).
- Architecture test: every framework `IGameModule` type is registered by the SampleHost local composition (so the package
  covers every framework route and section).
- Docs: consumer guide §4 (declare host routes), §5 (conformance test), §10 (generation step: inputs, line endings,
  version pair, verify, cleaning, `.meta`, IL2CPP/`link.xml`, using generated routes and sections), §11 (bump: regenerate
  and commit client files); `module-authoring.md` (routes in the descriptor, typed mapper with hidden members, conformance
  test); `02-them-command.html` §4.4 (Routes in `ModuleDescriptor`), §4.5 (map from the declaration, typed mapper), §4.8
  (conformance test), §4.10 (WireGen in-repo vs consumer, generated constant in Unity), §5 steps and checklist (no
  `"api/v1/counter/reset"` literal), §10 (new startup lines); `templates/module/README.md`; PCR platform §20 (catalog
  semantics, naming rule, check rules incl. pre-serving, conditions, section contract, export format v1), PCR unity-sdk
  (generated files, typed accessor, version pair), module PCRs (route names; sections with message and hidden members);
  pages stamped "framework v0.7.0 (chưa phát hành)" (FR-073); release-notes draft (FR-090).

## Facts to confirm before coding (VERIFY)

| # | Fact | How to confirm | Fallback |
| --- | --- | --- | --- |
| V1 | A lifecycle service registered first in `AddGameServer` runs `StartingAsync` before `GenericWebHostService.StartAsync` (pipeline build + Kestrel bind) on ASP.NET Core 10.0.x; at that point `app.DataSources` contains endpoints mapped after `UseGameServer` (incl. a `MapGroup`); a `GameServerStartupException` from it reaches `RunAsync` (unwrapped by `FindStartupException`) and from `WebApplicationFactory`/`TestServer` `StartAsync`; no port is bound | Spike test: host on a fixed free port maps an undeclared endpoint after `UseGameServer` → exit 1, then `TcpListener` binds the same port; same via `TestHost.afterUseGameServer` | R8 fallback 1 (`IStartupFilter`), then 2 (first-request refusal + `StopApplication`) |
| V2 | Re-materializing `DataSources.Endpoints` + validation for 200 endpoints costs ≤ 50 ms (SC-007) | Opt-in perf test (`GAMESERVER_PERF=1`) with a synthetic 200-endpoint module | Skip per-endpoint re-validation when `UseGameServer` was called from `RunGameServerAsync` (nothing can be mapped in between) and keep only unmapped + endpoints not seen before (by `METHOD template` key) |
| V3 | A second `Endpoints` enumeration yields endpoints with the same metadata (conventions applied) | Covered by V1 spike asserting profile/operation metadata on re-enumerated endpoints | — (if not, validate in `IStartupFilter` after the pipeline's own materialization) |
| V4 | Export mode on SampleHost and Fixture.Host, Local flavor, empty environment: `Build()` + resolving `WireCatalog` opens no store, no socket, reads no secret; side effects recorded (Serilog may create `logs/`) | Run export with network disabled / no Couchbase / no user secrets; list created files | Resolve the catalog through a minimal provider scope; document side effects |
| V5 | `dotnet run --project <host> --no-launch-profile [--no-build] -c <cfg> -- --environment Local --export-wire-contract <f>`: `--environment` selects the flavor in `WebApplication.CreateBuilder(args)` and is not swallowed by the switch parsing | Fixture end-to-end test | Pass `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` in the child environment |
| V6 | `Grpc.Tools` 2.84.0 layout `tools/<os>_<arch>/protoc[.exe]`, includes `build/native/include/google/protobuf/*.proto`; `$(PkgGrpc_Tools)` set under CPM with `GeneratePathProperty`; `protoc --version` = `libprotoc 35.1`; Unix exec bit state after restore | Inspect the restored package; run `protoc --version` | Resolve the package folder from `NUGET_PACKAGES` / `~/.nuget/packages`; `chmod` via `File.SetUnixFileMode` |
| V7 | The tool's per-file protoc runs reproduce today's committed files byte for byte (`Core.cs` nested, `Identity/Economy/Iap.cs`, KestrelDemo `SampleGame.cs`) | `generate` into a temp copy and diff | Accept a one-time diff (paths and `.meta` GUIDs unchanged), record in tasks.md |
| V8 | C# naming from descriptors: `csharp_namespace` or package PascalCase (`samplegame.v1` → `Samplegame.V1`), class = message name, nested `Outer.Types.Inner` | Fixture proto without `csharp_namespace` + nested message, compiled by `Fixture.Client.csproj` | Read the type name from the generated C# (`partial class` scan) as a cross-check |
| V9 | .NET 10 STJ contract: `JsonTypeInfo.Properties` exposes `AttributeProvider` (C# member), `Set`, `AssociatedParameter`; sample JSON deserializes `required`/`init`/positional records with `GameServerJson.Options` | Unit test on zh-pvp-shaped states (fixture copy) | Fall back to constructor parameter names matching (case-insensitive) when `AssociatedParameter` is null |
| V10 | `dotnet msbuild <proj> -getItem:PackageVersion` prints JSON with `Version` metadata under CPM on SDK 10.0.403; no CPM → no items | Run on SampleHost and Fixture.Host | Skip the Grpc.Tools check with a printed note (`Google.Protobuf` still checked through the export) |
| V11 | `Runtime/Plugins/Google.Protobuf.dll` product/file version = 3.36.2 (assembly version is 3.0.0.0) | `FileVersionInfo` in the test | Compare `AssemblyInformationalVersion` from metadata |
| V12 | `MapHealthChecks` → `RouteEndpoint` without `HttpMethodMetadata`; `MapOpenApi(Route)` raw text `/gm/v1/openapi/{documentName}.json`, GET; `WithDeclaration` metadata is visible on both | Unit test on a built app | Declare the probe with explicit GET + HEAD if the metadata differs |
| V13 | A default interface member on `IRequestOutcomeFeature` passes the framework analyzers (`latest-recommended`, warnings as errors) | Build | `RequestOutcomeExtensions.RouteName(this HttpContext)` reading the endpoint + catalog instead |
| V14 | Generated route/section files and protoc output compile in `Fixture.Client.csproj` (netstandard2.1, `LangVersion 9`, `TreatWarningsAsErrors`) | Solution build | Adjust renderer (e.g. no target-typed `new`) |

## VERIFY outcomes (2026-11-03, implementation, T002–T008; .NET SDK 10.0.403, ASP.NET Core 10.0.14)

| # | Outcome | Consequence |
| --- | --- | --- |
| V1 | **Holds.** A lifecycle service registered first runs `StartingAsync` before the server starts (no address bound, the port is free) on Kestrel `RunAsync`, `TestServer` `StartAsync` and `WebApplicationFactory.CreateClient()`; it sees endpoints mapped after the hand-over, also inside `MapGroup`; its `GameServerStartupException` surfaces unwrapped in all three; the port is bindable afterwards | R8 primary hook kept (T033); no fallback, no doc change |
| V2 | **Fails.** 217 endpoints: median 439.9 ms = materialization of the endpoint set 439.2 ms (RequestDelegateFactory, about 2 ms per endpoint) + validation 1.0 ms (T035) | The stated fallback (skip re-validation of seen endpoints) cannot help: the cost is the materialization. **Decided (V16):** endpoints are built once before serving — `UseGameServer` runs the endpoint validators only on its failure path; the pre-serving run is the only one on success. SC-007 restated (validation ≤ 50 ms, at most one build before serving). R8 updated; T035 |
| V3 | **Holds.** Two consecutive enumerations of `DataSources.Endpoints` carry the same `HttpMethodMetadata` and custom metadata | — |
| V4 | **Holds for the composition part.** `Build()` of the SampleHost and Fixture.Host Local compositions (no `COUCHBASE_*`, no user secrets) creates or modifies no file under the host folders, `samples/_database/` or `tests/ConsumerFixture/` | Final check with the export in T039. Fixture `appsettings.Local.json` sets `Urls http://localhost:5180`: export never listens; tests serving the fixture host override `Urls` |
| V5 | **Holds.** `--environment Local` passes `GameServerCommandLine.Parse` unchanged and selects the Local flavor in `CreateBuilder(HostArgs)` | — |
| V6 | **Holds with a correction.** Package root from `NUGET_PACKAGES` (this machine `D:\devcache\nuget`); `$(PkgGrpc_Tools)` set under CPM with `GeneratePathProperty`; protoc 35.1; OS folders `windows_x64/x86`, `linux_x64/x86/arm64`, **`macosx_universal`** (not `macosx_x64`); exec bit not observable on Windows | R11: map macOS (any arch) to `macosx_universal`; set the Unix bit with `File.SetUnixFileMode` |
| V7 | **Holds.** Per-file protoc runs with the csproj's roots reproduce `Core.cs`, `Identity.cs`, `Economy.cs`, `Iap.cs` and KestrelDemo `SampleGame.cs` byte for byte | No one-time diff expected in T059/T070 |
| V8 | **Holds with a precision.** Namespace without `csharp_namespace` = protoc `UnderscoresToCamelCase(segment, true)` per dot segment: `fixture.v1` → `Fixture.V1`, `my_game.v2beta` → `MyGame.V2Beta` (letter after `_` **or a digit** upper-cased); class = message name verbatim; nested `Outer.Types.Inner`; `csharp_namespace` wins | R11 rule text: "like protoc", including the after-digit rule |
| V9 | **Holds.** `init`/`required` properties: `Set != null`; positional record members: `Set != null` and `AssociatedParameter`; computed get-only property: neither (it is still serialized) | Member rule of R16 unchanged |
| V10 | **Holds.** JSON `Items.PackageVersion[]` with `Identity`, `Version`; without CPM the array is empty | — |
| V11 | **Differs in detail.** Product version `3.36.2+<sha>`, file version `3.36.2.0`, assembly version `3.36.2.0` (not `3.0.0.0`) | `VersionTests` compares the product version up to `+` |
| V12 | **Holds.** `MapHealthChecks` → no `HttpMethodMetadata`; `MapOpenApi(route)` raw text unchanged, GET; added metadata visible on both | — |
| V13 | **Holds.** Default interface member builds clean under the framework analyzers | — |
| V14 | **Holds.** Protoc output + generated-files §3–§4 shapes compile clean as netstandard2.1 / C# 9 under the consumer fixture props once `ImplicitUsings` is disabled (else CS8773) | `Fixture.Client.csproj` overrides: TFM, `LangVersion 9.0`, `ImplicitUsings disable`, explicit `Compile` glob |
| V15 | **Differs.** `DOTNET_HOST_PATH` and `DOTNET_ROOT` are not set under `dotnet test` (MTP runner, apphost) nor under `dotnet run`; resolution falls back to PATH | Tool keeps `--dotnet <path>`; e2e tests pass the resolved host explicitly |
| V16 | **Holds: nothing is cached.** ASP.NET Core 10.0.14 builds every endpoint again (conventions, `RequestDelegateFactory`) on each enumeration of `DataSources[i].Endpoints`; `EndpointRoutingMiddleware` holds its own `CompositeEndpointDataSource` and builds the set once more on the **first request**, whatever was enumerated before. Scratch probe (Kestrel on port 0, 217 `MapPost` endpoints with timeout + limiter conventions, a convention counting builds, Release, 3 runs): builds before the first response = 217 × (pre-start enumerations + 1); time to first response ≈ 660 ms with 0 pre-start enumerations, ≈ 1120 ms with 1 (013), ≈ 1620 ms with 2 (014 as committed in f2be391, same whether the second runs inline or in `StartingAsync`); each enumeration ≈ 480 ms (≈ 2.2 ms per endpoint, also on the second pass: not JIT warm-up) | The second pre-start build is pure added cost (≈ 2 ms per endpoint to time-to-first-response). Decision (T035): one build before serving — the pre-serving run on success, `UseGameServer` only on its failure path. Deferred: reusing the pre-serving build for routing (snapshot `DefaultEndpointDataSource` swap, would save the first-request build too) and the Request Delegate Generator (cannot intercept `MapDeclared(…, Delegate)`) — both beyond SC-007 |
