# Release notes draft — v0.7.0 (slice 014 wire-contract)

Framework `0.7.0` (unreleased). Input of the `v0.7.0` annotated tag message (FR-090), merged with the notes of slices 013,
015 and 016 at the end of the round (one tag). Final pass of slice 014 (T080): US1 to US5.

**Fixed:** FG-021 (a game generates its Unity C# from its own repository), FG-026 (one route catalog: (1) declarations,
(2) generated route constants for Unity, (3) startup check of every mapped endpoint), FG-027 (wire-mapper conformance,
hidden fields in code), FG-028 (section ↔ message contract, typed section reader). **Breaking:** BC-1 to BC-6 (table
below). **Obsolete:** the untyped `AddAggregateWireMapper<TState>` (BC-3, kept through v0.7.x).

**Unchanged:** no player API route, wire message, document or GM API response changes; the Unity package stays `0.7.0`
(no public SDK member removed or changed, framework test against the 0.6.0 API).

## Breaking changes

| Id | Change | How it surfaces | Fix |
| --- | --- | --- | --- |
| BC-1 | Every mapped endpoint must be declared in the route catalog and match its declaration (template, method, profile, command operation, GM permission); an endpoint mapped from a declaration instance that no module lists (or from a copy of a listed one) is not declared, and an endpoint mapped while its hosting condition does not hold is refused | Startup exit 1 in every environment, one line per endpoint: `[endpoint POST /api/v1/account/init] is not declared in the route catalog. Fix: …` | Declare each route in `ModuleDescriptor.Routes` (host endpoints: `GameServerBuilder.DeclareRoutes`) and map it from that same declaration (FG-026 below) |
| BC-2 | Endpoints mapped after `UseGameServer` are validated before the server listens (catalog, profile, version segment, duplicates, Public allowlist, Dev rule, command route) | Startup exit 1 (or `GameServerStartupException` from `StartAsync`/`WebApplicationFactory`) with the same lines as for endpoints mapped before `UseGameServer`. For an otherwise passing configuration, endpoint faults now surface from `StartAsync`/`RunAsync`, not from `UseGameServer`, and the startup line moves there | Fix the endpoint as the line says, or move it into a module |
| BC-3 | `AddAggregateWireMapper<TState>(id, Func<TState, IMessage>)` is obsolete | Compiler warning CS0618 (a build error under `TreatWarningsAsErrors`); startup Warning per untyped section | `AddAggregateWireMapper<TState, TWire>(id, map, o => …)` naming the section's message (FG-028 below) |
| BC-4 | Two wire mappers for one aggregate id stop the host (was: the last registration won) | Startup exit 1: `[aggregate {id}] has 2 wire mappers. Fix: …` | One registration per aggregate; replace a framework mapper with `ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(…))`, which must keep the replaced mapper's message (`TWire`; else `[aggregate {id}] ReplaceWireMapper must keep {message}; a new payload needs a new aggregate (FG-021).`) |
| BC-5 | A route listed in `ModuleDescriptor.PublicEndpoints` must be declared with profile Public | Startup exit 1: `[module {module}] lists {template} in PublicEndpoints but route {name} declares profile {profile}. …` | Declare it Public, or remove the entry (Public declarations no longer need `PublicEndpoints`) |
| BC-6 | `sdk/GameServer.Client` no longer runs protoc: the Unity package files and the KestrelDemo sample files come from WireGen | Framework contributors only: building the SDK no longer refreshes `Runtime/Generated` or `KestrelDemo/Assets/SampleGame/Generated`; `CommittedOutputTests` fail until regenerated | `dotnet run --project tools/GameServer.WireGen -c Release -- generate --config sdk/GameServer.Client/wiregen.json` (package) and `… --config samples/SampleGame/wiregen.json` (sample client) |

## zh-pvp-server migration checklist (in this order)

1. **Typed mappers first** (CS0618 blocks the build): `src/ZhPvp.Game.Adapters/GameModule.cs:28-30` →
   `services.AddAggregateWireMapper<ProgressState, Wire.ProgressState>(GameAggregates.Progress, GameWireMapping.ToWire,
   o => o.Hidden("ActiveBattle.WinCash", "ActiveBattle.TrophyWin", "ActiveBattle.IsProtect", "ActiveBattle.ConfigVersion"));`
   and the armory and chests registrations with `Wire.ArmoryState` / `Wire.ChestsState` (BC-3, FG-027, FG-028). Delete the
   hidden-field comment of `src/ZhPvp.Game.Adapters/GameWireMapping.cs:6-9`.
2. **`account.init` declaration** (BC-1): a `GameRoutes` class in `ZhPvp.Game` with
   `RouteDeclaration.Command(InitAccountHandler.Descriptor, "/api/v1/account/init")`, `Routes = [GameRoutes.InitAccount]` in
   `GameModuleDefinition`, and `MapPlayerCommand(GameRoutes.InitAccount, …)` instead of the plain string of
   `src/ZhPvp.Game.Adapters/GameModule.cs:20` (`InitAccountRoute` may stay as a `const` alias with the same literal). Host-level
   endpoints of `ZhPvp.Host`, if any: `gameServer.DeclareRoutes(...)`.
3. **`src/ZhPvp.Host/RequestLog/RequestOutcome.cs:22`** (copy of the config route): delete the `RequestLog` folder (the
   framework's request event covers it since 013), or compare `HttpContext.RequestOutcome().RouteName ==
   PlatformRoutes.Config.Name`.
4. **`wiregen.json` (CRLF) replaces `tools/UnityProtoGen`**: commit `tools/unity-codegen/wiregen.json` (FG-021 below),
   generate with `dotnet run --project framework/tools/GameServer.WireGen -c Release -- generate --config
   tools/unity-codegen/wiregen.json [--output-root <client checkout>]`, add `verify … --no-build` to CI; delete the
   `NormalizeDirectory` workaround, the CRLF inline task, the clean warning and the `FRAMEWORK-GAP FG-021` marker of
   `tools/UnityProtoGen/UnityProtoGen.csproj` (or keep the project as a thin `Exec` of the command). `ZhPvp.Host` `Program`
   must use `GameServerCommandLine.Parse(args)` + `app.RunGameServerAsync(mode)`.
5. **Unity client** (research-and-development-games, not breaking): commit the generated `GameRoutes.cs`/`GameSections.cs`
   and the `.meta` files Unity creates; `Assets/MadProject/Scripts/Online/OnlineSession.cs:39` `InitAccountRoute` →
   `GameRoutes.AccountInit`; the ids of `Assets/MadProject/Scripts/Online/PlayerReadModel.cs:31-37` → `GameSections.*Id` /
   `FrameworkSections.WalletId`; the `switch` of `PlayerReadModel.cs:173-193` → `client.Sections.Register(GameSections.Table)`
   once after creating the client, then `client.Sections.Get(GameSections.Progress)` / `Get(FrameworkSections.Wallet)` or
   `TryRead(id, out var message)` in a `SectionChanged` handler.
6. **Conformance test** in `tests/ZhPvp.Tests`:
   `WireConformance.CheckModules([new EconomyModule(), new IdentityModule(), new GameModule()]).ThrowIfFailed();` and
   `docs/pcr/game.md` "Aggregate" takes its hidden list from the catalog (`hiddenMembers` of the export).

Then build Debug and Release (0 warnings), run the game suite and start each flavor once (consumer guide §11).

## Consumer impact (zh-pvp-server and its Unity client)

| FG | Workaround today | Removable after `v0.7.0` | Stays |
| --- | --- | --- | --- |
| FG-021 | `tools/UnityProtoGen/UnityProtoGen.csproj` (`FRAMEWORK-GAP FG-021` marker): own generator project, `NormalizeDirectory` workaround, CRLF inline task, client-root guard; `docs/COMMANDS.md` entry | The marker, the CRLF task, the separator workaround and the version comment; the project becomes a documented command (or a thin `Exec`) over the game's `wiregen.json` | Sibling-checkout default for the client path and a client-root marker (`output.root`, `output.marker`); client `Assets/link.xml` (game choice) |
| FG-026 | Hand copies: `src/ZhPvp.Game.Adapters/GameModule.cs:20` `InitAccountRoute` (plain string), `src/ZhPvp.Host/RequestLog/RequestOutcome.cs:22` (config route), client `Assets/MadProject/Scripts/Online/OnlineSession.cs:39` `InitAccountRoute` | Server: a declaration mapped from (BC-1 requires it); `RequestOutcome.cs` with the `RequestLog` folder or via `PlatformRoutes.Config`; client: the generated `GameRoutes.AccountInit` | — |
| FG-028 | Client `Assets/MadProject/Scripts/Online/PlayerReadModel.cs:31-37` (hand-copied ids `wallet`, `progress`, `armory`, `chests`) and `:173-193` (`switch` id → parser) | Generated section constants (`FrameworkSections.WalletId` from the package, game ids from `GameSections`) and the typed accessors / generated table | Server ids `src/ZhPvp.Game/State/GameAggregates.cs` (source of truth) |
| FG-027 | Hidden fields in the comment `src/ZhPvp.Game.Adapters/GameWireMapping.cs:6-9` and in `docs/pcr/game.md` "Aggregate" | `Hidden(...)` on the typed registrations (`GameModule.cs:28-30`); `docs/pcr/game.md` from the catalog; one conformance test (new) | Hand-written mapper bodies; `PlayerReadModel`/`LocalSaveHydrator` field handling in the client |

Markers removable: FG-021 (one). FG-026, FG-027 and FG-028 have no marker; their hand copies (3 route copies, 1 id/parser
table) and the hidden-field comment become removable.

## FG-021 — a game generates its Unity C# from its own repository

### New

- **Host export mode** `--export-wire-contract <path>` (`HostMode.ExportWireContract`, `GameServerCommandLine.ExportPath`):
  `dotnet run --project <host> --no-launch-profile [--no-build] -c <cfg> -- --environment Local --export-wire-contract <file>`
  checks the composition (deployment identity, module rules, route and section declarations; no game config) and writes the
  catalog export (format `gameserver.wire-contract/1`, UTF-8 without BOM, LF, no timestamp, path, machine or environment
  value) to `<file>`, then exits 0. It never maps or serves, starts no hosted service, opens no store and reads no secret.
  Exit 1 with one line per fault, or when the folder of `<file>` does not exist. Refused like `--provision`:
  `--export-wire-contract=…`, `-export-wire-contract`, `/export-wire-contract`, the switch without a path, twice, or with
  `--provision`. Hosts already written as `GameServerCommandLine.Parse(args)` + `app.RunGameServerAsync(mode)` need no change.
- **`tools/GameServer.WireGen`** (framework tool, in `GameServer.slnx`): one committed `wiregen.json` (format
  `gameserver.wiregen/1`) per output, two verbs:
  `dotnet run --project <framework>/tools/GameServer.WireGen -c Release -- generate|verify --config <wiregen.json>
  [--output-root <client>] [--catalog <export>] [--configuration <cfg>] [--no-build] [--dotnet <path>]`.
  It runs the host export (or reads `catalog.file` / `--catalog`), checks the versions, runs the framework-pinned protoc over
  the game's protos and writes, into `output.directory`: one message file per listed proto, `<routesClass>.cs` (a constant
  per player-facing route of the game, no leading slash, `…Method` constants, parameter helpers with escaped values) and
  `<sectionsClass>.cs` (typed `SectionKey<T>` per section, `…Id` constants, `Table`). Framework routes, sections and proto
  types are never generated into a game's output: they ship in the Unity package.
- **Framework proto roots without listing**: `proto/`, the identity, economy and IAP `Protos` folders and the well-known types
  are always import roots; a game proto imports `gameserver/core/v1/core.proto` as is.
- **Version pair**: `Grpc.Tools` 2.84.0 (protoc 35.1) generating for `Google.Protobuf` 3.36.2, the dll shipped in
  `Runtime/Plugins` of the Unity package. The tool uses its own restored protoc, never the game's, and fails naming both
  versions when the export was made by another framework version, when the host resolves another `Google.Protobuf`, or when
  the host's central package versions pin another `Grpc.Tools` or `Google.Protobuf` (`VersionOverride` in other projects is
  not seen). A framework test keeps CPM and the Unity dll equal.
- **Deterministic, write-if-changed output**: the same inputs give byte-identical files on any checkout path (mixed `/` and
  `\` accepted), in `lf` (default) or `crlf`, UTF-8 without BOM, no timestamp, absolute path, user or machine; an unchanged
  file is not rewritten (no Unity reimport). The tool never deletes a file and never reads, writes or deletes a `.meta`; for
  each new file it prints `new file <path>: Unity creates <path>.meta on import; commit it`.
- **`verify` for CI**: writes nothing, lists every differing or missing file relative to the output root, exit 2.
- **Exit codes are a contract**: 0, 1 or 2 only; an unexpected failure of the tool itself is exit 1 with one `[internal] …`
  line, and a temp folder that cannot be deleted is a note that changes neither the exit code nor the error shown.
- **Errors before any write** (exit 1, one line naming the input and the fix): unreadable config or unknown format major,
  missing `output.root` / `output.marker` / `output.directory` (never created), an `output.marker` or `output.directory`
  that is absolute or leaves `output.root` through `..` (the tool writes only inside the root), missing proto, protoc compile error
  (`file:line` relayed), a game message duplicating a framework message (protoc "already defined", or the same C# type name
  through `csharp_namespace`), a section whose message the client does not have, colliding generated names, a player-facing
  route with several methods or with a constraint, optional, default or catch-all parameter. When the host itself does not
  build (e.g. CS0618 of BC-3 before migrating), the tool relays only the `dotnet run` summary ("The build failed…"): run
  `dotnet build <host project>` to see the compiler errors.
- **Clean-safe**: the outputs are not items of any MSBuild project, so `dotnet clean` or a rebuild never deletes them.
- A section registered with the untyped (obsolete, BC-3) wire mapper gets its `…Id` constant only, and the tool prints a
  warning (exit code unchanged). The framework's own sections are typed, so a framework-origin run prints none.


### Breaking changes

- **BC-6 — the framework's Unity files come from WireGen, not from the SDK build.** `sdk/GameServer.Client` has no
  `Grpc.Tools`/`<Protobuf>` items any more: it compiles the committed `Runtime/**/*.cs`, and building it never writes into
  the package nor into `samples/SampleGame/KestrelDemo`. *How it surfaces:* framework contributors only — a changed
  framework proto, route or section is not picked up by the SDK build, and `CommittedOutputTests` (`verify` of the committed
  configs) fail naming the stale file. *Fix:* `dotnet run --project tools/GameServer.WireGen -c Release -- generate --config
  sdk/GameServer.Client/wiregen.json` for the package and `… -- generate --config samples/SampleGame/wiregen.json` for the
  KestrelDemo sample client, then commit the result.

### zh-pvp-server (first consumer)

- Replace `tools/UnityProtoGen/UnityProtoGen.csproj` by a committed `tools/unity-codegen/wiregen.json`:
  `{ "format": "gameserver.wiregen/1", "catalog": { "hostProject": "../../src/ZhPvp.Host" }, "protos": [ { "root":
  "../../src/ZhPvp.Game.Adapters/Protos", "files": ["zhpvp_game.proto"] } ], "output": { "root":
  "../../../research-and-development-games/src/GameClient", "marker": "Assets/MadProject/Scripts", "directory":
  "Assets/MadProject/Scripts/Online/Generated", "lineEndings": "crlf" }, "namespace": "<game's choice>" }`.
- Generate with `dotnet run --project framework/tools/GameServer.WireGen -c Release -- generate --config
  tools/unity-codegen/wiregen.json` (add `--output-root <client checkout>` when the client is elsewhere; CI: `verify …
  --no-build`). The project may stay as a thin `Exec` of that command, or be deleted.
- Delete the `NormalizeDirectory` workaround, the CRLF inline task, the clean warning and the `FRAMEWORK-GAP FG-021` marker.
- `ZhPvp.Host` `Program` must use `GameServerCommandLine.Parse(args)` + `app.RunGameServerAsync(mode)` (the export mode is
  part of `RunGameServerAsync`).
- The first generation adds `<routesClass>.cs` and `<sectionsClass>.cs` next to the message file: commit the `.meta` files
  Unity creates for them.

## FG-026 — one route catalog: declarations (1), generated route constants (2), endpoint check (3)

### New

- **Route declarations** (`GameServer.Routing.RouteDeclaration`, in `GameServer.Core`, no ASP.NET or Protobuf): name, methods,
  template, security profile, surface (`PlayerApi`, `GmApi`, `Host`), command operation, GM permission, hosting condition.
  Factories `Get`, `Post`, `Put`, `Delete`, `AnyMethod` and `Command(descriptor, template)` (name = operation, POST, profile
  and permission from the `CommandDescriptor`). A module declares its routes in `ModuleDescriptor.Routes`; a host declares
  the endpoints it maps itself with `GameServerBuilder.DeclareRoutes(...)` (owner `host`).
- **Framework routes are public declarations**: `PlatformRoutes` (time, snapshot, config, receipt, GM OpenAPI),
  `ObservabilityRoutes` (`/health/live`, `/health/ready`), `IdentityRoutes`, `IapRoutes`, `EconomyRoutes`, `CheatRoutes`,
  `GmRoutes`. The existing `public const string …Route` members stay, with the same values. No consumer needs to copy a
  framework route string.
- **Mapping from the declaration**: `MapPlayerCommand`, `MapDevCommand` and `MapGmCommand` take a `RouteDeclaration`
  (template, operation and, for GM, permission written once); `MapDeclared(declaration, handler)` for other routes and
  `.WithDeclaration(declaration)` for endpoints made by library helpers (`MapHealthChecks`, `MapOpenApi`). The string
  overloads stay; endpoints mapped with a plain string are matched by method and template.
- **`WireCatalog`** (DI singleton, `GameServer.Platform.Web.Catalog`): every declared route with owner (module, `platform`,
  `host`), origin (`framework` for the platform and `GameServer.*` assemblies, else `game`) and service-only flag; every
  client-visible section with its wire mapper. Available after `Build()` without starting the host. Named hosting conditions:
  `development_tools` (platform) and `gm.sso` (GM); a module adds its own with `services.AddRouteCondition(name, holds)`.
- **Startup check** (every environment, no setting turns it off): declaration rules (name `<area>.<route>` in lower case,
  ≤ 64; unique name; unique method + template; valid, versioned template; GM routes and only GM routes carry a permission;
  known condition) and mapped endpoints ↔ catalog (not declared — including an endpoint bound to a declaration instance no
  module, platform or host lists, so the export cannot silently lose a route; bound to a copy of a listed declaration;
  template/method/profile/operation/permission differs; mapped while its registered condition does not hold; declared but not
  mapped while its surface is mapped, its owner is not service-only and its condition holds). One report line per fault,
  naming the route and the fix; exit 1. `--provision` checks the declarations too. A Public declaration whose condition does
  not hold is not a public endpoint.
- **Endpoints mapped after `UseGameServer`** are validated before the server listens, with every endpoint rule (catalog,
  security profile, version segment, duplicates, Public allowlist, Dev rule, command route). The request-time Dev refusal of
  slice 013 stays as defense in depth.
- **Route name on the request outcome**: `HttpContext.RequestOutcome().RouteName` (default interface member, so existing
  implementations of `IRequestOutcomeFeature` still compile) and request event property `RouteName` (after `Route`; the
  console line is unchanged). Compare by name instead of a copied string: `RequestOutcome().RouteName == PlatformRoutes.Config.Name`.
- Not breaking: Public declarations of modules and of the platform count as public endpoints, so `/v1/time` no longer needs
  `GameServer:PublicEndpointAllowlist` (its default value stays).

- **Generated route constants for Unity (FG-026 (2))**: the Unity package ships `Runtime/Generated/FrameworkRoutes.cs`,
  produced by the same WireGen step as a game (`sdk/GameServer.Client/wiregen.json`, origin `framework`, catalog of the
  SampleHost Local composition): every player-facing framework route — `IdentityLoginGuest`, `IdentityLoginProvider`,
  `IdentityLink`, `IdentityLogout`, `IdentityRequestDeletion`, `IdentitySetDisplayName`, `IapPurchase`, `CheatGrant`,
  `PlatformSnapshot`, `PlatformConfig`, `PlatformTime`, `PlatformReceipt(receiptId)` — each with `…Method`, no leading
  slash, no `gm.*`, and no unversioned health probe (`/health/live`, `/health/ready` serve load balancers, not the client).
  A game gets the same for its own routes (`<routesClass>.cs`, FG-021).
- **SDK route constants** `GameServerClient.LoginRoute … TimeRoute` are now defined from `FrameworkRoutes` (same values).
  The SDK's own code and the KestrelDemo client script contain no route literal (framework tests scan them; the sample
  script is also checked for section id literals).

### Breaking changes

- **BC-1 — every mapped endpoint must be declared.** An undeclared endpoint, or one whose template, method, security
  profile, command operation or GM permission differs from its declaration, stops the host in every environment.
  *How it surfaces:* exit 1 with one report line per endpoint, e.g.
  `[endpoint POST /api/v1/account/init] is not declared in the route catalog. Fix: declare it in the module's ModuleDescriptor.Routes (or GameServerBuilder.DeclareRoutes for a host endpoint) and map it from the declaration (FG-026).`
  *Fix:*
  1. In the business project add `public static class <Game>Routes` with one `RouteDeclaration` per route
     (`RouteDeclaration.Command(Handler.Descriptor, "/api/v1/…")` for commands).
  2. Add `Routes = [...]` to the module's `ModuleDescriptor`.
  3. Map with the declaration overloads: `MapPlayerCommand(<Game>Routes.X, toDomain, toWire)`, `MapDeclared(<Game>Routes.Y, handler)`.
  4. Declare host-only endpoints with `gameServer.DeclareRoutes(...)` before `Build()`. Public host routes still need
     `GameServer:PublicEndpointAllowlist` (operator control, SEC-09).
  5. Start each flavor once; each remaining line names the route and the fix.
- **BC-2 — endpoints mapped after `UseGameServer` are validated before listening.** Such endpoints were not checked at all
  (an endpoint without profile answered 403 per request). *How it surfaces:* exit 1 (or `GameServerStartupException` from
  `StartAsync` / `WebApplicationFactory` start) with the same report lines as for endpoints mapped before `UseGameServer`.
  *Fix:* correct the endpoint as the line says, or move it into a module. Tests that map extra endpoints in the kit's
  `afterUseGameServer`/`beforeUseGameServer` hooks declare them too (`gs.DeclareRoutes(...)` in the `modules` callback;
  remember that passing `modules` replaces the default modules).
- **BC-5 — a route listed in `ModuleDescriptor.PublicEndpoints` must be declared Public.** *How it surfaces:* exit 1,
  `[module {module}] lists {template} in PublicEndpoints but route {name} declares profile {profile}. Fix: declare it Public or remove it from PublicEndpoints (SEC-09).`
  *Fix:* make the declaration Public, or remove the entry (Public declarations no longer need `PublicEndpoints`; the
  framework modules dropped theirs).

### zh-pvp-server (first consumer)

- `account.init` (`src/ZhPvp.Game.Adapters/GameModule.cs:20` `InitAccountRoute`, mapped with a plain string today): in the
  game's business project
  `public static RouteDeclaration InitAccount { get; } = RouteDeclaration.Command(InitAccountHandler.Descriptor, "/api/v1/account/init");`
  in a `GameRoutes` class, `Routes = [GameRoutes.InitAccount]` in `GameModuleDefinition`, and
  `routes.MapPlayerCommand<Wire.InitAccountRequest, InitAccountCommand, InitAccountResult>(GameRoutes.InitAccount, …)` in the
  adapter. `InitAccountRoute` may stay as a `const` alias with the same literal (a `const` cannot read
  `GameRoutes.InitAccount.Template`, and game tests use it in `const` expressions).
- `src/ZhPvp.Host/RequestLog/RequestOutcome.cs:22` copies the config route: use `PlatformRoutes.Config.Template`, or compare
  `HttpContext.RequestOutcome().RouteName == PlatformRoutes.Config.Name` (or drop the folder: the framework's request event
  covers it since 013).
- Host-level endpoints of `ZhPvp.Host`, if any, are declared with `gameServer.DeclareRoutes(...)`; Public ones stay on
  `GameServer:PublicEndpointAllowlist`.
- Unity client (not breaking): `Assets/MadProject/Scripts/Online/OnlineSession.cs:39` `InitAccountRoute` → the generated
  `GameRoutes.AccountInit` (name from the route `account.init`).

## FG-027 — a wire mapper that drops a field fails a test; hidden fields are code

### New

- **`WireConformance` in `GameServer.TestKit`** (no test framework, no host, no store):
  `WireConformance.CheckModules([new EconomyModule(), new GameModule()]).ThrowIfFailed();` (modules' `ConfigureServices` run
  against a bare service collection), `Check(module, aggregateId)`, `CheckAll(app.Services)` (every client-visible aggregate
  of a built composition). The kit builds a sample state from the persisted members (System.Text.Json contract of
  `GameServerJson.Options`: members with a setter or a constructor parameter; collections get two elements, dictionaries
  two entries, each leaf a distinct value; the last element or entry is perturbed, so a mapper that keeps only the first is
  caught), maps it, and reports: `FieldAtDefault` (a proto field still default; repeated/map
  empty; presence fields absent; oneof unset), `MemberNotOnWire` (perturbing a non-hidden member leaves the message
  unchanged, nested paths such as `ActiveBattle.Seed`), `HiddenMemberOnWire` (a hidden member reaches the wire, or the
  declaration is stale), `MemberNotFillable` (a member type the kit cannot sample, e.g. `object`: supply it with
  `WithMemberValue`), `SampleNotSerializable`, `UntypedMapper`, `NoMapper`. One line per finding:
  `[aggregate {id}] {problem}: {path} — {fix}`. Options: `new WireConformanceOptions().WithSampleState(id, state)` (the
  sample, still perturbed) and `.WithMemberValue(id, path, value)` (not perturbed; noted in the report text); the `With*`
  methods chain. `Check` and `CheckModules` see the mappers the modules register; only `CheckAll` sees a host's
  `ReplaceWireMapper` override.
- **Hidden members and may-stay-default fields are declared at the registration**: `o => o.Hidden("ActiveBattle.WinCash")`
  (C# member names joined by `.`, collection elements transparent) and `o => o.MayStayDefault("kind")` (proto field names).
  Both are validated at startup (unknown path → exit 1, `[aggregate {id}] hidden member '{path}' does not exist on {TState}.
  Fix: use C# member names joined by '.' (FG-027).`) and listed per section in `WireCatalog.Sections` and the export
  (`hiddenMembers`, `mayStayDefault`).
- Framework declarations (every framework, sample and template mapper passes, payload bytes unchanged): `profile` hides
  `Ban`, `Deleted`, `Identities` (ban details, deletion request and linked mapping keys stay on the server); sample `battle`
  hides `ActiveBattle.ConfigVersion`, `ActiveBattle.Xp`, `ActiveBattle.Rewards`, `ActiveBattle.Drops` (rewards captured at
  battle start, PLY-05). No field needs `MayStayDefault`.

### zh-pvp-server (first consumer)

- The comment of `src/ZhPvp.Game.Adapters/GameWireMapping.cs:6-9` becomes code on the progress registration:
  `o => o.Hidden("ActiveBattle.WinCash", "ActiveBattle.TrophyWin", "ActiveBattle.IsProtect", "ActiveBattle.ConfigVersion")`.
- Add one test in `tests/ZhPvp.Tests`:
  `WireConformance.CheckModules([new EconomyModule(), new IdentityModule(), new GameModule()]).ThrowIfFailed();` — each
  finding names the member or field and the fix (map it, or declare it `Hidden`/`MayStayDefault`).
- `docs/pcr/game.md`: take the hidden list from the catalog (`WireCatalog.Sections[…].HiddenMembers`, or the export's
  `hiddenMembers`).

## FG-028 — section ↔ message contract, typed section reader

### New

- **Typed wire mapper** `services.AddAggregateWireMapper<TState, TWire>(aggregateId, map, o => o.Hidden(…).MayStayDefault(…))`
  (`TWire` = the section's Protobuf message): the catalog and the export name each section's message
  (`gameserver.economy.v1.WalletState`, …) and WireGen emits a typed key for it. Every framework, sample and template mapper
  uses it.
- **Replacing a mapper**: `gameServer.ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(aggregateId, map, configure))`
  removes the mappers registered for the id so far (through `AddAggregateWireMapper`) and registers the new one; for an id
  without a mapper it simply registers it. The replacement must keep the message of the typed mapper it replaces: the
  client parses the section with that message's parser (a framework section with the package's parser), so another
  `TWire` stops the host with `[aggregate {id}] ReplaceWireMapper must keep {message}; a new payload needs a new aggregate (FG-021).`
- **Mapper state type**: a typed mapper whose `TState` does not take the aggregate's state type (which failed every
  snapshot of the section with a cast error, HTTP 500) stops the host: `[aggregate {id}] wire mapper takes {TState} but the
  aggregate holds {state}. Fix: AddAggregateWireMapper<{state}, TWire>(…) (API-10).`
- **Generated sections in the Unity package**: `Runtime/Generated/FrameworkSections.cs` (`Wallet`/`WalletId`,
  `Inventory`/`InventoryId`, `Profile`/`ProfileId`, `Table`). The protoc output of the package
  (`gameserver/core/v1/Core.cs`, `Identity.cs`, `Economy.cs`, `Iap.cs`) is byte-identical to 0.6.0; paths and `.meta`
  GUIDs of existing files are unchanged. A game gets `<sectionsClass>.cs` for its own sections (FG-021).
- **Typed section reader** on `client.Sections` (`SectionStore`): `Register(SectionTable)` (once, with the game's generated
  `GameSections.Table`; the framework table is registered by the constructor; the same id with the same message type is
  idempotent, another type throws `ArgumentException`), `Get(SectionKey<T>)` (the parsed message or null), `IsKnown(id)`,
  `TryRead(id, out IMessage? message)` → `Absent` (not received), `Unknown` (no registered table knows the id: kept raw,
  never parsed — a newer server) or `Parsed`. `Get<T>(id, parser)`, `TryGet`, `RevOf`, `SectionChanged` are unchanged.
  Package version stays `0.7.0`; no public SDK member was removed or changed (framework test against the 0.6.0 API).
- **Untyped-section warning**: a client-visible section registered with the untyped mapper logs, once per section at startup
  (category `GameServer.Startup`, Warning): `[aggregate {id}] wire mapper registered without its message type: the catalog has
  no message for this section and Unity gets no parser. Fix: AddAggregateWireMapper<TState, TWire>(…) (FG-028).` The host
  still starts.
- Framework guards: every file and folder of the Unity package has a `.meta`, recorded GUIDs never change, GUIDs are unique;
  `verify` of the committed SDK and fixture configs runs in every framework test run; the SampleHost Local composition
  registers every framework module (so the package covers every framework route and section).

### Breaking changes

- **BC-3 — `AddAggregateWireMapper<TState>(id, Func<TState, IMessage>)` is obsolete** (kept through v0.7.x). *How it
  surfaces:* compiler warning CS0618 `Use AddAggregateWireMapper<TState, TWire>(aggregateId, map) so the catalog and Unity
  know the section's message (FG-028). Kept through v0.7.x.` — a build error where `TreatWarningsAsErrors` is on (the
  framework props and the first consumer); plus the startup Warning above per untyped section. *Fix:* name the message type:
  `services.AddAggregateWireMapper<TState, Wire.TState>(id, map)`.
- **BC-4 — two wire mappers for one aggregate id stop the host** (was: the last registration won silently). *How it
  surfaces:* exit 1, `[aggregate {id}] has 2 wire mappers. Fix: register one mapper per aggregate; replace a framework mapper
  with ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(…)) (API-10).` *Fix:* keep one registration; to change a
  framework section's payload use `ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(id, map))` (above). The
  registry serves the first registration (no last-wins left anywhere).

### zh-pvp-server (first consumer)

- `src/ZhPvp.Game.Adapters/GameModule.cs:28-30` (CS0618 is an error there):
  `services.AddAggregateWireMapper<ProgressState, Wire.ProgressState>(GameAggregates.Progress, GameWireMapping.ToWire, o => o.Hidden(…));`
  (hidden list: FG-027 above), `services.AddAggregateWireMapper<ArmoryState, Wire.ArmoryState>(GameAggregates.Armory,
  GameWireMapping.ToWire);`, `services.AddAggregateWireMapper<ChestsState, Wire.ChestsState>(GameAggregates.Chests,
  GameWireMapping.ToWire);`.
- Unity client (not breaking; after updating the package and generating with the game's `wiregen.json`): the section ids
  of `Assets/MadProject/Scripts/Online/PlayerReadModel.cs:31-37` → `GameSections.ProgressId` … and
  `FrameworkSections.WalletId`; the `switch` of `PlayerReadModel.cs:173-193` → `client.Sections.Register(GameSections.Table)`
  once after creating the client, then `client.Sections.Get(GameSections.Progress)` (and `FrameworkSections.Wallet` …) or,
  in a `SectionChanged` handler, `client.Sections.TryRead(id, out var message)`. Server ids stay in
  `src/ZhPvp.Game/State/GameAggregates.cs`.
- Commit the `.meta` files Unity creates for the generated `<routesClass>.cs` and `<sectionsClass>.cs`.

## Framework repository: sample client and docs on the same path

- `samples/SampleGame/wiregen.json` generates the KestrelDemo client files (`SampleGame.cs` unchanged, new
  `SampleCounter.cs`, `SampleGameRoutes.cs`, `SampleGameSections.cs`); `SampleGameClient.cs` uses
  `SampleGameRoutes.BattleStart`/`BattleEnd`/`ShopBuy`, `Register(SampleGameSections.Table)` and
  `Get(FrameworkSections.Wallet)` instead of hand-written strings. Every framework test run verifies the three committed
  configs (package, sample, consumer fixture).
- Docs: consumer guide §4 (host composition: `DeclareRoutes`, export mode, the reserved `GameServer.` prefix), §5.5
  (conformance), §10 (route declaration, endpoint check, typed mappers, `wiregen.json`, generate/verify, line endings,
  version pair, cleaning, `.meta`, IL2CPP, generated code in Unity), §11 (bump steps); `docs/guides/module-authoring.md`
  §5, §8, §9; `docs/html/02-them-command.html` §4.4, §4.5, §4.8, §4.10, §5, §10; PCR platform §20 (catalog, check, export
  format, section contract), PCR unity-sdk (generated files, typed accessor, version pair), route and section tables in the
  identity, economy, iap, gm, sample-counter and sample-game PCRs.

## Manual checks

| Check | Status |
| --- | --- |
| KestrelDemo in Unity 6 (`sdk/unity/ci/smoke-build.ps1 -CompileOnly`, then `-E2E` against SampleHost on 5080), T072 | Done 2026-11-03 (Unity 6000.3.22f1): compile exit 0, E2E exit 0 (login, battle start/end OK, buy, gold, `result=OK`); Unity created the three `.meta` files of the new sample files; no `.meta` created or changed under `sdk/unity` |
| Reviewer walkthrough of the consumer guide §10 and `02-them-command.html` §4.10/§5 (story 5), T081 | Done 2026-11-03: 11 missing or unclear steps (command registration, rejection test, when to rerun conformance, `WithSampleState`, proto C# namespace, `At`/`Now`/`CommandResponse` fields, fixture WireGen and test commands, `-c Release`, illustrative Unity snippet, the three verified configs) fixed in both docs (T086) |
| First-consumer CRLF regeneration in a temp clone of zh-pvp-server (SC-009), T082 | Done 2026-11-03 in a temp clone (framework at `2dc74e8`): after BC-3 and BC-1 (as in the checklist), `generate` with `crlf` reproduced the committed `ZhpvpGame.cs` byte for byte and added only `GameRoutes.cs` and `GameSections.cs`; rerun wrote nothing, `verify` 0, conformance over economy, identity and game passes; nothing bound on 5180 |
| Generation on Linux/macOS (determinism across OS, N9) | Pending: no CI runner yet (CI-01); Windows covers two checkout paths, mixed separators, LF and CRLF |

## Known before the tag

- **When endpoint faults surface (SC-007).** Endpoint faults of an otherwise valid configuration are reported when the host
  starts (`StartAsync`, before any hosted service I/O and before Kestrel binds): same report lines, exit 1 through
  `RunGameServerAsync`, nothing bound. With another fault, `UseGameServer` still throws one report holding the endpoint lines
  too. The startup line (`Starting {game} … on framework …`) is logged after the endpoint check (BC-2). The endpoint set is built
  once before serving, as in 0.6.0: no added startup cost beyond the check itself (≈ 1 ms for 217 endpoints on the reference
  machine).
