# Tasks: GM API contract — a complete, machine-readable GM catalog served in every environment, declared input rules enforced by the server, option sources, typed lists, console bootstrap endpoints and a snapshot test for games (slice 017 gm-api-contract, GM-1)

**Input**: [plan.md](plan.md) (design, spec defaults D1–D15, architect decisions A1–A10, narrowings N1–N5, breaking
changes BC-1..BC-8), [spec.md](spec.md) (US1–US6, FR-001..FR-091, SC-001..SC-008), [research.md](research.md) (R1–R14,
VERIFY V1, V10, V11–V13, test inventory R9, fixture traps R10), [data-model.md](data-model.md),
[contracts/public-api.md](contracts/public-api.md), [contracts/gm-document.md](contracts/gm-document.md). Design input:
`AgentMemories/features/gm-tool/01_design.md` §19. **Where this file and plan.md differ, this file wins** (see Planning
notes at the end).

**Architect decisions applied** (owner questions 1–6 of the plan stay open; the release notes track their status, T002):
- **One attribute walk** (`GmBodyContract`, over the System.Text.Json contract: property **and** positional-record
  parameter attributes, nested ≤ 3) feeds both the schema transformer and the server validator, so the document shows
  exactly what the server enforces (D6, A6). An absent value-type `[Required]` property is "missing" (JSON presence, D7).
- **Validation answer** = existing `400 BAD_REQUEST` Problem Details + `errors` (JSON path → messages), carried by the
  new init property `GameServerException.Errors` (Core) and written for Problem Details only; the `reason` rule joins
  the same `errors` set (A5). `GmHttp.ReadJsonAsync` validates annotations too (D8, N1).
- **Document**: operation id = route name (metadata, else catalog lookup), tag = area, template path parameters,
  `X-Request-Id` header from metadata, `x-gm-kind/-operation/-target/-confirm/-paged`, request-body `required` = real
  rules (`null` dropped for `[Required]`), `reason` rule, `x-gm-options`, enum strings (V12), no `servers` (D1–D5, A7).
- **Catalog**: OpenAPI registered in every environment; `GmCatalogDocument` (public singleton) makes canonical bytes
  once per process, lazily (D12, A1, A2); `gm.console_catalog` answers them with a strong ETag, `Cache-Control: private,
  no-cache` and 304 (A8). The Dev/Test route `platform.gm_openapi` stays.
- **Bootstrap**: `gm.auth_methods` Public, allowlisted by declaration (V10); `GmMeResponse` + `frameworkVersion`,
  `gameVersion` on `/me` and the local login answer (D9).
- **Lists/options**: `ProducesGmPage<T>` on `RouteHandlerBuilder` (A3); audit, staff audit and ledger pages free-form
  (D10, D11); `BanScopes` shared by the ban command and `identity.ban_scopes` (A9); unknown `[GmOptions]` route = startup
  line (D13); framework bodies annotated only where exact (A10, research R13).
- **Kit**: `GmOpenApiSnapshot` over `IServiceProvider` + `TestHost` overloads (A4); the consumer fixture dogfoods GM (D14).
- **No version bump, tag or push** in this slice (D15).

**Tests**: mandatory (constitution VI). Each story phase writes its tests before or with the code they cover and ends
green. Couchbase tests skip with a message when `COUCHBASE_CONNSTR` is not set; the full Couchbase suite runs in the final
gate (T061). Opt-in suite `GAMESERVER_PERF=1`: catalog generation and warm answer (SC-004). Scenario ids (`US2-3`) are the
spec's acceptance scenarios. **The committed-document test** (`tests/GameServer.Gm.Tests/OpenApiTests.cs`) turns red
inside a phase whenever the document changes; it is made green by the phase checkpoint, which regenerates
`docs/api/gm-openapi.json` with `UPDATE_OPENAPI=1` and reviews the diff (never regenerate without reading the diff).

**Format**: `- [ ] T0NN [P] [USn] description with paths`, then `Done when:` (verifiable) and the FR/SC/BC ids covered.
`[P]` = different files and no open dependency. Record findings as `Note:` lines under the task.

**MVP = US1 (Phases 1–3).** Every GM command fully described in the document (body, path, header, ids, tags, `x-gm-*`).
US1 ends at a shippable checkpoint (T017).

**[VERIFY]**: a runtime fact from research.md to confirm on the pinned versions (.NET SDK 10.0.403, ASP.NET Core 10.0.14,
`Microsoft.AspNetCore.OpenApi` 10.0.14) **before** the task that depends on it. Scratch checks run in a throw-away project
under the session scratch folder (never committed); V10 is a permanent test. Record the result as a `Note:`. When the fact
is wrong, apply the stated fallback and record the deviation (contract, docs).

| Item | Fact | Fallback | Check | Used by |
| --- | --- | --- | --- | --- |
| V1 | The generator emits DataAnnotations of an `Accepts<T>` body as JSON Schema keywords (`[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`, `[Length]`, `[RegularExpression]`, `[Description]`, `[Required]`) for property and positional-parameter targets; `long` = `["integer","string"]` + `int64` + `pattern` | Our schema transformer writes every keyword from `GmBodyContract` (overwrite) | T003 (**gate**) | T021, T022 |
| V10 | A Public route declared by a module is accepted by the startup endpoint check with the default `GameServer:PublicEndpointAllowlist` and with an explicit allowlist that does not list it | List the template in `GmModule`'s `ModuleDescriptor.PublicEndpoints`; still no game configuration | T004 (**gate**, permanent test) | T028, T031 |
| V11 | `IOpenApiDocumentProvider` (keyed `"gm"`) produces the GM document without an HTTP request, in every environment, equal to the dev route's document except `servers` | Invoke the `MapOpenApi` request delegate on a synthetic `DefaultHttpContext` (research R4) | T005 (**gate**) | T010 |
| V12 | The generator uses the HTTP `JsonOptions` (Web defaults, no enum converter); inventory of framework runtime users of those options | `ConfigureHttpJsonOptions` with the `GameServerJson` enum converter, or an enum schema transformer when the inventory shows a runtime user | T003 (**gate**) | T011 |
| V13 | `JsonPropertyInfo.AssociatedParameter.AttributeProvider` exposes positional parameters; the schema transformer context exposes `JsonPropertyInfo` | Primary-constructor parameter lookup by name; property resolution from the parent `JsonTypeInfo` | T003 (**gate**) | T009, T021 |

**Environment**: `"$HOME/.dotnet10/dotnet.exe"`; build `build GameServer.slnx -c Release`; test
`test --solution GameServer.slnx -c Release --no-build` (one class: `--project X --filter-class "*Name"`). Couchbase
variables as in `AgentMemories/features/p05-speckit/00_summary.md` ("Environment"; password from the user variable
`COUCHBASE_PW`, never printed). **Never grep recursively over `samples/`** (Unity `Library/` under KestrelDemo). Search
`src/`, `tests/`, `templates/`, `tools/`, `modules/`, `docs/` and, by project folder only, `samples/SampleHost`,
`samples/SampleGame/SampleGame`, `samples/SampleGame/SampleGame.Adapters`. No Unity or SDK change in this slice.
Port 5180 belongs to the owner and is never bound or probed; scratch hosts use 5091/5092. `.specify/feature.json` points to
`specs/017-gm-api-contract`. Repository docs are in Vietnamese, except the consumer guide and the release notes
(English); keys, routes and code stay literal. **Never edit `E:\ProjGits\*`** (read-only checks only). No commit is part of
any task (commits: no Claude signature lines, owner rule).

## Phase 1: Setup

- [ ] T001 Baseline on branch `017-gm-api-contract` (base `59d37f6`): Debug and Release build of `GameServer.slnx`; full
  suite in memory (no `COUCHBASE_*`) and with `GAMESERVER_TEST_STORE=File`. Record into the scratch folder (not
  committed):
  (a) total/passed/skipped per configuration and the wall time of the `GameServer.Gm.Tests` project alone (reference for
  the "host start unchanged" check of T060);
  (b) SHA-256 of `docs/api/gm-openapi.json` and its path/operation list (input of every checkpoint diff review);
  (c) `git status` (only `specs/017-gm-api-contract/` and the owner's `tasks/` untracked).
  Done when: 0 warnings in both configurations, 0 failures; the values recorded as a Note. (FR-091)
- [ ] T002 [P] Release notes draft `specs/017-gm-api-contract/release-notes-draft.md` (FR-065), created now and extended by
  every story checkpoint (T017, T026, T034, T043, T050) before the final pass (T058). English, like the 013–016 drafts.
  Skeleton:
  - summary: GM-1 of the GM tool track (design §19); "backend contract only; no cookie, CORS, SSO, hosting or frontend
    change; no proto, Couchbase or Unity change; GM JSON changes are additive";
  - BC-1..BC-8 table (change, who is affected, how it surfaces, fix), from the spec's table;
  - first-consumer migration (zh-pvp): bump → `GmOpenApiSnapshot` test over `LocalFlavorFactory<Program>` in write mode →
    commit `docs/api/gm-openapi.json` → nothing for the allowlist (V10) → optional first GM command with an annotated
    body; zh-pvp facts of the spec (no GM command, no allowlist setting in `src/ZhPvp.Host/appsettings*.json`);
  - "Decisions awaiting owner override": plan owner questions 1–6, each "applied, owner confirmation pending";
  - "Next tag": proposed `v0.8.0`, not part of the slice (D15).
  Done when: the file exists with every section and the BC table; later checkpoints only fill sections. (FR-065)
- [ ] T003 [P] **[VERIFY] V1, V12, V13 — decision gate for schemas and validation** in a throw-away net10.0 web project
  under the session scratch folder (package `Microsoft.AspNetCore.OpenApi` 10.0.14, the repo's `global.json` SDK):
  1. a probe body with every attribute of FR-010 twice — once with `[property: …]`, once on the positional parameter
     without target — on `int`, `int?`, `long`, `long?`, `double`, `string?`, `List<string>?`, a nested record and a
     `List<NestedRecord>?`, plus an enum property; map `MapPost("/gm/v1/probe/{playerId}", (HttpContext h) => …)
     .Accepts<ProbeBody>("application/json")`; `AddOpenApi("gm")`; read the document over HTTP;
  2. record per attribute and target whether a keyword appears, its value types (`minimum` as number?), the `required`
     list, the `long`/`int` representation, the enum schema (V1, V12);
  3. resolve `IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>` and compare with a `JsonSerializerOptions` built like
     `GameServerJson.Options` (naming, number handling, converters); grep `src/` for runtime users of the HTTP JSON
     options (`Results.Json(` without an options argument, typed body parameters on minimal APIs, `TypedResults.Json`)
     and list them (V12 inventory);
  4. in a schema transformer, log whether `context.JsonPropertyInfo` is set for property schemas and whether
     `JsonPropertyInfo.AssociatedParameter?.AttributeProvider` returns the parameter's attributes (V13).
  Done when: a Note records the table (attribute × target → keyword or none), the number/enum representation, the V12
  inventory with the chosen option (HTTP JSON options or enum transformer), and the V13 answers with the chosen access path;
  any fallback taken is written into research R2/R5/R6 as a one-line deviation. (FR-010, FR-014, FR-021, FR-090)
- [ ] T004 [P] **[VERIFY] V10 — decision gate for the Public allowlist (permanent test)**: new
  `tests/GameServer.Platform.Web.Tests/PublicDeclarationAllowlistTests.cs` with a test-only module declaring a Public GM
  route (`RouteDeclaration.Get("probe.public", "/gm/v1/probe/public", SecurityProfile.Public, RouteSurface.GmApi)`, mapped
  with `MapDeclared(...).AsPublicEndpoint(protobuf: false)`): (a) a `TestHost` with default settings starts and answers 200
  anonymously; (b) a host with `GameServer:PublicEndpointAllowlist:0 = /v1/time` (explicit, not listing it) starts and
  answers 200; (c) the same route mapped without a declaration (`MapGet` + `AsPublicEndpoint`) fails startup with the
  allowlist line (`EndpointSecurityValidator`), proving the test sees the check. Re-read (read-only)
  `E:\ProjGits\zh-pvp-server\src\ZhPvp.Host\appsettings*.json` for `PublicEndpointAllowlist`.
  Done when: (a) and (b) green, (c) fails startup with the expected line; a Note records the result and the zh-pvp finding.
  If (a) or (b) fails: apply the V10 fallback in T028 and record it. (FR-035, SC-007, FR-090)
- [ ] T005 [P] **[VERIFY] V11 — decision gate for document generation without a request** in a throw-away test under the
  scratch folder referencing `src/GameServer.Platform.Web` and the shared test host: register `GmOpenApi.Add` regardless
  of environment (scratch copy of the registration), start a `TestHost` with `DeploymentEnvironment` Beta-like settings
  (development tools off) and the GM module, resolve the keyed `IOpenApiDocumentProvider` (`"gm"`), call
  `GetOpenApiDocumentAsync()`, serialize as OpenAPI 3.1 JSON; on a Development host compare the normalized JSON with the dev
  route's answer (`/gm/v1/openapi/gm.json`); record what `servers` holds without a request; time the first and a second
  generation.
  Done when: a Note records the provider type/lookup used (or that the fallback of research R4 is needed and works), the
  `servers` value, equality with the dev route (except `servers`), and the timings (input of SC-004). (FR-030, FR-031,
  FR-090, SC-004)
- [ ] T006 [P] **Inventory of pins and fixture traps** (read-only; research R9, R10): grep `src/`, `tests/`, `templates/`,
  `tools/`, `modules/`, `docs/` for `gm-openapi`, `GmOpenApi`, `DocumentPath`, `GmMeResponse`, `"gm.` route-name lists,
  `PublicEndpointAllowlist`, `x-gm-`, `reason is required`, and the four new templates (clash check). In
  `tests/ConsumerFixture`: tests pinning route lists or the wire-contract export (`FixtureCatalogTests`), WireGen committed
  outputs and `CommittedOutputTests` inputs, `Fixture.Host` config and project references, the fixture module's
  permission catalog. List every test whose expectation this slice changes and the task that updates it.
  Done when: a Note lists each pin with file:line and its updating task (at least R9's table, confirmed or corrected), and
  the fixture changes needed by T047 (project reference to `GameServer.Modules.Gm.Adapters`, settings). (BC-5, FR-054)

## Phase 2: Foundational (behaviour-neutral)

Purpose: types and registrations every story uses, with no visible change to any answer or to the committed document.

- [ ] T007 [P] Field errors on the error path (after T001; public-api §1, data-model §3.1):
  `src/GameServer.Core/Errors/GameServerException.cs` gains `public IReadOnlyDictionary<string, IReadOnlyList<string>>?
  Errors { get; init; }` (XML doc: safe messages, Problem Details only). `src/GameServer.Platform.Web/Errors/ErrorResponseWriter.cs`
  `WriteAsync` gains an optional trailing `errors` parameter and writes `errors` (object of string arrays, ordinal key
  order) after `details` in Problem Details only; `src/GameServer.Platform.Web/Middleware/ExceptionMiddleware.cs` passes
  `ex.Errors`. New `tests/GameServer.Platform.Web.Tests/GmProblemDetailsErrorsTests.cs`: a test endpoint (JSON profile) that
  throws with `Errors` → body has `errors` with the exact map and every existing member unchanged; the same throw on a
  Protobuf endpoint → `ErrorEnvelope` bytes equal to the same throw without `Errors`; `Errors = null` → no `errors` member.
  Done when: the new tests pass and the full Platform.Web and Gm test projects stay green. (FR-026, FR-022, BC-7)
- [ ] T008 [P] Presentation API (after T001; public-api §2, data-model §2): new
  `src/GameServer.Platform.Web/Endpoints/GmPresentation.cs` with `GmConfirm`, `GmConfirmMetadata`, `GmPagedMetadata`,
  `GmQueryParameterMetadata`, `GmRequestIdMetadata`, `GmOptionsAttribute`, `GmOption`, `GmOptionList`,
  `GmPresentationExtensions` (`WithGmConfirm`, `ProducesGmPage<T>` on `RouteHandlerBuilder` = `Produces<GmPage<T>>` +
  `GmPagedMetadata` + the two query-parameter metadata, `WithGmQueryParameter`, `WithGmRequestId`). Metadata only. New
  `tests/GameServer.Platform.Web.Tests/GmPresentationTests.cs`: each extension adds exactly the expected metadata to a
  mapped endpoint (bare app); `ProducesGmPage<StaffView-like>` adds `IProducesResponseTypeMetadata` for `GmPage<T>`.
  Done when: tests pass; `ArchitectureTests` (Core/business projects without ASP.NET) still green. (FR-048, FR-040, FR-041)
- [ ] T009 Attribute walk (after T003 decision V13; research R2): new internal
  `src/GameServer.Platform.Web/Endpoints/GmBodyContract.cs`: `GmBodyContract.For(Type)` (cached per type) built from
  `GameServerJson.Options.GetTypeInfo(type)`: per JSON property the JSON name, CLR type, getter, `ValidationAttribute`s and
  `GmOptionsAttribute` from the property and from its positional parameter (V13 path or its fallback), de-duplicated;
  `DescriptionAttribute` kept for the document only; nested contracts for object properties and lists of objects to depth 3;
  a flag "has a non-nullable value-type `[Required]`" per contract. New
  `tests/GameServer.Platform.Web.Tests/GmBodyContractTests.cs`: property target, parameter target, both (no duplicate),
  `[JsonPropertyName]` and camelCase names, nested object, list of objects, depth cut at 3, primitives/strings/
  dictionaries/`JsonNode`/enums/`DateTimeOffset` not walked into, cache returns the same instance.
  Done when: tests pass. (FR-021, FR-010)
- [ ] T010 Catalog bytes in every environment (after T005 decision V11; research R4, R8): `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs`
  calls `OpenApi.GmOpenApi.Add(services)` outside the `AllowsDevelopmentTools` block and registers
  `services.AddSingleton<GmCatalogDocument>()`; `GameServerApplicationExtensions.cs` keeps mapping the dev route only with
  development tools. New public `src/GameServer.Platform.Web/OpenApi/GmCatalogDocument.cs` (`GetAsync` →
  `GmCatalogBytes(Bytes, ETag)`): V11 provider path (or fallback), OpenAPI 3.1 JSON, `JsonNode` re-write indented + `\n`,
  UTF-8 without BOM; strong ETag (32 hex of SHA-256); `Lazy<Task<…>>` with a faulted task dropped; one Information log line
  `GM catalog generated: {Operations} operations, {Bytes} bytes, {ElapsedMs} ms.` (category `GameServer.Gm.Catalog`). New
  `tests/GameServer.Platform.Web.Tests/GmCatalogDocumentTests.cs`: on a host without development tools the bytes parse as
  the GM document; on a Development host the bytes equal the dev route's answer re-written the same way (the document still
  has `servers` at this point only in the dev route, so the comparison removes `servers` on both sides until T014); 20
  concurrent first calls → one log line, same instance; a generation that throws once is not cached and the next call succeeds (test double);
  bytes end with exactly one `\n`, contain no `\r`, no BOM.
  Done when: tests pass; `SampleHostFlavorTests` and `CatalogCompositionTests` unchanged and green (no route added);
  `GameServer.Gm.Tests` wall time within 5 % of T001 (a). (FR-030, FR-031, FR-032, FR-033, A1, A2, D12)
- [ ] T011 [P] Enum representation (after T003 decision V12; research R5): apply the chosen option — either
  `services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))`
  in `GameServerBuilder.cs` (only when the T003 inventory shows no runtime user whose JSON would change), or an internal
  enum schema transformer in `src/GameServer.Platform.Web/OpenApi/` registered by `GmOpenApi.Add`. Test in
  `tests/GameServer.Platform.Web.Tests/GmOpenApiTransformerTests.cs` (new file, bare app with `GmOpenApi.Add` and a body
  with an enum): the enum schema lists the camelCase names as strings; when the HTTP options path is taken, the T003
  inventory's runtime users keep their exact output (one assertion each).
  Done when: test passes; the committed GM document is unchanged (no enum in it today). (FR-014, A7)
- [ ] T012 Foundations checkpoint (after T007–T011): Debug and Release build 0 warnings; full in-memory suite green;
  `tests/GameServer.Gm.Tests/OpenApiTests.cs` green **without** regeneration (the document is unchanged so far).
  Done when: all three hold; counts recorded as a Note against T001. (FR-091)

## Phase 3: US1 — Every GM command is fully described in the GM document (P1, H1, H4, H7) MVP

**Goal**: operation ids, area tags, body/path/header of every GM command, `x-gm-*` keys, no `servers`.
**Independent test**: spec US1 "Independent Test" on the framework test host's document.

- [ ] T013 [US1] Command endpoints carry their contract (after T008; plan "US1"): in
  `src/GameServer.Platform.Web/Commands/GmCommandEndpointFactory.cs` `MapGmCommandCore` adds
  `.Accepts<TBody>(GmHttp.JsonContentType)` and `.WithMetadata(GmRequestIdMetadata.Instance)` (all four overloads go
  through it). No request handling change.
  Done when: a test in `tests/GameServer.Platform.Web.Tests/GmPresentationTests.cs` maps a GM command through the string
  overload and through the declaration overload and finds `AcceptsMetadata` (request type `TBody`, content type
  `application/json`) and `GmRequestIdMetadata` on both; every existing GM command test still green. (FR-003, FR-006)
- [ ] T014 [US1] Operation and document transformers (after T013; research R1, data-model §1.1): new internal
  `src/GameServer.Platform.Web/OpenApi/GmOperationTransformer.cs` and `GmDocumentTransformer.cs`, registered by
  `GmOpenApi.Add` after the existing transformer:
  - declaration from `RouteDeclarationMetadata`, else `WireCatalog.FindRouteName(method, template)` → `operationId`;
  - tags `[area]` unless the endpoint has `ITagsMetadata`;
  - every template `{name}` missing from `operation.Parameters` as a required string path parameter (`playerId` with the
    `GmHttp.RequirePlayerId` pattern);
  - `GmRequestIdMetadata` → header `X-Request-Id` (required, string, `format: uuid`);
  - `GmQueryParameterMetadata` / `GmPagedMetadata` → query parameters (`pageSize` integer 1..`PagedQuery.MaxPageSize`
    required, `cursor` string optional);
  - `x-gm-kind`, `x-gm-operation`, `x-gm-target`, `x-gm-confirm` (non-GET only), `x-gm-paged`;
  - document: `servers` removed; one sentence about `x-gm-*` appended to `info.description`.
  Tests in `tests/GameServer.Platform.Web.Tests/GmOpenApiTransformerTests.cs` (bare app with a catalog of test
  declarations): id via metadata; id via template binding (endpoint mapped with `MapPost` + `AsGmEndpoint`, declaration
  listed but not attached); explicit `WithTags("x")` kept; name without a dot → whole-name tag; `{playerId}` and
  `{staffId}` path parameters; header present only with the metadata; GET → `query` without confirm; POST without
  metadata → `action` + `simple`; `WithGmConfirm(Typed)` → `typed`; `{playerId}` → `player`, else `global`; command →
  `x-gm-operation`; paged → `x-gm-paged` + both parameters; no `servers` in the serialized document.
  Done when: tests pass; `GmCatalogDocumentTests` no longer strips `servers` before comparing with the dev route (the
  document transformer removes it for both). (FR-001..FR-009, D1–D4)
- [ ] T015 [P] [US1] Typed confirmation on the three Q7 commands (after T008): `.WithGmConfirm(GmConfirm.Typed)` on the
  `MapGmCommand` calls of `identity.ban`, `identity.cancel_deletion` (`src/GameServer.Modules.Identity.Adapters/IdentityModule.cs`)
  and `economy.adjust` (`src/GameServer.Modules.Economy.Adapters/EconomyModule.cs`). (The two Q7 actions follow in T039.)
  Done when: the three endpoints carry `GmConfirmMetadata(Typed)` (asserted in T016). (FR-046, FR-005)
- [ ] T016 [US1] Document shape of commands (after T014, T015): new `tests/GameServer.Gm.Tests/GmDocumentShapeTests.cs`
  (reads the document through `GmCatalogDocument` of `GmTestHost` with SSO configured, as `OpenApiTests` does): for
  `identity.ban`, `identity.unban`, `identity.cancel_deletion`, `economy.adjust`, `progression.set`: `operationId`,
  `tags`, `requestBody` (`application/json`, required, `$ref` to the body type), `playerId` path parameter (required,
  pattern), `X-Request-Id` header, `200` → `GmCommandResponse`, `x-gm-kind: command`, `x-gm-operation`,
  `x-gm-target: player`, `x-gm-confirm` (`typed` for the three of T015, `simple` otherwise). For every operation of the
  document: `operationId` = route name of its endpoint (via `WireCatalog`) and unique; tag rule; every GET is `query`
  without confirm; no `servers`.
  Done when: tests pass. (SC-001, FR-001..FR-006, US1-1..US1-6)
- [ ] T017 [US1] US1 checkpoint (after T013–T016): regenerate `docs/api/gm-openapi.json` (`UPDATE_OPENAPI=1`, Gm.Tests
  `OpenApiTests`) and review the diff against T001 (b): only operation ids, tags, `servers` removal, command
  bodies/parameters, `x-gm-*` keys, info sentence — nothing removed from paths, schemas or permissions. Full in-memory suite
  green. Release notes draft: US1 section (document changes, BC-1 detail, MVP note).
  Done when: diff review recorded as a Note (counts of operations and of changed members); suite green; draft updated.
  (FR-055 partial, BC-1, SC-001)

## Phase 4: US2 — A GM body's input rules are declared once and enforced by the server (P2, H3, Q8)

**Goal**: DataAnnotations shown as keywords and enforced before `toDomain`, with `errors`.
**Independent test**: spec US2 "Independent Test" (sample progression).

- [ ] T018 [US2] Validator (after T009; research R2, R3, data-model §3.1): new internal
  `src/GameServer.Platform.Web/Endpoints/GmBodyValidator.cs`: `Validate(object? body, ReadOnlySpan<byte> json, bool commandBody)`
  → sorted `IReadOnlyDictionary<string, IReadOnlyList<string>>` (empty = valid): walks `GmBodyContract`, runs each
  `ValidationAttribute.GetValidationResult` with `MemberName` = CLR name and `DisplayName` = JSON name, JSON paths for
  nested values (`a.b`, `items[1].c`), JSON presence (via `JsonDocument`, case-insensitive like the options) for
  non-nullable value-type `[Required]`, and, when `commandBody`, the `reason` rule (null/white space/over 500 →
  `reason is required (1..500 characters, not only white space).`). New
  `tests/GameServer.Platform.Web.Tests/GmBodyValidatorTests.cs`: per attribute type and target, the boundary value
  accepted and the next value refused with the JSON name as key; `[Range]` on null passes; `[Required]` on null, `""`,
  `"  "` refused; absent required `long` refused, present `0` accepted; mixed-case JSON key counts as present; nested and
  list paths; several failures → all keys, ordinal order; reason merged with annotation errors; messages contain no
  exception text.
  Done when: tests pass. (FR-020, FR-021, FR-022, FR-023, D6, D7, A5)
- [ ] T019 [US2] Command wiring (after T007, T018): `MapGmCommandCore` replaces the reason block with
  `GmBodyValidator.Validate(wire, body, commandBody: true)`; non-empty → `throw new GameServerException(CoreErrorCodes.BadRequest,
  "Body validation failed: " + string.Join(", ", keys)) { Errors = errors }` before `toDomain`. New
  `tests/GameServer.Gm.Tests/GmCommandValidationTests.cs` (first part): missing, blank and 501-char `reason` on
  `identity.unban` → 400 `BAD_REQUEST` with `errors.reason`, same status/code as `main`; a valid unban unchanged; the
  Information log line "Request rejected with BAD_REQUEST" is written once and `http_errors_total{code="BAD_REQUEST"}` + 1
  (FR-025); a counting `IGmTargetValidator` and the receipts collection show 0 calls/documents on a refusal.
  Done when: tests pass; all existing GM tests green. (FR-020, FR-023, FR-025, US2-2, US2-5)
- [ ] T020 [P] [US2] `GmHttp.ReadJsonAsync` validates (after T007, T018): `src/GameServer.Platform.Web/Endpoints/GmHttp.cs`
  calls `GmBodyValidator.Validate(value, body, commandBody: false)` after deserialization and throws the same exception.
  Test in `tests/GameServer.Platform.Web.Tests/GmBodyValidatorTests.cs` (host section): a test GM endpoint reading an
  annotated body through `ReadJsonAsync` → 400 with `errors`; an unannotated body unchanged.
  Done when: tests pass; staff and gate tests green. (FR-024, US2-6, N1)
- [ ] T021 [US2] Schema transformer (after T009, T014, **T003** decisions V1/V13; research R6, data-model §1.2): new internal
  `src/GameServer.Platform.Web/OpenApi/GmSchemaTransformer.cs`, registered by `GmOpenApi.Add`: request-body type set from
  `AcceptsMetadata` of `/gm/**` endpoints (computed once); for those types `required` = `[Required]` JSON names (+ `reason`
  for `IGmCommandBody`), `null` removed from the type of `[Required]` properties; `reason` = string, 1..500, pattern `\S`;
  property keywords from `GmBodyContract` written on the use-site property schema (overwrite); `x-gm-options` on the
  property or its `items`. Tests in `GmOpenApiTransformerTests.cs`: a probe body (both targets, nested, list, enum) →
  exact keywords; a response record with the same shape keeps the generator's `required`; a shared nested type used by two
  bodies with different rules → keywords on each use site, none on the component.
  Done when: tests pass. (FR-010, FR-011, FR-012, FR-013, D5, A6)
- [ ] T022 [P] [US2] Probe module and the SC-002 matrix (after T019, T021): a test-only GM module in
  `tests/GameServer.Gm.Tests/Infrastructure/ProbeGmModule.cs` (permission `probe.edit`, one aggregate, one GM command whose
  body has every supported attribute on both targets, a nested record and a list of records; added through
  `GmTestHost.StartAsync(extra: …)` only in the tests that need it, so the committed framework document is not affected).
  `GmDocumentShapeTests` (probe host): keywords per property; `GmCommandValidationTests` (second part): for each property
  the schema's boundary value is accepted and the value one step outside refused with that key (SC-002), the document
  read from the same host.
  Done when: tests pass, 100% agreement in the matrix. (SC-002, US2-1..US2-4)
- [ ] T023 [P] [US2] Framework body annotations (after T019, T020; research R13): `[property: Required]` on
  `AdjustResourceBody.Resource` (`src/GameServer.Modules.Economy.Adapters/EconomyModule.cs`; the `IsNullOrWhiteSpace`
  check in `toDomain` stays as a guard); `[property: StringLength(500)]` on `MaintenanceBody.Message`
  (`src/GameServer.Modules.Gm.Adapters/Ops/GateEndpoints.cs`; the length check stays). Tests: `GmAdjustTests` gains blank
  `resource` → 400 with `errors.resource` (status and code as on `main`); `GateAndBanTests` gains `message` of 500 chars →
  OK and 501 → 400 with `errors.message`.
  Done when: tests pass; every existing assertion of `GmAdjustTests`, `GateAndBanTests`, `DeletionGmTests` unchanged and
  green. (FR-027, BC-3, US2-7)
- [ ] T024 [P] [US2] Sample reference example (after T019): `SetProgressionBody`
  (`samples/SampleGame/SampleGame.Adapters/SampleGameModule.cs:119`) gets `[property: Range(1, int.MaxValue)]` on `Level`
  and `Stage`, `[property: Range(0, long.MaxValue)]` on `Xp`; `toDomain` keeps only the "at least one of level, xp, stage"
  rule. Tests in `GmCommandValidationTests` (third part, sample host): `level: 0, xp: -1` → 400 with `errors.level` and
  `errors.xp`, no receipt; `stage: 1` → OK; `{}` + reason → 400 without `errors` (cross-field rule, as today); the
  existing sample GM tests (`tests/SampleGame.Tests`, grep `progression`) unchanged.
  Done when: tests pass. (FR-027, US2 independent test)
- [ ] T025 [US2] Problem Details contract check (after T019, T023, T024): `GmCommandValidationTests` asserts the full JSON
  member set of a validation answer (`type, title, status, code, codeNumber, requestId, retryable, errors`) and that a
  non-validation `BAD_REQUEST` (bad JSON, missing `X-Request-Id`) has no `errors`.
  Done when: tests pass. (FR-022, BC-2)
- [ ] T026 [US2] US2 checkpoint (after T018–T025): regenerate `docs/api/gm-openapi.json`, review the diff (request-body
  `required` lists, `reason` rule, keywords on `SetProgressionBody`, `AdjustResourceBody`, `MaintenanceBody`; nothing else);
  full in-memory and File suites green; release notes: US2 section (annotation set, `errors`, BC-2, BC-3 with "how to find
  annotated GM bodies": grep `IGmCommandBody` records for `[`, D5–D8).
  Done when: diff review Note; suites green; draft updated. (FR-055 partial, BC-2, BC-3)

## Phase 5: US3 — A console finds the catalog, the sign-in methods and the server versions in every environment (P3, H2, D2, D8)

**Goal**: `gm.console_catalog` (every environment, ETag/304), `gm.auth_methods` (Public), versions in `/me`.
**Independent test**: spec US3 "Independent Test" (Beta and Local flavors).

- [ ] T027 [US3] Catalog route (after T010, T014; research R8, data-model §3.5): `src/GameServer.Modules.Gm.Adapters/GmRoutes.cs`
  gains `ConsoleCatalog` (`gm.console_catalog`, GET, `GmModule.ConsoleCatalogRoute = "/gm/v1/console/openapi.json"`, Gm,
  `gm.access`) in `All`; `GmModule.MapGmApi` maps it with `.ExcludeFromDescription().AsGmEndpoint(GmPermissions.Access)`
  and a handler that reads `GmCatalogDocument.GetAsync`, compares `If-None-Match` (comma list, trimmed, `W/` stripped, `*`)
  and answers 304 (no body) or 200 (`application/json`, bytes), always with `ETag` and
  `Cache-Control: private, no-cache`. `WithSummary` added.
  Done when: the host starts (014 endpoint check green) and T030 passes. (FR-030, FR-031, FR-009)
- [ ] T028 [US3] Sign-in methods route (after T004 decision V10; data-model §3.2): `GmRoutes.AuthMethods` (`gm.auth_methods`,
  GET, `GmModule.AuthMethodsRoute = "/gm/v1/auth/methods"`, Public, GmApi) in `All`; new record `GmAuthMethods(bool Local,
  bool Sso)` in `GmModule.cs`; mapped with `.Produces<GmAuthMethods>().AsPublicEndpoint(protobuf: false)
  .WithRequestTimeout(ApiRouteExtensions.GmTimeoutPolicy).RequireRateLimiting(ApiRouteExtensions.GmLimiterPolicy)
  .WithSummary(...)`; `Local = GameServerOptions.AllowsDevelopmentTools` (the condition of `gm.auth_login`),
  `Sso = GmOptions.Oidc.IsConfigured`. If T004 took the V10 fallback, also list the template in
  `GmModule.Describe().PublicEndpoints`.
  Done when: the host starts with default settings and T031 passes. (FR-034, FR-035)
- [ ] T029 [P] [US3] Versions in `GmMeResponse` (after T001; research R11): `GmMeResponse` gains init members
  `FrameworkVersion` and `GameVersion` (default `""`); `GmModule.Me` and `LoginAsync` set them from
  `GameServerRuntimeInfo` (`FrameworkVersion`, `GameVersion`).
  Done when: T032 passes; existing `/me` and login tests green. (FR-036, BC-4)
- [ ] T030 [US3] Catalog route tests (after T027): new `tests/GameServer.Gm.Tests/GmCatalogRouteTests.cs` on `GmTestHost`:
  anonymous → 401 `UNAUTHENTICATED`; staff of another environment (`other-env`) → 403; a staff with only `gm.access`
  (seed one with `Permissions: [gm.access]`, no role) → 200, `Content-Type: application/json`, `ETag` of 34 chars,
  `Cache-Control: private, no-cache`, body bytes equal to `GmCatalogDocument` bytes and equal (as JSON) to the dev route's
  answer; `If-None-Match` with the tag, with `W/` + tag, with a list containing it, with `*` → 304 and empty body; with an
  unknown tag → 200; body contains neither `GameServerVersion.FrameworkInformational` nor the runtime game version nor
  `"servers"`; two staff get identical bytes (no filtering).
  Done when: tests pass. (FR-030..FR-033, FR-008, US3-1..US3-5)
- [ ] T031 [P] [US3] Sign-in methods tests (after T028): new `tests/GameServer.Gm.Tests/GmAuthMethodsTests.cs`: Test host
  without OIDC → `{"local":true,"sso":false}`; with OIDC settings (as `OpenApiTests`) → `{"local":true,"sso":true}`;
  anonymous call succeeds with no cookie; member set exactly `local`, `sso`; host with
  `GameServer:PublicEndpointAllowlist:0 = /v1/time` starts and answers (V10 on the real route). The Beta case
  (`{false,true}`) is covered by T033.
  Done when: tests pass. (FR-034, FR-035, SC-007, US3-6, US3-7)
- [ ] T032 [P] [US3] Versions tests (after T028, T029): `tests/GameServer.Gm.Tests/GmVersionEndpointTests.cs` gains: `/me`
  and the local login answer carry `frameworkVersion` = `GameServerVersion.FrameworkInformational` and `gameVersion` =
  runtime game version (same values as `ops/version`); the "no version" test adds `GET /gm/v1/auth/methods` to its list of
  responses.
  Done when: tests pass. (FR-036, US3-8, OBS-03)
- [ ] T033 [US3] Flavor matrix (after T027, T028): `tests/SampleGame.Tests/CatalogCompositionTests.cs` adds
  `gm.auth_methods`, `gm.console_catalog` to `FrameworkRouteNames` and asserts the catalog route is mapped exactly when
  the GM API is mapped (every flavor), while the dev route keeps its `developmentTools && gm` rule (`:75`);
  `tests/GameServer.Platform.Couchbase.IntegrationTests/SampleHostFlavorTests.cs` adds, per flavor: catalog route
  anonymous → 401; `auth/methods` → `local` true only on Local/Dev flavors with development tools, `sso` as configured by
  the flavor's settings; dev route assertions (`:35`, `:66`) unchanged.
  Done when: `CatalogCompositionTests` green in memory; `SampleHostFlavorTests` green on local Couchbase (or skipped with
  the message when `COUCHBASE_CONNSTR` is unset, then run in T061). (SC-003, BC-5)
- [ ] T034 [US3] US3 checkpoint (after T027–T033): regenerate `docs/api/gm-openapi.json` (adds `gm.auth_methods` only; the
  catalog route is excluded; `GmMeResponse` schema gains two members), review the diff; full in-memory suite green;
  release notes: US3 section (routes, caching, versions, BC-4, BC-5 partial, V10 result).
  Done when: diff review Note; suite green; draft updated. (FR-055 partial)

## Phase 6: US4 — Lists, choices and confirmations are described, not guessed (P4, H5, H11, Q7)

**Goal**: typed paged lists, documented query parameters, option routes and `x-gm-options`, startup check, request-id
header and typed confirmation on actions, summaries everywhere.
**Independent test**: spec US4 "Independent Test".

- [ ] T035 [US4] Ban scopes (after T008; research R7): new internal `src/GameServer.Modules.Identity.Adapters/BanScopes.cs`
  (singleton: `login` + first segment of every registered command operation, ordinal set and sorted list, built once from
  `ModuleRegistry`), registered in `IdentityModule.ConfigureServices`; the ban `toDomain` in `IdentityModule.MapGmApi` uses
  it instead of the local `groups`; `IdentityRoutes.BanScopes` (`identity.ban_scopes`, GET `/gm/v1/players/ban-scopes`, Gm,
  `player.ban`, GmApi) listed in the module's routes and mapped in `MapGmApi` →
  `GmOptionList(scopes.Select(s => new GmOption(s, s)))`, `.Produces<GmOptionList>().AsGmEndpoint(...)`; `BanBody.Scope`
  gets `[property: GmOptions("identity.ban_scopes")]`.
  Done when: existing ban tests (`GateAndBanTests`, scope messages) green unchanged; T041 passes. (FR-043, FR-044, A9)
- [ ] T036 [P] [US4] Resource options (after T008): `src/GameServer.Modules.Economy/EconomyRoutes.cs` gains
  `ResourceOptionsTemplate = "/gm/v1/economy/resources"` and `ResourceOptions` (`economy.resource_options`, GET, Gm,
  `EconomyPermissions.PlayerRead`, GmApi) in `All`; `EconomyModule.MapGmApi` maps it → `ResourceCatalog.All` ordered by id →
  `GmOption(id, displayName)`; `AdjustResourceBody.Resource` gets `[property: GmOptions("economy.resource_options")]`
  (next to T023's `[Required]`).
  Done when: the host starts; T041 passes. (FR-043, FR-044)
- [ ] T037 [US4] Startup check of option routes (after T009, T035, T036; research R12, data-model §6): new internal
  `src/GameServer.Platform.Web/Validation/GmOptionsCheck.cs` and its call in `src/GameServer.Platform.Web/Hosting/EndpointValidation.cs`
  (pre-serving run and the error-path run): for each `/gm/**` endpoint with `AcceptsMetadata`, each `GmOptionsAttribute` of
  the body contract must name a catalog route with method GET, surface GmApi, whose condition holds; one line per
  (endpoint, property). New `tests/GameServer.Platform.Web.Tests/GmOptionsCheckTests.cs`: unknown name, a POST route name,
  a Player-surface route name, a route whose condition does not hold → one line each (exact text); valid names → no line;
  one host test: a test module with a bad `[GmOptions]` → `GameServerStartupException` containing the line next to an
  unrelated endpoint fault (one report).
  Done when: tests pass; framework hosts start. (FR-045, D13, BC-8)
- [ ] T038 [P] [US4] Paged lists and query parameters (after T008, T014): `src/GameServer.Modules.Gm.Adapters/GmPlayerEndpoints.cs`
  — `gm.player_search` `.ProducesGmPage<GmPlayerSearchItem>().WithGmQueryParameter("displayName", required: true, maxLength: 64,
  minLength: 1)`, `gm.player_receipts` `.ProducesGmPage<GmReceiptSummary>()`, `gm.player_audit` `.ProducesGmPage<JsonObject>()`;
  `Staff/StaffEndpoints.cs` — `gm.staff_list` `.ProducesGmPage<StaffView>()` (replaces `Produces<GmPage<StaffView>>`),
  `gm.staff_audit` `.ProducesGmPage<JsonObject>()`; `EconomyModule.cs` — `economy.ledger` `.ProducesGmPage<JsonObject>()`.
  Handlers unchanged.
  Done when: existing paging tests (`GmLookupTests`, ACC-24 cases) green; T042 passes. (FR-040, FR-041, FR-042, D10, D11)
- [ ] T039 [P] [US4] Request id and typed confirmation on actions (after T008, T014): `.WithGmRequestId()` on
  `gm.staff_create`, `gm.staff_grant`, `gm.staff_disable`, `gm.staff_enable`, `gm.staff_revoke_sessions`
  (`StaffEndpoints.cs`), `gm.ops_gate_maintenance`, `gm.ops_gate_min_client_version` (`GateEndpoints.cs`),
  `gm.ops_rotate_keys` (`GmModule.cs`); `.WithGmConfirm(GmConfirm.Typed)` on `gm.ops_rotate_keys` and
  `gm.ops_gate_maintenance`. First confirm with the code which of these refuse a request without `X-Request-Id` today
  (`StaffEndpoints.Change` and the gate/rotate paths); annotate exactly those and record any exception as a Note.
  Done when: T042 passes. (FR-006, FR-046, D3)
- [ ] T040 [US4] Summaries (after T027, T028, T035–T039, same files): `.WithSummary("…")` (English, one sentence) on every
  GM route of `GmModule.cs`, `GmPlayerEndpoints.cs`, `StaffEndpoints.cs`, `GateEndpoints.cs`, `Sso/GmSso.cs`,
  `IdentityModule.cs` (3 commands + ban scopes), `EconomyModule.cs` (adjust, ledger, resources), the sample's
  `progression.set` (`SampleGameModule.cs`) and the counter's GM read (`modules/Sample.Counter/Sample.Counter.Adapters/CounterModule.cs`).
  Done when: T042's summary assertion passes. (FR-047, R14)
- [ ] T041 [P] [US4] Option route tests (after T035, T036): new `tests/GameServer.Gm.Tests/GmOptionRoutesTests.cs`:
  resources → sorted by value, labels = display names of the sample `resources.json`, `support` staff (has
  `player.read`) 200, a staff without it 403; ban scopes → `login` + every command group of the host, `admin` 200,
  `support` 403; every listed scope is accepted by `identity.ban` and an unlisted one is refused (same set, A9).
  Done when: tests pass. (FR-043, US4-5, US4-6)
- [ ] T042 [US4] Document shape of lists, options, actions and summaries (after T037–T041):
  `tests/GameServer.Gm.Tests/GmDocumentShapeTests.cs` gains: the six lists with `x-gm-paged`, `pageSize` (required, 1..200),
  `cursor` (optional), `GmPage…` schemas (typed items for search/receipts/staff, object items for the three others);
  `displayName` parameter; `x-gm-options` on `AdjustResourceBody.resource` and on `BanBody.scope` items; the Q7 typed set
  (five routes) and `simple` elsewhere; the `X-Request-Id` header on the eight actions; every operation whose route is
  owned by `gm`, `identity`, `economy`, `platform`, `sample.game`, `counter` has a non-empty `summary`.
  `tests/SampleGame.Tests/CatalogCompositionTests.cs` adds `economy.resource_options`, `identity.ban_scopes`.
  Done when: tests pass. (FR-040..FR-047, SC-001, US4-1..US4-7)
- [ ] T043 [US4] US4 checkpoint (after T035–T042): regenerate `docs/api/gm-openapi.json`, review the diff (summaries, list
  parameters/schemas, option routes, `x-gm-options`, headers and typed confirm on actions); full in-memory and File suites
  green; release notes: US4 section (option routes, startup line BC-8, list schemas, D10/D11, BC-5 complete).
  Done when: diff review Note; suites green; draft updated. (FR-055 partial)

## Phase 7: US5 — A game proves its GM contract in CI with one test (P5, H8, CI-01)

**Goal**: `GmOpenApiSnapshot` in the kit, used by the framework test and by the consumer fixture.
**Independent test**: spec US5 "Independent Test" (fixture).

- [ ] T044 [US5] Kit call (after T010; public-api §8, data-model §5): new `src/GameServer.TestKit/GmOpenApiSnapshot.cs`:
  `GetDocumentAsync(IServiceProvider)`, `WriteAsync(IServiceProvider, path)`, `DiffAsync(IServiceProvider, path)` and the
  `TestHost` overloads of `WriteAsync`/`DiffAsync`; reads `GmCatalogDocument` (public, no internal); `WriteAsync` creates the
  folder and writes through a temp file + replace; `DiffAsync` strips `\r` on both sides, returns `""` or the first
  differing line message of data-model §5 (200-char cut, "end of file"), or the missing-file message. XML docs name the
  CI-01 use and the `LocalFlavorFactory<Program>` pattern.
  Done when: builds with 0 warnings; `TestKitArchitectureTests` green with the allow-lists unchanged. (FR-050, FR-051,
  FR-052, A4)
- [ ] T045 [P] [US5] Kit tests (after T044): new `tests/GameServer.Platform.Web.Tests/GmOpenApiSnapshotTests.cs` on a
  `TestHost` with the GM module (temp folder): `WriteAsync` then `DiffAsync` → `""`; file bytes equal
  `GmCatalogDocument` bytes, no BOM, no `\r`, one final `\n`; a one-character change on line 1, a middle line and the last
  line → the message names that line with both texts; a CRLF copy → `""`; a truncated copy → "end of file" message; a
  missing file → the create message, no exception; `GetDocumentAsync` equals the catalog route's body.
  Done when: tests pass. (SC-005, US5-1..US5-4)
- [ ] T046 [US5] Framework document test on the kit (after T044; FR-053): `tests/GameServer.Gm.Tests/OpenApiTests.cs` keeps
  the endpoint loop (every GM endpoint documented with its permission and CSRF flag; the exclusion filter now also skips the
  catalog route through `ExcludeFromDescription`) and replaces the committed-copy block with:
  `UPDATE_OPENAPI=1` → `GmOpenApiSnapshot.WriteAsync(host, committed)`; then
  `(await GmOpenApiSnapshot.DiffAsync(host, committed)).ShouldBeEmpty()`. Class summary updated (catalog in every
  environment, kit call).
  Done when: test green against the committed file; a deliberate local edit of the committed file makes it fail with the
  line message (then reverted). (FR-053, FR-055)
- [ ] T047 [US5] Fixture GM composition (after T006 inventory, T008, T019; research R10): `tests/ConsumerFixture/src/Fixture.Host/Program.cs`
  adds `.AddModule<GmModule>()` (and the project reference to `GameServer.Modules.Gm.Adapters` through the framework path
  if T006 found it missing); `tests/ConsumerFixture/src/Fixture.Game/` gains `FixtureGmNote.cs`: permission
  `fixture.note.edit` (in the fixture module's `Permissions`), `SetNoteCommand`/`SetNoteHandler` (`fixture.note_set`,
  Gm profile, touches the notes aggregate, exactly one `AddAudit`), declarations `FixtureRoutes.NoteSet`
  (`POST /gm/v1/players/{playerId}/note`) and `FixtureRoutes.NotePresets` (`GET /gm/v1/fixture/note-presets`, Gm,
  `fixture.note.edit`), and the adapter mapping in `FixtureModule.cs` (`IGmApiModule.MapGmApi`):
  `MapGmCommand<SetNoteBody, SetNoteCommand, SetNoteResult>(FixtureRoutes.NoteSet, …).WithSummary(…)` with
  `SetNoteBody([property: Required, StringLength(140)] string? Text, [property: GmOptions("fixture.note_presets")] string? Preset, string? Reason)`
  and the preset route answering a fixed `GmOptionList`. Every fixture test listed by T006 updated as recorded there.
  Done when: the fixture solution builds with 0 warnings; all fixture tests green; WireGen `CommittedOutputTests` green
  (no generated file changes); `FixtureHostSmokeTests` green. (FR-054, D14)
- [ ] T048 [US5] Fixture snapshot test (after T044, T047): new `tests/ConsumerFixture/tests/Fixture.Tests/FixtureGmOpenApiTests.cs`
  over `LocalFlavorFactory<Program>`: (a) `DiffAsync(factory.Services, <fixture>/docs/api/gm-openapi.json)` empty
  (`UPDATE_OPENAPI=1` writes it; first run creates `tests/ConsumerFixture/docs/api/gm-openapi.json`); (b) a copy of the
  committed file with the `maxLength` of `text` changed (temp folder) → the message names that line (SC-006); (c) GM round
  trip with a GM caller as recorded by T006 (Planning note 6): `text` of 141 chars → 400 with `errors.text`;
  valid body → 200 `OK`, receipt present; `GET /gm/v1/fixture/note-presets` → the fixed list.
  Done when: tests pass and the committed fixture document is reviewed (contains `fixture.note_set` with keywords and
  `x-gm-options`). (FR-054, SC-006)
- [ ] T049 [P] [US5] Opt-in catalog performance (after T027): new `tests/GameServer.Gm.Tests/GmCatalogPerformanceTests.cs`
  (`GAMESERVER_PERF=1` only, skipped with a message otherwise): first catalog call wall time and p95 of 1 000 warm calls
  (in-process client) on `GmTestHost`.
  Done when: the opt-in run records first ≤ 1 s and warm p95 ≤ 5 ms as a Note; skipped by default. (SC-004)
- [ ] T050 [US5] US5 checkpoint (after T044–T049): regenerate `docs/api/gm-openapi.json` one last time with the kit-based
  test and confirm `DiffAsync` empty in a second run; full in-memory, File and fixture suites green; release notes: US5
  section (kit call, fixture example, game migration steps finalized).
  Done when: suites green; draft updated. (FR-055, CI-01)

## Phase 8: US6 — The docs tell module authors and games how to declare a GM route for the generic console (P6, H12, PCR-01)

**Goal**: PCRs, guides, HTML page, stale ownership lines, release notes.
**Independent test**: reviewer walkthrough (T059).

- [ ] T051 [P] [US6] PCR gm `docs/pcr/gm.md` (after T043, T050): header lines 4–5 rewritten (owner builds the GM console as
  framework GM-2..GM-4 from the GM document; `/gm/v1/ui` stays a P0 page; design reference); permission table (`gm.access`
  also for the catalog); endpoint table + route-catalog table: `gm.auth_methods`, `gm.console_catalog` (27 → 29 routes);
  Public allowlist (SEC-09) lists `gm.auth_methods` with "allowlisted through its declaration, no configuration";
  new section "GM document and console contract (slice 017)": catalog route and caching (ETag, 304, `private, no-cache`,
  lazy generation, every environment, no version/servers), every `x-gm-*` key with its rule (data-model §1.1), schema
  rules (§1.2), the validation answer with `errors` (§3.1), `/me` members, the compatibility promise (contracts/gm-document
  §5), annotation conventions; the dev route paragraph (Dev/Test only) kept.
  Done when: every item of FR-060 for gm is present; no line says another developer owns the frontend. (FR-060, FR-064)
- [ ] T052 [P] [US6] PCR identity, economy, sample-game (`docs/pcr/identity.md`, `docs/pcr/economy.md`,
  `docs/pcr/sample-game.md`; after T043): `identity.ban_scopes` route and its set; `BanBody.scope` options; typed
  confirmation of ban and cancel deletion; `economy.resource_options` route; `AdjustResourceBody` annotations and typed
  confirmation; ledger page item shape (free-form, stored document); `SetProgressionBody` ranges as the reference example
  and the cross-field rule kept in `toDomain`.
  Done when: each route and body change of US2/US4 owned by these modules is listed. (FR-060)
- [ ] T053 [P] [US6] PCR platform `docs/pcr/platform.md` (after T043): route-catalog section (GM document in every
  environment; `platform.gm_openapi` unchanged; the `[GmOptions]` startup line, data-model §6), error section (Problem
  Details `errors` member, `GameServerException.Errors`, Protobuf unchanged), presentation API summary, breaking changes
  BC-1..BC-8 in the PCR changelog style of 016.
  Done when: FR-060 platform items present. (FR-060)
- [ ] T054 [P] [US6] `docs/guides/module-authoring.md` and `templates/module/README.md` (after T043): "GM route for the
  generic console": `MapGmCommand(route, toDomain)` + `WithSummary`; DataAnnotations (supported set, use `[property: …]` or
  parameter attributes — both enforced; nullable = optional; `[Required]` on numbers checks presence; cross-field rules in
  `toDomain`; custom attributes enforced but not shown); `[GmOptions]` + an option route answering `GmOptionList` (GET,
  GmApi, startup check); `ProducesGmPage<T>` / `WithGmQueryParameter`; `WithGmConfirm(Typed)` for dangerous operations;
  `WithGmRequestId` for mutations outside `MapGmCommand`; regenerate the game's document.
  Done when: the section exists with one complete code example taken from the fixture (T047). (FR-061)
- [ ] T055 [P] [US6] `docs/guides/consumer-guide.md` (English; after T050): new §14 "GM API contract": catalog route and
  `/me`/`auth/methods` for the console, `GmOpenApiSnapshot` test over `LocalFlavorFactory<Program>` (code from T048),
  committed `docs/api/gm-openapi.json` as the input of console codegen (GM-2), what changes on a bump; §11 bump checklist
  gains "run the GM snapshot test; on a diff, review and rewrite"; §5 test list mentions the kit call.
  Done when: §14 exists and §11/§5 lines added. (FR-062)
- [ ] T056 [P] [US6] HTML pages (after T051): `docs/html/02-them-command.html` §8 — table row "Map route" shows
  `MapGmCommand<TBody, TRequest, TResult>(route, toDomain)` (declaration overload), row "Wire" adds annotations and the
  `errors` answer, the sample code shows the annotated `SetProgressionBody` and `SampleGameRoutes.ProgressionSet`, the
  paragraph on `docs/api/gm-openapi.json` mentions `GmOpenApiSnapshot` and the catalog route; `docs/html/01-gioi-thieu.html`
  lines 526, 661, 687 and `docs/html/03-deploy.html` line 733 (ownership and "runtime OpenAPI route only Dev/Test")
  corrected; page stamps "Đúng với framework v0.7.0 … + nhánh `017-gm-api-contract` (chưa phát hành)" on touched pages, in
  the 016 style.
  Done when: grep of the four HTML lines' old phrases returns 0; stamps updated. (FR-063, FR-064)
- [ ] T057 [P] [US6] Resume file ownership lines (after T001): `AgentMemories/features/p05-speckit/00_summary.md` lines
  9–10 (owner decisions 2026-10-27: the GM frontend line now says "superseded 2026-11-05: owner builds the GM console as
  framework slices GM-1..GM-4, design `AgentMemories/features/gm-tool/01_design.md`") and line 389 (open items: TBD-21
  decided 2026-11-05, GM-1 in progress). Also the session's auto-memory file `gm-frontend-out-of-scope.md` (Claude project
  memory, outside the repository) is rewritten or removed with its index line, so later sessions do not reapply it.
  Done when: no line of the resume file says another developer owns the frontend; the auto-memory entry no longer states
  it. (FR-064, SC-008)
- [ ] T058 [US6] Release notes pass (after T051–T057): `specs/017-gm-api-contract/release-notes-draft.md` complete: every
  FR group summarized, BC-1..BC-8 final, migration steps, owner questions, V1/V10/V11–V13 results, docs touched, test
  counts placeholder for T060.
  Done when: a read-through finds every BC id and every new route; nothing marked TODO. (FR-065)

## Phase 9: Verification, review and close

- [ ] T059 Reviewer walkthrough (story 6; after T058): a fresh agent with no context beyond the docs (module-authoring
  guide, consumer guide §14, PCR gm, `02-them-command.html` §8) adds, in a scratch copy of `tests/ConsumerFixture` (never
  the real folder), a second GM command with an annotated body and an option route, regenerates the scratch fixture's GM
  document and runs its snapshot test. Findings (missing or wrong doc lines) are fixed in the docs.
  Done when: the walkthrough succeeds without help, or every finding is fixed and the walkthrough re-run succeeds; the
  scratch copy is deleted. (US6 independent test, FR-061, FR-062)
- [ ] T060 Final verification (after T059): Debug and Release build of `GameServer.slnx` (0 warnings); full suite in memory
  and with `GAMESERVER_TEST_STORE=File`; consumer fixture suites; opt-in `GAMESERVER_PERF=1` run of
  `GmCatalogPerformanceTests` (SC-004); `GameServer.Gm.Tests` wall time compared with T001 (a) (within 10 %); WireGen
  `verify` for the three configs (no generated change); `git diff main -- proto src/*/Protos sdk` empty; `git diff main --
  docs/api/gm-openapi.json` reviewed once more as a whole; SC-008 grep (repository minus `specs/0[0-1][0-6]*` and past
  release notes) for the ownership phrases = 0 hits; `git status` lists only intended files.
  Done when: all hold; counts recorded against T001 and the checkpoints. (FR-091, SC-004, SC-008)
- [ ] T061 Couchbase full suite (after T060; run through PowerShell in the background, variables from the resume file,
  password never printed), including `SampleHostFlavorTests` (catalog and auth-methods per flavor).
  Done when: 0 failures; total/passed/skipped and duration recorded; a known flaky class (TelemetryTests) rerun alone before
  reporting. (FR-091, SC-003)
- [ ] T062 Tech-lead review (after T060): `tech-lead-reviewer` gets the spec, the plan, this file and the changed files
  (`git diff --stat main`); focus: document and server agreement (one walk, both targets, presence, nested paths); no
  behaviour change for unannotated bodies and for framework commands; `errors` safety (no exception text, no secrets);
  catalog route auth (`gm.access`), caching headers, no version/servers in the document, lazy generation and failure
  handling; `gm.auth_methods` exposes only two booleans; `BanScopes` single source; startup check lines; kit has no
  test-framework dependency; docs accuracy and H12 completeness.
  Done when: a findings list (Blocker/Major/Minor) is recorded. (constitution governance)
- [ ] T063 Review fixes (after T062): fix every accepted finding, each with a test that fails without the fix; rerun the
  affected projects, then the in-memory suite; regenerate the committed documents if they change.
  Done when: every accepted finding closed or recorded as an owner decision; suites green. (FR-091)
- [ ] T064 Resume and design status (after T063): `AgentMemories/features/p05-speckit/00_summary.md` gains a 017 row
  (DONE n/n, review outcome, test counts) and "Technical notes from 017" (catalog, validator, kit call, open owner
  questions); `AgentMemories/features/gm-tool/00_summary.md` "Bước kế tiếp" moves to GM-2 and records GM-1 results (V1,
  V10, V11–V13 notes, D/A decisions awaiting the owner).
  Done when: both files updated. (handover)

## Dependencies

**Phase order**: Setup (T001–T006) → Foundations (T007–T012) → US1 (T013–T017) → US2 (T018–T026) → US3 (T027–T034) →
US4 (T035–T043) → US5 (T044–T050) → US6 (T051–T058) → Close (T059–T064).

**Gates**: T003 (V1, V12, V13) blocks T009, T011, T021; T004 (V10) blocks T028; T005 (V11) blocks T010.

**Task dependencies** (beyond phase order):

| Task | After |
| --- | --- |
| T007, T008 | T001 |
| T009 | T003 |
| T010 | T005 |
| T011 | T003 |
| T013 | T008 |
| T014 | T013 |
| T015 | T008 |
| T016 | T014, T015 |
| T018 | T009 |
| T019 | T007, T018 |
| T020 | T007, T018 |
| T021 | T009, T014, T003 |
| T022 | T019, T021 |
| T023 | T019, T020 |
| T024 | T019 |
| T025 | T019, T023, T024 |
| T027 | T010, T014 |
| T028 | T004 |
| T029 | T001 |
| T030 | T027 |
| T031 | T028 |
| T032 | T028, T029 |
| T033 | T027, T028 |
| T035, T036 | T008 |
| T037 | T009, T035, T036 |
| T038, T039 | T008, T014 |
| T040 | T027, T028, T035–T039 (same files) |
| T041 | T035, T036 |
| T042 | T037–T041 |
| T044 | T010 |
| T045, T046 | T044 |
| T047 | T006, T008, T019 |
| T048 | T044, T047 |
| T049 | T027 |
| T051–T053 | T043 (T051 also T050) |
| T054 | T043 (example from T047) |
| T055 | T050 |
| T056 | T051 |
| T057 | T001 |
| T058 | T051–T057 |
| T059 → T060 → T061; T062 after T060; T063 after T062; T064 after T063 | |

**Parallel opportunities** (different files, no open dependency):
- Setup: T002, T003, T004, T005, T006 together after T001.
- Foundations: T007, T008 together; T009 and T011 after T003; T010 after T005.
- US2: T020, T022, T023, T024 after their inputs (different files).
- US3: T029 any time after T001; T031 and T032 together.
- US4: T035/T036 together; T038/T039 together (different files: player/staff/economy vs staff/gate/module — **T038 and
  T039 both edit `StaffEndpoints.cs`; run them one after the other if one agent does both**); T041 with T037.
- US5: T045 with T046; T049 any time after T027.
- US6: T051–T057 together (different files).

**Shared files to sequence** (not [P] against each other): `GmCommandEndpointFactory.cs` (T013, T019), `GmOpenApi.cs`
(T011 if transformer path, T014, T021), `GmModule.cs` (T027, T028, T029, T039, T040), `StaffEndpoints.cs` (T038, T039,
T040), `IdentityModule.cs` (T015, T035, T040), `EconomyModule.cs` (T015, T023, T036, T038, T040),
`GmDocumentShapeTests.cs` (T016, T022, T042), `GmCommandValidationTests.cs` (T019, T022, T024, T025),
`CatalogCompositionTests.cs` (T033, T042), `docs/api/gm-openapi.json` (checkpoints only).

## Implementation strategy

1. **MVP (US1, T001–T017)**: the document describes every GM command; shippable alone (no behaviour change besides the
   document).
2. **Increment 2 (US2)**: validation with `errors`; the one behaviour change of the slice, guarded by unchanged framework
   tests.
3. **Increment 3 (US3)**: catalog in every environment and console bootstrap — what GM-2 needs to start.
4. **Increment 4 (US4)**: lists and options — what GM-3 needs.
5. **Increment 5 (US5)**: the kit call and the fixture — what games need for CI-01.
6. **Docs and close (US6, Phase 9).**

Each checkpoint (T012, T017, T026, T034, T043, T050) leaves the suite green and the committed document reviewed, so the
work can stop at any checkpoint with a consistent branch.

## Implementation risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| V1/V13 show the generator ignores parameter-target attributes or puts keywords on shared components | Wrong or duplicated keywords | A6: our transformer writes keywords on use sites from the same walk (T021 tests a shared nested type) |
| V11 fails (no provider outside a request) | No catalog outside Development | R4 fallback in T010; T005 proves it before T010 starts |
| V12 shows a runtime user of the HTTP JSON options | Enum change leaks into a runtime answer | Enum schema transformer instead (T011) |
| The validator changes an answer of a framework command | Silent behaviour change | T023/T024 limited to exact rules (research R13); existing tests unchanged; boundary tests |
| `OpenApiTests` regenerated without reading the diff | Unreviewed contract changes committed | Every checkpoint records a diff review Note against T001 (b) |
| `StaffEndpoints.cs`, `GmModule.cs` edited by parallel tasks | Merge conflicts | "Shared files to sequence" list above |
| Fixture gains the GM module and a fixture test pins routes or outputs | Red fixture suite late | T006 inventory before T047; T047 done-when includes every fixture test and WireGen outputs |
| Catalog generation slower than expected on hosts with many routes | Slow first console load | SC-004 opt-in test (T049); generation is once per process |
| Test-suite time grows because OpenAPI services are registered everywhere | CI time | T010 and T060 compare with T001 (a); lazy generation (D12) |
| Versions on `/me` rejected by the owner | Rework | Two init members, one test file (T029/T032); release note marks it pending |

## FR coverage

| FR | Tasks |
| --- | --- |
| FR-001, FR-002 | T014, T016 |
| FR-003 | T013, T014, T016 |
| FR-004, FR-005 | T014, T015, T016, T042 |
| FR-006 | T013, T014, T039, T042 |
| FR-007, FR-009 | T014, T027, T046 |
| FR-008 | T014, T030 |
| FR-010 | T003, T009, T021, T022 |
| FR-011, FR-012, FR-013 | T021, T022, T042 |
| FR-014 | T003, T011 |
| FR-020, FR-021 | T009, T018, T019, T022 |
| FR-022 | T007, T018, T025 |
| FR-023 | T018, T019 |
| FR-024 | T020, T023 |
| FR-025 | T019 |
| FR-026 | T007 |
| FR-027 | T023, T024 |
| FR-030, FR-031, FR-032 | T005, T010, T027, T030 |
| FR-033 | T010, T030, T033 |
| FR-034, FR-035 | T004, T028, T031, T033 |
| FR-036 | T029, T032 |
| FR-040, FR-041, FR-042 | T008, T038, T042 |
| FR-043, FR-044 | T035, T036, T041 |
| FR-045 | T037 |
| FR-046 | T015, T039, T042 |
| FR-047 | T040, T042 |
| FR-048 | T008 |
| FR-050, FR-051, FR-052 | T044, T045 |
| FR-053 | T046 |
| FR-054 | T047, T048 |
| FR-055 | T017, T026, T034, T043, T050 |
| FR-060 | T051, T052, T053 |
| FR-061 | T054, T059 |
| FR-062 | T055, T059 |
| FR-063 | T056 |
| FR-064 | T051, T056, T057 |
| FR-065 | T002, T058 |
| FR-090 | T003, T004, T005 |
| FR-091 | T001, T012, T060, T061, T063 |

| SC | Tasks |
| --- | --- |
| SC-001 | T016, T042 |
| SC-002 | T022 |
| SC-003 | T030, T033, T061 |
| SC-004 | T005, T049, T060 |
| SC-005 | T045 |
| SC-006 | T048 |
| SC-007 | T004, T031 |
| SC-008 | T057, T060 |

## Planning notes (gaps between spec, plan and code, resolved here)

1. **Committed document churn inside phases**: `OpenApiTests` compares the committed copy with the host, so it is red
   between a document change and its checkpoint. Resolved by regenerating only at checkpoints (T017, T026, T034, T043,
   T050) with a recorded diff review, never inside a task.
2. **Probe module isolation**: the SC-002 matrix needs a body with every attribute, but adding it to `GmTestHost` would put
   it in the committed framework document. Resolved: `ProbeGmModule` is added only through `GmTestHost.StartAsync(extra:)`
   in the tests that need it (T022).
3. **`ProducesGmPage<T>` signature**: design §5.2's `<TBuilder, T>` cannot be called with inference; the plan's one-type
   form on `RouteHandlerBuilder` (A3) works because every GM list is mapped with `MapDeclared` (returns
   `RouteHandlerBuilder`). Checked: `StaffEndpoints.cs:54-56`, `GmPlayerEndpoints.cs:125-170`, `EconomyModule.cs:36-45`.
4. **Request-id header on actions**: confirmed by reading: staff writes and gate writes go through
   `StaffEndpoints.Change` (`StaffEndpoints.cs:146-152`, `GateEndpoints.cs:109`) and key rotation calls it too
   (`GmModule.cs:231`); T039 re-checks at implementation time.
5. **`SetProgressionBody` is not an existing example** (design §19 wording): it has no annotation today; T024 makes it the
   example (spec "Design claims re-checked").
6. **Fixture staff for the GM round trip** (T048 (c)): the fixture's Local flavor has no `Gm:Bootstrap:Staff`; T006
   records whether `LocalFlavorOptions` can pass `Gm:Bootstrap:Staff:*` settings or whether the fixture host's test
   authentication (enabled with development tools, `Program.cs`) gives a GM client; T048 uses the one T006 confirms.
7. **`CatalogCompositionTests` updated twice** (T033 for the two gm routes, T042 for the two option routes) because the
   routes arrive in different phases; each phase ends green.
8. **SC-003 "bytes identical except conditional routes"**: Beta has no `gm.auth_login` (development tools off) and has SSO
   routes only when OIDC is configured; T030 compares Local and Beta documents after removing those paths.
9. **Auto-memory file** (H12): `gm-frontend-out-of-scope.md` lives in the Claude project memory, not in the repository;
   T057 updates it so later sessions do not reapply the outdated rule. It is not part of any commit.
10. **No `GetDocumentAsync(TestHost)` overload** (plan N4): `host.Services` suffices; the contract lists only the
    `WriteAsync`/`DiffAsync` `TestHost` overloads.
11. **Version bump/tag** stay outside the slice (D15); T058 writes "next tag proposed `v0.8.0`" and nothing bumps
    `GameServerFrameworkVersion` or the Unity package version (`VersionTests` would require both together).
