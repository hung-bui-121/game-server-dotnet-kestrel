# Implementation Plan: Wire contract (slice 014 wire-contract)

**Branch**: `014-wire-contract` | **Date**: 2026-11-03 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/014-wire-contract/spec.md`

## Summary

Group B of the improvement round from the first consumer (zh-pvp-server, framework `v0.6.0`): FG-026 (direction B),
FG-028, FG-021 and FG-027, before tag `v0.7.0`.

- **Route catalog (FG-026 (1)).** A pure-data `RouteDeclaration` in `GameServer.Core` (no ASP.NET, no Protobuf: ARC-01
  holds), declared by modules in `ModuleDescriptor.Routes`, by the platform in `PlatformRoutes` and by a host through
  `GameServerBuilder.DeclareRoutes`. `WireCatalog` (Platform.Web) is the read-only catalog of a host: route entries
  (declaration + owner + origin) and section entries (aggregate id + owner + message full name + hidden members). Mapping
  helpers gain overloads that take the declaration, so the route string, method, operation and (for commands) profile and
  permission are written once.
- **Endpoint check (FG-026 (3)).** `EndpointCatalogValidator` compares every mapped `RouteEndpoint` with the catalog:
  undeclared, template/method/profile/operation/permission differs, declared-but-unmapped (surface mapped, module not
  service-only, hosting condition holds). Together with every existing endpoint validator it runs once, over the
  **final** endpoint set, in a framework `IHostedLifecycleService.StartingAsync` that runs before the web host builds its
  pipeline and binds Kestrel (late-mapped endpoints, FR-012). `UseGameServer` runs the endpoint validators only when its
  other checks already failed, so a failing start still gives one complete report (research R8, V16: the endpoint set is
  built once before serving, as in 013).
- **Consumer codegen (FG-021).** A framework tool `tools/GameServer.WireGen` (`generate` / `verify`) driven by a
  committed `wiregen.json`: it gets the catalog from the host itself (`--export-wire-contract`, a new host mode next to
  `--provision`: builds the composition, writes a deterministic JSON export, never starts), runs the framework-pinned
  protoc from the restored `Grpc.Tools` directly (no `<Protobuf>` MSBuild items, so no separator trap and no clean
  deleting outputs), renders route and section files, and writes byte-stable output (LF/CRLF, UTF-8 without BOM,
  unchanged files not rewritten, no `.meta` touched) or lists differences.
- **Generated routes and sections, typed reader (FG-026 (2), FG-028).** `FrameworkRoutes` / `FrameworkSections` generated
  into the Unity package (new files with committed `.meta`); `GameServerClient.*Route` constants keep names and values but
  are defined from the generated ones; `SectionKey<T>` + `SectionTable` + `SectionStore.Get(key)` / `TryRead(id, …)`;
  `SectionStore.Get<T>(id, parser)` unchanged.
- **Typed wire mapper and conformance (FG-027, FG-028).** `AddAggregateWireMapper<TState, TWire>(id, map, configure)` with
  `Hidden(...)` / `MayStayDefault(...)`; untyped overload obsolete (warning + startup Warning); two mappers for one id
  fail startup; `ExtensionRegistry.ReplaceWireMapper` is the documented replacement path. `GameServer.TestKit` gains
  `WireConformance` (sample state from the aggregate's JSON contract, map → parse → defaults, one-member perturbation).
- **Own path (US5) and docs.** The SDK csproj stops running Grpc.Tools; the package and `KestrelDemo` are produced by
  WireGen from committed configs; the consumer fixture gets a game proto, a client-visible aggregate, a stand-in Unity
  client folder compiled as C# 9 / `netstandard2.1`, and CRLF/LF determinism tests. Consumer guide §10, module authoring,
  `02-them-command.html`, template README, PCRs, release-notes draft.

Decisions: [research.md](research.md) (R1–R17, VERIFY V1–V14); schemas and formats: [data-model.md](data-model.md);
contracts: [public-api.md](contracts/public-api.md), [wiregen-cli.md](contracts/wiregen-cli.md),
[generated-files.md](contracts/generated-files.md).

## Technical Context

**Language/Version**: C# / .NET 10 (SDK 10.0.403, ASP.NET Core 10.0.x); Unity package C# 9 / `netstandard2.1`

**Primary Dependencies**: unchanged. `Grpc.Tools` 2.84.0 (protoc from the package, used by WireGen) and
`Google.Protobuf` 3.36.2 stay the pinned pair; `sdk/GameServer.Client` drops its `Grpc.Tools` reference.

**Storage**: no change (no collection, document, index or wire message change; FR-055)

**Testing**: xUnit v3 + Shouldly; kit `TestHost`, `LocalFlavorFactory`; architecture tests; new
`tests/GameServer.WireGen.Tests` (renderer/unit + committed-output verify + end-to-end over the consumer fixture);
`tests/ConsumerFixture` (consumer-shaped codegen, catalog and conformance); SDK tests; Unity `smoke-build.ps1` manual

**Target Platform**: Linux containers; Windows/macOS/Linux developer machines; game repositories with the framework as a
submodule; Unity 2021.3+ clients (IL2CPP)

**Project Type**: web-service framework + Unity SDK + consumer tooling

**Performance Goals**: endpoint set built once before serving (as 013) and pre-serving validation ≤ 50 ms for 200 endpoints (SC-007, V16); request path +1 metadata lookup
(route name, FR-015)

**Constraints**: ARC-01 (Core free of ASP.NET/Protobuf), ARC-07, API-02, API-10, SEC-07/08/09, SDK-01..03, PCR-01,
ARC-08, constitution II/V/VI; Unity package stays `0.7.0`, additive API only, existing generated paths and `.meta`
GUIDs unchanged; no NuGet packaging

**Scale/Scope**: 1 new tool project, 1 new test project, 1 fixture client stand-in project; ~62 framework route
declarations; 6 typed mapper migrations (framework 3, sample 3) + template + fixture; ~20 new public types; 6 breaking
changes (BC-1..BC-6)

## Overview

```text
             Core (pure data, ARC-01)                         Platform.Web
 ModuleDescriptor.Routes ──┐                      ┌─► WireCatalog ── routes (decl + owner + origin + serviceOnly)
 RouteDeclaration          │  ModuleRegistry ─────┤                └ sections (id, owner, message, hidden, mayStayDefault)
 RouteSurface/Conditions   │  PlatformRoutes ─────┤          ▲  ▲
                           │  GameServerBuilder   │          │  └── AggregateWireMapperRegistry ◄─ AddAggregateWireMapper<TState,TWire>
                           └─ .DeclareRoutes ─────┘          │                                    (Hidden / MayStayDefault)
                                                             │
  mapping:  MapPlayerCommand(decl,…)  MapGmCommand(decl,…)  MapDeclared(decl, handler)  .WithDeclaration(decl)
            └──────────── RouteDeclarationMetadata on the endpoint ─────────────┘
                                                             │
  UseGameServer ── WireCatalog.Validate (names, dupes, templates, Public, conditions, hidden paths, duplicate mappers)
               └── only if errors so far: endpoint validators over endpoints mapped so far ─► one report, exit 1
  EndpointSetValidationService.StartingAsync  (before GenericWebHostService builds the pipeline and binds Kestrel)
               └── endpoint validators (security, commands, catalog) over the FINAL endpoint set + declared-but-unmapped
                   ─► GameServerStartupException → exit 1; pass → startup line (013 FR-054)

  host --export-wire-contract <file>  ─► wire-contract.json (format gameserver.wire-contract/1)
                                                │
  tools/GameServer.WireGen generate|verify --config wiregen.json
     ├ catalog: run host export (dotnet run … --environment Local) or read a given export
     ├ versions: export.frameworkVersion / protobufVersion; host CPM Grpc.Tools + Google.Protobuf (msbuild -getItem)
     ├ protoc (Grpc.Tools 2.84.0): descriptor run (game + framework protos: duplicates, message names) → C# per proto
     ├ render <Routes>.cs (constants, helpers, methods) + <Sections>.cs (ids, SectionKey<T>, SectionTable)
     └ normalize (EOL, UTF-8 no BOM) → write only changed files | verify: list differences, exit 2

  Unity package Runtime/Generated: Core.cs (nested), Identity/Economy/Iap.cs, FrameworkRoutes.cs, FrameworkSections.cs
  GameServerClient.LoginRoute = FrameworkRoutes.IdentityLoginGuest …   SectionStore.Get(SectionKey<T>) · TryRead · Register(table)

  GameServer.TestKit.WireConformance ── JSON contract of TState (GameServerJson.Options) → sample → map → parse
                                     └─ defaults per proto field · one-member perturbation · hidden checks
```

Style: unchanged modular monolith with explicit registration. Declarations are data in the business layer; binding,
checking and export are Platform.Web concerns; codegen is an out-of-process framework tool that consumes a versioned
export (pipes and filters), so neither the server nor the Unity package depends on the generator.

## Module breakdown

| Module (assembly / namespace) | Responsibility | Depends on | Does NOT own |
| --- | --- | --- | --- |
| Core `GameServer.Routing` (`RouteDeclaration`, `RouteSurface`, `RouteConditions`) | Describe one route as data | `SecurityProfile`, `CommandDescriptor` | template parsing, binding, checking |
| Core `ModuleDescriptor.Routes` | Carry a module's declarations | `RouteDeclaration` | owner (derived by the registry) |
| Platform.Web `Routing.PlatformRoutes` | Declarations of the platform routes (time, snapshot, config, receipt, GM OpenAPI) | Core | mapping (still `CoreEndpoints`) |
| Platform.Web `Routing.RouteDeclarationMetadata`, `DeclaredRouteExtensions` | Map a declaration / tag an endpoint with it | ASP.NET routing | profile conventions (`As*Endpoint` stay explicit) |
| Platform.Web `Commands.*EndpointFactory` | + overloads taking a declaration | metadata | — |
| Platform.Web `Catalog.WireCatalog` (+ entries, export, validation) | The host's catalog, declaration rules, deterministic export | registry, host declarations, mapper registry, `RoutePatternFactory` | mapped endpoints |
| Platform.Web `Catalog.RouteConditionRegistration` | Named hosting conditions and their evaluation | DI | which routes use them |
| Platform.Web `Validation.EndpointCatalogValidator` | Mapped endpoints ↔ catalog | catalog, endpoint metadata | security rules (existing validators) |
| Platform.Web `Hosting.EndpointValidation` (internal) | One entry point running all endpoint validators over a given endpoint set | validators | when it runs |
| Platform.Web `Hosting.EndpointSetValidationService` (internal) | Run `EndpointValidation` over the final set before serving | lifetime, state handed by `UseGameServer` | report formatting (shared) |
| Platform.Web `Hosting.GameServerCommandLine` / `RunGameServerAsync` | + export mode | catalog export | generation |
| Platform.Web `Aggregates.*` | Typed mapper, contract (message, parser, hidden, may-stay-default), duplicate detection, state member paths (internal) | Google.Protobuf, `GameServerJson` | conformance perturbation |
| Platform.Web `Requests` | + route name on the outcome and the request event | metadata, catalog lookup | — |
| `GameServer.TestKit` `WireConformance` | Conformance check per aggregate / module set / host | Platform.Web aggregates (IVT for member paths) | unit-test framework |
| `tools/GameServer.WireGen` | Generate / verify Unity C# from protos + export | Google.Protobuf (descriptors), protoc from Grpc.Tools, `dotnet` CLI | the server (reads only the export) |
| Unity package `Runtime/Core` | `SectionKey`, `SectionTable`, `SectionRead`, store additions, client routes from generated constants | generated files | route strings |
| Unity package `Runtime/Generated` | Generated messages + `FrameworkRoutes` + `FrameworkSections` | Google.Protobuf | hand edits |

No circular dependency: Core ← Platform ← Platform.Web ← {modules' adapters, Observability} ← TestKit; WireGen depends
on no framework assembly (only on the export format and protoc). The SDK depends on nothing server-side.

## Constitution Check

| Principle | How this slice meets it | Result |
| --- | --- | --- |
| I Server authoritative, one transaction per command | No change to the runner, documents or transactions; the conformance check runs mappers only, no store | PASS |
| II Explicit registration | Routes declared explicitly with the module (or host); the host refuses undeclared or mismatching endpoints in every environment, including endpoints mapped after `UseGameServer`; duplicate mappers fail startup; no discovery (codegen reads the host's own export) | PASS (strengthened) |
| III Security deny-by-default | Every endpoint now also needs a declaration whose profile and GM permission it must match; late-mapped endpoints get the security, Public allowlist and Dev rules before listening; Public declarations of modules feed the allowlist, host Public endpoints still need the operator allowlist | PASS (strengthened) |
| IV Async, bounded load | Startup-only work (≤ 50 ms budget, V2); request path adds one metadata read; no new queue | PASS |
| V Stable contract | Catalog = PCR source for routes and sections (names stable, ARC-08); export format versioned; generated client code from the same source; wire messages unchanged (FR-055); `Google.Protobuf`/`Grpc.Tools` pair pinned and tested against the Unity dll | PASS |
| VI Multi-layer tests | Unit (declarations, validator, template parser, renderer, normalizer), host (fault classes, late endpoints, flavor × hosting matrix), conformance (framework/sample/template + mutation set), SDK (typed accessor, literal scan, constants = catalog), tooling (committed outputs verify, determinism, CRLF, mixed separators, clean), consumer fixture end to end; manual: Unity smoke, reviewer walkthrough, zh-pvp CRLF regeneration | PASS |
| VII Observability | Startup report lines name route, owner, field and fix; request event gains `RouteName`; untyped mapper Warning | PASS |

Re-check after design: unchanged. One deviation recorded in Complexity Tracking (new tool + IVT).

## Design decisions (details in research.md)

| # | Decision | Research |
| --- | --- | --- |
| D1 | `RouteDeclaration` is a Core class (name, methods, template, profile, surface, operation, permission, condition); factories `Get/Post/Put/Delete/AnyMethod/Command`; owner never declared, derived from who registered it | R1 |
| D2 | Modules declare in `ModuleDescriptor.Routes`; platform in `PlatformRoutes`; host in `GameServerBuilder.DeclareRoutes`; framework route `const string`s stay (public API) and feed the declarations | R2 |
| D3 | Name rule `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$`, ≤ 64; command routes default to the operation; template canonical form = leading `/`, compared ordinally | R3 |
| D4 | Mapping overloads take the declaration and attach `RouteDeclarationMetadata`; `As*Endpoint` conventions stay explicit for non-command routes and the check compares the profile | R4 |
| D5 | `WireCatalog` singleton built lazily from registry + platform + host declarations + mapper registry; `Validate()` returns report lines; origin = framework when the owner is the platform or the module type's assembly name starts with `GameServer.` | R5 |
| D6 | Hosting conditions are names; the platform owns `development_tools`; a module registers others with `AddRouteCondition(name, predicate)` (GM: `gm.sso`; GM local login uses `development_tools`); unknown name fails startup | R6 |
| D7 | Endpoint check binds by metadata first, by (method, template) otherwise; one line per fault; unmapped reported only in the pre-serving run | R7 |
| D8 | Pre-serving hook = framework `IHostedLifecycleService` registered first in `AddGameServer`; `StartingAsync` re-runs every endpoint validator over the final set; fallbacks `IStartupFilter`, then first-request refusal (never silent) | R8, V1 |
| D9 | Host export mode `--export-wire-contract <file>`: builds the composition, validates declarations, writes the export, exits 0/1; never runs `UseGameServer` or starts | R9 |
| D10 | WireGen = console tool, config file `wiregen.json`, verbs `generate`/`verify`, exit 0/1/2; runs protoc itself (descriptor run + per-file C# run), never MSBuild `<Protobuf>` items | R10, R11 |
| D11 | Version guard: export carries framework and runtime `Google.Protobuf` versions; with `hostProject`, the tool evaluates the host's CPM `Grpc.Tools`/`Google.Protobuf`; framework test ties CPM `Google.Protobuf` to the Unity dll | R12 |
| D12 | Generated route file: `const` per route (no leading slash), `…Method` const, static helper per parameterized route (`Uri.EscapeDataString`); collisions and unsupported templates fail | R13 |
| D13 | Generated section file: `…Id` const, `SectionKey<T>` field, `Table`; untyped sections get the id only + generator warning | R13 |
| D14 | SDK: `SectionKey<T>`/`SectionTable`/`SectionRead`, `SectionStore.Register/Get(key)/TryRead/IsKnown`, framework table registered by default; client route constants assigned from `FrameworkRoutes` | R14 |
| D15 | Typed mapper `AddAggregateWireMapper<TState, TWire>` with `WireMapperOptions.Hidden/MayStayDefault`; `IAggregateWireContract`; untyped `[Obsolete]` + startup Warning; duplicates reported by catalog validation; `ExtensionRegistry.ReplaceWireMapper` | R15 |
| D16 | Conformance builds samples from the System.Text.Json contract of `TState` (members with setter or constructor parameter), perturbs one leaf at a time by ≥ 1 wire unit, compares payload bytes; shared member-path model in Platform.Web (IVT to the kit) | R16 |
| D17 | Own path: SDK csproj compiles `Runtime/**` without Grpc.Tools; committed `wiregen.json` for SDK, KestrelDemo, fixture; verify tests in `GameServer.WireGen.Tests` | R17 |

## Design per user story

### US1 — One catalog, checked against what is mapped (FR-001..FR-015)

**Declarations (Core, `src/GameServer.Core/Routing/RouteDeclaration.cs`)** — signatures in
[public-api.md §1](contracts/public-api.md#1-core-gameservercore). Example (game business project):

```csharp
public static class GameRoutes
{
    public static RouteDeclaration InitAccount { get; } = RouteDeclaration.Command(InitAccountHandler.Descriptor, "/api/v1/account/init");
    public static RouteDeclaration Summary { get; } = RouteDeclaration.Post("account.summary", "/api/v1/account/summary", SecurityProfile.Player);
}
// ModuleDescriptor: Routes = [GameRoutes.InitAccount, GameRoutes.Summary]
// Adapter:          routes.MapPlayerCommand<Wire.InitAccountRequest, InitAccountCommand, InitAccountResult>(GameRoutes.InitAccount, toDomain, toWire);
//                   routes.MapDeclared(GameRoutes.Summary, (Func<HttpContext, Task>)SummaryAsync).AsPlayerEndpoint();
// Host-only:        gameServer.DeclareRoutes(RouteDeclaration.Post("host.webhook", "/v1/hooks/store", SecurityProfile.Public, RouteSurface.Host));
```

**Framework migration** (every route the framework can map, data-model §1.3): `PlatformRoutes` (new, Platform.Web) and
`CoreEndpoints` mapping from it; `IdentityRoutes`, `IapRoutes`, `EconomyRoutes`, `CheatRoutes`, `GmRoutes`,
`ObservabilityRoutes` next to each module's descriptor; existing `const string …Route` members stay and feed the
declarations. Framework descriptors drop their `PublicEndpoints` lists (Public declarations replace them, FR-006).
Samples, template and fixture declare their routes the same way; `CounterModule.cs:39,44,60` and
`TemplateModule.cs:26` lose their inline literals.

**Catalog (`src/GameServer.Platform.Web/Catalog/`)**: `WireCatalog` (public), `RouteEntry`, `SectionEntry`,
`CatalogOrigin`, `WireCatalogExport` (format constants + `ToExportJson`), `WireCatalogBuilder` (internal),
`WireCatalogRules` (internal: name regex, template parse via `RoutePatternFactory.Parse`, version segment, duplicates,
Public consistency, condition names, hidden paths, duplicate mappers). Registered in `AddGameServer` as a lazy singleton
together with `HostRouteDeclarations` (filled by `DeclareRoutes`) and the `development_tools` condition.

**Check** (`src/GameServer.Platform.Web/Validation/EndpointCatalogValidator.cs`), fault classes and texts in
data-model §2:

| Step | Rule |
| --- | --- |
| Expand | each `RouteEndpoint` → pairs (method or `*`, canonical template); each declaration → pairs |
| Bind | metadata `RouteDeclarationMetadata` → that declaration; else exact pair lookup; else "undeclared" (or "method differs" when the template is declared with other methods) |
| Compare | template (metadata-bound only), method set, profile (`SecurityProfileMetadata`, only when exactly one), operation (`CommandOperationMetadata`), permission (`RequiredPermissionMetadata`, Gm only) |
| Unmapped (pre-serving only) | declaration pair with no endpoint pair, when owner registered and not service-only, surface mapped (`PlayerApi` ↔ `Hosting:MapPlayerApi`, `GmApi` ↔ `Hosting:MapGmApi`, `Host` always), condition holds |

**Where it runs** (`GameServerApplicationExtensions.UseGameServer`): after the existing validators, `catalog.Validate()`
joins `errors`. If `errors` is not empty, `EndpointValidation.Run(endpoints mapped so far, …, includeUnmapped: false)`
joins it too and `UseGameServer` throws (one complete report). Otherwise `UseGameServer` enumerates no endpoint and hands
the application (`IEndpointRouteBuilder`) to `EndpointValidationState`. `EndpointSetValidationService.StartingAsync` runs
`EndpointValidation.Run(finalEndpoints, …, includeUnmapped: true)` and throws one `GameServerStartupException` with every
line, or logs the 013 startup line on success; `RunGameServerAsync` already maps it to stderr + exit 1
(`FindStartupException`). Building the endpoint set once before serving is the SC-007 rule (research V16). `EndpointSecurityValidator` and
`CommandRouteValidator` keep their signatures; the Public allowlist passed to them becomes operator allowlist ∪ module
`PublicEndpoints` ∪ templates of Public declarations owned by modules or the platform. `--provision` runs
`catalog.Validate()` only (spec edge case).

**FR-015**: `IRequestOutcomeFeature.RouteName` (default interface member returning null, so implementers do not break),
set by `RequestLogMiddleware` at finalization from the endpoint's `RouteDeclarationMetadata`, else from
`WireCatalog.FindRouteName(method, template)`; request event field `RouteName` (data-model §6).

### US2 — Consumer generation step (FR-020..FR-028)

Contract: [wiregen-cli.md](contracts/wiregen-cli.md); config schema data-model §4; file formats
[generated-files.md](contracts/generated-files.md).

**Host side.** `GameServerCommandLine.Parse` recognizes `--export-wire-contract <path>` (two tokens, both removed from
`HostArgs`; missing or `=`-form → exit 1 as for `--provision`), `HostMode.ExportWireContract`. `RunGameServerAsync`:
resolve `WireCatalog` → `Validate()` → errors: stderr, exit 1; else write `ToExportJson()` (LF, UTF-8 no BOM) to the
path, dispose the app, exit 0. No `UseGameServer`, no hosted service, no store, no secret read (V4).

**Tool** (`tools/GameServer.WireGen/`): `Program.cs` (verbs), `WireGenConfig.cs` (JSON config + path resolution relative
to the config file, `Path.GetFullPath` normalization), `CatalogSource.cs` (export via `dotnet run --project <host>
--no-launch-profile [--no-build] -c <cfg> -- --environment <env> --export-wire-contract <tmp>` or read a file),
`VersionGuard.cs`, `Protoc.cs` (locate `protoc` for the OS under the baked `Grpc.Tools` root, set the exec bit on Unix,
run the descriptor pass and one C# pass per proto into a temp folder), `ProtoModel.cs` (parse `FileDescriptorSet`: full
names → C# type names, duplicates), `RouteRenderer.cs`, `SectionRenderer.cs`, `TemplateParser.cs`, `Names.cs`,
`OutputWriter.cs` (EOL, BOM, compare, write-if-changed, verify listing). Build-time `AssemblyMetadata`: framework root,
`Grpc.Tools` root (`GeneratePathProperty`), `Grpc.Tools`/`Google.Protobuf`/framework versions.

**Steps of `generate`** (exit codes 0/1/2, contract §3): config → output root/marker/directory exist (else exit 1,
nothing created) → catalog → version guard → protoc descriptor pass over consumer protos + all framework protos
(protoc fails on duplicate symbols before anything is written; message index built) → section/route selection by origin
(data-model §3.1) → validate (section message known, names collide, templates expressible) → protoc C# pass per consumer
proto (flat or `by-root` layout; exactly one `.cs` expected per proto, missing → exit 2) → render route and section files
→ normalize → write changed files only (or `verify`: list differing/missing files, exit 2, write nothing). The tool never
deletes a file and never reads, writes or deletes `.meta`; it prints "new file X: commit X.meta" for files it created.

**Clean** (FR-026): outputs are never MSBuild items of any project, so `dotnet clean` cannot delete them; the consumer's
old generator project can be deleted or reduced to an `Exec` of the documented command.

### US3 — Generated routes and sections in Unity code (FR-030..FR-035, FR-050..FR-055)

- **Selection.** Routes: catalog origin = config `origin`, surface `PlayerApi` or `Host`, profile Player/Public/Dev, owner
  not service-only. Sections: client-visible sections of that origin. Framework output = origin `framework` from the
  SampleHost local composition (which registers every framework module; architecture test).
- **Package files.** `Runtime/Generated/FrameworkRoutes.cs`, `FrameworkSections.cs` (+ committed `.meta` with new GUIDs).
  `Core.cs` keeps `gameserver/core/v1/`, `Identity/Economy/Iap.cs` stay flat (layout `by-root`, one root per proto).
- **Client.** `GameServerClient.LoginRoute = FrameworkRoutes.IdentityLoginGuest;` and the seven others (data-model §5.1);
  every `SendAsync`/`SendCommandAsync` call keeps using those constants. New typed API in `Runtime/Core/SectionStore.cs`
  and `Runtime/Core/SectionKey.cs` ([public-api.md §5](contracts/public-api.md#5-unity-sdk-sdkunitycomgameserverclient)).
  `SectionStore` constructor registers `FrameworkSections.Table`; a game calls `client.Sections.Register(GameSections.Table)`.
- **Literal scan** (FR-033, SC-002): `SdkRouteTests` fails on any string literal shaped like a route
  (`"(api|gm|health|v[0-9]+)/…"`) in `Runtime/Core/**` and `Runtime/Unity/**`, and on route-shaped or known section-id
  literals in `KestrelDemo/Assets/SampleGame/SampleGameClient.cs` (that one file is opened by path; never a recursive
  scan of `samples/`).
- **FR-035**: catalog of the SampleHost local composition → every framework player-facing route has a `FrameworkRoutes`
  member whose value (or helper output for sample arguments) equals the template without leading slash; committed SDK
  outputs pass `verify`.

### US4 — Typed mapper, hidden members, conformance (FR-050, FR-051, FR-054, FR-060..FR-066)

- `src/GameServer.Platform.Web/Aggregates/AggregateWireMapperRegistry.cs`: `AggregateWireMapper<TState, TWire>`
  (implements `IAggregateWireMapper` and new `IAggregateWireContract`), `WireMapperOptions`, typed extension, obsolete
  untyped extension, registry keeps the first mapper and records duplicates (`internal IReadOnlyList<string> Duplicates`)
  instead of last-wins.
- `src/GameServer.Platform.Web/Aggregates/StateMembers.cs` (internal): member tree of a state type from
  `GameServerJson.Options` (data-model §7.1) — used by hidden-path validation (catalog) and by the kit.
- Catalog validation adds: duplicate mapper, hidden path unknown, may-stay-default field unknown, untyped mapper →
  Warning (startup log category, once per aggregate).
- Migrations: economy `wallet`, `inventory`; identity `profile`; sample `progression`, `battle`, `counter`; template;
  fixture `card` (new).
- `src/GameServer.TestKit/WireConformance.cs` (+ `WireConformanceReport.cs`, `WireConformanceOptions.cs`,
  `SampleStates.cs`): algorithm data-model §7; `GameServer.Platform.Web.csproj` adds `InternalsVisibleTo
  GameServer.TestKit`.

### US5 — The framework's own SDK, sample and docs on the same path (FR-070..FR-073, story 5)

- `sdk/GameServer.Client/GameServer.Client.csproj`: remove `Grpc.Tools` and the five `<Protobuf>` items; compile
  `$(PackageRoot)Runtime\**\*.cs` (Generated included). New `sdk/GameServer.Client/wiregen.json` (origin framework,
  layout `by-root`, namespace `GameServer.Client`, classes `FrameworkRoutes`/`FrameworkSections`, LF).
- `samples/SampleGame/wiregen.json` → `KestrelDemo/Assets/SampleGame/Generated` (flat, namespace `SampleGame.Client`,
  classes `SampleGameRoutes`/`SampleGameSections`, protos `sample_game.proto` + `sample_counter.proto`, LF). `SampleGameClient.cs`
  deletes its `SampleGameRoutes` class and `"wallet"` literals, uses `SampleGameRoutes.BattleStart/BattleEnd/ShopBuy`,
  `FrameworkSections.Wallet`/`WalletId`, registers `SampleGameSections.Table`. New `.meta` files in KestrelDemo come from
  Unity (`smoke-build.ps1 -CompileOnly`) and are committed.
- Consumer fixture: `Fixture.Game/Protos/fixture_game.proto` (imports `gameserver/core/v1/core.proto`), client-visible
  aggregate `card`, route declarations, typed mapper with one hidden member; `tests/ConsumerFixture/client/`
  (`Fixture.Client.csproj` netstandard2.1 / C# 9 compiling `Assets/**/*.cs` against `sdk/GameServer.Client`, committed
  `Assets/Generated/*`), `tests/ConsumerFixture/tools/wiregen.json`.
- Docs: research R17 list.

## Where the plan narrows the spec

| # | Spec says | Plan does | Why |
| --- | --- | --- | --- |
| N1 | FR-005 route, method, profile, operation written once | Command routes: all four once (profile/permission from the descriptor). Non-command routes: declaration + explicit `As*Endpoint`, and the check fails on a different profile | `As*Endpoint` carries more than the profile (transport, gate, timeout, limiter, CSRF); applying it implicitly would hide those choices |
| N2 | FR-030 read the catalog "without starting a web server" | Through the host's own `--export-wire-contract` mode; the host `Program` must use `GameServerCommandLine.Parse` + `RunGameServerAsync` (as SampleHost and the consumer guide do) | The catalog depends on the real composition (flavor-dependent modules, host declarations); only the host knows it |
| N3 | FR-022 consumer `Grpc.Tools` version check | Checked when the catalog comes from `hostProject` (CPM evaluation of that project); with a pre-made export only `Google.Protobuf` (runtime version in the export) and the framework version are checked; `VersionOverride` in other projects is not seen | Evaluating arbitrary projects of a game repo is unbounded |
| N4 | FR-032 method "constant or member" | One `…Method` constant; a player-facing declaration with several methods fails generation ("declare one route per method"); any-method routes get `"*"` | No such route exists; keeps the generated API one-to-one |
| N5 | FR-061/FR-062 member paths "nested with `.`" | Collection and dictionary elements are transparent in paths (`Slots.UnlockEndUtc`); a dictionary key is attributed to the dictionary member | One path syntax for declarations, reports and PCR |
| N6 | FR-006 Public declarations count as public endpoints | Module and platform Public declarations count; host Public declarations still need `GameServer:PublicEndpointAllowlist` | Keeps today's operator control over host-level public endpoints (SEC-09) |
| N7 | FR-010 declared-but-unmapped | Reported only in the pre-serving run (final set) | A host may map its declared routes after `UseGameServer` |
| N8 | FR-025 marker "under the output root" | Config `output.root` (must exist) + optional `output.marker` (relative to root, must exist) + `output.directory` (relative to root, must exist; never created) | Matches the first consumer's client-root guard |
| N9 | FR-023 "on any machine, OS" | Automated: two checkout paths, mixed separators, LF/CRLF on Windows; Linux/macOS run is a documented manual check until CI-01 | No CI runner (pre-existing gap) |
| N10 | FR-034 new package files ship with `.meta` | Package `.meta` hand-authored (new GUIDs) and enforced by a test; KestrelDemo `.meta` produced by Unity and committed after the smoke run | KestrelDemo is a normal (mutable) Unity project |
| N11 | FR-015 SHOULD | Implemented as a default interface member + one event field | Additive for implementers of the interface |
| N12 | FR-051 untyped "produces an id constant" | Id constant only; generator prints a warning per untyped section | — |

## Data flow

**Startup (serve).** `AddGameServer` registers `WireCatalog`, `HostRouteDeclarations`, condition `development_tools`,
`EndpointValidationState` and `EndpointSetValidationService` (first hosted service) → modules add declarations
(descriptor) and typed mappers (DI instances) → host `DeclareRoutes` → `Build()` → `RunGameServerAsync` →
`UseGameServer`: registry/options checks + `catalog.Validate()` → errors: + endpoint validators over the current set,
aggregated report (exit 1) / success: state gets the app (no endpoint enumerated) → `RunAsync` → host `StartAsync` →
`EndpointSetValidationService.StartingAsync` (before `GenericWebHostService`): all endpoint validators over the final set
+ unmapped → throw (exit 1) or startup line and continue → pipeline built, Kestrel binds.

**Export.** `host --export-wire-contract f` → `Build()` → `WireCatalog` (registry, platform, host, mapper registry) →
`Validate()` → `ToExportJson()` → file → exit 0.

**Generate.** `wiregen generate --config c` → paths → catalog (export run or file) → versions → protoc descriptor pass →
model → select + validate → protoc C# pass → render → normalize → compare → write changed files → summary.

**Client.** Response sections → `SectionStore.Set/Apply` (raw, unchanged) → game code `Get(GameSections.Progress)` (key
parser) or `TryRead(id, out msg)` (registered tables; unknown id → `Unknown`, not parsed).

**Conformance.** Test → `WireConformance.CheckModules(modules)` → per client-visible aggregate with a typed mapper:
member tree → sample JSON → `TState` → map → bytes → parse → default-field findings → per leaf: perturbed JSON → state →
map → bytes compare → member findings → report.

**State ownership.** Declarations: immutable, owned by the declaring module/host. Catalog: one immutable singleton per
host, lazily built after `Build()`. Endpoint validation state: written once by `UseGameServer`, read once by the
service. Export: a file the tool deletes after reading (temp) or the consumer commits. Generated files: the client repo
(or the package) owns them; the tool only writes them. Section tables: per `SectionStore` instance.

## Performance

- Pre-serving check: the one materialization of `DataSources.Endpoints` before serving (RequestDelegateFactory per
  endpoint, about 2 ms each; 013 paid it in `UseGameServer`, 014 pays it in `StartingAsync` instead, never both) + O(n)
  dictionary work (about 1 ms for 217 endpoints). ASP.NET caches nothing: routing builds the set again at the first
  request in any case (V16). SC-007: build count before serving = 1 (always-on test) and validation ≤ 50 ms / 200 endpoints
  (opt-in `GAMESERVER_PERF=1`, synthetic 200-endpoint composition) (V2, V16, T035).
- Catalog: built once (~100 entries), dictionaries by name and by `METHOD template`.
- Request path: `RouteName` = one `EndpointMetadataCollection.GetMetadata<T>()` (cached by ASP.NET) per request at
  finalization; the catalog lookup fallback (plain-mapped consumer endpoints only) allocates one key string.
- Typed mapper: `ToPayload` unchanged in cost (typed delegate, no extra allocation). Conformance and WireGen: test/tool
  time only.

## Seams for later slices and extension points

- **015 (config, FG-019).** A config-distribution route is one more `PlatformRoutes` declaration → generated in
  `FrameworkRoutes` automatically. The export format accepts additive top-level members (readers ignore unknown ones), so
  `configFiles` can be added without a format bump. Consumer middleware compares `RouteName == PlatformRoutes.Config.Name`
  instead of a string.
- **016 (login/commands).** A login variant without snapshot (FG-013) or account-creation hook route (FG-015) is a new
  identity declaration; `LoginResponse` sections go through the same `SectionStore.Set`, so typed keys and `TryRead` work
  on login and partial snapshots (absent → null / `Absent`); FG-029 per-section failures use the generated `…Id`
  constants; FG-022's `SendCommandAsync(route, request, parser, requestId)` takes the generated constant.
- **FG-014 (`MapPlayerQuery`).** Non-command declarations already exist (`RouteDeclaration.Post(...)`); the helper only
  adds a mapping overload.
- **Packaging (FG-005/008/009).** WireGen's inputs/outputs are the config file and the export format; a dotnet-tool
  package later wraps the same CLI.
- **Deferred.** Typed client call wrappers per command; request/result message names in declarations; orphan-file
  detection in the output folder; TypeScript; route name in the console line.

## Project Structure

### Documentation (this feature)

```text
specs/014-wire-contract/
├── spec.md
├── plan.md                         # this file
├── research.md                     # R1..R17, VERIFY V1..V14, code facts
├── data-model.md                   # catalog entries, framework route list, fault lines, export, wiregen config, conformance
├── contracts/public-api.md         # C# surface (Core, Platform.Web, TestKit, Unity SDK)
├── contracts/wiregen-cli.md        # host export switch, WireGen CLI, exit codes, behaviour
├── contracts/generated-files.md    # export JSON, routes file, sections file examples
├── release-notes-draft.md          # implementation phase (FR-090)
└── tasks.md                        # next stage (/speckit-tasks)
```

### Source Code (repository root)

```text
GameServer.slnx                                            # + tools/GameServer.WireGen, tests/GameServer.WireGen.Tests, tests/ConsumerFixture/client/Fixture.Client
src/GameServer.Core/
├── Routing/RouteDeclaration.cs                            # NEW RouteDeclaration, RouteSurface, RouteConditions
└── Modules/ModuleDescriptor.cs                            # + Routes
src/GameServer.Platform.Web/
├── GameServer.Platform.Web.csproj                         # + InternalsVisibleTo GameServer.TestKit
├── Catalog/WireCatalog.cs · RouteEntry.cs · SectionEntry.cs · WireCatalogExport.cs              # NEW public
├── Catalog/WireCatalogBuilder.cs · WireCatalogRules.cs · HostRouteDeclarations.cs              # NEW internal
├── Catalog/RouteConditions.cs                             # NEW AddRouteCondition, RouteConditionRegistration
├── Routing/PlatformRoutes.cs                              # NEW public declarations of platform routes
├── Routing/RouteDeclarationMetadata.cs · DeclaredRouteExtensions.cs                            # NEW
├── Routing/EndpointMetadata.cs                            # unchanged
├── Endpoints/CoreEndpoints.cs                             # maps from PlatformRoutes (MapDeclared)
├── OpenApi/GmOpenApi.cs                                   # WithDeclaration(PlatformRoutes.GmOpenApi)
├── Commands/CommandEndpointFactory.cs                     # + declaration overloads (3 Player, 1 Dev)
├── Commands/GmCommandEndpointFactory.cs                   # + declaration overloads (2)
├── Validation/EndpointCatalogValidator.cs                 # NEW
├── Aggregates/AggregateWireMapperRegistry.cs              # typed mapper, contract, options, duplicates, obsolete untyped
├── Aggregates/StateMembers.cs                             # NEW internal member tree (JSON contract)
├── Requests/IRequestOutcomeFeature.cs · RequestOutcome.cs · RequestLogMiddleware.cs · RequestLogEntry.cs   # RouteName
├── Hosting/GameServerApplicationExtensions.cs             # catalog validation, endpoint validation entry, state handoff, untyped warning
├── Hosting/EndpointValidation.cs · EndpointSetValidationService.cs · EndpointValidationState.cs   # NEW internal
├── Hosting/GameServerBuilder.cs                           # DeclareRoutes, catalog/condition/service registration
├── Hosting/ExtensionRegistry.cs                           # ReplaceWireMapper
├── Hosting/GameServerCommandLine.cs · GameServerRunExtensions.cs                               # export mode
└── Hosting/ProvisioningRunner.cs                          # + catalog.Validate()
src/GameServer.Platform.Observability/ObservabilityModule.cs · ObservabilityRoutes.cs           # declarations, PublicEndpoints removed
src/GameServer.Modules.Identity.Adapters/IdentityModule.cs · IdentityRoutes.cs                  # declarations, typed mapper
src/GameServer.Modules.Economy/EconomyModuleDefinition.cs · EconomyRoutes.cs                    # declarations (economy, cheat)
src/GameServer.Modules.Economy.Adapters/EconomyModule.cs                                         # map from declarations, typed mappers
src/GameServer.Modules.Iap/IapModel.cs · IapRoutes.cs, src/GameServer.Modules.Iap.Adapters/IapModule.cs
src/GameServer.Modules.Gm.Adapters/GmModule.cs · GmRoutes.cs · GmPlayerEndpoints.cs · Ops/GateEndpoints.cs · Sso/GmSso.cs · Staff/StaffEndpoints.cs
src/GameServer.TestKit/WireConformance.cs · WireConformanceOptions.cs · WireConformanceReport.cs · SampleStates.cs   # NEW
modules/Sample.Counter/Sample.Counter/CounterModuleDefinition.cs · Sample.Counter.Adapters/CounterModule.cs
samples/SampleGame/SampleGame/SampleGameModel.cs (routes) · SampleGame.Adapters/SampleGameModule.cs
samples/SampleGame/wiregen.json                            # NEW
samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs                            # generated constants, typed sections
samples/SampleGame/KestrelDemo/Assets/SampleGame/Generated/{SampleGameRoutes,SampleGameSections,SampleCounter}.cs (+ .meta from Unity)
templates/module/Template.Module/TemplateModuleDefinition.cs · Template.Module.Adapters/TemplateModule.cs · README.md
sdk/GameServer.Client/GameServer.Client.csproj             # no Grpc.Tools / <Protobuf>; compiles Runtime/** 
sdk/GameServer.Client/wiregen.json                         # NEW
sdk/unity/com.gameserver.client/Runtime/Core/SectionKey.cs (+ .meta)                             # NEW SectionKey, SectionKey<T>, SectionTable, SectionRead
sdk/unity/com.gameserver.client/Runtime/Core/SectionStore.cs · GameServerClient.cs
sdk/unity/com.gameserver.client/Runtime/Generated/FrameworkRoutes.cs · FrameworkSections.cs (+ .meta)
tools/GameServer.WireGen/                                  # NEW tool (see US2)
tests/GameServer.WireGen.Tests/                            # NEW: renderer, names, templates, normalizer, protoc model, CLI, committed outputs
tests/ConsumerFixture/src/Fixture.Game/{Protos/fixture_game.proto, Fixture.Game.csproj, FixtureModule.cs, FixtureModuleDefinition.cs}
tests/ConsumerFixture/src/Fixture.Host/Program.cs          # unchanged composition (already RunGameServerAsync)
tests/ConsumerFixture/client/{Fixture.Client.csproj, Assets/Generated/*.cs}                     # NEW stand-in Unity client
tests/ConsumerFixture/tools/wiregen.json                   # NEW
tests/ConsumerFixture/tests/Fixture.Tests/{FixtureCatalogTests,FixtureConformanceTests,FixtureCodegenTests}.cs   # NEW
tests/GameServer.Platform.Web.Tests/
├── RouteDeclarationTests.cs · WireCatalogTests.cs · EndpointCatalogCheckTests.cs · LateEndpointValidationTests.cs
├── WireMapperRegistrationTests.cs · WireConformanceTests.cs (+ mutation states) · RouteNameOutcomeTests.cs
└── (existing tests mapping ad-hoc endpoints: + DeclareRoutes; StartupValidationTests messages)
tests/SampleGame.Tests/CatalogCompositionTests.cs          # NEW flavor × hosting matrix, framework catalog completeness
tests/GameServer.Client.Tests/SectionTableTests.cs · SdkRouteTests.cs                            # NEW
tests/GameServer.Architecture.Tests/ArchitectureTests.cs · VersionTests.cs · UnityPackageMetaTests.cs   # Core rule kept; Protobuf pair; .meta
tests/GameServer.Observability.Tests, GameServer.Gm.Tests, Sample.Counter.Tests              # ad-hoc endpoints declared
docs/guides/consumer-guide.md (§4, §5, §10, §11) · module-authoring.md
docs/html/02-them-command.html (§4.4, §4.5, §4.8, §4.10, §5 + checklist, §10) · docs/html/index.html stamp
docs/pcr/platform.md (§20) · unity-sdk.md · identity.md · economy.md · iap.md · gm.md · sample-counter.md · sample-game.md
templates/module/README.md · README.md (one line)
```

**Structure Decision**: one framework tool (`tools/GameServer.WireGen`, like `GameServer.LocalRun`) with its own test
project; everything else extends existing projects. Catalog code sits in a new `Catalog/` folder of Platform.Web; Core
gets one data file.

## Test strategy

| Story / rule | Tests |
| --- | --- |
| US1 declarations (FR-001..FR-008) | `RouteDeclarationTests` (factories, canonical template, command defaults); `WireCatalogTests` (owner/origin, platform + host entries, export determinism, FR-007 faults: duplicate name, duplicate method+template naming both owners, invalid template, missing version segment, bad name, Public in `PublicEndpoints` but other profile, unknown condition); consumer read of `PlatformRoutes.Config` in `FixtureCatalogTests` |
| US1 check (FR-010..FR-014, SC-001) | `EndpointCatalogCheckTests`: 9 fault classes (undeclared player/GM/host, method, profile, operation, permission, declared-unmapped, duplicate name, late-mapped) each → exactly one line; health probe `*`; two methods; group prefix; service-only and `MapPlayerApi/MapGmApi=false` not reported; GM OpenAPI and SSO conditions; `LateEndpointValidationTests` (afterUseGameServer hook: undeclared, Dev outside dev via settings flip, no version, duplicate, Public not allowlisted, command operation unknown → exit 1 through `ServeAsync`, port not bound; WebApplicationFactory start throws); `CatalogCompositionTests` (SampleHost flavors local/dev/beta/uat/live × player/GM on/off, service-only module: 100% mapped ⊆ catalog); `EndpointCheckPerformanceTests` (SC-007: always-on build count = 1 per endpoint before serving + no startup line on an endpoint fault; opt-in validation ≤ 50 ms) |
| US1 FR-015 | `RouteNameOutcomeTests` (outcome + event carry the name for metadata-bound and plain-mapped endpoints; null for unmatched) |
| US2 (FR-020..FR-028, SC-003, SC-004) | `WireGen.Tests`: config errors (missing proto, missing folder, missing marker, nothing created), protoc error relayed, duplicate full name, framework type never in output, deterministic bytes (two temp checkouts, mixed separators), LF/CRLF, no BOM, unchanged timestamp on rerun, verify lists edited and missing files and writes nothing, no `.meta` touched, version mismatch messages (export framework version, protobuf, CPM Grpc.Tools); `FixtureCodegenTests` (end to end with `hostProject` from the fixture, committed client output verifies; `Fixture.Client.csproj` builds in the solution with 0 warnings = C# 9 / netstandard2.1 compile); `dotnet clean` of the fixture leaves the client files (test) |
| US3 (FR-030..FR-035, FR-052, FR-053, SC-002, SC-006) | `WireGen.Tests` renderer (constant names, collisions, helper escaping, unsupported templates, method constants, GM excluded, Dev included, untyped section id only, unknown message fails); committed-output verify for SDK and KestrelDemo; `SdkRouteTests` (catalog ↔ `FrameworkRoutes`, `GameServerClient` constants unchanged in value, literal scan of SDK runtime and `SampleGameClient.cs`); `SectionTableTests` (wallet/inventory/profile + a sample section after snapshot and `SectionUpdate`: typed = `Get<T>(id, parser)`; unknown id stored raw and `TryRead` → `Unknown`; conflicting table → exception); existing SDK tests unchanged |
| US4 (FR-050, FR-051, FR-054, FR-060..FR-066, SC-005) | `WireMapperRegistrationTests` (message full name in catalog, untyped obsolete → no message + one Warning, duplicate → startup line, unknown hidden/may-stay-default path → startup line, `ReplaceWireMapper`); `WireConformanceTests` (framework, sample, template mappers pass; mutation set: unmapped member, unset proto field, nested unmapped member, stale hidden, hidden on wire, empty mapped collection, unknown hidden path; unsupported member named; supplied sample state; `CheckAll` over a host); `FixtureConformanceTests` (nested record, map, list, nullable, `DateTimeOffset`→ms, hidden member) |
| US5 (story 5, FR-070..FR-073) | committed-output verify (SDK, KestrelDemo, fixture); `UnityPackageMetaTests` (every file under the package has a `.meta`, existing GUIDs unchanged vs a recorded list); manual: `smoke-build.ps1 -CompileOnly` then `-E2E` on KestrelDemo, reviewer walkthrough of consumer guide §10 + HTML §4.10/§5, zh-pvp CRLF regeneration without diff |
| Cross-cutting (FR-091) | Debug + Release build 0 warnings; full suite in-memory, File, Couchbase; `VersionTests` (CPM `Google.Protobuf` = Unity dll product version) |

## Migration notes for consumers (v0.6.0 → v0.7.0, feeds the release notes)

| BC | What breaks | Steps for a game |
| --- | --- | --- |
| BC-1 | Every mapped endpoint must be declared; undeclared or mismatching endpoints fail startup, also in Staging/Production | 1. In the business project add `public static class <Game>Routes` with one `RouteDeclaration` per route (`RouteDeclaration.Command(Handler.Descriptor, "/api/v1/…")` for commands). 2. Add `Routes = [...]` to the `ModuleDescriptor`. 3. Map with the declaration overloads (`MapPlayerCommand(<Game>Routes.X, toDomain, toWire)`, `MapDeclared(...)`). 4. Declare host-only endpoints with `gameServer.DeclareRoutes(...)` before `Build()`. 5. Start each flavor once; each report line names the route and the fix. zh-pvp: `account.init` (`GameModule.cs:20`). |
| BC-2 | Endpoints mapped after `UseGameServer` are validated before Kestrel listens (catalog, profile, version, duplicates, Public allowlist, Dev rule, command route) | Fix each reported endpoint or move it into a module; tests that map extra endpoints in `afterUseGameServer` declare them in the `modules` callback (`gs.DeclareRoutes(...)`). |
| BC-3 | `AddAggregateWireMapper<TState>(id, Func<TState, IMessage>)` is obsolete (CS0618 = error with `TreatWarningsAsErrors`) + startup Warning per untyped section | Replace with `services.AddAggregateWireMapper<ProgressState, Wire.ProgressState>(GameAggregates.Progress, GameWireMapping.ToWire, o => o.Hidden("ActiveBattle.WinCash", "ActiveBattle.TrophyWin", "ActiveBattle.IsProtect", "ActiveBattle.ConfigVersion"))`; delete the hidden-field comment in `GameWireMapping.cs`; add one test `WireConformance.CheckModules([new GameModule(), …]).ThrowIfFailed()`. |
| BC-4 | Two mappers for one aggregate id fail startup (was: last wins) | Keep one registration; to replace a framework mapper use `gameServer.ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(id, map))`. |
| BC-5 | A route in `ModuleDescriptor.PublicEndpoints` whose declaration has another profile fails startup | Make the declaration Public or remove the entry (Public declarations no longer need `PublicEndpoints`). |
| BC-6 | `dotnet build sdk/GameServer.Client` no longer regenerates any C# (framework or sample) | Framework/sample: `dotnet run --project tools/GameServer.WireGen -- generate --config sdk/GameServer.Client/wiregen.json` (and `samples/SampleGame/wiregen.json`). Games: replace `tools/UnityProtoGen` with a committed `wiregen.json` and `dotnet run --project framework/tools/GameServer.WireGen -c Release -- generate --config <file> [--output-root <client>]` (`lineEndings: crlf`); delete the `NormalizeDirectory`, CRLF task, clean warning and `FRAMEWORK-GAP FG-021` marker. |

Non-breaking but recommended: client — replace `OnlineSession.InitAccountRoute` with the generated constant,
`PlayerReadModel` ids and `switch` with `GameSections` keys / `TryRead`; server — compare request routes with
`HttpContext.RequestOutcome().RouteName` or `PlatformRoutes.Config.Template` instead of copied strings.

## Implementation phases (input for tasks.md)

1. **Foundations (blocking)** — Core `RouteDeclaration` + `ModuleDescriptor.Routes`; Platform.Web catalog types,
   builder, rules, export, conditions, metadata + `MapDeclared/WithDeclaration`; command factory overloads;
   `DeclareRoutes`; typed mapper + contract + options + duplicates + `StateMembers`; IVT; verify V1, V4, V9, V12 first.
2. **US1 migration** — platform, observability, identity, economy/cheat, iap, GM (5 files), sample counter, sample game,
   template, fixture; remove framework `PublicEndpoints`; framework tests that map ad-hoc endpoints declare them.
3. **US1 check** — `EndpointCatalogValidator`, `EndpointValidation`, pre-serving service + state, `UseGameServer` and
   provision wiring; fault-class tests; late-endpoint tests; composition matrix; FR-015; perf opt-in.
4. **US4** — typed mapper migrations (framework, sample, template), untyped Warning, `ReplaceWireMapper`; kit
   `WireConformance` + mutation tests; fixture aggregate `card` + conformance test.
5. **US2 tool** — export mode; WireGen project (config, catalog source, versions, protoc, model, renderers, writer);
   unit tests; fixture proto, stand-in client project, config, end-to-end + determinism tests.
6. **US3 SDK** — `SectionKey`/`SectionTable`/store API; generate package files + `.meta`; client constants from
   generated; SDK tests (typed accessor, literal scan, catalog ↔ constants).
7. **US5 own path** — SDK csproj change, SDK/sample configs, KestrelDemo regeneration + `SampleGameClient.cs`, committed
   output verify tests, `.meta` test, version pair test; Unity smoke (manual).
8. **Docs and close** — consumer guide §4/§5/§10/§11, module-authoring, HTML 02 (+ stamp), template README, PCRs
   (platform §20, unity-sdk, 6 module PCRs), release-notes draft, reviewer walkthrough, full suite (in-memory, File,
   Couchbase), tech-lead review, resume file.

Phase 1 blocks all. 2 before 3 (the check fails every undeclared framework route). 4 and 5 can run in parallel after 1
(disjoint files except the fixture: do the fixture aggregate in 4 before the fixture codegen in 5). 6 needs 5; 7 needs 6;
8 last. **Estimated tasks: ~78** (foundations 10, US1 migration 11, check 10, US4 9, US2 tool 14, US3 7, US5 7,
docs/close 10).

## Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| `StartingAsync` does not see late endpoints or runs after Kestrel binds on 10.0.x (V1) | FR-012 not met | Verify first (phase 1); fallback `IStartupFilter`, then first-request refusal with the same report + `StopApplication` (never silent) |
| Second endpoint materialization exceeds 50 ms on large hosts (V2) | SC-007 | **Occurred** (440 ms / 217 endpoints, V2/V16). Resolved: the endpoint validators run once before serving (pre-serving run); `UseGameServer` runs them only on its failure path — for every host, not only `RunGameServerAsync`/`TestHost`. Endpoint lines of a passing configuration now surface from the host start (same text, exit 1, port unbound) (T035) |
| Export mode has side effects or needs secrets for some compositions (V4) | Codegen cannot run from a clean checkout | Export never resolves stores/options; consumer guide: run with the local flavor; record the Serilog log-file side effect |
| BC-1 floods framework tests with undeclared ad-hoc endpoints | Many red tests during migration | Phase 2 migrates all framework routes before the check is switched on; ad-hoc test endpoints (~18 in 8 files) declared through the `modules` callback |
| WireGen protoc output differs from the Grpc.Tools-generated committed files (V7) | One-time diff in package/KestrelDemo | Accept once, record; paths and `.meta` GUIDs unchanged (FR-034) |
| C# type name rule from descriptors wrong for edge cases (V8) | Generated sections do not compile | Fixture covers no `csharp_namespace`, nested message; compile project in the solution |
| Conformance false positives (member used only in a condition, enum zero meaningful, derived fields) | Consumer friction | Supplied sample values/state, `MayStayDefault`, documented perturbation table; messages name the member and the fix |
| CS0618 breaks the game build at bump (BC-3) | Bump blocked until mappers migrate | Release notes first items; migration is mechanical (3 registrations in zh-pvp) |
| Two catalogs drift: SampleHost misses a future framework module | Framework routes missing from the package | Architecture test: every framework `IGameModule` type is registered by SampleHost in the local flavor |
| Unity creates `.meta` for new package files differently than hand-authored ones | Package import warnings | `.meta` copied from the existing MonoImporter template with fresh GUIDs; smoke build checks import |
| Route-literal scan false positives (comments, doc strings) | Noisy test | Scan string literals only (simple C# lexer: skip comments), allow-list none |

## Complexity Tracking

| Item | Why needed | Simpler alternative rejected because |
| --- | --- | --- |
| New tool project `tools/GameServer.WireGen` + test project | FR-020..FR-028: deterministic, verifiable, consumer-usable generation outside any consumer project | MSBuild targets around Grpc.Tools `<Protobuf>` items (rejected: FG-021 traps — separators, clean deletes outputs, warnings not errors, no verify mode, CRLF task) |
| Host export mode | FR-030: the catalog of the real composition without serving | Loading the host assembly into the tool (rejected: `HostFactoryResolver`-style interception, dependency loading); a hand-maintained catalog file (rejected: direction A) |
| Pre-serving hosted service | FR-012: endpoints mapped after `UseGameServer` | Request-time checks only (rejected by spec: must refuse before listening) |
| `InternalsVisibleTo GameServer.TestKit` on Platform.Web | Startup hidden-path validation and the kit must agree on what a state member is | Two member walkers (rejected: drift); public member-tree API (rejected: test-only surface) |
| Named hosting conditions with registered evaluators | GM local login (development tools), GM SSO (configured), GM OpenAPI (development tools) are conditional; FR-010/FR-011 | A boolean "optional" flag (rejected: an unmapped route would never be reported even when its condition holds) |
