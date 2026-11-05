# Implementation Plan: GM API contract (slice 017 gm-api-contract, GM-1)

**Branch**: `017-gm-api-contract` | **Date**: 2026-11-05 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/017-gm-api-contract/spec.md`; design
`AgentMemories/features/gm-tool/01_design.md` §19 (scope) and §5 (contract).

## Summary

First slice of the GM tool track (GM-1 of GM-1..GM-4), backend contract only.

- **Document (US1, US4).** `GmOpenApi` gets an operation transformer (operation id = route name, area tag, path and
  header parameters, `x-gm-kind`/`-operation`/`-target`/`-confirm`/`-paged`, query parameters from metadata), a schema
  transformer (DataAnnotations keywords, real `required` of request bodies, `reason` rule, `x-gm-options`) and a document
  transformer (no `servers`). A metadata-only presentation API (`WithGmConfirm`, `ProducesGmPage<T>`,
  `WithGmQueryParameter`, `WithGmRequestId`, `[GmOptions]`, `GmOptionList`) annotates framework routes; every framework GM
  route gets a summary.
- **Validation (US2).** One attribute walk over the System.Text.Json contract of a body (`GmBodyContract`) feeds both the
  schema and the server (`GmBodyValidator`): `MapGmCommand` and `GmHttp.ReadJsonAsync` answer `400 BAD_REQUEST` with an
  additive `errors` member (new init property on `GameServerException`, written by `ErrorResponseWriter` for Problem
  Details only). Framework bodies get only annotations they already enforce; the sample `SetProgressionBody` is the
  reference example.
- **Catalog and bootstrap (US3).** The GM document is registered in every environment; `GmCatalogDocument` makes its
  canonical bytes once per process; new routes `gm.console_catalog` (Gm, `gm.access`, ETag/304) and `gm.auth_methods`
  (Public, allowlisted by declaration); `GmMeResponse` gains `frameworkVersion`/`gameVersion`. The dev route stays.
- **Options (US4).** `economy.resource_options` and `identity.ban_scopes` (shared `BanScopes` with the ban command);
  startup line for a `[GmOptions]` naming no GM GET route.
- **Snapshot (US5).** `GmOpenApiSnapshot` in the test kit (over `IServiceProvider` and `TestHost`); the framework document
  test uses it; the consumer fixture gains the GM module, a GM command, an option route, a committed document and a
  snapshot test.
- **Docs (US6).** PCR gm/identity/economy/platform/sample-game, module-authoring and consumer guides,
  `docs/html/02-them-command.html` §8, stale ownership lines (H12), release notes draft.

Decisions: [research.md](research.md) (R1–R14, VERIFY V1, V10, V11–V13); shapes: [data-model.md](data-model.md);
contracts: [public-api.md](contracts/public-api.md), [gm-document.md](contracts/gm-document.md).

## Technical Context

**Language/Version**: C# / .NET 10 (SDK 10.0.403, ASP.NET Core 10.0.14)

**Primary Dependencies**: `Microsoft.AspNetCore.OpenApi` 10.0.14 (already referenced by Platform.Web),
`System.ComponentModel.DataAnnotations` (BCL), System.Text.Json (BCL). No new package.

**Storage**: none changed. No Couchbase document, collection, index or transaction change.

**Testing**:
- xUnit v3 + Shouldly; `TestHost`, `SampleTestHost`/`GmTestHost` (framework GM tests), `LocalFlavorFactory<T>`;
- Couchbase integration project: `SampleHostFlavorTests` (flavor matrix incl. the catalog route), full suite at the end;
- consumer fixture (`tests/ConsumerFixture`), architecture tests, the snapshot tests;
- opt-in perf (`GAMESERVER_PERF=1`) for SC-004.

**Target Platform**: Linux containers; Windows/macOS/Linux dev machines. No Unity change.

**Project Type**: web-service framework + consumer test kit

**Performance Goals**: catalog warm answer ≤ 5 ms p95 in-process, first generation ≤ 1 s; host start time unchanged
(document not generated at startup); validation adds one cached contract walk per GM body (microseconds).

**Constraints**: ARC-01 (Core gets only a BCL-typed init property; presentation API in Platform.Web), SEC-07/08/09
(new routes declared, permissions in catalogs, Public by declaration), API-04 (`errors` safe messages), GM-06 (paging
rules unchanged), CI-01 (snapshot), OBS-03 (no version on Public routes), PCR-01; constitution II, III, V, VI.

**Scale/Scope**:
- 4 framework routes (+2 fixture routes); 2 framework option endpoints;
- ~14 new public types/members (Core 1, Platform.Web 10, Gm 3, Identity/Economy 2 route declarations, TestKit 1);
- 3 transformers, 1 validator, 1 startup check;
- 8 breaking changes (BC-1..BC-8), all additive or doc-level, each with a mechanical fix.

## Overview

```text
 Module mapping (adapters)                         Platform.Web                                        Consumers
 MapGmCommand(route, toDomain) ──────────────────► Commands/GmCommandEndpointFactory                  console (GM-2..4)
   + Accepts<TBody>, GmRequestIdMetadata             read bytes → deserialize → GmBodyValidator ─┐       ▲ catalog bytes
 MapDeclared(...).ProducesGmPage<T>()                                       (400 + errors)        │       │ ETag/304
   .WithGmConfirm(Typed).WithSummary("…")          Endpoints/GmPresentation (metadata only)       │   gm.console_catalog
   .WithGmQueryParameter(...) .WithGmRequestId()   Endpoints/GmBodyContract ◄── one attribute walk┤   (Gm, gm.access)
 bodies: [property: Range|StringLength|Required|    Endpoints/GmHttp.ReadJsonAsync → validator     │       │
          GmOptions("route")]                       OpenApi/GmOpenApi.Add (every env)              │   GmCatalogDocument
                                                      ├ operation transformer (R1, R6)             │   (lazy bytes, ETag)
 gm.auth_methods (Public) ─► {local, sso}             ├ schema transformer ◄───────────────────────┘       │
 gm.me ─► + frameworkVersion, gameVersion             └ document transformer (no servers)                  ▼
 economy.resource_options / identity.ban_scopes    Validation/GmOptionsCheck (pre-serving report)   TestKit GmOpenApiSnapshot
   ─► GmOptionList                                 Errors/ErrorResponseWriter (+ errors)            (Write/Diff, canonical bytes)
                                                   Core: GameServerException.Errors (init)           docs/api/gm-openapi.json
```

Style unchanged: explicit Minimal-API mapping from declarations (constitution II), metadata read by transformers, no
runtime behaviour outside validation, the catalog route and the startup check.

## Module breakdown

| Module (assembly / namespace) | Responsibility | Depends on | Does NOT own |
| --- | --- | --- | --- |
| Core `GameServer.Errors.GameServerException` | carry field errors (`Errors`) | BCL | how they are written |
| Platform.Web `Endpoints/GmPresentation` | presentation metadata, attribute, option records, extension methods | ASP.NET routing | rendering; validation |
| Platform.Web `Endpoints/GmBodyContract` (internal) | per-type attribute walk over the JSON contract (property + positional parameter, nested ≤ 3) | `GameServerJson.Options` | which endpoints use it |
| Platform.Web `Endpoints/GmBodyValidator` (internal) | run the walk on a body (+ JSON presence for value-type `[Required]`), build `errors` | `GmBodyContract` | status codes (exception) |
| Platform.Web `Endpoints/GmHttp` | `ReadJsonAsync` validates | validator | — |
| Platform.Web `Commands/GmCommandEndpointFactory` | `Accepts<TBody>`, request-id metadata, validation before `toDomain` | validator | rules of a body |
| Platform.Web `Errors/ErrorResponseWriter` | write `errors` in Problem Details | — | CORS (GM-2) |
| Platform.Web `OpenApi/GmOpenApi` + transformers | describe operations and body schemas, drop `servers` | catalog, contract, metadata | permissions (unchanged) |
| Platform.Web `OpenApi/GmCatalogDocument` | canonical bytes + ETag, once per process | OpenAPI document provider (V11) | HTTP caching policy of GM-2 |
| Platform.Web `Validation/GmOptionsCheck` | startup lines for unknown option routes | catalog, contract | — |
| Platform.Web `Hosting/GameServerBuilder` | register OpenAPI + catalog in every environment; HTTP JSON enum converter (V12) | — | — |
| Gm.Adapters `GmModule`, `GmRoutes` | `gm.auth_methods`, `gm.console_catalog`, `/me` versions, summaries, confirm/paged annotations of GM routes | Platform.Web | the document |
| Identity.Adapters `IdentityModule`, `IdentityRoutes`, `BanScopes` | `identity.ban_scopes`, ban body option, typed confirm | registry | — |
| Economy.Adapters `EconomyModule`, `EconomyRoutes` | `economy.resource_options`, adjust body annotations, ledger page | `ResourceCatalog` | — |
| TestKit `GmOpenApiSnapshot` | write/diff the committed document | `GmCatalogDocument` | test framework |
| SampleGame.Adapters | annotated `SetProgressionBody` (reference example) | — | — |
| Consumer fixture | GM module + GM command + option route + snapshot test | TestKit | — |

No circular dependency: Core ← Platform.Web ← module adapters ← TestKit; the fixture references the kit and modules as a
game does.

## Constitution Check

| Principle | How this slice meets it | Result |
| --- | --- | --- |
| I Server authoritative, one transaction per command | No command path change; validation runs before the runner and writes nothing; GM commands still commit through `ICommandRunner` with audit | PASS |
| II Explicit registration | Four new routes declared in module/platform route lists and mapped from declarations; option routes checked at startup (unknown name → startup fails); no discovery | PASS |
| III Security deny-by-default | Catalog requires `gm.access` (Q5); `gm.auth_methods` Public by declaration and listed in the PCR allowlist (SEC-09), answers two booleans; option routes need the permission of the operation they serve; backend still validates every value (`[GmOptions]` is presentation) | PASS |
| IV Async, bounded load | Catalog generated once, cached; validation on bodies already bounded by `GmBodyBytes`; no new limiter needed (the catalog uses the GM limiter/timeout policies of `AsGmEndpoint`) | PASS |
| V Stable contract | GM JSON changes are additive (`errors`, `/me` members, new routes); the document's changes are recorded (BC-1) and diffed in CI by snapshot; PCR updated | PASS |
| VI Multi-layer tests | Unit (contract walk, validator, transformers), host (document shape, catalog/304, validation answers, options), Couchbase flavor matrix, fixture consumer path, architecture (kit), opt-in perf | PASS |
| VII Observability | Validation failures counted as `BAD_REQUEST` (existing metric/log); one Information line per catalog generation; correlation id in every answer | PASS |

Requirements check: GM-06 paging rules unchanged; SEC-09 satisfied through the declaration + PCR (014 rule); SEC-13's
per-IP GM limit stays out (Q9); OBS-03 holds (versions only on Gm routes). Re-check after design: unchanged.

## Design decisions

Spec defaults D1–D15 and the decisions this plan adds (A1–A10). **Rows marked "architect" are architect decisions; the
owner may override them.**

| # | Decision | Status | Research |
| --- | --- | --- | --- |
| D1 | Tag = area (route-name segment before the first dot) | confirmed | R1 |
| D2 | `x-gm-confirm` only on mutations; Q7 list typed | confirmed | R14 |
| D3 | `X-Request-Id` documented on every mutation that requires it | confirmed | — |
| D4 | No `servers` in the document | confirmed | R4 |
| D5 | Request-body `required` = real rules; `null` dropped for `[Required]` | confirmed + detail | R6 |
| D6 | Validation over the JSON contract (property + positional parameter) | confirmed | R2 |
| D7 | Absent value-type `[Required]` = missing (JSON presence) | confirmed | R3 |
| D8 | `GmHttp.ReadJsonAsync` validates too | confirmed | R2 |
| D9 | Versions in `/me` and the local login answer | confirmed (owner confirmation 2 open) | R11 |
| D10 | Audit, staff audit, ledger pages: free-form items | confirmed | — |
| D11 | Staff audit paged | confirmed | — |
| D12 | Lazy generation on first catalog request | confirmed | R8 |
| D13 | Unknown `[GmOptions]` route fails startup | confirmed | R12 |
| D14 | Fixture dogfoods GM | confirmed | R10 |
| D15 | No version bump/tag in the slice | confirmed | — |
| A1 | **Architect.** Canonical bytes = JSON of the document re-written by System.Text.Json indented + LF + final newline (today's committed style), shared by the catalog route, the dev route's comparison and the kit | new | R4 |
| A2 | **Architect.** `GmCatalogDocument` is a public Platform.Web singleton so the kit reads it without an internal (kit allow-list unchanged) | new | — |
| A3 | **Architect.** `ProducesGmPage<T>` extends `RouteHandlerBuilder` with one type argument (design's two-argument form cannot be called with inference) | changed from design | — |
| A4 | **Architect.** `GmOpenApiSnapshot` takes `IServiceProvider` (plus `TestHost` overloads), so games use their real host through `LocalFlavorFactory` | changed from design | — |
| A5 | **Architect.** Reason errors and annotation errors are merged into one `errors` answer (one round trip shows every field) | new | R2 |
| A6 | **Architect.** Keywords are written by our transformer from the same walk as the server, overwriting the generator's (correct whatever V1 finds); keywords on the use-site property schema, never on a shared component | new | R6 |
| A7 | **Architect.** Enum schemas follow `GameServerJson` (camelCase strings) through the HTTP JSON options or an enum transformer (V12) | new | R5 |
| A8 | **Architect.** Catalog response `Cache-Control: private, no-cache`, strong ETag (SHA-256, 32 hex), `If-None-Match` lists/weak tags/`*` accepted; a failed generation is not cached | new | R8 |
| A9 | **Architect.** `BanScopes` is one singleton used by the ban command's `toDomain` and by `identity.ban_scopes` | new | R7 |
| A10 | **Architect.** Framework bodies annotated only where the rule is exact (R13 table); others stay imperative and are listed in the PCR | new | R13 |

## Design per user story

### US1 — Every GM command is fully described (FR-001..FR-009)

- `GmCommandEndpointFactory.MapGmCommandCore`: after `MapPost`, add `.Accepts<TBody>(GmHttp.JsonContentType)` and
  `.WithMetadata(GmRequestIdMetadata.Instance)` (both overloads families share this core, so string-pattern and
  declaration overloads behave alike).
- `GmOpenApi.Add` registers, in this order: the existing permission/CSRF transformer (unchanged), a new
  `GmOperationTransformer` (internal class in `OpenApi/`), a `GmSchemaTransformer`, a `GmDocumentTransformer`.
  `GmOperationTransformer` per operation:
  1. declaration = `RouteDeclarationMetadata` or catalog lookup (R1) → `operationId`; tags when no `ITagsMetadata`;
  2. template parameters: add each `{name}` missing from `operation.Parameters` as a required path parameter
     (`playerId` with the `GmHttp` pattern);
  3. `GmRequestIdMetadata` → header parameter;
  4. `GmQueryParameterMetadata` (and the two of `GmPagedMetadata`) → query parameters;
  5. `x-gm-kind`, `x-gm-operation`, `x-gm-target`, `x-gm-confirm` (non-GET), `x-gm-paged`.
- `GmDocumentTransformer`: `document.Servers = null` (or empty, whichever the writer omits; V11 records it); info
  description gains one sentence naming the `x-gm-*` keys.
- Annotations of framework routes (FR-046, FR-047, D3) are part of US4's mapping edits; US1 only needs
  `WithGmConfirm(Typed)` on the three Q7 commands to satisfy its scenario 6 for commands (ban, cancel deletion, adjust).

### US2 — Rules declared once, enforced by the server (FR-010..FR-014, FR-020..FR-027)

- `GameServerException.Errors` (Core, init) → `ErrorResponseWriter` writes `errors` (JSON object of string arrays) after
  `details`, Problem Details only. `ExceptionMiddleware` passes the exception (signature change of the internal call:
  `WriteAsync(context, code, retryAfterMs, details, errors)`; the public `WriteAsync` gains an optional trailing
  parameter).
- `GmBodyContract.For(Type)` (cached) per R2; `GmBodyValidator.Validate(object body, ReadOnlySpan<byte> json,
  bool commandBody)` returns the sorted `errors` map (empty = valid), including the `reason` rule when `commandBody`
  (message `reason is required (1..500 characters, not only white space).`).
- `MapGmCommandCore`: replaces the reason block with the validator call; non-empty → `throw new
  GameServerException(CoreErrorCodes.BadRequest, "Body validation failed: <keys>.") { Errors = errors }` (the detail is
  logged at Information, never sent).
- `GmHttp.ReadJsonAsync<T>`: the same validator call with `commandBody: false` (annotations only; framework actions keep
  their own `reason` handling, N1).
- `GmSchemaTransformer` per R6, reading the same contract: keywords, `required`, `null` removal for `[Required]`,
  `reason` rule, `x-gm-options`.
- Bodies (R13): `AdjustResourceBody`, `BanBody`, `MaintenanceBody`, `SetProgressionBody`; their imperative checks stay
  in place (they now never trigger for the annotated parts, except `toDomain` rules that remain); the sample's
  `toDomain` keeps only the "at least one field" rule.
- V1/V12/V13 decide whether the generator already emits some keywords and which JSON options it uses; A6 makes the result
  correct either way.

### US3 — Catalog, sign-in methods, versions (FR-030..FR-036)

- `GameServerBuilder`: `OpenApi.GmOpenApi.Add(services)` and `services.AddSingleton<GmCatalogDocument>()` outside the
  `AllowsDevelopmentTools` block; `GameServerApplicationExtensions` keeps mapping the dev route only with development
  tools.
- `GmCatalogDocument` per R4/R8; one Information log line `GM catalog generated: {Operations} operations, {Bytes} bytes,
  {ElapsedMs} ms.` (category `GameServer.Gm.Catalog`).
- `GmRoutes.ConsoleCatalog` + `GmModule.MapGmApi`: `MapDeclared(GmRoutes.ConsoleCatalog, CatalogAsync)
  .ExcludeFromDescription().AsGmEndpoint(GmPermissions.Access)`; the handler compares `If-None-Match`, writes 304 or the
  bytes with `ETag`/`Cache-Control`.
- `GmRoutes.AuthMethods` + `MapDeclared(..., (HttpContext http) => GmHttp.Json(new GmAuthMethods(local, sso)))
  .Produces<GmAuthMethods>().AsPublicEndpoint(protobuf: false).WithRequestTimeout(Gm).RequireRateLimiting(Gm)` with
  `local = options.AllowsDevelopmentTools` (the condition of `gm.auth_login`), `sso = GmOptions.Oidc.IsConfigured`.
- `GmMeResponse` init members (R11) set in `Me` and `LoginAsync` from `GameServerRuntimeInfo`.
- V10 confirms no configuration is needed; PCR gm lists the route in the SEC-09 allowlist.

### US4 — Lists, options, confirmations, summaries (FR-040..FR-048)

- `GmPresentation.cs` (contracts/public-api §2).
- Lists: `gm.player_search` → `.ProducesGmPage<GmPlayerSearchItem>().WithGmQueryParameter("displayName", required: true,
  maxLength: 64, minLength: 1)`; `gm.player_receipts` → `<GmReceiptSummary>`; `gm.staff_list` → `<StaffView>` (replaces
  `Produces<GmPage<StaffView>>`); `gm.player_audit`, `gm.staff_audit`, `economy.ledger` → `<JsonObject>` (D10).
- Options: `EconomyRoutes.ResourceOptions` + map in `EconomyModule.MapGmApi` (`ResourceCatalog` from DI → sorted
  `GmOptionList`); `IdentityRoutes.BanScopes` + `BanScopes` singleton (registered in `IdentityModule.ConfigureServices`,
  built lazily from `ModuleRegistry`), used by the ban `toDomain` and the route. `[GmOptions]` on the two bodies.
- `GmOptionsCheck.Validate(endpoints, catalog, conditionHolds)` called from `EndpointValidation.Run` (pre-serving and the
  error path), lines per data-model §6.
- `WithGmConfirm(GmConfirm.Typed)` on the five Q7 routes; `WithGmRequestId()` on staff writes (5), gate writes (2), key
  rotation; `WithSummary` on every framework GM route (R14).

### US5 — Snapshot (FR-050..FR-055)

- `src/GameServer.TestKit/GmOpenApiSnapshot.cs` per contracts/public-api §8 and data-model §5.
- `tests/GameServer.Gm.Tests/OpenApiTests.cs`: keep the endpoint loop (permission/CSRF), replace the committed-copy block
  with `UPDATE_OPENAPI=1 → WriteAsync`; `DiffAsync(...).ShouldBeEmpty()`; new `GmDocumentShapeTests` for FR-001..FR-006,
  FR-010..FR-014, FR-040..FR-047 on the framework test host's document.
- Fixture: research R10 (`Fixture.Game` GM command + option route + permission; `Fixture.Host` adds `GmModule`;
  `tests/ConsumerFixture/docs/api/gm-openapi.json`; `FixtureGmOpenApiTests`).
- `docs/api/gm-openapi.json` regenerated last (after every annotation), reviewed as a diff.

### US6 — Docs (FR-060..FR-065)

See "Project Structure → Docs". Repository docs are Vietnamese except the consumer guide and release notes (English);
keys, routes and code stay literal. Page stamps: "Đúng với framework v0.7.0 (…) + nhánh `017-gm-api-contract` (chưa phát
hành)" style of 016.

## Where the plan narrows or changes the spec

| # | Spec item | Plan | Why |
| --- | --- | --- | --- |
| N1 | FR-024 "same validation" in `GmHttp.ReadJsonAsync` | annotations only; no `reason` rule for actions | framework actions handle `reason` in `StaffAdminService`/gate code with their own rules |
| N2 | FR-036 "values of ops/version" | same `GameServerRuntimeInfo` fields (`FrameworkVersion`, `GameVersion`) | one source |
| N3 | FR-042 typed pages | `JsonObject` item type for the three stored-document lists | D10 |
| N4 | FR-050 "convenience overloads for `TestHost`" | `WriteAsync`/`DiffAsync` only (no `GetDocumentAsync(TestHost)`) | `host.Services` is one property away |
| N5 | FR-010 "8 attribute types" | `[Required]`, `[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`, `[Length]`, `[RegularExpression]`, `[Description]` (the last is `System.ComponentModel`, not a `ValidationAttribute`: document only) | `Description` validates nothing |

## Data flow

1. **Command with an annotated body**: request → (existing middleware: request log, exception, correlation, gate, profile
   enforcement with CSRF + permission) → `MapGmCommandCore`: player id, request id, bounded body bytes, deserialize →
   `GmBodyValidator` (walk + presence) → 400 with `errors`, or → `toDomain` → target validators → `ICommandRunner` →
   `GmCommandResponse`.
2. **Catalog**: GET with cookie → profile enforcement (`gm.access`) → handler → `GmCatalogDocument.GetAsync` (first call:
   document provider → transformers → serialize → normalize → hash; later calls: cached) → 304 or 200 bytes.
3. **Document generation**: provider enumerates `/gm/**` endpoint descriptions → for each operation: existing transformer,
   `GmOperationTransformer` (catalog lookup, metadata) → schemas: `GmSchemaTransformer` (contract walk) → document
   transformer (servers, info).
4. **Startup**: `UseGameServer` → endpoint validation (pre-serving) runs `GmOptionsCheck` with the other checks → one
   report on faults.
5. **Snapshot in a game's CI**: test starts the game host (`LocalFlavorFactory<Program>`) → `GmOpenApiSnapshot.DiffAsync`
   → `GmCatalogDocument` of that host → compare with the committed file.

## Performance

- Document generation: measured once in T005 (V11) on the framework test host (≈ 35 operations, ≈ 60 KB); SC-004 budget
  1 s first call. Warm path: an `If-None-Match` comparison and a `ReadOnlyMemory<byte>` write.
- Validation: contract built once per body type (reflection + JSON contract) and cached; per request a walk over a few
  properties; the `JsonDocument` parse runs only for bodies with a value-type `[Required]`.
- Startup: `GmOptionsCheck` walks the contracts of GM body types (≈ 10) once; ≤ 1 ms. Registering OpenAPI services costs
  nothing until first use (no host-start change, D12).

## Seams for later work and extension points

- `x-gm-kind: action` + `x-gm-target: global` are ready for `MapGmAction` (giftcode slice): it will add
  `GmRequestIdMetadata` and `Accepts<TBody>` like `MapGmCommandCore` and reuse `GmBodyValidator`.
- `GmQueryParameterMetadata` serves future GM queries of games (FG-014 `MapPlayerQuery` has a GM sibling later).
- `GmOptionList` routes of game modules: any GET GM route answering `GmOptionList`; `[GmOptions]` checked at startup.
- GM-2 adds CORS exposure of `ETag` for the catalog; nothing to change here.
- Typed views of audit/ledger items (D10) can replace `JsonObject` later (document change, additive item properties).

## Project Structure

### Documentation (this feature)

```text
specs/017-gm-api-contract/
├── spec.md                     # US1–US6, FR-001..FR-091, BC-1..BC-8, SC-001..SC-008, D1–D15
├── plan.md                     # this file
├── research.md                 # R1–R14, VERIFY V1, V10, V11–V13, code facts
├── data-model.md               # document members, metadata, HTTP shapes, routes, snapshot, startup line
├── contracts/public-api.md     # Core, Platform.Web, Gm, Identity, Economy, TestKit, sample signatures
├── contracts/gm-document.md    # operation example, kinds table, bootstrap routes, validation answer, compatibility
├── release-notes-draft.md      # implementation phase (FR-065)
└── tasks.md                    # task list
```

### Source code (files to change)

```text
src/GameServer.Core/Errors/GameServerException.cs                       # + Errors (init)
src/GameServer.Platform.Web/
├── Endpoints/GmPresentation.cs                                          # NEW presentation API
├── Endpoints/GmBodyContract.cs · GmBodyValidator.cs                     # NEW (internal)
├── Endpoints/GmHttp.cs                                                  # ReadJsonAsync validates
├── Commands/GmCommandEndpointFactory.cs                                 # Accepts, request-id metadata, validator
├── Errors/ErrorResponseWriter.cs · Middleware/ExceptionMiddleware.cs     # errors member
├── OpenApi/GmOpenApi.cs                                                 # transformers registered
├── OpenApi/GmOperationTransformer.cs · GmSchemaTransformer.cs · GmDocumentTransformer.cs   # NEW (internal)
├── OpenApi/GmCatalogDocument.cs                                         # NEW (public)
├── Validation/GmOptionsCheck.cs                                         # NEW (internal) + call in Hosting/EndpointValidation.cs
└── Hosting/GameServerBuilder.cs                                         # register in every env; JSON enum option (V12)
src/GameServer.Modules.Gm.Adapters/
├── GmRoutes.cs                                                          # AuthMethods, ConsoleCatalog (+ All)
├── GmModule.cs                                                          # routes, GmAuthMethods, GmMeResponse members, summaries, typed/paged/request-id annotations
├── GmPlayerEndpoints.cs · Staff/StaffEndpoints.cs · Ops/GateEndpoints.cs # summaries, ProducesGmPage, query param, request id, typed confirm, MaintenanceBody annotation
src/GameServer.Modules.Identity.Adapters/IdentityModule.cs · IdentityRoutes.cs · BanScopes.cs (NEW)   # option route, BanBody, summaries, typed
src/GameServer.Modules.Economy/EconomyRoutes.cs                          # ResourceOptions
src/GameServer.Modules.Economy.Adapters/EconomyModule.cs                 # option route, AdjustResourceBody, ledger page, summaries, typed
src/GameServer.TestKit/GmOpenApiSnapshot.cs                              # NEW
samples/SampleGame/SampleGame.Adapters/SampleGameModule.cs               # SetProgressionBody annotations, summary
modules/Sample.Counter/Sample.Counter.Adapters/CounterModule.cs          # summary of the GM read (framework sample module)
tests/ConsumerFixture/src/Fixture.Game/FixtureGmNote.cs (NEW) · FixtureModuleDefinition.cs · FixtureModule.cs
tests/ConsumerFixture/src/Fixture.Host/Program.cs                        # AddModule<GmModule>()
tests/ConsumerFixture/docs/api/gm-openapi.json                           # NEW committed fixture document
docs/api/gm-openapi.json                                                 # regenerated
```

Tests (new unless marked):

```text
tests/GameServer.Platform.Web.Tests/
├── GmBodyContractTests.cs                 # attribute sources (property/parameter), JSON names, nesting, cache
├── GmBodyValidatorTests.cs                # each attribute at boundary/outside, presence, reason merge, paths, messages
├── GmProblemDetailsErrorsTests.cs         # errors member written for JSON, ignored for Protobuf
├── GmOptionsCheckTests.cs                 # unknown name, POST route, Player surface, condition off → lines; valid → none
└── GmOpenApiTransformerTests.cs           # bare app: operation id via metadata and via catalog lookup, tags, params, x-gm-*
tests/GameServer.Gm.Tests/
├── OpenApiTests.cs (existing)             # snapshot via kit, endpoint loop kept
├── GmDocumentShapeTests.cs                # FR-001..FR-014, FR-040..FR-047 on the framework document
├── GmCommandValidationTests.cs            # sample progression 400/200, framework commands unchanged, reason errors
├── GmCatalogRouteTests.cs                 # 401/403/200/304, same bytes as dev route, no version, lazy generation once
├── GmAuthMethodsTests.cs                  # local/sso matrix, anonymous, no secrets; V10 allowlist variants
├── GmOptionRoutesTests.cs                 # resources, ban scopes = ban validation set, permissions
├── GmVersionEndpointTests.cs (existing)   # + /me versions, + auth/methods carries no version
└── GmAdjustTests.cs · GateAndBanTests.cs (existing)   # unchanged expectations re-run; + errors member where annotated
tests/SampleGame.Tests/CatalogCompositionTests.cs (existing)   # + 4 route names; catalog route mapped with GM in every flavor
tests/GameServer.Platform.Couchbase.IntegrationTests/SampleHostFlavorTests.cs (existing)   # + catalog route per flavor
tests/GameServer.Platform.Web.Tests/GmOpenApiSnapshotTests.cs           # kit snapshot over TestHost (no separate kit test project)
tests/ConsumerFixture/tests/Fixture.Tests/FixtureGmOpenApiTests.cs       # snapshot + GM round trip
tests/GameServer.Architecture.Tests/TestKitArchitectureTests.cs (existing)   # re-run; allow-list unchanged
```

Docs:

```text
docs/pcr/gm.md                # routes (29), allowlist, catalog, x-gm-*, validation answer, /me, conventions; H12 lines 4-5
docs/pcr/identity.md · economy.md · sample-game.md   # option routes, annotated bodies, typed confirm
docs/pcr/platform.md          # GM document every env, errors member, presentation API, startup line (route-catalog section)
docs/guides/module-authoring.md · templates/module/README.md   # GM route for the generic console
docs/guides/consumer-guide.md # new §14 GM API contract; §11 bump checklist
docs/html/02-them-command.html (§8) · 01-gioi-thieu.html · 03-deploy.html   # GM command section; H12 lines; stamps
AgentMemories/features/p05-speckit/00_summary.md   # H12 lines 9-10, 389
specs/017-gm-api-contract/release-notes-draft.md
```

**Structure Decision**: no new project. Platform.Web gains six files (`Endpoints` 3, `OpenApi` 4 incl. the catalog,
`Validation` 1); the test kit gains one; Identity one.

## Test strategy (per FR and SC)

| FR / SC | Tests |
| --- | --- |
| FR-001, FR-002, SC-001 | `GmOpenApiTransformerTests` (bare app: declaration metadata, template-only binding, explicit `WithTags`, name without dot); `GmDocumentShapeTests`: every operation has an `operationId` equal to the route name of its endpoint, unique; tags |
| FR-003, FR-004, FR-005, FR-006, SC-001 | `GmDocumentShapeTests`: five commands (body, path, header, 200 schema, `x-gm-*`); every GET `query` without confirm; every other method `action`/`command` with confirm; header on the 8 framework actions; Q7 typed set |
| FR-007, FR-009 | `OpenApiTests` endpoint loop (unchanged assertions); `gm.ui` and the catalog absent |
| FR-008, SC-003 | `GmCatalogRouteTests`: no `servers`; bytes contain neither `GameServerVersion.FrameworkInformational` nor the game version; Beta/Local bytes equal except conditional routes (diff limited to `/gm/v1/auth/login` and SSO paths) |
| FR-010..FR-014, SC-002 | `GmBodyContractTests` + `GmDocumentShapeTests` (sample + probe test module with every attribute on property and parameter targets, nested object and list, enum) |
| FR-020..FR-023, SC-002 | `GmBodyValidatorTests` (unit) and `GmCommandValidationTests` (host): boundary accept/refuse per attribute and target; presence; reason merge; `errors` paths; handler/runner counters 0 on refusal; framework commands' existing tests unchanged |
| FR-024 | `GmCommandValidationTests`: maintenance `message` 501 chars → 400 with `errors.message`; 500 → OK |
| FR-025 | `GmCommandValidationTests`: `http_errors_total{code="BAD_REQUEST"}` + 1, Information log line, no Error line |
| FR-026 | `GmProblemDetailsErrorsTests`: JSON writes `errors`; Protobuf envelope bytes unchanged for an exception with `Errors` |
| FR-027 | existing `GmAdjustTests`, `GateAndBanTests`, `DeletionGmTests` green with unchanged status/code assertions; new cases: blank resource → 400 with `errors.resource` |
| FR-030..FR-033, SC-003 | `GmCatalogRouteTests` (in-memory): 401, 403 (staff without `gm.access` / other env), 200 headers, 304 variants, same document as dev route (normalized), generation runs once under 20 concurrent first requests (log line count = 1); `SampleHostFlavorTests` (Couchbase project): catalog 401 anonymous on Local/Dev/Beta/Uat/Live; `CatalogCompositionTests`: catalog mapped whenever GM is |
| FR-034, FR-035, SC-007 | `GmAuthMethodsTests`: Local without OIDC `{true,false}`, Local with OIDC `{true,true}`, Beta `{false,true}`; anonymous 200; explicit `PublicEndpointAllowlist = ["/v1/time"]` host starts and serves it (V10) |
| FR-036 | `GmVersionEndpointTests`: `/me` and local login carry the two members equal to `ops/version`; `auth/methods` added to the "no version" list |
| FR-040..FR-042 | `GmDocumentShapeTests` (six lists, parameters, schemas); existing paging tests (`GmLookupTests`, ACC-24) unchanged |
| FR-043, FR-044 | `GmOptionRoutesTests`: resources sorted, labels; 403 without permission; ban scopes equal to the set the ban command accepts (every listed value accepted, an unlisted one refused) |
| FR-045 | `GmOptionsCheckTests` (unit) + one host test: a test module with a bad `[GmOptions]` → `GameServerStartupException` line text |
| FR-046, FR-047 | `GmDocumentShapeTests`: typed set; every framework-owned operation has a summary |
| FR-048 | `ArchitectureTests` (existing): Core/business projects have no ASP.NET dependency (re-run) |
| FR-050..FR-052, SC-005 | `GmOpenApiSnapshotTests` (Platform.Web.Tests over `TestHost`): equal → empty; one char changed at line 1, middle, last → that line; CRLF copy → empty; missing → message; write → bytes equal to the catalog; `TestKitArchitectureTests` re-run |
| FR-053, FR-055 | `OpenApiTests` uses the kit; committed `docs/api/gm-openapi.json` regenerated and reviewed |
| FR-054, SC-006 | `FixtureGmOpenApiTests`: snapshot empty; mutation of a bound in a temp copy → expected line; GM round trip 400/200 |
| FR-060..FR-065, SC-008 | reviewer walkthrough (T059); grep for the H12 phrases in the repository = 0 hits outside history |
| FR-090, FR-091 | VERIFY notes; final build 0 warnings; full suites (in-memory, File, Couchbase) |
| SC-004 | opt-in `GmCatalogPerformanceTests` (`GAMESERVER_PERF=1`): first call and 1 000 warm calls p95 |

## Breaking changes and migration (feeds the release notes)

| BC | What changes | Steps for a game (zh-pvp) |
| --- | --- | --- |
| BC-1 | GM document shape (ids, tags, parameters, `required`, `x-gm-*`, no `servers`) | Commit the game's document with the kit (first time); review diffs on later bumps |
| BC-2 | `errors` member on validation 400s | None |
| BC-3 | Annotations on GM bodies are now enforced | Check GM bodies for annotations (zh-pvp: none) |
| BC-4 | `/me` + local login carry versions for every `gm.access` staff | None (owner policy) |
| BC-5 | Four new route names / templates | Update pinned route-name lists in game tests if any |
| BC-6 | OpenAPI services in every environment | None |
| BC-7 | `GameServerException.Errors` | None |
| BC-8 | Unknown `[GmOptions]` route fails startup | Only for new code |

zh-pvp order after the bump: bump → add `GmOpenApiSnapshot` test over `LocalFlavorFactory<Program>`, run in write mode,
commit `docs/api/gm-openapi.json` → (optional) first game GM command with annotated body → record friction if any.

## Implementation phases (input for tasks.md)

1. **Setup and VERIFY (blocking).** Baseline; V1 (+V12, V13) gate in a scratch project; V10 as a permanent test against
   current `main` behaviour (with a temporary Public test route); V11 gate; inventory of pins (R9, R10).
2. **Foundations (behaviour-neutral).** `GameServerException.Errors` + writer; presentation API; `GmBodyContract`;
   `GmCatalogDocument` skeleton (not routed); OpenAPI registered in every environment (no route yet).
3. **US1.** Operation transformer, document transformer, `Accepts`/request-id metadata in `MapGmCommandCore`, typed confirm
   on the three Q7 commands; shape tests on commands.
4. **US2.** Validator, factory and `GmHttp` wiring, schema transformer (keywords, required, reason, enum per V12), framework
   and sample body annotations; validation tests.
5. **US3.** Catalog route, auth methods, `/me` versions; route tests; flavor matrix (Couchbase project) and composition
   test.
6. **US4.** Lists, query parameters, option routes + `BanScopes`, `[GmOptions]` + startup check, request-id and typed
   annotations on actions, summaries; shape tests.
7. **US5.** Kit snapshot + tests, framework `OpenApiTests`, fixture GM dogfood, regenerate committed documents.
8. **US6.** Docs, HTML, H12 lines, release notes draft.
9. **Close.** Reviewer walkthrough, final verification (Debug/Release, in-memory, File), Couchbase full suite, tech-lead
   review and fixes, resume update.

Dependencies: 1 → 2 → 3 → 4 (schema transformer needs 3's registration) → 5 → 6 → 7 → 8 → 9. Inside phases, test-only
and different-file tasks run in parallel ([P] in tasks.md). US3 depends only on phase 2 and could run in parallel with
US1/US2, but `GmModule.cs` and `GmOpenApi.cs` are shared files, so the order above avoids edit conflicts.

**Estimated tasks: 64** (setup 6, foundations 6, US1 5, US2 9, US3 8, US4 9, US5 7, US6 8, close 6; tasks.md is
authoritative).

## Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| The generator emits parameter-target attributes the server would not enforce, or vice versa (V1, V13) | Document and server disagree | One walk feeds both (A6); SC-002 boundary matrix on both targets |
| `IOpenApiDocumentProvider` not usable outside a request (V11) | No catalog outside Development | Fallback: invoke the `MapOpenApi` request delegate on a synthetic context (R4) |
| HTTP JSON options change runtime JSON of some endpoint (V12) | Wire change outside GM | V12 inventory first; fallback enum schema transformer |
| A framework body annotation is stricter/looser than its imperative rule | Silent behaviour change | R13 table limited to exact rules; boundary tests on each annotated property |
| Registering OpenAPI in every environment slows host start or the test suite | CI time | Lazy generation (D12); T001 baseline timing vs final run |
| Snapshot churn: any framework annotation edit changes every game's committed document | Noise in game bumps | Documented in the release note as expected on bumps; diff shows one line at a time |
| Fixture gains GM module and breaks fixture tests pinning routes/outputs | Red fixture suite | T006 inventory (R10) before T047; WireGen `CommittedOutputTests` re-run |
| `ban-scopes` literal path shadows a player id `ban-scopes` | That player unreachable by `gm.player` | Player ids are server-generated (never that word); same precedent as `search`; edge case documented |
| `[Required]` presence check misreads case-insensitive JSON names | False "missing" | Use the options' `PropertyNameCaseInsensitive`; unit cases with mixed case |
| Versions on `/me` against the 013 default | Policy disagreement | Owner confirmation 2; trivial to remove (two init members) |

## Complexity Tracking

| Item | Why needed | Simpler alternative rejected because |
| --- | --- | --- |
| `GmBodyContract` walk instead of `Validator.TryValidateObject` | Positional-record parameter attributes; JSON names; nesting; same source for schema and server | `Validator` misses parameter attributes and nesting (document/server mismatch) |
| JSON presence for value-type `[Required]` | Otherwise a documented rule is unenforced | `[JsonRequired]` gives a generic 400 without `errors` |
| `GmCatalogDocument` + canonical bytes | One byte-identical artifact for route, ETag and snapshot | Re-serializing per request (cost, ETag instability) |
| `GmOptionsCheck` at startup | A typo would point the console at a missing route silently | Runtime 404 in the console only |
| Fixture GM dogfood | Proves the consumer path of the kit and the zero-frontend promise on a game-shaped repo | Framework-only test cannot catch consumer friction |

## Owner questions (also in the release notes, "decisions awaiting owner override")

1. **D9 / BC-4**: versions in `/me` for every `gm.access` staff (wider than 013's `ops.read`). OK?
2. **D5–D7**: honest `required` in request bodies, validation over the JSON contract, absent required number = missing.
   OK?
3. **D10**: audit, staff audit and ledger items stay free-form in the document. OK?
4. **D14**: the consumer fixture gains the GM module and a GM command. OK?
5. **D15**: no version bump in the slice; next tag proposed `v0.8.0`. OK?
6. **A3 / A4**: `ProducesGmPage<T>` and `GmOpenApiSnapshot` shapes differ from design §5.2 (one type argument;
   `IServiceProvider`). OK?
