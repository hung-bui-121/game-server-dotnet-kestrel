# Tasks: Wire contract — one route catalog with endpoint check, consumer Unity codegen, section ↔ message contract and wire-mapper conformance (slice 014 wire-contract)

**Input**: [plan.md](plan.md) (authoritative design, decisions D1–D17, narrowings N1–N12, migration notes BC-1..BC-6),
[spec.md](spec.md) (US1–US5, FR-001..FR-091, BC-1..BC-6, SC-001..SC-009), [research.md](research.md) (R1–R17, VERIFY
V1–V14), [data-model.md](data-model.md), [contracts/public-api.md](contracts/public-api.md),
[contracts/wiregen-cli.md](contracts/wiregen-cli.md), [contracts/generated-files.md](contracts/generated-files.md)

**Tests**: mandatory (constitution VI). Each story phase writes its tests before or alongside the code they cover and
ends green. Couchbase tests skip with a message when `COUCHBASE_CONNSTR` is not set; the full Couchbase suite runs in the
final gate (T084). Opt-in suite: `GAMESERVER_PERF=1` (pre-serving check budget, SC-007). Scenario ids (`US1-3`) refer to
the spec's acceptance scenarios.

**Format**: `- [ ] T0NN [P] [USn] description with paths`, then `Done when:` (verifiable) and the FR/SC ids covered.
`[P]` = different files and no open dependency. Record findings as `Note:` lines under the task.

**MVP = US1 (Phases 1–3).** The catalog plus the endpoint check (FG-026 (1), (3)) is the root of FG-026 and the input of
every later story. Phase 3 depends only on Phase 2 and ends at a shippable checkpoint (T038).

**Story mapping follows the spec, not the plan's phase list** (see Planning notes 1–3): the typed mapper registration
(FR-050/FR-051/FR-054, acceptance US3-4) is US3; hidden members and conformance (FR-060..FR-066) are US4; the SDK
`SectionKey` types move to Phase 2 because US2's fixture client compiles generated section code; the SDK csproj switch
moves to US3 because the generated package files land there.

**[VERIFY]**: a runtime or tooling fact from research to confirm on the pinned versions (.NET SDK 10.0.403, ASP.NET Core
10.0.x, `Grpc.Tools` 2.84.0, `Google.Protobuf` 3.36.2) **before** the task that depends on it. Each check runs in a
throw-away project under the scratch/temp folder (never committed). Record the result as a `Note:`; if the fact is wrong,
apply the stated fallback and record the deviation (contract, messages, docs).

| Item | Fact | Fallback | Check | Used by |
| --- | --- | --- | --- | --- |
| V1 | A lifecycle service registered first in `AddGameServer` runs `StartingAsync` before `GenericWebHostService.StartAsync` (pipeline + Kestrel bind) on 10.0.x; `app.DataSources` then holds endpoints mapped after `UseGameServer` (incl. `MapGroup`); its `GameServerStartupException` reaches `RunAsync`/`FindStartupException`, `TestServer` and `WebApplicationFactory` start; no port bound | 1. `IStartupFilter`; 2. first-request refusal + `StopApplication` (never silent) | T002 (**decision gate**) | T033 |
| V2 | Re-materializing `DataSources.Endpoints` + validation for 200 endpoints ≤ 50 ms | skip per-endpoint re-validation for endpoints already seen by `UseGameServer` (key `METHOD template`) | T035 (needs the implementation) | T033 |
| V3 | A second `Endpoints` enumeration carries the same metadata (conventions applied) | validate in `IStartupFilter` after the pipeline's own materialization | T002 | T033 |
| V4 | Export mode (`Build()` + resolve `WireCatalog`) on SampleHost and Fixture.Host, Local flavor, empty environment: no store open, no socket, no secret read; side effects listed | resolve the catalog through a minimal provider scope; document side effects | T004 (composition part), T039 (final) | T039, T042 |
| V5 | `dotnet run --project <host> --no-launch-profile [--no-build] -c <cfg> -- --environment Local --export-wire-contract <f>`: `--environment` reaches `WebApplication.CreateBuilder` and is not swallowed | child environment `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` | T004, T039 | T042, T053 |
| V6 | `Grpc.Tools` 2.84.0 layout `tools/<os>_<arch>/protoc[.exe]`, includes `build/native/include/google/protobuf/*.proto`; `$(PkgGrpc_Tools)` set under CPM with `GeneratePathProperty`; `protoc --version` = `libprotoc 35.1`; Unix exec bit after restore | package folder from `NUGET_PACKAGES` / `~/.nuget/packages`; `File.SetUnixFileMode` | T005 | T040, T045 |
| V7 | Per-file protoc runs reproduce today's committed files byte for byte (`Core.cs` nested, `Identity/Economy/Iap.cs`, KestrelDemo `SampleGame.cs`) | accept a one-time diff (paths and `.meta` GUIDs unchanged), record it here | T005 | T059, T070 |
| V8 | C# naming from descriptors: `csharp_namespace` or package PascalCase (`samplegame.v1` → `Samplegame.V1`), class = message name, nested `Outer.Types.Inner` | cross-check by scanning `partial class` names in the generated C# | T005 | T045, T048, T051 |
| V9 | .NET 10 STJ contract: `JsonPropertyInfo.AttributeProvider`, `Set`, `AssociatedParameter`; sample JSON deserializes `required`/`init`/positional records with `GameServerJson.Options` | constructor parameter names matched case-insensitively when `AssociatedParameter` is null | T006 | T013, T065 |
| V10 | `dotnet msbuild <proj> -getItem:PackageVersion` prints JSON with `Version` metadata under CPM on SDK 10.0.403; no CPM → no items | skip the `Grpc.Tools` check with a printed note (`Google.Protobuf` still checked through the export) | T007 | T043 |
| V11 | `Runtime/Plugins/Google.Protobuf.dll` product/file version = 3.36.2 (assembly version 3.0.0.0) | compare `AssemblyInformationalVersion` from metadata | T007 | T044 |
| V12 | `MapHealthChecks` → `RouteEndpoint` without `HttpMethodMetadata`; `MapOpenApi(route)` raw text `/gm/v1/openapi/{documentName}.json`, GET; `WithDeclaration` metadata visible on both | declare the probe with explicit GET + HEAD | T003 | T010, T019, T020 |
| V13 | A default interface member on `IRequestOutcomeFeature` passes the framework analyzers (`latest-recommended`, warnings as errors) | `RequestOutcomeExtensions.RouteName(this HttpContext)` reading the endpoint + catalog | T003 | T036 |
| V14 | Generated route/section files and protoc output compile in a netstandard2.1 / `LangVersion 9` / `TreatWarningsAsErrors` project under the consumer fixture's `Directory.Build.props` (analyzers, banned APIs) | adjust the renderer (no target-typed `new`, no file-scoped namespace) | T008 | T047, T048, T052 |
| V15 | The tool and the tests start child `dotnet` processes with the same host that runs them (`DOTNET_HOST_PATH` → `DOTNET_ROOT/dotnet` → PATH, as `tests/GameServer.Architecture.Tests/RepositoryTooling.cs`); on this machine `dotnet` is `$HOME/.dotnet10/dotnet.exe` and may not be on PATH | config/CLI option `--dotnet <path>` documented in wiregen-cli §2 | T004 | T042, T053, T063, T071 |

V1–V14 are the `[VERIFY]` items of research.md; V15 is added here (the tool runs the host with `dotnet run`, and the
framework suite runs the tool; the existing tests already needed this resolution order).

**Environment**: `"$HOME/.dotnet10/dotnet.exe"`; build `build GameServer.slnx -c Release`; test
`test --solution GameServer.slnx -c Release --no-build` (one class: `--project X --filter-class "*Name"`). Couchbase
variables as in `AgentMemories/features/p05-speckit/00_summary.md` ("Environment"; password from the user variable
`COUCHBASE_PW`, never printed). **Never grep recursively over `samples/`** (Unity `Library/` under KestrelDemo); open
KestrelDemo files by exact path only (`samples/SampleGame/KestrelDemo/Assets/SampleGame/...`). Unity runs only through
`sdk/unity/ci/smoke-build.ps1` (owner's Unity 6 install; owner-assisted when the agent cannot start Unity).
`.specify/feature.json` already points to `specs/014-wire-contract`. Repository docs are written in Vietnamese, except the
consumer guide (English); keys, field names, commands and code stay literal. Commits: no Claude signature lines (owner
rule).

## Phase 1: Setup

- [ ] T001 Baseline on branch `014-wire-contract` (base `2999a20`): Debug and Release build of `GameServer.slnx`; full
  suite in memory (no `COUCHBASE_*`) and with `GAMESERVER_TEST_STORE=File`. Also record, into the scratch folder (not
  committed): (a) SHA-256 of every file under `sdk/unity/com.gameserver.client/Runtime/Generated/` and of
  `samples/SampleGame/KestrelDemo/Assets/SampleGame/Generated/SampleGame.cs`; (b) the GUID of every `.meta` under
  `sdk/unity/com.gameserver.client/` (input of T063); (c) a public-API snapshot of the SDK assembly
  (`sdk/GameServer.Client` Release build: every public type and member signature, sorted; input of SC-008 in T063).
  Done when: 0 warnings in both configurations, 0 failures; total/passed/skipped counts, the three scratch file paths and
  the `git status` of the tree recorded as a Note (reference for T038, T054, T064, T069, T083).
- [ ] T002 **[VERIFY] V1 and V3 — decision gate for FR-012** in a throw-away web project under the scratch folder that
  references `src/GameServer.Platform.Web` (nothing committed): register an `IHostedLifecycleService` first in the service
  collection; after `UseGameServer`-like code, map one endpoint directly and one inside `MapGroup("/g")`; in
  `StartingAsync` enumerate `((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints)` and throw a
  `GameServerStartupException` when the late endpoints are present. Run (a) `app.RunAsync()` on a fixed free port, then
  bind the same port with a `TcpListener`; (b) `TestServer` (`UseTestServer`) `StartAsync`; (c) a
  `WebApplicationFactory<Program>` client creation. Assert metadata (a custom metadata object, `HttpMethodMetadata`) on
  the re-enumerated endpoints (V3).
  Done when: a Note records for each of (a)–(c): whether `StartingAsync` ran before the server started, whether the late
  endpoints (direct and grouped) were visible with their metadata, the exception type that surfaced and whether the
  port stayed free; and the **decision** for T033: `StartingAsync` (primary), else fallback 1 `IStartupFilter` (repeat
  (a)–(c) with it), else fallback 2 first-request refusal + `StopApplication` (exit 1 via `ServeAsync`). If a fallback is
  chosen, the Note lists the doc/contract lines to change (plan D8, research R8, PCR platform §20). (FR-012, BC-2)
- [ ] T003 [P] **[VERIFY] V12 and V13**: (V12) throw-away app with `MapHealthChecks("/health/live")` and
  `MapOpenApi("/gm/v1/openapi/{documentName}.json")`, each followed by `.WithMetadata(new object())` (stand-in for
  `WithDeclaration`); dump `RouteEndpoint.RoutePattern.RawText`, `HttpMethodMetadata` and the custom metadata. (V13)
  in a throw-away library with the framework's `Directory.Build.props` settings (`AnalysisLevel latest-recommended`,
  `TreatWarningsAsErrors`), a public interface with a default member `string? RouteName => null;` and a sealed class
  implementing it.
  Done when: a Note records the raw texts, the method metadata (none / GET) and that the custom metadata is visible
  (V12), and whether the build is clean (V13); fallbacks chosen for T010/T020/T036 if needed.
- [ ] T004 [P] **[VERIFY] V4 (composition part), V5 and V15**: (V4) with no `COUCHBASE_*`, no user secrets and the
  network adapter unchanged, build the SampleHost and Fixture.Host compositions only (a scratch copy of each `Program`
  that stops after `builder.Build()` and disposes; `--provision` is not usable here because it opens the store) in the
  Local flavor; list every file and folder created under `samples/SampleHost/` and
  `tests/ConsumerFixture/src/Fixture.Host/` (git status + `logs/`, `_database/`, `.local/`). (V5) run the scratch copy
  with `dotnet run --project <scratch> --no-launch-profile -c Release -- --environment Local` and print
  `builder.Environment.EnvironmentName` after `GameServerCommandLine.Parse(args)` → `CreateBuilder(HostArgs)`. (V15) in a
  throw-away test, the `DOTNET_HOST_PATH` value under `dotnet test`, and under `dotnet run` of a console.
  Done when: a Note records the side effects (expected: Serilog `logs/` only; none under `_database/`), whether
  `--environment Local` selects the Local flavor through `GameServerCommandLine.Parse` → `CreateBuilder(HostArgs)`, and
  the `DOTNET_HOST_PATH` values; fallbacks chosen for T039/T042. Nothing written outside the scratch folder except
  gitignored log files (deleted afterwards).
- [ ] T005 [P] **[VERIFY] V6, V7 and V8**: inspect the restored `Grpc.Tools` 2.84.0 package folder (`tools/*`,
  `build/native/include/google/protobuf/`), run `protoc --version`; with that protoc and the same `-I` roots as
  `sdk/GameServer.Client/GameServer.Client.csproj` (`proto/`, each module `Protos` folder, `proto` as extra import dir),
  run one `--csharp_out` per framework proto and for `samples/SampleGame/SampleGame.Adapters/Protos/sample_game.proto`
  into scratch folders (layout: `Core.cs` under `gameserver/core/v1/`, the others flat) and compare bytes with the
  committed files (T001 hashes). (V8) a scratch proto without `csharp_namespace` (`package fixture.v1;`) with a nested
  message: run the descriptor pass (`--descriptor_set_out --include_imports`) and compare the C# type names derived by
  the rule with the generated C#.
  Done when: a Note records the protoc path per OS folder name, the version line, the exec-bit state (Windows: n/a),
  byte equality per file (or the exact diff, then the V7 fallback is recorded for T059/T070), and the V8 name pairs.
- [ ] T006 [P] **[VERIFY] V9** in a throw-away test project referencing `src/GameServer.Platform` (for
  `GameServerJson.Options`): zh-pvp-shaped states copied as plain records into the scratch project (`init` properties,
  `required` members, a positional record `PendingChest(string Tier, int Chapter)`, `IReadOnlyDictionary`,
  `IReadOnlyList`, nullable record, `DateTimeOffset?`, an enum, a computed get-only property); print for every
  `JsonPropertyInfo`: name, `AttributeProvider` member name, `Set != null`, `AssociatedParameter`; round-trip a JSON
  built from those names.
  Done when: a Note records which members expose a setter or an associated parameter, that the computed property has
  neither, and that deserialization of `required`/`init`/positional members succeeds; fallback chosen for T013 if needed.
- [ ] T007 [P] **[VERIFY] V10 and V11**: `dotnet msbuild samples/SampleHost/SampleHost.csproj -getItem:PackageVersion`
  and the same on `tests/ConsumerFixture/src/Fixture.Host/Fixture.Host.csproj` and on a scratch project without CPM;
  `FileVersionInfo.GetVersionInfo("sdk/unity/com.gameserver.client/Runtime/Plugins/Google.Protobuf.dll")` (product,
  file) and `AssemblyName.GetAssemblyName(...)`.
  Done when: a Note records the JSON shape (item `Identity`, `Version` metadata; remember 013 V2: one switch may print
  plain text, JSON escapes `+`), the result without CPM, and the dll's product/file/assembly versions; fallbacks chosen
  for T043/T044.
- [ ] T008 [P] **[VERIFY] V14**: in a scratch copy of the consumer-fixture layout (its `Directory.Build.props` and
  `Directory.Packages.props` imports), a `netstandard2.1` / `LangVersion 9.0` / `ImplicitUsings disable` project
  referencing `sdk/GameServer.Client/GameServer.Client.csproj`, containing the T005 protoc outputs plus hand-written
  copies of the route and section file examples of [generated-files.md](contracts/generated-files.md) §3–§4 (with a
  stand-in `SectionKey<T>`/`SectionTable` if T018 has not landed).
  Done when: a Note records a clean build (0 warnings) or each diagnostic with the renderer rule that avoids it
  (expected traps: `ImplicitUsings enable` from the fixture props → CS8773 global usings under C# 9; Banned API analyzer
  on generated code); the property overrides `Fixture.Client.csproj` needs are listed for T052.

**Checkpoint**: baseline counts known; V1 decided (primary hook or fallback) with its consequences; V3–V15 confirmed or
their fallbacks recorded. V2 is measured by T035.

## Phase 2: Foundational (behaviour-neutral)

Nothing in this phase changes what a host accepts: no declaration exists yet, the catalog is not validated at startup,
the untyped mapper is not marked obsolete (CS0618 would break the build until every call site migrates, T057) and the
registry keeps its winner. Each task ends with the full suite green and unchanged.

- [ ] T009 Core declarations in a new file `src/GameServer.Core/Routing/RouteDeclaration.cs` (research R1, D1,
  contracts/public-api.md §1): `RouteSurface`, `RouteConditions.DevelopmentTools`, sealed `RouteDeclaration` (constructor
  canonicalizes only: exactly one leading `/`, methods upper-cased, distinct, ordinal-sorted; never validates), factories
  `Get/Post/Put/Delete/AnyMethod/Command` (`Command`: name = operation unless given, POST, profile/permission from the
  `CommandDescriptor`, surface `GmApi` for Gm, else `PlayerApi`), `ToString()` = `"POST /api/v1/battle/start
  (battle.start)"`. `src/GameServer.Core/Modules/ModuleDescriptor.cs`: `Routes` (default `[]`); `PublicEndpoints` doc
  comment per contract.
  Tests in a new file `tests/GameServer.Platform.Web.Tests/RouteDeclarationTests.cs`: canonical template (`api/v1/x`,
  `/api/v1/x`, `//api/v1/x` → `/api/v1/x`), method canonicalization, empty methods = any, `Command` defaults for a
  Player, Dev and Gm descriptor (name, method, profile, permission, surface), explicit name/condition kept, two
  identical declarations are not `Equals` (reference type, R1). `tests/GameServer.Architecture.Tests/ArchitectureTests.cs`
  ARC-01 rule green unchanged (Core still free of ASP.NET/Protobuf).
  Done when: green; nothing reads `Routes` yet. (FR-001, FR-003, FR-004, FR-008)
- [ ] T010 [P] Platform.Web routing types (after T003, T009; research R2, R4): new files
  `src/GameServer.Platform.Web/Routing/RouteDeclarationMetadata.cs` (`sealed record RouteDeclarationMetadata`),
  `src/GameServer.Platform.Web/Routing/DeclaredRouteExtensions.cs` (`MapDeclared` → `MapMethods(template, methods, …)`
  or `Map(template, …)` for any-method + metadata, returns `RouteHandlerBuilder`; `WithDeclaration<TBuilder>` adds the
  metadata), `src/GameServer.Platform.Web/Routing/PlatformRoutes.cs` (`Time`, `Snapshot`, `Config`, `Receipt`,
  `GmOpenApi` with `RouteConditions.DevelopmentTools`, `All`; values of data-model §1.3, templates from the existing
  `CoreEndpoints` constants and `GmOpenApi.cs:22`, so the strings exist once). `CoreEndpoints` still maps its strings.
  Tests in a new file `tests/GameServer.Platform.Web.Tests/DeclaredRouteMappingTests.cs` on a bare `WebApplication`:
  `MapDeclared` of a POST, a GET/HEAD and an any-method declaration → `RoutePattern.RawText`, `HttpMethodMetadata`
  (absent for any), `RouteDeclarationMetadata` present; `WithDeclaration` on `MapHealthChecks` and `MapOpenApi` (V12);
  `PlatformRoutes.All` = the five declarations with the data-model names.
  Done when: green. (FR-002 platform part, FR-005)
- [ ] T011 Command mapping overloads (after T010; research R4, contracts/public-api.md §2.2):
  `src/GameServer.Platform.Web/Commands/CommandEndpointFactory.cs` (3 `MapPlayerCommand`, 1 `MapDevCommand`) and
  `src/GameServer.Platform.Web/Commands/GmCommandEndpointFactory.cs` (2 `MapGmCommand`) gain `RouteDeclaration route`
  overloads that map `route.Template`, attach `CommandOperationMetadata(route.Operation)` and `RouteDeclarationMetadata`,
  and throw `ArgumentException` when `route.Operation` is null (GM: also `route.Permission` null, and the existing
  `{playerId}` check). Existing string overloads unchanged (delegate to a shared private method, no behaviour change).
  Tests in a new file `tests/GameServer.Platform.Web.Tests/DeclaredCommandMappingTests.cs`: each overload attaches both
  metadata objects and the declared template; null operation/permission → `ArgumentException` naming the route;
  profile/permission conflicts are **not** thrown (the check reports them, R4).
  Done when: green; existing command endpoint tests green unchanged. (FR-005)
- [ ] T012 [P] Typed wire mapper types (research R15, contracts/public-api.md §2.5) in
  `src/GameServer.Platform.Web/Aggregates/AggregateWireMapperRegistry.cs` (or sibling files in the same folder):
  `IAggregateWireContract`, `AggregateWireMapper<TState, TWire>` (`Message` = `new TWire().Descriptor`, `Parser`,
  `Map`, `ToPayload` through the typed delegate), `WireMapperOptions.Hidden/MayStayDefault` (ordinal-sorted, distinct),
  typed `AddAggregateWireMapper<TState, TWire>(id, map, configure)`; `AggregateWireMapperRegistry.TryGetContract` and an
  internal `Duplicates` list (ids registered more than once). **Not yet**: `[Obsolete]` on the untyped overload (T057),
  failing on duplicates (validation wired in T032), changing which registration wins.
  Tests in a new file `tests/GameServer.Platform.Web.Tests/WireMapperRegistrationTests.cs` (unit part): typed mapper
  payload equals the untyped mapper's payload for the same function; contract exposes message full name, parser, hidden
  and may-stay-default lists; untyped mapper has no contract; `Duplicates` lists an id registered twice.
  Done when: green; every existing mapper test green unchanged. (FR-050, FR-060 registration part)
- [ ] T013 State member tree (after T006; research R16, data-model §7.1): new internal
  `src/GameServer.Platform.Web/Aggregates/StateMembers.cs` (members = `JsonPropertyInfo` with `Set != null` or an
  `AssociatedParameter` under `GameServerJson.Options`; C# member names from `AttributeProvider`; node kinds scalar,
  object (depth limit 8, cycle → not fillable), nullable, collection (transparent), dictionary (key attributed to the
  dictionary path, value transparent); `Resolve(path)` for hidden-path validation). `InternalsVisibleTo
  GameServer.TestKit` in `src/GameServer.Platform.Web/GameServer.Platform.Web.csproj`; extend
  `tests/GameServer.Architecture.Tests/TestKitArchitectureTests.cs` (013 list of internal types the kit may use) with
  `StateMembers` and its node types only.
  Tests in a new file `tests/GameServer.Platform.Web.Tests/StateMembersTests.cs` with zh-pvp-shaped records (V9):
  paths for nested records, list elements (`Slots.UnlockEndUtc`), dictionary values, nullable members; computed
  properties absent; `Resolve` of an unknown path = false.
  Done when: green; `TestKitArchitectureTests` green with the new allowed types. (FR-060, FR-062 groundwork)
- [ ] T014 Catalog build (after T009, T012, T013; research R5, R6, data-model §1.2): new files under
  `src/GameServer.Platform.Web/Catalog/`: `WireCatalog.cs` (public: `Routes` ordinal by name, `Sections` ordinal by id,
  `TryGetRoute`, `TryGetSection`, `FindRouteName(method, template)` with any-method matching), `RouteEntry.cs`
  (`PlatformOwner`, `HostOwner`), `SectionEntry.cs`, `CatalogOrigin`, `RouteConditions.cs` (`RouteConditionRegistration`,
  `AddRouteCondition`), internal `WireCatalogBuilder.cs` (registry modules incl. service-only flagged, `PlatformRoutes.All`,
  `HostRouteDeclarations`, sections = client-visible aggregates with a mapper, owner = module declaring the aggregate,
  message from the contract or null; origin `Framework` iff owner `platform` or module type assembly name starts with
  `GameServer.`, host = `Game`), internal `HostRouteDeclarations.cs`. `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs`:
  `DeclareRoutes(params RouteDeclaration[])`, lazy singleton `WireCatalog`, `HostRouteDeclarations`, condition
  `development_tools` (`GameServerOptions.AllowsDevelopmentTools`).
  Tests in a new file `tests/GameServer.Platform.Web.Tests/WireCatalogTests.cs` (build part) via
  `SampleTestHost.BuildApp` with a test module declaring routes and a typed client-visible aggregate: owner, origin
  (framework module vs test assembly vs host), service-only flag, platform entries present, section entry with message
  full name, untyped section without message, `FindRouteName` for an any-method declaration, resolving the catalog
  starts nothing (no hosted service started, store untouched).
  Done when: green. (FR-001, FR-003, FR-030 groundwork, FR-050 catalog part)
- [ ] T015 Catalog validation (after T014; research R3, R5, data-model §2.1): internal
  `src/GameServer.Platform.Web/Catalog/WireCatalogRules.cs` and `WireCatalog.Validate(GameServerOptions)` returning one
  line per fault, exact texts of data-model §2.1: bad name (regex + ≤ 64), duplicate name (both owners), duplicate method
  + template (any-method overlaps every method; both names and owners), invalid template (`RoutePatternFactory.Parse`
  message), missing version segment (health probes excepted), Gm without permission / non-Gm with permission, unknown
  condition, `PublicEndpoints` vs profile (BC-5), duplicate mapper (BC-4), unknown hidden member (via `StateMembers`),
  unknown may-stay-default field (proto field names on the descriptor); untyped mapper → a separate Warning list
  (`ValidationWarnings` internal) for T057.
  Tests in `tests/GameServer.Platform.Web.Tests/WireCatalogTests.cs` (validation part, same file as T014 → after it): one
  test per line above with the exact text, each fault seeded once in an otherwise valid composition → exactly one line;
  a valid composition → empty.
  Done when: green; still not called at startup. (FR-006, FR-007, FR-054 line, FR-060 startup line)
- [ ] T016 Catalog export (after T015; research R5, data-model §3, contracts/generated-files.md §1):
  `src/GameServer.Platform.Web/Catalog/WireCatalogExport.cs` (`Format = "gameserver.wire-contract/1"`) and
  `WireCatalog.ToExportJson()` (UTF-8 without BOM, LF, 2-space indent, property order of data-model §3, nulls and empty
  arrays omitted, `serviceOnly` only when true, `methods` omitted for any, routes by name, sections by id,
  `frameworkVersion` = `GameServerVersion.Framework`, `protobufVersion` = informational version of the loaded
  `Google.Protobuf`).
  Tests in `tests/GameServer.Platform.Web.Tests/WireCatalogTests.cs` (export part, after T015): structure and order
  match the generated-files.md §1 example for the same declarations; two builds give identical bytes; no `\r`, no BOM;
  no timestamp, path, machine or environment value; hidden and may-stay-default lists exported.
  Done when: green. (FR-030, FR-065 export part)
- [ ] T017 Endpoint validation entry point (behaviour-neutral; research R8): new internal
  `src/GameServer.Platform.Web/Hosting/EndpointValidation.cs` running `EndpointSecurityValidator.Validate` and
  `CommandRouteValidator.Validate` over a given endpoint set with the same inputs `UseGameServer` passes today
  (`src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs:113-119`); `UseGameServer` calls it. The Public
  allowlist passed to `EndpointSecurityValidator` becomes operator allowlist ∪ module `PublicEndpoints` ∪ templates of
  Public declarations owned by modules or the platform (host Public declarations excluded, plan N6) ∪ GM OpenAPI when
  mapped (unchanged). No declaration exists yet, so the effective allowlist is unchanged.
  Tests: `tests/GameServer.Platform.Web.Tests/StartupValidationTests.cs` green unchanged; new cases in
  `tests/GameServer.Platform.Web.Tests/EndpointValidationTests.cs`: a module Public declaration allowlists its template; a
  host Public declaration does not (the operator allowlist still must list it).
  Done when: green. (FR-006, FR-012 groundwork, SEC-09)
- [ ] T018 [P] SDK section key types (moved from the plan's phase 6: the generated section files of T048/T052 compile
  against them; research R14, contracts/public-api.md §5): new file
  `sdk/unity/com.gameserver.client/Runtime/Core/SectionKey.cs` (`SectionKey`, `SectionKey<T>` with `TypedParser`,
  `SectionTable` (duplicate id → `ArgumentException`, `TryGet`, `Keys`), `SectionRead`), C# 9 only, no reflection; a
  hand-written `SectionKey.cs.meta` copied from the MonoImporter template of `Runtime/Core/SectionStore.cs.meta` with a
  fresh random GUID (32 lower-case hex, unique in the package).
  Tests in a new file `tests/GameServer.Client.Tests/SectionTableTests.cs` (key part; SDK alias `sdk`): table lookup,
  duplicate id throws, `Parser` of `SectionKey<T>` parses the same bytes as `TypedParser`.
  Done when: green; `dotnet build sdk/GameServer.Client -c Release` 0 warnings; the new GUID is not used by any other
  `.meta` under `sdk/unity/`. (FR-053 groundwork, FR-034)

**Checkpoint**: Release build 0 warnings; full suite green with the T001 counts plus the new tests; startup behaviour
unchanged (no declaration, no catalog validation at startup).

## Phase 3: US1 — Every server endpoint is declared once and checked against what is mapped (P1, FG-026 (1), (3)) MVP

Goal: every route the framework, the samples, the template and the fixture can map is a declaration; the host refuses
undeclared or mismatching endpoints, also when they are mapped after `UseGameServer`, before listening.
Independent test: T032 (fault classes) + T033 (late endpoints) + T034 (flavor × hosting matrix, consumer read).
Depends on: Phase 2. **Order inside the phase**: all declarations and test-endpoint declarations (T019–T030) land before
the check is switched on (T032), otherwise every undeclared framework route fails startup (plan risk "BC-1 floods").
Each migration task maps from the declaration and keeps the existing `public const string …Route` members as aliases of
the template (one string per route in the server code, R2); if the code differs from data-model §1.3 in a method,
permission or profile, the code wins and the task records the difference as a Note (PCR is corrected in T078).

### Declarations (behaviour-neutral until T032)

- [ ] T019 [US1] Platform routes (after T010): `src/GameServer.Platform.Web/Endpoints/CoreEndpoints.cs` maps time,
  snapshot, receipt and config with `MapDeclared(PlatformRoutes.X, …)` and the same `As*Endpoint` calls as today; its
  route constants become aliases of the `PlatformRoutes` templates (still `internal`; `PlatformRoutes` is the public
  source, FR-002). `src/GameServer.Platform.Web/OpenApi/GmOpenApi.cs`: `.WithDeclaration(PlatformRoutes.GmOpenApi)`.
  Tests: existing core endpoint, time, snapshot, receipt, config and `tests/GameServer.Gm.Tests/OpenApiTests.cs` green
  unchanged; `DeclaredRouteMappingTests` gains "every endpoint mapped by `CoreEndpoints` carries its
  `RouteDeclarationMetadata`".
  Done when: green; `docs/api/gm-openapi.json` unchanged. (FR-002, FR-005)
- [ ] T020 [P] [US1] Observability (after T010): new file `src/GameServer.Platform.Observability/ObservabilityRoutes.cs`
  (`Live` = `AnyMethod("platform.health_live", "/health/live", Public, Host)`, `Ready` = GET
  `platform.health_ready`, per V12); `src/GameServer.Platform.Observability/ObservabilityModule.cs`: `Routes = [Live,
  Ready]`, `.WithDeclaration(...)` on both `MapHealthChecks`, `PublicEndpoints` removed (the module Public declarations
  allowlist them through T017).
  Done when: `tests/GameServer.Observability.Tests` green; probes still answer without authentication. (FR-002, FR-006)
- [ ] T021 [P] [US1] Identity (after T011): new file `src/GameServer.Modules.Identity.Adapters/IdentityRoutes.cs` (9
  declarations of data-model §1.3; command routes via `RouteDeclaration.Command(<Handler>.Descriptor, template)`; GM
  ban/unban/cancel carry the permission the mapping passes today); `src/GameServer.Modules.Identity.Adapters/IdentityModule.cs`:
  `Routes`, map every route from its declaration (`MapPlayerCommand`/`MapGmCommand` overloads, `MapDeclared` + the
  existing `AsPublicEndpoint`/`AsPlayerEndpoint` for login/logout), `PublicEndpoints` removed (`:69`), constants `:32-40`
  kept as aliases.
  Done when: identity, provider, link, deletion and GM ban tests green unchanged. (FR-002, FR-005, FR-006)
- [ ] T022 [P] [US1] Economy and cheat (after T011): new files `src/GameServer.Modules.Economy/EconomyRoutes.cs`
  (`economy.adjust` command, `economy.ledger` GET, template consts) and `src/GameServer.Modules.Economy/CheatRoutes.cs`
  (`cheat.grant` Dev command) next to the business-project descriptors (`EconomyModuleDefinition.cs` and the cheat
  definition) with `Routes`; `src/GameServer.Modules.Economy.Adapters/EconomyModule.cs`: `AdjustRoute`/`LedgerRoute`
  (`:23-24`) and `CheatModule.GrantRoute` (`:77`) become aliases of the template consts; map from declarations.
  Done when: economy GM, ledger and cheat tests green unchanged; ARC-01 green (no ASP.NET/Protobuf in the business
  project). (FR-002, FR-004, FR-005)
- [ ] T023 [P] [US1] IAP (after T011): new file `src/GameServer.Modules.Iap/IapRoutes.cs` (`iap.purchase`,
  `PurchaseTemplate`), descriptor `Routes` in `src/GameServer.Modules.Iap/IapModel.cs` (or wherever the IAP descriptor
  is built — research says the adapter: then `Routes` goes there and only the declaration class sits in the business
  project); `src/GameServer.Modules.Iap.Adapters/IapModule.cs:22` `PurchaseRoute` = alias; map from the declaration.
  Done when: IAP tests green unchanged; a Note records where the descriptor lives. (FR-002, FR-005)
- [ ] T024 [US1] GM part 1 (after T011): new file `src/GameServer.Modules.Gm.Adapters/GmRoutes.cs` with all 27 `gm.*`
  declarations of data-model §1.3 (permissions copied from each `AsGmEndpoint(...)` call; `gm.auth_login` condition
  `development_tools`; `gm.sso_start`/`gm.sso_callback` condition `gm.sso`); `src/GameServer.Modules.Gm.Adapters/GmModule.cs`:
  `Routes = GmRoutes.All`, map auth/CSRF/login/logout/me/UI/health/rotate/version (`:54-61`) from declarations,
  `ConfigureServices` registers `services.AddRouteCondition("gm.sso", sp => <GmOptions>.Oidc.IsConfigured)`,
  `PublicEndpoints` removed (`:93`); `src/GameServer.Modules.Gm.Adapters/Sso/GmSso.cs` (`:24-25`) maps from
  `GmRoutes.SsoStart/SsoCallback`.
  Done when: `tests/GameServer.Gm.Tests` auth, CSRF, SSO (`SsoTests`, configured and not configured), UI and ops tests
  green unchanged. (FR-002, FR-005, FR-006, FR-011 conditions)
- [ ] T025 [US1] GM part 2 (after T024, same `GmRoutes.cs`): `src/GameServer.Modules.Gm.Adapters/GmPlayerEndpoints.cs`
  (`:85-90`), `src/GameServer.Modules.Gm.Adapters/Ops/GateEndpoints.cs` (`:30-32`),
  `src/GameServer.Modules.Gm.Adapters/Staff/StaffEndpoints.cs` (`:43-49`; `/gm/v1/staff` GET and POST = two declarations
  `gm.staff_list`, `gm.staff_create`) map from `GmRoutes`. Run `tests/GameServer.Gm.Tests/OpenApiTests.cs`: GM route
  shapes must not change; if the committed `docs/api/gm-openapi.json` differs only in a way caused by `MapDeclared`
  (e.g. operation order), review it, regenerate with `UPDATE_OPENAPI=1` and record why in a Note; any change of path,
  method, permission, CSRF flag or schema is a defect to fix, not to commit.
  Done when: GM tests green; `docs/api/gm-openapi.json` unchanged or the reviewed regeneration recorded. (FR-002, FR-005)
- [ ] T026 [P] [US1] Sample counter (after T011): declarations `counter.increment` (command), `counter.get` (POST,
  Player), `counter.gm_read` (GET, Gm + its permission) in `modules/Sample.Counter/Sample.Counter/CounterModuleDefinition.cs`
  (`Routes`); `modules/Sample.Counter/Sample.Counter.Adapters/CounterModule.cs:39,44,60` lose their inline literals and
  map from the declarations (`MapPlayerCommand(decl, …)`, `MapDeclared(decl, (Func<HttpContext, Task<IResult>>)…)`).
  Done when: `tests/Sample.Counter.Tests` and every test using `SampleTestHost` defaults green; `grep -n '"/api/\|"/gm/'
  CounterModule.cs` = 0. (FR-002 scope, FR-003, FR-005)
- [ ] T027 [P] [US1] Sample game (after T011): declarations `battle.start`, `battle.end`, `shop.buy`, `progression.set`
  (GM command) in the business project `samples/SampleGame/SampleGame/SampleGameModel.cs` (or a new
  `SampleGameRoutes.cs` there — server namespace `SampleGame`, distinct from the client's `SampleGame.Client`) and the
  descriptor `Routes`; `samples/SampleGame/SampleGame.Adapters/SampleGameModule.cs:20-24` constants become aliases; map
  from declarations.
  Done when: `tests/SampleGame.Tests` green. (FR-003, FR-005)
- [ ] T028 [P] [US1] Template (after T011): `template.earn` in `templates/module/Template.Module/TemplateModuleDefinition.cs`
  (`Routes`), `templates/module/Template.Module.Adapters/TemplateModule.cs:26` maps from it (no inline literal).
  Done when: Release build 0 warnings (the template has no test project; it is exercised in T066). (FR-003, FR-005)
- [ ] T029 [P] [US1] Consumer fixture (after T011): `fixture.save` = `RouteDeclaration.Command(SaveNoteHandler.Descriptor,
  "/api/v1/fixture/save")` in `tests/ConsumerFixture/src/Fixture.Game/FixtureModuleDefinition.cs` (`Routes`);
  `tests/ConsumerFixture/src/Fixture.Game/FixtureModule.cs`: `SaveRoute` alias, `MapPlayerCommand(FixtureRoutes.Save, …)`.
  Done when: `tests/ConsumerFixture/tests/Fixture.Tests` green. (FR-003, FR-005; BC-1 shape for games)
- [ ] T030 [US1] Framework tests that map endpoints outside modules declare them (research code facts, BC-1/BC-2;
  before T032): ad-hoc `MapGet/MapPost` in `tests/GameServer.Platform.Web.Tests/ErrorContractTests.cs` (4),
  `RequestLogHost.cs` (2), `RequestOutcomeFeatureTests.cs` (2), `StartupValidationTests.cs` (6),
  `tests/GameServer.Observability.Tests/DeploymentTests.cs` (1), `ObservedHost.cs` (1), `tests/GameServer.Gm.Tests/GmAuthTests.cs`
  (1), plus ad-hoc command mappings in `AfterExecuteHookTests.cs`, `AnalyticsSeamTests.cs`, `RequestOutcomeFeatureTests.cs`,
  `UncataloguedCommitCodeTests.cs`, `StartupValidationTests.cs` (4). (`tests/GameServer.Architecture.Tests/RunLocalScriptTests.cs:156`
  writes a plain `WebApplication`, not a game server: unaffected.) Each test declares its routes with
  `gs.DeclareRoutes(...)` in the `modules` callback. **Trap**: passing `modules` replaces the default modules
  (`SampleTestHost.StartAsync(modules ?? DefaultModules)`), so the callback must call
  `SampleTestHost.DefaultModules(gs)` (or the host helper's own defaults) first; add a small helper (e.g.
  `SampleTestHost.WithRoutes(params RouteDeclaration[])` in `tests/Shared/SampleTestHost.cs`) and use it everywhere.
  `StartupValidationTests` that assert one specific validator line declare their endpoint consistently with what they
  test (e.g. the "no profile" endpoint declared Player) so that, after T032, only the intended line is reported; where a
  test asserts the whole report, record whether it uses `ShouldContain` or equality. `ErrorContractTests.Endpoint_added_after_validation_without_profile_is_denied`
  is left for T033 (it changes meaning under FR-012).
  Done when: full suite green (check not yet on); `git grep -n "afterUseGameServer:\|beforeUseGameServer:" tests` lists
  only call sites whose routes are declared (Note lists every file changed). (BC-1, BC-2, FR-014 groundwork)

**Checkpoint**: every route the framework, samples, template and fixture map has a declaration (`WireCatalog.Routes` of
the SampleHost Local composition lists the 47 framework names of data-model §1.3 plus the sample ones); behaviour
unchanged.

### Endpoint check

- [ ] T031 [US1] `EndpointCatalogValidator` (research R7, data-model §2.2): new file
  `src/GameServer.Platform.Web/Validation/EndpointCatalogValidator.cs` (public `Validate(endpoints, catalog, context,
  includeUnmapped)`, `EndpointCheckContext`): expand endpoints into (method | `*`, canonical template) pairs; bind by
  `RouteDeclarationMetadata`, else exact pair, else "method differs" when the template is declared with other methods,
  else "not declared"; compare template (metadata-bound), methods, profile (only when exactly one
  `SecurityProfileMetadata`), operation, permission (Gm); unmapped declarations only with `includeUnmapped`, when the
  owner is registered and not service-only, its surface is mapped and its condition holds. Exact line texts of
  data-model §2.2; one line per fault; non-route endpoints ignored.
  Tests first, in a new file `tests/GameServer.Platform.Web.Tests/EndpointCatalogValidatorTests.cs` on fake
  `RouteEndpoint` lists: each fault line (not declared, template differs, method differs, profile differs, operation
  differs, permission differs, declared not mapped) exactly once with its text; health probe `*` matches an any-method
  declaration; one declaration listing GET+HEAD and two single-method declarations both match; a `MapGroup` prefix
  produces "template differs" for a metadata-bound endpoint (one line, no extra "undeclared"/"unmapped"); a plain-string
  typo yields "not declared" + "declared but not mapped" (two lines, by construction, R7); service-only owner,
  `MapPlayerApi=false`, `MapGmApi=false` and a condition that does not hold → no unmapped line; leading-slash and case
  differences in the mapped template → compared after leading-slash normalization, case-sensitive.
  Done when: green. (FR-010, FR-011, FR-014, edge cases)
- [ ] T032 [US1] Switch the check on in `UseGameServer` and `--provision` (after T019–T031): in
  `src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs` add `catalog.Validate(options)` and
  `EndpointCatalogValidator` (through `EndpointValidation`, `includeUnmapped: false`) to the aggregated report;
  `src/GameServer.Platform.Web/Hosting/ProvisioningRunner.cs` runs `catalog.Validate(options)` only (spec edge case
  `--provision`). No setting disables either (FR-013).
  Tests in a new file `tests/GameServer.Platform.Web.Tests/EndpointCatalogCheckTests.cs` through `SampleTestHost` (each
  fault seeded once, exact single line): undeclared player endpoint, undeclared GM endpoint, undeclared host endpoint
  (mapped in `beforeUseGameServer`), method mismatch, profile mismatch, operation mismatch, permission mismatch,
  duplicate name, duplicate method + template naming both owners, a module listing a template in `PublicEndpoints` whose
  declaration is Player (BC-5), a module declaring an unknown condition, two mappers for one aggregate id (BC-4, startup
  line); the same compositions in Staging and Production fail the same way; `--provision` with a duplicate route name
  → exit 1 and the provisioner is never called (`GameServerCommandLineTests` style fake `IStoreProvisioner`).
  Done when: green; full suite green (any newly failing test is a missed declaration from T030 → fix there, Note).
  (FR-006, FR-007, FR-010, FR-012, FR-013, FR-014, FR-054, SC-001, BC-1, BC-4, BC-5, US1-3, US1-4, US1-7)
- [ ] T033 [US1] Pre-serving validation of the final endpoint set (after T002 decision, T032; research R8): new internal
  `src/GameServer.Platform.Web/Hosting/EndpointValidationState.cs` (written once by `UseGameServer` after its own
  validation succeeded) and `src/GameServer.Platform.Web/Hosting/EndpointSetValidationService.cs`
  (`IHostedLifecycleService`, registered **first** in `AddGameServer`; `StartingAsync` runs `EndpointValidation.Run(final
  endpoints, includeUnmapped: true)` — security, command, catalog validators — and throws one
  `GameServerStartupException` with every line; state empty → no-op) — or the fallback chosen in T002. The request-time
  Dev refusal of 013 stays.
  Tests in a new file `tests/GameServer.Platform.Web.Tests/LateEndpointValidationTests.cs`: via `afterUseGameServer`:
  undeclared endpoint, endpoint without version segment, duplicate route+method, Public endpoint not allowlisted, Dev
  endpoint with Staging settings (compose through `settings`), command route with an unknown operation, declared route of
  a mapped module not mapped (only reported here, N7) → `StartAsync` throws `GameServerStartupException` with the same
  line texts as in-pipeline; a host process (`RunGameServerAsync` on a fixed free port, scratch host inside the test or
  `ServeAsync` directly) → exit 1, stderr lines, the port can be bound afterwards; `WebApplicationFactory` start throws.
  Rewrite `tests/GameServer.Platform.Web.Tests/ErrorContractTests.cs` `Endpoint_added_after_validation_without_profile_is_denied`
  into "is refused at startup" (moved here) and keep one test for the 013 request-time Dev refusal (settings flipped after
  start, as `DevProfileMiddlewareTests` does).
  Done when: green; full suite green. (FR-012, FR-013, FR-014, SC-001 late-mapped class, BC-2, US1-5, US1-6)
- [ ] T034 [US1] Composition matrix, completeness and consumer read (after T033): new file
  `tests/SampleGame.Tests/CatalogCompositionTests.cs`: SampleHost through the kit's `LocalFlavorFactory<Program>` for
  flavors local, dev, beta, uat, live (store overridden to in-memory as `SampleHostFlavorTests` do) × `Hosting:MapPlayerApi`
  on/off × `Hosting:MapGmApi` on/off, plus one composition with a module added `ServiceOnly()`: host starts, every mapped
  route endpoint has a catalog entry, no unmapped line, GM OpenAPI present only with development tools; framework
  completeness: the catalog's framework entries equal the data-model §1.3 name list (47, a failing diff names the
  missing/extra ones); no configuration key turns the check off (search `GameServerOptions` binding for a key containing
  `Catalog`/`EndpointCheck` = none). New file `tests/ConsumerFixture/tests/Fixture.Tests/FixtureCatalogTests.cs`: reads
  `PlatformRoutes.Config.Template`, `IdentityRoutes.LoginGuest`, the fixture host's `WireCatalog` (public API only) and
  checks `fixture.save` owner/origin `game`.
  Done when: green. (FR-001, FR-002, FR-011, FR-013, FR-014, SC-001, US1-1, US1-2, US1-5)
- [ ] T035 [P] [US1] Opt-in performance check (after T033; V2, SC-007): new file
  `tests/GameServer.Platform.Web.Tests/EndpointCheckPerformanceTests.cs` (skips unless `GAMESERVER_PERF=1`): a synthetic
  module declaring and mapping 200 endpoints; measure `EndpointValidation.Run` over the final set (median of 5 after one
  warm-up).
  Done when: a Note records the median on the reference machine; ≤ 50 ms, or the V2 fallback (skip re-validation of
  endpoints already checked in `UseGameServer`, key `METHOD template`) is implemented in T033's service with a test and
  re-measured. (SC-007)
- [ ] T036 [US1] Route name on the request outcome and event (after T003, T032; FR-015, data-model §6):
  `src/GameServer.Platform.Web/Requests/IRequestOutcomeFeature.cs` default member `string? RouteName => null;` (V13, else
  the fallback extension), `RequestOutcome.cs` implements it, `RequestLogMiddleware.cs` sets it at finalization from the
  endpoint's `RouteDeclarationMetadata`, else `WireCatalog.FindRouteName(method, template)`; `RequestLogEntry.cs` event
  property `RouteName` after `Route` (console line unchanged).
  Tests in a new file `tests/GameServer.Platform.Web.Tests/RouteNameOutcomeTests.cs`: metadata-bound command route,
  plain-mapped declared route (catalog lookup), unmatched path (null); update the 013 request-event field-list
  assertions (`RequestLogTests`, `RequestOutcomeFeatureTests`, consumer-fixture outcome tests) for the new property only.
  Done when: green; no 013 field changed. (FR-015)
- [ ] T037 [US1] Release notes draft (FR-090): new file `specs/014-wire-contract/release-notes-draft.md` with the slice
  header (framework `0.7.0`, unreleased), "FG-026 (1), (3)" section: what is new (catalog, declarations, endpoint check,
  late-mapped validation, `RouteName`), **BC-1**, **BC-2**, **BC-5** with the plan's migration steps; zh-pvp specifics:
  declare `account.init` (`src/ZhPvp.Game.Adapters/GameModule.cs:20` `InitAccountRoute`) as
  `RouteDeclaration.Command(InitAccountHandler.Descriptor, "/api/v1/account/init")` in the game's business project,
  `Routes = [...]` in the descriptor, map with `MapPlayerCommand(GameRoutes.InitAccount, …)`; `RequestOutcome.cs:22`
  config-route copy → `PlatformRoutes.Config` or `RequestOutcome().RouteName == PlatformRoutes.Config.Name`; how to
  declare host-only endpoints (`DeclareRoutes`) and that host Public routes still need `GameServer:PublicEndpointAllowlist`.
  Done when: the file exists with those sections; every BC line names how it surfaces (exit 1 + report line) and the
  fix. (FR-090, BC-1, BC-2, BC-5)
- [ ] T038 [US1] US1 checkpoint: Debug and Release build; full suite in memory and with `GAMESERVER_TEST_STORE=File`.
  Done when: 0 warnings, 0 failures; counts vs T001 recorded (new tests listed by class); US1 can ship alone (MVP).
  (FR-091 partial)

**Checkpoint (MVP)**: the catalog is complete and public; undeclared, mismatching and late-mapped endpoints stop the host
with one line each in every environment.

## Phase 4: US2 — A game generates its Unity C# from its own repository (P2, FG-021)

Goal: `tools/GameServer.WireGen` (`generate` / `verify`, exit 0/1/2) driven by a committed `wiregen.json`, reading the
catalog through the host's `--export-wire-contract` mode, running the framework-pinned protoc itself, writing
byte-stable output. The route and section renderers are built here (they are part of the one tool and of every
generation run); their use in the SDK and the sample is US3/US5.
Independent test: T053 (consumer fixture end to end, LF/CRLF, two checkout paths, clean, errors) + T052 (C# 9 /
netstandard2.1 compile).
Depends on: Phase 2 (catalog, export, `SectionKey`), US1 declarations (T019–T029; the export validates them). The tool
tasks T040–T050 are one project: `[P]` only where files are disjoint and the task says so.

- [ ] T039 [US2] Host export mode (after T016, T029; research R9, contracts/wiregen-cli.md §1):
  `src/GameServer.Platform.Web/Hosting/GameServerCommandLine.cs`: `HostMode.ExportWireContract`,
  `ExportWireContractSwitch`, `ExportPath`; `--export-wire-contract <path>` (any letter case) and its argument removed
  from `HostArgs`; missing path, `=`-form, `-export-wire-contract`, `/export-wire-contract`, combined with `--provision`
  → `GameServerStartupException` (exit 1). `src/GameServer.Platform.Web/Hosting/GameServerRunExtensions.cs`
  (`RunGameServerAsync`): `ExportAsync` = resolve `WireCatalog`, `Validate(options)` (errors → stderr one line each, exit
  1), output folder must exist (else exit 1 naming it), write `ToExportJson()` (UTF-8 no BOM, LF), dispose, exit 0; never
  `UseGameServer`, hosted services, store, game config or secrets.
  Tests in `tests/GameServer.Platform.Web.Tests/GameServerCommandLineTests.cs` (parse cases) and a new file
  `tests/GameServer.Platform.Web.Tests/WireContractExportTests.cs`: SampleHost-like composition built in-process → export
  file equals `ToExportJson()`, a fake `IHostedService` never started, a throwing store factory never resolved, a
  declaration fault → exit 1 and no file. **Final V4/V5**: `dotnet run --project samples/SampleHost --no-launch-profile
  --no-build -c Release -- --environment Local --export-wire-contract <scratch>/sample.json` and the same for
  `tests/ConsumerFixture/src/Fixture.Host` with no `COUCHBASE_*`: exit 0, files written, side effects as recorded in T004.
  Done when: green; a Note records the final V4/V5 result. (FR-030, FR-031 groundwork, US2-1 catalog source)
- [ ] T040 [US2] WireGen and its test project skeletons (after T005; research R10, plan Structure): new
  `tools/GameServer.WireGen/GameServer.WireGen.csproj` (net10.0 console, framework build props, `PackageReference
  Google.Protobuf` and `Grpc.Tools` with `GeneratePathProperty="true"` and no `<Protobuf>` items; `AssemblyMetadata`
  baked at build: framework root, `$(PkgGrpc_Tools)`, `Grpc.Tools` and `Google.Protobuf` versions from CPM,
  `GameServerFrameworkVersion`; `InternalsVisibleTo GameServer.WireGen.Tests`), `Program.cs` stub; new
  `tests/GameServer.WireGen.Tests/GameServer.WireGen.Tests.csproj` (xUnit v3 + Shouldly like the other test projects,
  references the tool); both added to `GameServer.slnx` (`/tools/`, `/tests/`).
  Done when: Release build 0 warnings; a smoke test reads the baked metadata (versions `2.84.0`, `3.36.2`, `0.7.0`, the
  `Grpc.Tools` root exists and contains the V6 protoc for the current OS); `dotnet msbuild … -getProperty:IsTestProject`
  true only for the test project. (FR-020, FR-022)
- [ ] T041 [US2] Configuration (after T040; data-model §4, contracts/wiregen-cli.md §4 steps 1–3): new
  `tools/GameServer.WireGen/WireGenConfig.cs`: JSON `gameserver.wiregen/1` (unknown major → error), required fields,
  defaults (`environment Local`, `origin game`, `layout flat`, `lineEndings lf`, `routesClass GameRoutes`,
  `sectionsClass GameSections`), paths relative to the config folder normalized with `Path.GetFullPath` (mixed `/` and
  `\` accepted), `--output-root` override; checks `output.root`, `output.marker`, `output.directory` exist and each proto
  exists under its root — every failure names the field/path and the fix, and nothing is created (N8).
  Tests in a new file `tests/GameServer.WireGen.Tests/WireGenConfigTests.cs`: valid config with defaults; each missing
  field; bad `format`; mixed separators resolve to the same full path; missing root, marker, directory, proto → message
  names it and no folder exists afterwards (assert the temp tree is unchanged).
  Done when: green. (FR-020, FR-024, FR-025, US2-5)
- [ ] T042 [US2] Catalog source (after T039, T041, T004 V15): new `tools/GameServer.WireGen/CatalogSource.cs`: from
  `catalog.hostProject` run `<dotnet> run --project <host> --no-launch-profile [--no-build] -c <cfg> -- --environment
  <env> --export-wire-contract <temp file>` (dotnet resolved as V15; temp file deleted after reading), else read
  `--catalog` / `catalog.file`; parse the export (format major 1, ignore unknown properties); host exit ≠ 0 → exit 1
  relaying the host's stderr lines.
  Tests in a new file `tests/GameServer.WireGen.Tests/CatalogSourceTests.cs`: read a committed sample export (copy of
  generated-files.md §1 under `tests/GameServer.WireGen.Tests/TestData/`); unknown major → error naming the file; unknown
  property ignored; host run against `tests/ConsumerFixture/src/Fixture.Host` with `--no-build` (after the solution
  build) yields the fixture routes; a host that exits 1 (bad `--environment Nope`) → error with the host's lines.
  Done when: green. (FR-030, US2-1)
- [ ] T043 [US2] Version guard (after T042, T007 V10; research R12, plan N3): new `tools/GameServer.WireGen/VersionGuard.cs`:
  export `frameworkVersion` = baked framework version, export `protobufVersion` = pinned `Google.Protobuf`; with
  `hostProject`, `<dotnet> msbuild <host> -getItem:PackageVersion` → `Grpc.Tools` and `Google.Protobuf` items (when
  present) = pinned; messages name both versions and which side to change (texts of research R12).
  Tests in a new file `tests/GameServer.WireGen.Tests/VersionGuardTests.cs`: each mismatch message (fake export values;
  fake msbuild JSON per V10), no CPM → note printed and only the export checks run.
  Done when: green. (FR-022, US2-4)
- [ ] T044 [P] [US2] Version pair test (after T007 V11): `tests/GameServer.Architecture.Tests/VersionTests.cs` new test:
  CPM `Google.Protobuf` version in `Directory.Packages.props` == product version of
  `sdk/unity/com.gameserver.client/Runtime/Plugins/Google.Protobuf.dll` (`FileVersionInfo`), and the WireGen baked
  `Grpc.Tools` version == CPM `Grpc.Tools`; failure names both values and the fix.
  Done when: green; a deliberately edited copy of the props in a temp folder makes the comparison fail (unit-level
  helper test). (FR-022)
- [ ] T045 [US2] protoc runner and proto model (after T040, T005 V6/V8; research R11): new
  `tools/GameServer.WireGen/Protoc.cs` (locate `protoc` under the baked `Grpc.Tools` root `tools/<os>_<arch>/`, set the
  Unix exec bit when missing, framework import roots always added: `proto/`,
  `src/GameServer.Modules.{Identity,Economy,Iap}.Adapters/Protos`, `Grpc.Tools` `build/native/include`; descriptor pass
  `--descriptor_set_out --include_imports` over consumer + all framework protos in a fresh temp folder; C# pass one run
  per consumer proto (no gRPC plugin) into a temp layout dir — `flat` or `by-root`; exactly one new `.cs` expected, none →
  exit 2 "expected output for <proto> missing"; protos passed as paths inside their `-I` root) and
  `tools/GameServer.WireGen/ProtoModel.cs` (parse `FileDescriptorSet`: message full name → file, package, C# type name
  per V8 incl. nested `Outer.Types.Inner`; C# full-name clash between a consumer and a framework message → error naming
  both).
  Tests in a new file `tests/GameServer.WireGen.Tests/ProtocTests.cs` (temp folders): a game proto importing
  `gameserver/core/v1/core.proto` → one `.cs`, no framework type in it (scan `partial class` names against the framework
  message list); a proto with a syntax error → protoc's `file:line` relayed, exit 1; a game proto redefining
  `gameserver.core.v1.Section` → duplicate symbol error before anything is written; a different package with the same
  `csharp_namespace` + message name as a framework message → clash error; `by-root` puts `core.proto` output under
  `gameserver/core/v1/`; the `source:` header carries only the relative proto path.
  Done when: green. (FR-020, FR-021, FR-022, FR-024, FR-025, US2-2)
- [ ] T046 [P] [US2] Template parser, names and selection (after T040; research R13, data-model §3.1, §5): new
  `tools/GameServer.WireGen/TemplateParser.cs` (literal segments and `{name}` parameters, also inside a segment
  `{id}.json`; `:` constraint, `?` optional, `=` default, `*` catch-all → error naming the route),
  `tools/GameServer.WireGen/Names.cs` (split on `.` and `_`, PascalCase, C# keyword parameters prefixed with `@`),
  `tools/GameServer.WireGen/Selection.cs` (routes: origin, surface `PlayerApi`/`Host`, profile Player/Public/Dev, not
  service-only; GM surface never; several methods in a player-facing declaration → error "declare one route per method",
  N4; sections: origin).
  Tests in a new file `tests/GameServer.WireGen.Tests/TemplateAndNameTests.cs`: `identity.login_guest` →
  `IdentityLoginGuest`; collisions (`a.b_c` vs `a_b.c`, a route named like another's `…Method`) → error naming both;
  each unsupported template form → error naming the route; GM Public routes (`gm.auth_login`, `gm.sso_start`) excluded;
  Dev `cheat.grant` included; service-only owner excluded; host Public `platform.health_live` included with method `*`.
  Done when: green. (FR-032 rules, FR-031, US3-1, US3-2 rules)
- [ ] T047 [US2] Route renderer (after T046, T008 V14): new `tools/GameServer.WireGen/RouteRenderer.cs` producing the
  file of contracts/generated-files.md §3 (header with framework version, `#nullable enable`, block namespace, `const
  string X` without leading slash, `const string XMethod` right after each route, `static string X(params)` helpers with
  `global::System.Uri.EscapeDataString` per parameter in template order, members ordered ordinally by route name, doc
  comments naming method, template, profile, module, command; no timestamp/path/user/machine).
  Tests in a new file `tests/GameServer.WireGen.Tests/RouteRendererTests.cs`: golden text for the framework routes of
  data-model §5.1 (constants and values), `PlatformReceipt("a/b c")` helper source escapes, `{id}.json` segment, `@class`
  parameter, any-method `"*"`, output contains no `{`; deterministic for shuffled input order.
  Done when: green. (FR-032, FR-023 ordering, US3-1, US3-2)
- [ ] T048 [US2] Section renderer (after T045, T046, T018; research R13, contracts/generated-files.md §4): new
  `tools/GameServer.WireGen/SectionRenderer.cs`: per section `static readonly SectionKey<T> X`, `const string XId`,
  `Table` last; types `global::`-qualified from `ProtoModel`; untyped section → `XId` only with the "(registered without
  its message type: no parser)" comment + a generator warning line; a section whose message is in neither the consumer
  nor the framework protos → error naming section and message; a game section reusing a framework message references the
  framework type.
  Tests in a new file `tests/GameServer.WireGen.Tests/SectionRendererTests.cs`: golden text for wallet/inventory/profile
  (generated-files.md §4), untyped id-only + warning, unknown message error, collision `X`/`XId`/`Table` → error.
  Done when: green. (FR-052, FR-051 generator part, N12, US3-5)
- [ ] T049 [US2] Output writer (after T040; research R10, contracts/wiregen-cli.md §4 steps 10–11, §5): new
  `tools/GameServer.WireGen/OutputWriter.cs`: normalize every file (`\r\n`/`\n` → selected ending, UTF-8 without BOM);
  compare bytes with disk; write only changed files (temp file in the same folder, then replace); create subfolders only
  inside `output.directory` (`by-root`); never delete; never read/write/delete `.meta`; `verify` writes nothing and lists
  each differing or missing file relative to the output root (exit 2); "new file <path>: Unity creates <path>.meta on
  import; commit it" for created files.
  Tests in a new file `tests/GameServer.WireGen.Tests/OutputWriterTests.cs`: LF and CRLF bytes; BOM stripped; unchanged
  file keeps its `LastWriteTimeUtc`; changed file rewritten; verify lists an edited and a missing file and writes nothing;
  a `.meta` next to an output is byte- and timestamp-unchanged; a stray file in the output folder is left alone.
  Done when: green. (FR-023, FR-026, FR-027, US2-3, US2-6, US2-7)
- [ ] T050 [US2] CLI and orchestration (after T041–T049): `tools/GameServer.WireGen/Program.cs`: verbs `generate` /
  `verify`, options `--config` (required), `--output-root`, `--catalog`, `--configuration` (default Release),
  `--no-build`, (`--dotnet` only if T004 chose that fallback); step order of contracts/wiregen-cli.md §4 (everything that
  can fail runs before any write); exit codes 0/1/2; summary lines (written, unchanged count, new-file `.meta` hints,
  untyped-section warnings).
  Tests in a new file `tests/GameServer.WireGen.Tests/CliTests.cs` (in-process entry point, `--catalog` with a test
  export, temp output): unknown verb/option → exit 1 with usage; generate then verify → 0; edit one output → verify 2
  naming it; a protoc error after a valid config → exit 1 and nothing written (output tree hash unchanged).
  Done when: green; `dotnet run --project tools/GameServer.WireGen -- --help` prints the contract's usage. (FR-020,
  FR-025, FR-027)
- [ ] T051 [US2] Consumer fixture game proto and client-visible aggregate `card` (after T012, T029; data-model §1.4,
  §1.5; also the US4 fixture input): new `tests/ConsumerFixture/src/Fixture.Game/Protos/fixture_game.proto`
  (`package fixture.v1;`, **no** `csharp_namespace` (V8), imports `gameserver/core/v1/core.proto` and uses one of its
  messages, `CardState` with a nested message, a map, a repeated field, an optional field, `int64` UTC milliseconds,
  plus the request/response messages `fixture.card` needs); `tests/ConsumerFixture/src/Fixture.Game/Fixture.Game.csproj`
  adds `Grpc.Tools` (PrivateAssets all) and `<Protobuf … GrpcServices="None" ProtoRoot="Protos" AdditionalImportDirs="$(GameServerRoot)proto">`
  for the **server** C# (normal consumer server build; Unity output is WireGen's job); `FixtureModuleDefinition.cs`:
  `CardState` record (nested record, `IReadOnlyDictionary`, `IReadOnlyList`, nullable member, `DateTimeOffset`, member
  `InternalNote` kept on the server), aggregate `card` (client-visible), declaration `fixture.card` (POST, Player;
  returns the `card` message); `FixtureModule.cs`: typed `AddAggregateWireMapper<CardState, CardState-wire>("card", …,
  o => o.Hidden("InternalNote"))` (DateTimeOffset → UTC ms), map `fixture.card` with `MapDeclared(...).AsPlayerEndpoint()`.
  Done when: fixture builds 0 warnings; `FixtureHostSmokeTests` green (update any assertion on snapshot section count or
  ids — the snapshot now carries `card`; Note); `FixtureCatalogTests` sees section `card` with message
  `fixture.v1.CardState` and hidden `InternalNote`. (FR-003, FR-050, FR-060 fixture input)
- [ ] T052 [US2] Fixture stand-in Unity client and committed generation (after T050, T051, T008 V14; plan US5 fixture
  bullet): new `tests/ConsumerFixture/client/Fixture.Client.csproj` (`netstandard2.1`, `LangVersion 9.0`,
  `ImplicitUsings disable`, `Nullable enable`, `TreatWarningsAsErrors`, other overrides from T008; compiles
  `Assets/**/*.cs`; references `$(GameServerRoot)sdk/GameServer.Client/GameServer.Client.csproj` and nothing server-side),
  `tests/ConsumerFixture/client/Assets/` (marker) with a hand-written `FixtureClientUsage.cs` that calls
  `client.SendCommandAsync(FixtureRoutes.FixtureSave, …)` and reads `client.Sections.Get(FixtureSections.Card)` after
  `Register(FixtureSections.Table)` (compile proof; `Get(key)`/`Register` land in T060 — until then use only
  `FixtureSections.Card.TypedParser` and `FixtureRoutes` constants, then extend in T060); new
  `tests/ConsumerFixture/tools/wiregen.json` (data-model §4 row: host `../src/Fixture.Host`, protos
  `../src/Fixture.Game/Protos: fixture_game.proto`, root `../client`, marker `Assets`, dir `Assets/Generated`, flat, lf,
  `Fixture.Client`, `FixtureRoutes`/`FixtureSections`); add `Fixture.Client` to `GameServer.slnx`
  (`/tests/ConsumerFixture/`); run `generate` once and commit `Assets/Generated/FixtureGame.cs`, `FixtureRoutes.cs`,
  `FixtureSections.cs` (no `.meta`: not a Unity project).
  Done when: solution Release build 0 warnings including `Fixture.Client` (C# 9 / netstandard2.1 compile of protoc,
  route and section output, SC-004); `git status` shows no framework file modified by the generation; the generated
  files contain no `gameserver.core.v1` type definition. (FR-020, FR-021, FR-028, FR-031, SC-004, US2-1, US2-2)
- [ ] T053 [US2] Fixture codegen end to end (after T052, T004 V15): new file
  `tests/ConsumerFixture/tests/Fixture.Tests/FixtureCodegenTests.cs` (runs the tool in-process or `dotnet run --project
  $(GameServerRoot)tools/GameServer.WireGen --no-build`; decide by speed, Note): `verify` of the committed config → 0;
  generate into two temp copies of the client folder from two config locations (different absolute paths, one written
  with mixed separators) → byte-identical; `lineEndings: crlf` → only line endings differ from LF and a rerun changes no
  timestamp; no BOM, no absolute path/user/machine/timestamp in any output; `dotnet clean` of `Fixture.Client` and of
  `Fixture.Game` leaves `Assets/Generated/*` untouched; a fake `Assets/Generated/FixtureGame.cs.meta` in a temp copy is
  unchanged after generate; wrong `--output-root` / missing marker / missing proto → exit 1 naming it, no folder created;
  editing one committed output in a temp copy → `verify` exit 2 naming it; a game proto duplicating a framework message
  → exit 1 before any write.
  Done when: green; SC-003 per line-ending style shown by the test names; Linux/macOS run recorded as a manual check
  (N9). (FR-021, FR-023, FR-024, FR-025, FR-026, FR-027, FR-028, SC-003, SC-004, US2-1..US2-7)
- [ ] T054 [US2] Release notes + checkpoint: `specs/014-wire-contract/release-notes-draft.md` "FG-021" section (the tool,
  `wiregen.json`, the version pair `Grpc.Tools` 2.84.0 / protoc 35.1 / `Google.Protobuf` 3.36.2, verify for CI, clean,
  `.meta`); zh-pvp: replace `tools/UnityProtoGen/UnityProtoGen.csproj` by a committed `wiregen.json` (`lineEndings:
  crlf`, marker `Assets/MadProject/Scripts`, data-model §4 illustrative row) and the documented `dotnet run --project
  framework/tools/GameServer.WireGen -c Release -- generate --config …` (optionally an `Exec` of it); delete the
  `NormalizeDirectory` workaround, the CRLF inline task, the clean warning and the `FRAMEWORK-GAP FG-021` marker; the
  host `Program` must use `GameServerCommandLine.Parse` + `RunGameServerAsync` (N2). Then Debug and Release build; full
  suite in memory and File.
  Done when: section present; 0 warnings, 0 failures; counts recorded. (FR-090, FR-091 partial)

**Checkpoint**: a consumer generates message, route and section C# for its own protos from its own repo, deterministically,
with verify for CI and no framework file touched.

## Phase 5: US3 — Unity code uses generated routes and section parsers, not copied strings (P2, FG-026 (2), FG-028)

Goal: every framework, sample and template mapper names its wire message (typed registration), the untyped one is
obsolete, duplicates fail; the Unity package ships generated `FrameworkRoutes`/`FrameworkSections`, the SDK route
constants come from them, and the SDK reads sections through typed keys and `TryRead`.
Independent test: T061 (typed accessor), T062 (catalog ↔ SDK constants, literal scan), T063 (package guards + committed
SDK output verify).
Depends on: US2 (tool, T050), T018. **Shared file order**: `sdk/GameServer.Client/GameServer.Client.csproj` and
`Runtime/Generated` change only in T059; `Runtime/Core/SectionStore.cs` and `GameServerClient.cs` only in T060.

- [ ] T055 [US3] Typed mappers — framework (after T012): `src/GameServer.Modules.Economy.Adapters/EconomyModule.cs`
  (`wallet`, `inventory`) and `src/GameServer.Modules.Identity.Adapters/IdentityModule.cs` (`profile`) switch to
  `AddAggregateWireMapper<TState, TWire>(id, map)`; mapper bodies unchanged (no `Hidden` yet: T066 decides).
  Done when: economy/identity snapshot and command tests green with unchanged payload bytes (an existing
  snapshot-bytes assertion or a new one comparing typed vs previous untyped payload for a fixed state); the SampleHost
  catalog lists `wallet` → `gameserver.economy.v1.WalletState`, `inventory`, `profile` with message names.
  (FR-050, FR-051 migration, US3-4)
- [ ] T056 [P] [US3] Typed mappers — samples and template (after T012): `samples/SampleGame/SampleGame.Adapters/SampleGameModule.cs:35-38`
  (`progression`, `battle`), `modules/Sample.Counter/Sample.Counter.Adapters/CounterModule.cs:33` (`counter`),
  `templates/module/Template.Module.Adapters/TemplateModule.cs:21` (`template`).
  Done when: `tests/SampleGame.Tests`, `tests/Sample.Counter.Tests` green; catalog sections carry
  `samplegame.v1.ProgressionState`/`BattleState` (actual package names per the protos — record them; data-model §1.5
  says `TemplateState`, the real full name is `template.module.v1.TemplateState`), `sample.counter.v1.CounterState`.
  (FR-050, FR-051 migration)
- [ ] T057 [US3] Untyped registration obsolete + startup Warning (after T055, T056, T051; research R15, data-model §2.1
  Warning): `[Obsolete("Use AddAggregateWireMapper<TState, TWire>(aggregateId, map) so the catalog and Unity know the
  section's message (FG-028). Kept through v0.7.x.")]` on the untyped overload; `UseGameServer` logs the catalog's
  untyped-section Warning once per untyped client-visible section under the startup category (same path as 011
  `IStartupCheck` warnings). Search `src/`, `modules/`, `samples/SampleGame/`, `samples/SampleHost/`, `templates/`,
  `tests/` (never `samples/` recursively — list the sample folders explicitly) for `AddAggregateWireMapper<` with one
  type argument: 0 outside the tests that cover the obsolete path, which wrap it in `#pragma warning disable CS0618`
  with a comment.
  Tests in `tests/GameServer.Platform.Web.Tests/WireMapperRegistrationTests.cs` (host part, after T012): untyped
  client-visible aggregate → catalog section without message, exactly one Warning with the data-model text, host still
  starts; typed → no Warning; export of an untyped section has no `message`.
  Done when: Debug and Release build 0 warnings; green. (FR-050, FR-051, BC-3)
- [ ] T058 [US3] Duplicates and the replacement path (after T032, T057; research R15, BC-4):
  `src/GameServer.Platform.Web/Hosting/ExtensionRegistry.cs` `ReplaceWireMapper<TState, TWire>(id, map, configure)`
  (removes earlier mapper registrations for the id, adds the typed one); registry keeps the first mapper and records
  duplicates (no last-wins left anywhere).
  Tests in `tests/GameServer.Platform.Web.Tests/WireMapperRegistrationTests.cs` (after T057): two
  `AddAggregateWireMapper` for `wallet` → startup exit 1 with the BC-4 line naming `wallet` and the fix;
  `ConfigureExtensions(e => e.ReplaceWireMapper<WalletState, Wire.WalletState>("wallet", custom))` → host starts and the
  snapshot carries the custom payload; replace for an id without an earlier mapper simply registers it (contract
  silent; documented in T077, Planning note 5).
  Done when: green. (FR-054, BC-4)
- [ ] T059 [US3] Unity package generation and the SDK csproj switch — one change (after T050, T055, T005 V7; research
  R17, BC-6): new `sdk/GameServer.Client/wiregen.json` (data-model §4 row: origin `framework`, catalog
  `../../samples/SampleHost` Local, protos `proto: gameserver/core/v1/core.proto` + identity, economy, iap `Protos`
  roots, output `../unity/com.gameserver.client/Runtime/Generated`, `by-root`, lf, namespace `GameServer.Client`,
  `FrameworkRoutes`/`FrameworkSections`); run `generate`; `Runtime/Generated/gameserver/core/v1/Core.cs`,
  `Identity.cs`, `Economy.cs`, `Iap.cs` byte-identical to T001 (else V7 fallback: accept once, Note with the diff);
  new `Runtime/Generated/FrameworkRoutes.cs`, `FrameworkSections.cs` + hand-written `.meta` (MonoImporter template of
  `Iap.cs.meta`, fresh unique GUIDs). In the same change `sdk/GameServer.Client/GameServer.Client.csproj`: remove
  `Grpc.Tools` and the five `<Protobuf>` items (incl. the KestrelDemo `sample_game.proto` line, BC-6); `Compile
  Include="$(PackageRoot)Runtime\**\*.cs"` (Generated included, no exclude). `proto/` and every module `Protos` folder
  unchanged (FR-055).
  Done when: Release build 0 warnings; `dotnet build sdk/GameServer.Client` twice changes no file under `sdk/unity/` or
  `samples/SampleGame/KestrelDemo/Assets/` (timestamps from T001); `git diff --stat proto src/*/Protos` empty;
  `FrameworkRoutes` lists exactly the player-facing framework routes of data-model §5.1 (incl. `CheatGrant`,
  `PlatformReceipt(string)`, `PlatformHealthLive` `*`) and no `gm.*`. (FR-031, FR-032, FR-034, FR-052, FR-055, BC-6,
  US3-1, US3-5, US5-1)
- [ ] T060 [US3] SDK typed reader and route constants (after T059; research R14, contracts/public-api.md §5):
  `sdk/unity/com.gameserver.client/Runtime/Core/SectionStore.cs`: `Register(SectionTable)` (same id + same message type
  idempotent, different type → `ArgumentException`), `IsKnown(id)`, `Get<T>(SectionKey<T>)` (null when absent),
  `TryRead(id, out IMessage? message)` (`Absent`/`Unknown`/`Parsed`; unknown ids never parsed), constructor registers
  `FrameworkSections.Table`; `Set`, `Apply`, `Get<T>(id, parser)`, `TryGet`, `RevOf`, `SectionChanged`, `HandlerFailed`,
  `Clear` unchanged; unknown ids still stored raw. `Runtime/Core/GameServerClient.cs:58-65`: the eight constants defined
  from `FrameworkRoutes` (data-model §5.1), same values; no other route literal in `Runtime/Core/**` or
  `Runtime/Unity/**`. Extend `tests/ConsumerFixture/client/Assets/FixtureClientUsage.cs` to call `Register` and
  `Get(key)` (T052 note).
  Done when: Release build 0 warnings (SDK and `Fixture.Client`); existing `tests/GameServer.Client.Tests` green
  unchanged. (FR-033, FR-034, FR-053, US3-3, US3-6, US3-7)
- [ ] T061 [US3] Typed accessor tests (after T060): `tests/GameServer.Client.Tests/SectionTableTests.cs` (host part, same
  file as T018 → after it) against SampleHost through the existing `SdkHarness`: after login snapshot and after a
  `SectionUpdate` (a command that changes the wallet), `Get(FrameworkSections.Wallet/Inventory/Profile)` equals
  `Get<T>(id, parser)` with the right parser; a game table (test-defined `SectionKey<T>` over a well-known type) after
  `Register`; `TryRead` → `Parsed` for known, `Absent` before receipt, `Unknown` for an id no table knows (raw bytes still
  in `All`/`TryGet`, never parsed); registering a conflicting table throws; `Get<T>(id, parser)` behaviour unchanged.
  Done when: green. (FR-053, SC-006, US3-6, US3-7)
- [ ] T062 [US3] SDK route tests (after T060): new file `tests/GameServer.Client.Tests/SdkRouteTests.cs`: build the
  SampleHost Local catalog in-process (Client.Tests references SampleHost) → every player-facing framework route has a
  `FrameworkRoutes` member whose value (or helper output for sample arguments) equals the template without leading
  slash, and its `…Method`; no `gm.*` member; `GameServerClient.LoginRoute … TimeRoute` keep their exact former values
  (literal expectations in the test); literal scan of `sdk/unity/com.gameserver.client/Runtime/Core/**/*.cs` and
  `Runtime/Unity/**/*.cs` — string literals only (simple lexer skipping comments and verbatim/raw strings handled),
  pattern `"(api|gm|health|v[0-9]+)/…"` → 0 hits. (`SampleGameClient.cs` scan is added in T071.)
  Done when: green; a seeded literal in a temp copy of one file makes the scan fail (helper test). (FR-033, FR-035,
  SC-001 SDK part, SC-002 SDK part, US3-3)
- [ ] T063 [P] [US3] Package guards and committed SDK output (after T059): new file
  `tests/GameServer.Architecture.Tests/UnityPackageMetaTests.cs`: every file and folder under
  `sdk/unity/com.gameserver.client/` (except `.meta` themselves) has a `.meta`; GUIDs of the files recorded in T001 are
  unchanged (commit the recorded list as `tests/GameServer.Architecture.Tests/TestData/unity-package-guids.txt`); every
  GUID is unique; package `package.json` version `0.7.0` and asmdef name unchanged. SC-008: a test comparing the SDK
  assembly's public API with the T001 snapshot (committed as
  `tests/GameServer.Architecture.Tests/TestData/sdk-public-api.txt`): no removed or changed member (additions allowed).
  New file `tests/GameServer.Architecture.Tests/FrameworkModuleCoverageTests.cs`: every framework `IGameModule` type
  (assemblies `GameServer.*`) is registered by the SampleHost Local composition (so the package covers every framework
  route and section). New file `tests/GameServer.WireGen.Tests/CommittedOutputTests.cs`: `verify --config
  sdk/GameServer.Client/wiregen.json --no-build --configuration <test configuration>` → exit 0 (and the fixture config of
  T052, which already exists). 
  Done when: green; each test fails on a seeded fault in a temp copy (missing `.meta`, changed GUID, removed public
  member, unregistered module, edited generated file). (FR-034, FR-035, SC-008, US5-1)
- [ ] T064 [US3] Release notes + checkpoint: `specs/014-wire-contract/release-notes-draft.md` sections "FG-026 (2)" and
  "FG-028": generated `FrameworkRoutes`/`FrameworkSections`, typed accessor (`Register`, `Get(key)`, `TryRead`), the
  untyped-section Warning, **BC-3** (zh-pvp: the 3 registrations in `src/ZhPvp.Game.Adapters/GameModule.cs:28-30` →
  `AddAggregateWireMapper<ProgressState, Wire.ProgressState>(GameAggregates.Progress, GameWireMapping.ToWire, …)` and the
  two others with their wire types; CS0618 is an error under `TreatWarningsAsErrors`), **BC-4** (`ReplaceWireMapper`),
  **BC-6** (framework/sample regeneration commands); client steps (non-breaking): `OnlineSession.InitAccountRoute` →
  generated `GameRoutes.AccountInit`, `PlayerReadModel.cs:31-37` ids and the `:173-193` `switch` →
  `client.Sections.Register(GameSections.Table)` + `Get(GameSections.X)` / `TryRead`. Then Debug and Release build;
  full suite in memory and File.
  Done when: sections present; 0 warnings, 0 failures; counts recorded. (FR-090, FR-091 partial)

**Checkpoint**: the Unity package is produced by the consumer step; SDK and server cannot drift (verify + catalog test);
sections are read through typed keys.

## Phase 6: US4 — A wire mapper that drops a field fails a test, and hidden fields are declared in code (P3, FG-027)

Goal: the consumer test kit proves that a mapper carries every non-hidden persisted member and sets every proto field;
hidden members are declared next to the registration and readable from the catalog.
Independent test: T066 (framework/sample/template pass), T067 (mutation set), T068 (fixture game-shaped aggregate).
Depends on: T013 (member tree), T055–T057 (typed mappers), T051 (fixture `card`). Independent of T059–T064 (can run in
parallel with them after T057).

- [ ] T065 [US4] `WireConformance` in the kit (after T013, T057; research R16, data-model §7, contracts/public-api.md
  §4): new files `src/GameServer.TestKit/WireConformance.cs` (`Check`, `CheckModules` — modules' `ConfigureServices`
  against a bare `ServiceCollection`, only `IAggregateWireMapper` instances resolved — and `CheckAll(IServiceProvider)`),
  `WireConformanceOptions.cs` (`WithSampleState`, `WithMemberValue`), `WireConformanceReport.cs` (report, finding,
  problem enum, exception, `ThrowIfFailed`, one line per finding `[aggregate {id}] {problem}: {path} — {fix}`),
  `SampleStates.cs` (sample and perturbation per leaf kind, data-model §7.2, values through
  `JsonSerializer.SerializeToNode(value, GameServerJson.Options)`). Algorithm: member tree → sample JSON → `TState`
  (failure → `SampleNotSerializable`) → map → bytes → parse with the contract parser → `FieldAtDefault` per proto field
  still default (§7.3; minus may-stay-default) → per leaf one perturbation → `MemberNotOnWire` / `HiddenMemberOnWire`;
  unsupported leaf without supplied value → `MemberNotFillable` (never skipped); untyped mapper → `UntypedMapper`;
  client-visible aggregate without mapper → `NoMapper`. No xUnit/Shouldly reference, no host, no store (FR-066).
  `tests/GameServer.Architecture.Tests/TestKitArchitectureTests.cs` still green (only `StateMembers` internals used).
  Done when: Release build 0 warnings; `dotnet msbuild src/GameServer.TestKit -getItem:PackageReference` shows no test
  framework. (FR-061, FR-062, FR-063, FR-066)
- [ ] T066 [US4] Conformance over the framework, sample and template mappers and their hidden declarations (after
  T065): new file `tests/GameServer.Platform.Web.Tests/WireConformanceTests.cs` (add a project reference to
  `templates/module/Template.Module.Adapters` in `tests/GameServer.Platform.Web.Tests/GameServer.Platform.Web.Tests.csproj`;
  the template has no test project): `WireConformance.CheckModules([Economy, Identity, SampleGame, Counter, Template])`.
  For each finding decide: a real mapper gap → fix the mapper (no wire change expected; if a fix would change the wire,
  stop and record it for the owner instead); a member intentionally server-only → `o.Hidden("…")` at the registration;
  an enum/flag whose meaningful value is the default → `o.MayStayDefault("…")`. Record every declaration in a Note (input
  of the module PCRs, T079).
  Done when: the call passes with 0 findings; Notes list each `Hidden`/`MayStayDefault` with its reason; snapshot bytes
  of the framework sections unchanged (same check as T055). (FR-060, FR-064 pass part, SC-005, US4-1, US4-2, US4-3)
- [ ] T067 [US4] Mutation set and kit behaviour (after T066): in `tests/GameServer.Platform.Web.Tests/WireConformanceTests.cs`
  (same file → after T066) with test-only states/messages (well-known or a small test proto compiled in the test
  project): unmapped member → `MemberNotOnWire` naming it; unset proto field → `FieldAtDefault` naming it; nested
  unmapped member (`ActiveBattle.Seed`-shaped) → `MemberNotOnWire` with the nested path; stale hidden member (declared
  hidden but mapped) → `HiddenMemberOnWire`; hidden member reaching the wire → `HiddenMemberOnWire`; mapped collection
  left empty by the mapper → `FieldAtDefault` + `MemberNotOnWire` for its element leaves; unknown hidden path → startup
  line of data-model §2.1 (host start fails, naming aggregate and path); unknown may-stay-default field → startup line;
  an `object`-typed member → `MemberNotFillable` unless `WithMemberValue` supplies it; `WithSampleState` used as the
  sample and still perturbed; `DateTimeOffset` → ms perturbation (+1 s) is not reported as dropped; `CheckAll` over a
  `SampleTestHost.BuildApp` composition covers every client-visible aggregate without naming them; `ThrowIfFailed` lists
  every line.
  Done when: green; each mutation reported by name exactly as data-model §7.4 maps it. (FR-060, FR-061, FR-062, FR-063,
  FR-064, SC-005, US4-1..US4-6)
- [ ] T068 [US4] Fixture conformance (after T051, T065): new file
  `tests/ConsumerFixture/tests/Fixture.Tests/FixtureConformanceTests.cs`: `WireConformance.CheckModules([new
  IdentityModule(), new FixtureModule()]).ThrowIfFailed()` passes for `card` (nested record, map, list, nullable,
  `DateTimeOffset` → UTC ms, hidden `InternalNote`); a test-local copy of the mapper that drops one member fails naming
  it. Public API only (no IVT to the fixture).
  Done when: green. (FR-061, FR-062, FR-063, FR-066, US4 independent test)
- [ ] T069 [US4] Catalog/export lists hidden members + release notes + checkpoint (after T066): export test in
  `tests/GameServer.Platform.Web.Tests/WireCatalogTests.cs` (after T016) that the SampleHost export lists each section's
  `hiddenMembers`/`mayStayDefault` as declared in T066 and that `WireCatalog.Sections` exposes them (PCR source);
  `specs/014-wire-contract/release-notes-draft.md` "FG-027" section: kit `WireConformance`, `Hidden`/`MayStayDefault`;
  zh-pvp: the comment in `src/ZhPvp.Game.Adapters/GameWireMapping.cs:6-9` becomes
  `o => o.Hidden("ActiveBattle.WinCash", "ActiveBattle.TrophyWin", "ActiveBattle.IsProtect", "ActiveBattle.ConfigVersion")`
  on the progress registration, one new test `WireConformance.CheckModules([new EconomyModule(), new GameModule(),
  …]).ThrowIfFailed()` in `tests/ZhPvp.Tests`, `docs/pcr/game.md` hidden list from the catalog. Then Debug and Release
  build; full suite in memory and File.
  Done when: green; counts recorded. (FR-065, FR-090, US4-7, FR-091 partial)

**Checkpoint**: every framework/sample/template mapper passes conformance; hidden members are code, not comments.

## Phase 7: US5 — The framework's own SDK, sample client and docs use the same path (P4, FG-021, FG-026, FG-028)

Goal: KestrelDemo is generated by the consumer step and uses generated constants and typed sections; every framework
build exercises the path (verify tests); the guides describe the shipped behaviour.
Independent test: T071 (committed outputs verify, literal scan), T072 (Unity smoke, manual), T081 (reviewer walkthrough).
Depends on: US3 (T059–T063); docs tasks also on US1, US2 and US4.

- [ ] T070 [US5] KestrelDemo regeneration and client update — **one change** (after T059, T060; research R17; generated
  and hand-written classes share the name `SampleGame.Client.SampleGameRoutes`, so they cannot coexist): new
  `samples/SampleGame/wiregen.json` (contracts/generated-files.md §2: host `../SampleHost/SampleHost.csproj` Local, protos
  `sample_game.proto` + `../../modules/Sample.Counter/Sample.Counter.Adapters/Protos/sample_counter.proto`, root
  `KestrelDemo`, marker `Assets/SampleGame`, dir `Assets/SampleGame/Generated`, flat, lf, `SampleGame.Client`,
  `SampleGameRoutes`/`SampleGameSections`); run `generate`: `Assets/SampleGame/Generated/SampleGame.cs` byte-identical
  to T001 (else V7 fallback, Note), new `SampleCounter.cs`, `SampleGameRoutes.cs`, `SampleGameSections.cs` (**no**
  `.meta` written by hand: Unity creates them in T072). Edit only
  `samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs` (opened by path): delete the hand-written
  `SampleGameRoutes` class (`:164-169`), use `SampleGameRoutes.BattleStart` / `BattleEnd` / `ShopBuy`,
  `client.Sections.Register(SampleGameSections.Table)` once after creating the client, `client.Sections.Get(FrameworkSections.Wallet)`
  and `RevOf(FrameworkSections.WalletId)` instead of `"wallet"` (`:157-159`).
  Done when: the tool prints the three "new file … commit .meta" lines; `git diff` of `SampleGameClient.cs` shows only
  those changes; a scratch compile of the four generated files + the package `Runtime/**` (no `UnityEngine`) under C# 9
  is clean (the MonoBehaviour itself compiles only in Unity: T072). (FR-031, FR-033, FR-052, BC-6, US5-2)
- [ ] T071 [US5] Committed sample output and literal scan (after T070, T062, T063): `tests/GameServer.WireGen.Tests/CommittedOutputTests.cs`
  (after T063) adds `verify --config samples/SampleGame/wiregen.json` → 0; `tests/GameServer.Client.Tests/SdkRouteTests.cs`
  (after T062) opens `samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs` **by exact path** (never a
  recursive scan of `samples/`) and fails on any route-shaped literal and on any literal equal to a known section id of
  the SampleHost catalog (`wallet`, `inventory`, `profile`, `progression`, `battle`, `counter`).
  Done when: green; both checks fail on a seeded literal / edited output in a temp copy. (FR-027, FR-033, FR-035,
  SC-002, US5-1, US5-2)
- [ ] T072 [US5] **Manual / owner-assisted** Unity smoke of KestrelDemo (after T070; FR-091 manual check): with the
  owner's Unity 6 (`smoke-build.ps1 -Unity "<path>\Unity.exe"`): `sdk/unity/ci/smoke-build.ps1 -CompileOnly`, then
  start SampleHost (`tools/local/run-local.ps1`, port 5080, never `-Reset`) and `smoke-build.ps1 -E2E`. Commit the three
  `.meta` files Unity created next to `SampleCounter.cs`, `SampleGameRoutes.cs`, `SampleGameSections.cs`. Check that
  Unity created **no** `.meta` under `sdk/unity/com.gameserver.client/` (the package is a `file:` dependency of
  KestrelDemo, so a missing package `.meta` would be created here with a random GUID — T063 guards it beforehand) and
  changed none (`git status sdk/unity`), and that `SampleGame.cs.meta` is unchanged.
  Done when: a Note records both runs (exit codes, E2E log lines: login, battle start/end, buy, gold), the three new
  `.meta` paths/GUIDs, and an empty `git status sdk/unity`. If the agent cannot start Unity, the task stays open with
  the exact commands for the owner and the release notes list it as pending. (FR-028 in Unity, FR-034, FR-091, US5-2)
- [ ] T073 [US5] Own-path stability (after T063, T071): run `generate` for `sdk/GameServer.Client/wiregen.json`,
  `samples/SampleGame/wiregen.json` and `tests/ConsumerFixture/tools/wiregen.json` twice each and a Release solution
  build in between.
  Done when: `git status` clean after both rounds (SC-003 "0 changes after a rerun"); no generated file timestamp changed
  on the second run; `CommittedOutputTests` green. (FR-023, FR-026, SC-003, US5-1)

### Documentation (FR-070..FR-073; Vietnamese except the consumer guide; pages state "framework 0.7.0, chưa phát hành / unreleased")

- [ ] T074 [US5] Consumer guide `docs/guides/consumer-guide.md` (after T054, T064, T069): §4 host composition — declare
  host-only endpoints with `DeclareRoutes` before `Build()`, host Public routes still need the operator allowlist, the
  `Program` must use `GameServerCommandLine.Parse` + `RunGameServerAsync` (export mode, N2), the `GameServer.` assembly
  prefix is reserved (origin rule); §5 tests — the conformance test (`WireConformance.CheckModules(...)`,
  `ThrowIfFailed`, `WithMemberValue`/`WithSampleState`, perturbation table); §10 Unity SDK — replace "coming in slice 014"
  with: route declaration, the endpoint check (incl. endpoints mapped after `UseGameServer`, every environment), typed
  mapper with `Hidden`/`MayStayDefault`, `wiregen.json` inputs, `generate`/`verify`, LF/CRLF, the version pair, cleaning,
  `.meta` (UPM package vs Unity project), IL2CPP/`link.xml` (none needed; existing file may stay), using generated
  routes/sections (`Register`, `Get(key)`, `TryRead`), CI verify command; §11 bump checklist — regenerate and commit the
  client files, act on BC-1/BC-3. Version stamp.
  Done when: no "coming in slice 014" left; every FR-070 bullet has a paragraph (checklist in a Note); commands copied
  from contracts/wiregen-cli.md §6 run as written in this repo. (FR-070, FR-073)
- [ ] T075 [P] [US5] `docs/guides/module-authoring.md`, `templates/module/README.md`, `README.md` (after T069): routes in
  `ModuleDescriptor.Routes` (business project), `RouteDeclaration.Command(...)`, mapping from the declaration
  (`MapPlayerCommand(decl, …)`, `MapDeclared(...).As*Endpoint()`), naming rule, typed mapper with hidden members,
  conformance test, generation step (in-repo: `samples/SampleGame/wiregen.json`; consumer: own config); README one line
  pointing to the consumer guide §10. Version stamp.
  Done when: no page tells a consumer to edit a framework file (`grep -n "GameServer.Client.csproj\|<Protobuf"` in these
  files only in a "framework maintainers" context). (FR-071, FR-073)
- [ ] T076 [P] [US5] `docs/html/02-them-command.html` (Vietnamese; after T069, T070): §4.4 (`Routes` in the
  `ModuleDescriptor`), §4.5 (map from the declaration, typed mapper registration with `Hidden`), §4.8 (conformance test),
  §4.10 (WireGen: in-repo variant with `samples/SampleGame/wiregen.json`, consumer-repo variant with the game's own
  config and CRLF; the generated constant used from Unity; remove the "add a `<Protobuf>` line to the csproj"
  instruction), §5 steps and checklist (step 8 uses the generated `SampleGameRoutes.CounterReset`-style constant, no
  literal `"api/v1/counter/reset"`; add "declare the route", "run generate/verify", "conformance test" items), §10 (the
  new startup report lines of data-model §2); `docs/html/index.html` stamp. Keep the hand-maintained HTML structure (ids
  `muc-4-4` … `muc-10`, `checklist-them-command`).
  Done when: `grep -c '"api/v1/counter/reset"' docs/html/02-them-command.html` = 0; no instruction to edit a framework
  file; the page opens without broken anchors (manual check in a browser or a link-check script, Note). (FR-071, FR-073)
- [ ] T077 [P] [US5] PCR platform `docs/pcr/platform.md` new `## 20.` (after T036, T058): catalog entry fields, naming
  rule, declaration faults (data-model §2.1) and endpoint faults (§2.2), pre-serving validation of late-mapped endpoints
  (the hook chosen in T002), hosting conditions (`development_tools`, `gm.sso`, `AddRouteCondition`), origin rule, export
  format `gameserver.wire-contract/1` (fields, determinism, versioning rule), section contract (typed mapper, untyped
  Warning, duplicates, `ReplaceWireMapper` incl. the no-earlier-mapper case), FR-055 (wire messages unchanged); §13
  request event gains the `RouteName` row; breaking changes BC-1, BC-2, BC-4, BC-5 listed like §17. Version stamp.
  Done when: every data-model §2 line appears verbatim once; `DocumentedConfigurationKeysTests` green (no new key).
  (FR-072, FR-008, FR-015 docs)
- [ ] T078 [P] [US5] PCR Unity SDK `docs/pcr/unity-sdk.md` (after T064): generated files and layout (`Core.cs` nested,
  module files flat, `FrameworkRoutes`/`FrameworkSections`), `GameServerClient.*Route` defined from generated constants,
  typed accessor (`SectionKey`, `SectionTable`, `Register`, `Get(key)`, `TryRead`, unknown ids raw), the version pair
  and its test, `.meta` rule for the UPM package, BC-6 regeneration command, package version `0.7.0`.
  Done when: section present; values match `FrameworkRoutes.cs`. (FR-072)
- [ ] T079 [P] [US5] Module PCRs `docs/pcr/identity.md`, `economy.md`, `iap.md`, `gm.md`, `sample-counter.md`,
  `sample-game.md` (after T066): route-name table per module (name, method, template, profile, operation, condition)
  generated from the SampleHost export (T039) and pasted, sections with message full name, hidden members and
  may-stay-default fields (T066 Notes); corrections for any method/permission difference recorded in T019–T029 Notes.
  Done when: every catalog route and section of those modules appears exactly once; a Note records the export command
  used. (FR-072, FR-008, FR-065 docs)

**Checkpoint**: KestrelDemo, the package and the fixture are all produced by the consumer step and verified by the
suite; guides and PCRs describe the shipped behaviour.

## Phase 8: Release notes, verification, review and close

- [ ] T080 Release notes final pass `specs/014-wire-contract/release-notes-draft.md` (after T074–T079; FR-090): one
  section per fixed FG (FG-021, FG-026, FG-027, FG-028) in round order; table BC-1..BC-6 with "how it surfaces" and the
  fix (plan migration notes); zh-pvp migration checklist in order: (1) typed mappers at `GameModule.cs:28-30` with the
  four hidden members (CS0618 first, it blocks the build), (2) `account.init` declaration + `Routes` + mapping from it
  (`GameModule.cs:20`), (3) `RequestOutcome.cs:22` copy, (4) `wiregen.json` (CRLF) replacing `tools/UnityProtoGen`, (5)
  client: generated `GameRoutes`/`GameSections`, remove `OnlineSession.InitAccountRoute`, `PlayerReadModel` ids and the
  `switch`, (6) conformance test; the "Consumer impact" table of the spec (removable vs stays); manual checks and their
  status (T072, T081, T082, Linux/macOS determinism N9); "no player API route, wire message, document or GM API response
  changes; Unity package stays 0.7.0".
  Done when: every BC and FG id of the spec appears; every zh-pvp file:line of the spec's consumer-impact table is
  referenced. (FR-090, SC-009)
- [ ] T081 Reviewer walkthrough (story 5, FR-091 manual; after T074–T076): a fresh agent (no context beyond the docs)
  follows the consumer guide §10 and `02-them-command.html` §4.10/§5 in a **temp copy** of the repository: add a
  `fixture.rename` command to the consumer fixture (declaration, mapping from it, typed mapper unchanged, conformance
  test), run `generate` for `tests/ConsumerFixture/tools/wiregen.json`, call the generated `FixtureRoutes.FixtureRename`
  from `Fixture.Client`, build and run the fixture tests; then the in-repo variant for `counter.reset` per §5.
  Done when: the agent's list of missing or wrong steps is recorded as a Note and each item is fixed in the docs (or
  answered); the temp copy is deleted; nothing of it is committed. (FR-070, FR-071, US5-3)
- [ ] T082 **Manual / owner-assisted** first-consumer CRLF regeneration (FR-091, SC-009; never edit
  `E:\ProjGits\zh-pvp-server` or `E:\ProjGits\research-and-development-games`): in a temp clone of the game repo with its
  `framework/` submodule checked out at this branch's head, write the illustrative config of data-model §4 (crlf,
  `--output-root` = a temp copy of the client's `src/GameClient`), run `generate`, and compare the game's message C# with
  the committed client file; expected: identical bytes for the message file (CRLF), new route/section files only. The
  game build may fail on BC-1/BC-3 — then apply the release-notes steps in the temp clone only.
  Done when: a Note records the result and any doc fix, or — if the owner prefers the game session to run it after the
  tag — the release notes list it as pending with the exact commands. (FR-091, SC-009)
- [ ] T083 Final verification (after T080–T082): Debug and Release build of `GameServer.slnx`; full suite in memory and
  with `GAMESERVER_TEST_STORE=File`; opt-in `GAMESERVER_PERF=1` run of `EndpointCheckPerformanceTests`; `git diff --stat
  main -- proto src/*/Protos samples/SampleGame/SampleGame.Adapters/Protos modules/Sample.Counter/Sample.Counter.Adapters/Protos`
  empty (FR-055); `docs/api/gm-openapi.json` unchanged or as recorded in T025; `git status` lists only intended files.
  Done when: 0 warnings, 0 failures; counts vs T001 and per-story checkpoints recorded. (FR-055, FR-091, SC-007)
- [ ] T084 Couchbase full suite (after T083; run through PowerShell in the background, variables from the resume file,
  password never printed).
  Done when: 0 failures; total/passed/skipped and duration recorded. (FR-091)
- [ ] T085 Tech-lead review (after T083): `tech-lead-reviewer` gets the spec, the plan, this file and the list of
  changed files (`git diff --stat main`); focus: endpoint check completeness (no bypass, late endpoints, conditions,
  service-only), export side effects, WireGen determinism and write safety (no delete, no `.meta`, no folder outside the
  output), SDK public API additive only, conformance false positives, docs accuracy.
  Done when: findings recorded (Blocker/Major/Minor with file:line). (FR-091)
- [ ] T086 Review fixes (after T085): fix every finding the orchestrator accepts, each with a test that fails without the
  fix where practical; update docs/release notes/PCR touched by a fix; re-run the affected classes, then T083 and T084.
  Append `## Review (T085/T086, …)` to this file in the 013 format (finding, fix, test, docs; deviations; test delta).
  Done when: suites green; review section complete. (FR-091)
- [ ] T087 Resume update `AgentMemories/features/p05-speckit/00_summary.md` (after T086): 014 row DONE with task count
  and review result; "Technical notes from 014" (catalog + check incl. the hook chosen in T002, export mode, WireGen
  commands and exit codes, `wiregen.json` locations, typed mapper + `ReplaceWireMapper`, `WireConformance`, SDK typed
  accessor, `.meta` rules, test counts); open items (T072/T082 if pending, Linux/macOS determinism N9, any V fallback).
  Done when: the file names the next slice (015) and how to resume. (resume DoD)

## Dependencies

```text
Phase 1  T001 ─┬─ T002 (V1,V3 GATE) ─────────────────────────────────────────────► T033
               ├─ T003 (V12,V13) ──► T010, T019, T020, T036
               ├─ T004 (V4,V5,V15) ► T039, T042, T053
               ├─ T005 (V6,V7,V8) ─► T040, T045, T059, T070
               ├─ T006 (V9) ───────► T013
               ├─ T007 (V10,V11) ──► T043, T044
               └─ T008 (V14) ──────► T047, T048, T052
Phase 2  T009 → T010 → T011 ; T012 ∥ T010 ; T013 (T006) ; T014 (T009,T012,T013) → T015 → T016 ; T017 ; T018 ∥
US1      T019..T029 (after T011; T024 → T025) → T030 → T031 → T032 → T033 (T002) → T034 → T035 ∥ T036 → T037 → T038
US2      T039 (T016,T029) → T042 → T043 ; T040 → T041, T044∥, T045, T046∥, T049∥ ; T047 (T046) ; T048 (T045,T046,T018)
         → T050 → T051 (T012,T029) → T052 → T053 → T054
US3      T055 ∥ T056 → T057 (T051) → T058 (T032) ; T059 (T050,T055) → T060 → T061, T062 ; T063 (T059) → T064
US4      T065 (T013,T057) → T066 → T067 ; T068 (T051,T065) ; T069 (T066)          [lane beside T059–T064]
US5      T070 (T059,T060) → T071 (T062,T063) → T072 (manual) ; T073 ; T074..T079 (docs, after their stories)
Close    T080 → T081 → T082 (manual) → T083 → T084 (Couchbase, background) → T085 → T086 → T087
```

- Phase 1 blocks everything; each V-check blocks only the tasks named in the [VERIFY] table. **T002 is a gate**: T033's
  design (and PCR §20 / research R8 wording) follows its decision.
- **US1 needs only Phase 2.** All declarations (T019–T030) land before the check (T032); T032 and T033 must not be split
  from the T030 fixes if a missed declaration shows up (fix it in T030's files).
- US2 needs US1's declarations because the export validates them (T039). US3 needs US2 (T050) and US4 needs T057; US4
  and T059–T064 can run in parallel lanes. US5 waits for US3.
- Shared files that force order (no `[P]` between these tasks):
  - `src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs`: T017 → T032 → T033 → T057.
  - `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs`: T014 → T033 (service registration).
  - `src/GameServer.Platform.Web/Aggregates/AggregateWireMapperRegistry.cs`: T012 → T057 → T058.
  - `src/GameServer.Platform.Web/Hosting/GameServerCommandLine.cs`, `GameServerRunExtensions.cs`: T039.
  - `src/GameServer.Modules.Gm.Adapters/GmRoutes.cs`: T024 → T025. `IdentityModule.cs`: T021 → T055.
    `EconomyModule.cs`: T022 → T055. `SampleGameModule.cs`: T027 → T056. `CounterModule.cs`: T026 → T056.
    `TemplateModule.cs`: T028 → T056.
  - `tests/ConsumerFixture/src/Fixture.Game/FixtureModule.cs`, `FixtureModuleDefinition.cs`: T029 → T051.
  - `tests/GameServer.Platform.Web.Tests/WireCatalogTests.cs`: T014 → T015 → T016 → T069;
    `WireMapperRegistrationTests.cs`: T012 → T057 → T058; `StartupValidationTests.cs`: T017 → T030;
    `ErrorContractTests.cs`: T030 → T033; `WireConformanceTests.cs`: T066 → T067;
    `GameServerCommandLineTests.cs`: T032 → T039.
  - `tests/GameServer.Client.Tests/SectionTableTests.cs`: T018 → T061; `SdkRouteTests.cs`: T062 → T071;
    `tests/GameServer.WireGen.Tests/CommittedOutputTests.cs`: T063 → T071.
  - `sdk/GameServer.Client/GameServer.Client.csproj` and `Runtime/Generated/*`: T059 only;
    `Runtime/Core/SectionStore.cs`, `GameServerClient.cs`: T060; `tests/ConsumerFixture/client/Assets/FixtureClientUsage.cs`:
    T052 → T060.
  - `GameServer.slnx`: T040 → T052.
  - `specs/014-wire-contract/release-notes-draft.md`: T037 → T054 → T064 → T069 → T080 → T086.
  - Docs: `docs/pcr/platform.md` T077 → T086; `docs/guides/consumer-guide.md` T074 → T081 → T086;
    `docs/html/02-them-command.html` T076 → T081.
- Parallel examples: T003 ∥ T004 ∥ T005 ∥ T006 ∥ T007 ∥ T008 (beside T002); T010 ∥ T012 ∥ T018; after T011:
  T020 ∥ T021 ∥ T022 ∥ T023 ∥ T026 ∥ T027 ∥ T028 ∥ T029 with the GM lane T024 → T025; after T040: T044 ∥ T046 ∥ T049;
  T055 ∥ T056; US4 lane (T065–T069) ∥ T059–T064; T075 ∥ T076 ∥ T077 ∥ T078 ∥ T079.

**Critical path**: T001 → T002 (gate) → T009 → T010 → T011 → T024 → T025 → T030 → T031 → T032 → T033 → T034 → T038 →
T039 → T042 → T043 → T045 → T048 → T050 → T051 → T052 → T053 → T054 → T055 → T057 → T059 → T060 → T062 → T063 → T064 →
T070 → T071 → T074 → T080 → T081 → T083 → T084 (Couchbase) → T085 → T086 → T087. The long poles are the US1 migration +
check (every framework route, the GM lane is the longest) and the WireGen tool chain (T040–T053). T072 and T082 are
manual (owner-assisted) and off the critical path, but their status must be in the release notes before T080 closes.

## Implementation strategy

1. **MVP = US1 (Phases 1–3).** Catalog + declarations + endpoint check + late-mapped validation; shippable at T038.
2. **US2** (tool) next, proven end to end on the consumer fixture (C# 9 compile, LF/CRLF, verify).
3. **US3** (typed mappers, package generation, SDK typed reader) with **US4** (conformance) as a parallel lane after T057.
4. **US5** (KestrelDemo + own path + docs), then the close (release notes, walkthrough, manual checks, full suites incl.
   Couchbase, review, resume).

With one implementer run the phases in order. With two: lane 1 = US1 → US2 → US3 → US5; lane 2 (after Phase 2) = the
GM declarations (T024/T025) and later US4 (after T057); merge before T070.

## Implementation risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| V1 fails: `StartingAsync` runs after Kestrel binds or does not see late endpoints | High (FR-012) | T002 gate before T033; fallback `IStartupFilter`, then first-request refusal + `StopApplication`; PCR/research wording follows the decision |
| A missed declaration makes a framework test red far from the cause once T032 lands | Medium | T030 before T032; T034 completeness test (47 names); a red test after T032 is fixed in T030's files, Note |
| Test hosts lose default modules when a `modules` callback is passed only to declare routes | Medium (tests silently test less) | T030 helper that calls `DefaultModules` first; grep of every `modules:` call site |
| `[Obsolete]` added before all call sites migrate | Medium (build red, CS0618) | T057 after T055/T056/T051; grep 0 untyped sites; pragma only in the obsolete-path tests |
| Typed mapper migration changes payload bytes | High (wire contract) | T055/T056/T066 compare payload bytes before/after; no mapper body change without owner note |
| WireGen protoc output differs from the committed Grpc.Tools output (V7) | Low–Medium (one-time diff) | T005 measures first; accept once with paths and `.meta` GUIDs unchanged, Note |
| Export mode touches the store, a secret or creates files (V4) | Medium (codegen from a clean checkout fails) | T004/T039 checks; export never resolves stores/options; side effects documented |
| Child `dotnet` not on PATH (`$HOME/.dotnet10`) | Medium (tool and e2e tests fail on this machine) | V15 resolution order, `--dotnet` fallback |
| Fixture client inherits consumer props (ImplicitUsings, analyzers) | Low–Medium (C# 9 build errors) | T008 lists overrides; T052 applies them |
| Unity writes random-GUID `.meta` into the `file:` package for a file without one | Medium (package GUID churn) | hand-written `.meta` in T018/T059; `UnityPackageMetaTests` (T063) before T072; T072 checks `git status sdk/unity` |
| Hand and generated `SampleGameRoutes` in one namespace | Low (compile error in Unity only) | T070 does both in one change; scratch compile of generated files |
| Conformance false positives on framework mappers | Medium | T066 decides per finding (fix, `Hidden`, `MayStayDefault`) with a reason; perturbation ≥ 1 wire unit |
| Request-event field tests of 013 break with `RouteName` | Low | T036 updates only those assertions |
| GM OpenAPI document changes after remapping GM routes | Low–Medium (GM frontend contract) | T025 runs `OpenApiTests`; only order-like differences may be regenerated, never path/method/permission/schema |
| Couchbase suite slow or flaky (cluster 503s, known Telemetry flake) | Low | Couchbase only in T084; rerun the flaky class alone before reporting |

## FR coverage

| FR | Tasks |
| --- | --- |
| FR-001 | T009, T014, T034 |
| FR-002 | T010, T019, T020, T021, T022, T023, T024, T025, T034 |
| FR-003 | T009, T014, T026, T027, T028, T029, T051 |
| FR-004 | T009, T022 |
| FR-005 | T010, T011, T019–T029 |
| FR-006 | T015, T017, T020, T021, T024, T032 |
| FR-007 | T015, T032 |
| FR-008 | T009, T077, T079 |
| FR-010 | T031, T032 |
| FR-011 | T024, T031, T034 |
| FR-012 | T002, T017, T032, T033 |
| FR-013 | T032, T033, T034 |
| FR-014 | T030, T031, T032, T033, T034 |
| FR-015 | T036, T077 |
| FR-020 | T040, T041, T045, T050, T052 |
| FR-021 | T045, T052, T053 |
| FR-022 | T040, T043, T044 |
| FR-023 | T047, T049, T053, T073 |
| FR-024 | T041, T045, T053 |
| FR-025 | T041, T045, T050, T053 |
| FR-026 | T049, T053, T073 |
| FR-027 | T049, T050, T053, T071 |
| FR-028 | T052, T053, T072 |
| FR-030 | T016, T039, T042 |
| FR-031 | T046, T052, T059, T070 |
| FR-032 | T046, T047, T059 |
| FR-033 | T060, T062, T070, T071 |
| FR-034 | T018, T059, T060, T063, T072 |
| FR-035 | T062, T063, T071 |
| FR-050 | T012, T014, T051, T055, T056, T057 |
| FR-051 | T048, T055, T056, T057 |
| FR-052 | T048, T059, T070 |
| FR-053 | T018, T060, T061 |
| FR-054 | T015, T032, T058 |
| FR-055 | T059, T083 |
| FR-060 | T012, T013, T015, T066, T067 |
| FR-061 | T065, T067, T068 |
| FR-062 | T065, T067, T068 |
| FR-063 | T065, T067, T068 |
| FR-064 | T066, T067 |
| FR-065 | T016, T069, T079 |
| FR-066 | T065, T068 |
| FR-070 | T074, T081 |
| FR-071 | T075, T076, T081 |
| FR-072 | T077, T078, T079 |
| FR-073 | T074, T075, T076 |
| FR-090 | T037, T054, T064, T069, T080 |
| FR-091 | T038, T054, T064, T069, T072, T081, T082, T083, T084, T085, T086 |

Every FR of the spec (FR-001..FR-008, FR-010..FR-015, FR-020..FR-028, FR-030..FR-035, FR-050..FR-055, FR-060..FR-066,
FR-070..FR-073, FR-090, FR-091) has at least one task.

| Success criterion | Tasks |
| --- | --- |
| SC-001 | T032, T033, T034, T062 |
| SC-002 | T062, T071 |
| SC-003 | T053, T073 |
| SC-004 | T052, T053 |
| SC-005 | T066, T067 |
| SC-006 | T061 |
| SC-007 | T035, T083 |
| SC-008 | T001, T063 |
| SC-009 | T080, T082 (removal itself is owned by the game session) |

Scenarios:
- US1: US1-1 → T034; US1-2 → T026–T029, T034; US1-3 → T032; US1-4 → T031, T032; US1-5 → T031, T033, T034;
  US1-6 → T033; US1-7 → T015, T032.
- US2: US2-1 → T042, T052, T053; US2-2 → T045, T052; US2-3 → T049, T053; US2-4 → T043, T044; US2-5 → T041, T053;
  US2-6 → T049, T053; US2-7 → T049, T053.
- US3: US3-1 → T046, T047, T059; US3-2 → T046, T047; US3-3 → T060, T062; US3-4 → T055, T056, T057; US3-5 → T048, T059;
  US3-6 → T060, T061; US3-7 → T061.
- US4: US4-1, US4-2, US4-3 → T066, T067; US4-4 → T015, T067; US4-5 → T067; US4-6 → T067; US4-7 → T069.
- US5: US5-1 → T059, T063, T073; US5-2 → T070, T071, T072; US5-3 → T074, T075, T076, T081.

Breaking changes: BC-1 → T029, T030, T032, T037; BC-2 → T030, T033, T037; BC-3 → T057, T064; BC-4 → T015, T032, T058,
T064; BC-5 → T015, T032, T037; BC-6 → T059, T070, T064.

[VERIFY] items: V1 → T002, T033; V2 → T035; V3 → T002; V4 → T004, T039; V5 → T004, T039; V6 → T005, T040, T045;
V7 → T005, T059, T070; V8 → T005, T045, T051; V9 → T006, T013, T065; V10 → T007, T043; V11 → T007, T044;
V12 → T003, T010, T020; V13 → T003, T036; V14 → T008, T047, T048, T052; V15 → T004, T042, T053.

## Planning notes (gaps between spec, plan and code, resolved here)

1. **Story mapping of the typed mapper.** The plan's phase 4 ("US4") holds the typed registration, the `[Obsolete]`
   mark and duplicate detection. The spec puts FR-050/FR-051/FR-054 under FG-028 = US3 (acceptance US3-4, independent
   test of US3). Tasks follow the spec: migrations, obsolete and duplicates in US3 (T055–T058); hidden members and
   conformance in US4.
2. **`SectionKey` order.** Plan phase 5 (US2 tool, fixture client compiled in the solution) precedes phase 6 (SDK
   `SectionKey`/`SectionTable`), but the generated section file references both types. `SectionKey.cs` moved to Phase 2
   (T018); the `SectionStore` additions stay in US3 (T060).
3. **SDK csproj switch.** The plan changes `GameServer.Client.csproj` in phase 7 (US5), but phase 6 defines
   `GameServerClient.LoginRoute = FrameworkRoutes.IdentityLoginGuest` while the csproj still excludes
   `Runtime\Generated\**` and regenerates through Grpc.Tools. T059 does the first package generation and the csproj
   switch in one change; BC-6 therefore lands in US3, KestrelDemo regeneration in US5 (T070).
4. **`[Obsolete]` timing.** With `TreatWarningsAsErrors`, marking the untyped overload in the foundations (plan phase 1
   "typed mapper + contract + options") breaks the build until every call site migrates. T012 adds the typed API only;
   T057 adds the mark after T055/T056/T051.
5. **Duplicate mapper semantics.** "Registry keeps the first mapper" in the foundations would silently switch last-wins
   to first-wins before any startup line exists. T012 records duplicates only, T032 turns the catalog line on, T058 adds
   `ReplaceWireMapper` and keep-first. The contract does not say what `ReplaceWireMapper` does for an id without an
   earlier mapper; T058 chooses "it registers the mapper" and T077 documents it — confirm with the owner.
6. **Route count.** Plan "Scale" says ~62 framework route declarations; data-model §1.3 has 47 rows, matching the code
   (46 route constants; `/gm/v1/staff` is GET + POST = two declarations). T034 asserts the 47 names.
7. **Template message name.** data-model §1.5 lists `TemplateState`; `templates/module/Template.Module.Adapters/Protos/template.proto`
   declares `package template.module.v1;` → full name `template.module.v1.TemplateState`. T056 records the real names.
8. **Test hooks and default modules.** Plan BC-2 says tests "declare them in the `modules` callback
   (`gs.DeclareRoutes(...)`)", but a `modules` callback replaces `DefaultModules` in `SampleTestHost`/`TestHost`. T030
   adds a helper that keeps the defaults; a missed default does not fail compilation.
9. **Ad-hoc endpoint count.** "~18 ad-hoc `Map*` calls in 8 files" counts `RunLocalScriptTests.cs:156`, which writes a
   plain `WebApplication` (unaffected): 17 calls in 7 framework test files. Not counted by the plan: 8 ad-hoc
   `MapPlayerCommand`/`MapGmCommand`/`MapDevCommand` mappings in `AfterExecuteHookTests`, `AnalyticsSeamTests`,
   `RequestOutcomeFeatureTests`, `UncataloguedCommitCodeTests`, `StartupValidationTests` (T030).
10. **Late-endpoint test changes meaning.** `ErrorContractTests.Endpoint_added_after_validation_without_profile_is_denied`
    asserts a request-time 403 for an endpoint mapped after `UseGameServer`; under FR-012 the host refuses to start.
    T033 rewrites it; the 013 request-time Dev refusal keeps its own test.
11. **Class name clash in KestrelDemo.** The hand-written `SampleGame.Client.SampleGameRoutes` and the generated class of
    the same name and namespace cannot coexist → T070 is one change.
12. **Fixture client props.** `tests/ConsumerFixture/client/` inherits `tests/ConsumerFixture/Directory.Build.props`
    (`net10.0`, `LangVersion latest`, `ImplicitUsings enable` → global usings need C# 10, Banned API analyzer). T008
    measures, T052 overrides.
13. **Fixture server C#.** `Fixture.Game` is one project (no business/adapter split) with no proto today; the server-side
    C# of `fixture_game.proto` needs `Grpc.Tools` + `<Protobuf>` in `Fixture.Game.csproj` (normal consumer server build,
    T051). The plan lists the csproj without this item.
14. **`file:` package and `.meta`.** KestrelDemo references the package as `file:../../../../sdk/unity/com.gameserver.client`
    (mutable): Unity would create a random-GUID `.meta` inside the package for a new file without one. Package `.meta`
    are hand-written (T018, T059) and guarded (T063) before the smoke run (T072).
15. **013 seams the plan does not list.** `TestKitArchitectureTests` restricts which internals the kit uses (T013 allows
    `StateMembers`); 013 request-event field-list assertions change with `RouteName` (T036); the template module has no
    test project, so the conformance test adds a project reference (T066).
16. **V15 added**: the tool and its tests start `dotnet` children; this machine's SDK is `$HOME/.dotnet10/dotnet.exe`.
17. **Renderers in US2.** FR-032/FR-052 are US3 requirements, but the route and section renderers are built with the tool
    in US2 (T046–T048) because every generation run (incl. the fixture end to end of US2) writes all three file kinds.
18. **FR-091 zh-pvp CRLF check** needs a game host built against this branch: only possible in a temp clone (T082) or by
    the game session after the tag; never by editing the game repositories.
19. **IAP descriptor location.** Research says the IAP descriptor is built in the adapter; public-api puts `IapRoutes`
    in `GameServer.Modules.Iap` (business). Both hold: declarations in the business project, `Routes` set where the
    descriptor is built (T023).
