# Release notes draft — v0.7.0 (slice 014 wire-contract)

Framework `0.7.0` (unreleased). Input of the `v0.7.0` annotated tag message (FR-090), merged with the notes of slices 013,
015 and 016 at the end of the round (one tag). Each user story of this slice appends its part; this draft holds US1.

FG ids addressed so far: FG-026 (1) route catalog, FG-026 (3) endpoint check. Breaking changes so far: BC-1, BC-2, BC-4, BC-5.

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

### Known before the tag

- **Startup cost of the pre-serving check (SC-007 not met).** Validating the final endpoint set materializes every endpoint
  once more (ASP.NET Core `RequestDelegateFactory` per endpoint): measured median ≈ 440 ms for 217 endpoints on the reference
  machine (validation itself ≈ 1 ms), ≈ 2 ms per endpoint, once per start. Decision pending (slice 014 tasks, T035).
