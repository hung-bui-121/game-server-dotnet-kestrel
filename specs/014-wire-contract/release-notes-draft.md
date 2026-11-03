# Release notes draft — v0.7.0 (slice 014 wire-contract)

Framework `0.7.0` (unreleased). Input of the `v0.7.0` annotated tag message (FR-090), merged with the notes of slices 013,
015 and 016 at the end of the round (one tag). Each user story of this slice appends its part; this draft holds US1 and US2.

FG ids addressed so far: FG-026 (1) route catalog, FG-026 (3) endpoint check, FG-021 consumer Unity code generation. Breaking
changes so far: BC-1, BC-2, BC-4, BC-5.

## FG-026 (1), (3) — one route catalog, checked against what is mapped

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
  known condition) and mapped endpoints ↔ catalog (not declared, template/method/profile/operation/permission differs,
  declared but not mapped while its surface is mapped, its owner is not service-only and its condition holds). One report
  line per fault, naming the route and the fix; exit 1. `--provision` checks the declarations too.
- **Endpoints mapped after `UseGameServer`** are validated before the server listens, with every endpoint rule (catalog,
  security profile, version segment, duplicates, Public allowlist, Dev rule, command route). The request-time Dev refusal of
  slice 013 stays as defense in depth.
- **Route name on the request outcome**: `HttpContext.RequestOutcome().RouteName` (default interface member, so existing
  implementations of `IRequestOutcomeFeature` still compile) and request event property `RouteName` (after `Route`; the
  console line is unchanged). Compare by name instead of a copied string: `RequestOutcome().RouteName == PlatformRoutes.Config.Name`.
- Not breaking: Public declarations of modules and of the platform count as public endpoints, so `/v1/time` no longer needs
  `GameServer:PublicEndpointAllowlist` (its default value stays).

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
- **BC-4 — two wire mappers for one aggregate id stop the host** (was: the last registration won silently). *How it
  surfaces:* exit 1, `[aggregate {id}] has 2 wire mappers. Fix: register one mapper per aggregate; replace a framework mapper
  with ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(…)) (API-10).` *Fix:* keep one registration. The
  documented replacement `ExtensionRegistry.ReplaceWireMapper` arrives with the typed wire mapper (US3 of this slice).
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
  adapter. `InitAccountRoute` may stay as an alias of the template.
- `src/ZhPvp.Host/RequestLog/RequestOutcome.cs:22` copies the config route: use `PlatformRoutes.Config.Template`, or compare
  `HttpContext.RequestOutcome().RouteName == PlatformRoutes.Config.Name` (or drop the folder: the framework's request event
  covers it since 013).
- Host-level endpoints of `ZhPvp.Host`, if any, are declared with `gameServer.DeclareRoutes(...)`; Public ones stay on
  `GameServer:PublicEndpointAllowlist`.

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
- **Errors before any write** (exit 1, one line naming the input and the fix): unreadable config or unknown format major,
  missing `output.root` / `output.marker` / `output.directory` (never created), missing proto, protoc compile error
  (`file:line` relayed), a game message duplicating a framework message (protoc "already defined", or the same C# type name
  through `csharp_namespace`), a section whose message the client does not have, colliding generated names, a player-facing
  route with several methods or with a constraint, optional, default or catch-all parameter.
- **Clean-safe**: the outputs are not items of any MSBuild project, so `dotnet clean` or a rebuild never deletes them.
- A section registered with the untyped wire mapper gets its `…Id` constant only, and the tool prints a warning (exit code
  unchanged).

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

### Known before the tag

- **When endpoint faults surface (SC-007).** Endpoint faults of an otherwise valid configuration are reported when the host
  starts (`StartAsync`, before any hosted service I/O and before Kestrel binds): same report lines, exit 1 through
  `RunGameServerAsync`, nothing bound. With another fault, `UseGameServer` still throws one report holding the endpoint lines
  too. The startup line (`Starting {game} … on framework …`) is logged after the endpoint check. The endpoint set is built
  once before serving, as in 0.6.0: no added startup cost beyond the check itself (≈ 1 ms for 217 endpoints on the reference
  machine).
