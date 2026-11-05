# Research: GM API contract (slice 017 gm-api-contract, GM-1)

**Date**: 2026-11-05 · **Spec**: [spec.md](spec.md) · **Plan**: [plan.md](plan.md)

Each item: decision, why, alternatives rejected, and what is checked at implementation time ([VERIFY]). Code facts are
from `main` = `59d37f6` and the pinned versions: .NET SDK 10.0.403, ASP.NET Core 10.0.14,
`Microsoft.AspNetCore.OpenApi` 10.0.14 (OpenAPI document `3.1.1`, `Microsoft.OpenApi` 2.x object model), System.Text.Json
of .NET 10.

## VERIFY items

Design §18 numbers V1–V10; V2–V9 belong to GM-2/GM-3. This slice checks V1 and V10 and adds V11–V13. Each check runs in
a throw-away project under the session scratch folder (never committed) unless it is a permanent test, and is recorded as
a `Note:` under its task. When a fact is wrong, the fallback is applied and the deviation recorded (contract, docs).

| Item | Fact to confirm | Fallback | Gate task | Used by |
| --- | --- | --- | --- | --- |
| V1 (design) | (a) The generator of `Microsoft.AspNetCore.OpenApi` 10.0.14 emits, for a type declared by `Accepts<T>("application/json")` on a minimal-API endpoint: `[Range]` → `minimum`/`maximum` (int, long, nullable int/long, double), `[StringLength]`/`[MinLength]`/`[MaxLength]`/`[Length]` → `minLength`/`maxLength` on strings and `minItems`/`maxItems` on `List<T>`/arrays, `[RegularExpression]` → `pattern`, `[Description]` → `description`, `[Required]` → `required` — for attributes on the property **and** on a positional record parameter without target. (b) `long` is `type: ["integer","string"]`, `format: int64`, `pattern` (as in the committed copy), `int` likewise with `int32`. (c) An enum property's schema (with the default HTTP JSON options) | Our schema transformer writes every keyword itself from the attribute walk (R2), overwriting what the generator emitted; FR-014 representation fixes go to R5 | T003 (**gate**) | T021, T022 |
| V10 (design) | A Public route declared by a module (`gm.auth_methods` in `GmRoutes`) is accepted by the startup endpoint check with the default `GameServer:PublicEndpointAllowlist` and with an explicit allowlist that does not list it; the game adds no configuration | List the template in `GmModule`'s `ModuleDescriptor.PublicEndpoints` (the legacy path still concatenated by `EndpointValidation.PublicTemplates`); still no game configuration | T004 (**gate**, permanent test) | T028, T031 |
| V11 (slice) | The GM document can be produced without an HTTP request: `IOpenApiDocumentProvider` (keyed by document name) is resolvable from the root provider in every environment once `AddOpenApi("gm")` is registered, and `GetOpenApiDocumentAsync()` yields the same document as `GET /gm/v1/openapi/gm.json`, except `servers` | Invoke the request delegate of the `MapOpenApi` endpoint (dev route) or of a private `MapOpenApi` mapping on a `DefaultHttpContext` (root-scope `RequestServices`, `MemoryStream` body, route value `documentName = gm`); the catalog keeps the bytes | T005 (**gate**) | T010 |
| V12 (slice) | The generator builds schemas from `IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>` (not `GameServerJson.Options`). Inventory of what differs: enum converter (GM wire: camelCase strings), naming policy (both camelCase), number handling (both Web defaults). Inventory of runtime users of the HTTP JSON options in the framework (typed body binding, `Results.Json` without options) | Register `ConfigureHttpJsonOptions` adding the same `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` when the inventory shows no runtime user that would change; otherwise a schema transformer that rewrites enum schemas to camelCase string enums | T003 (part of the gate) | T011 |
| V13 (slice) | System.Text.Json 10: for a positional record, `JsonPropertyInfo.AssociatedParameter` (with `AttributeProvider`) exposes the constructor parameter of a property; the OpenAPI schema transformer context exposes `JsonPropertyInfo` for property schemas (needed for `x-gm-options` and keywords) | Walker: match the primary constructor parameter by name (`GetConstructors()` with the most parameters, ordinal-ignore-case name); transformer: resolve the property from `context.JsonTypeInfo` of the parent schema and the schema's property name | T003 (part of the gate) | T009, T021 |

## R1 — Operation id, tags and the declaration of an endpoint

**Decision**: an `IOpenApiOperationTransformer` registered in `GmOpenApi.Add` resolves the declaration of each endpoint:
`RouteDeclarationMetadata.Declaration` when present, else `WireCatalog.FindRouteName(method, template)` (the 014 binding rule
for endpoints mapped by template, e.g. the string-pattern `MapGmCommand` overloads and `MapOpenApi`). It sets
`operationId = declaration.Name`. When the endpoint has no `ITagsMetadata`, `tags = [area]` where `area` is
`Name[..Name.IndexOf('.')]` (the whole name when there is no dot).

**Why**: route names are unique and stable across releases (014 rule), already used by the console design (override by
route name). The generator's default tag is the application name, which differs between the framework test host and a
game.

**Alternatives**: method-name based ids (the generator's default for named delegates, absent for lambdas); a new
`OperationId` field on `RouteDeclaration` (Core change, duplicate of `Name`).

**Notes**: an endpoint the catalog cannot name already fails the pre-serving endpoint check; the transformer leaves
`operationId` empty in that impossible case and the framework document test asserts every operation has one. The document
is only generated after the host started, so the catalog exists.

## R2 — DataAnnotations: one attribute walk for the document and the server

**Decision**: a new internal `GmBodyContract` (Platform.Web, `Endpoints/`) builds, per body type, from
`GameServerJson.Options.GetTypeInfo(type)`:
- for each `JsonPropertyInfo`: JSON name, CLR type, getter, and its `ValidationAttribute`s and `GmOptionsAttribute` from
  `AttributeProvider` (property) **and** `AssociatedParameter?.AttributeProvider` (positional parameter), de-duplicated
  by attribute type + values;
- nested contracts for properties whose type is a non-primitive object (not `string`, `JsonNode`, dictionary, enum,
  date/time, `Guid`) and for `IEnumerable<T>` of such objects, to depth 3 (the depth GM-3 renders, design §9.2).

The contract is cached per type (`ConcurrentDictionary`). `GmBodyValidator.Validate(object body, ReadOnlySpan<byte> json)`
walks it: for each property value runs each attribute's `GetValidationResult(value, context)` with
`MemberName = clrName`, `DisplayName = jsonName`; collects messages under JSON paths (`level`, `items[1].quantity`).
The schema transformer (R6) reads the same contract to write keywords.

**Why**: every GM body is a positional record. `Validator.TryValidateObject` (design §5.2) reads property attributes
only, while the document generator may show parameter attributes (V1a): the document and the server would disagree. One
walk over the JSON contract also yields JSON names (naming policy, `[JsonPropertyName]`) for `errors`.

**Alternatives**: `Validator.TryValidateObject` + an analyzer forcing `[property:]` (rule enforced by convention only;
nested objects not walked); `Microsoft.Extensions.Validation` / minimal-API validation of .NET 10 (works on bound
parameters; GM bodies are read by hand from bytes, and its error shape is `ValidationProblemDetails` without our `code`).

**Order inside `MapGmCommandCore`**: read bytes → deserialize (JSON error = 400 as today) → contract validation + the
existing reason rule, merged into one `errors` set → if any: `GameServerException(BadRequest, detail, errors)` →
`toDomain` → target validators → runner. Request id and route checks keep their place before the body read.

## R3 — `[Required]` on value types and property presence

**Decision**: for a `[Required]` property whose CLR type is a non-nullable value type, the validator checks presence in
the JSON (`JsonDocument.Parse` of the same bytes, walked along the contract path, property names matched with the
options' case-insensitivity); absent → the `RequiredAttribute` message. Nullable and reference types use the attribute as
is (null/blank → error).

**Why**: a missing `long` deserializes to `0`; `RequiredAttribute.IsValid(0)` is true. Without the presence check the
document would show `required` for a rule the server never applies. The parse runs only when such a property exists
(the contract knows), on a body already bounded by `GameServer:Limits:GmBodyBytes`.

**Alternatives**: `[JsonRequired]` (STJ throws `JsonException` → generic 400 without `errors`); ban `[Required]` on value
types with a startup line (surprising for authors).

## R4 — Producing the document without a request; canonical bytes; `servers`

**Decision**: `GmOpenApi.Add` is called in every environment. A new singleton `GmCatalogDocument` (Platform.Web,
`OpenApi/`) produces the bytes once per process: resolve the keyed `IOpenApiDocumentProvider` for `"gm"` (V11), get the
document, serialize it as OpenAPI 3.1 JSON, parse into `JsonNode`, write with
`new JsonSerializerOptions { WriteIndented = true }` and append `"\n"`, UTF-8 without BOM, LF. A document transformer
removes `servers`. The bytes are also the `GmOpenApiSnapshot` output.

**Why**: the same normalisation as the current `OpenApiTests` writes (`document.ToJsonString(indented) + "\n"`), so the
committed copy keeps its style (STJ's default encoder escapes the backtick as ```, as today). A host-derived
`servers` entry (`http://localhost/` in tests) would differ per environment and per request host.

**Alternatives**: copy the HTTP response of the dev route (needs Development); generate at startup (cost on every host
start, D12).

**Fallback (V11)**: build a `DefaultHttpContext` with `RequestServices` from a created scope, `Response.Body` =
`MemoryStream`, route value `documentName = "gm"`, and invoke the request delegate of an internal `MapOpenApi` mapping;
then the same normalisation.

## R5 — JSON options of the generator

**Decision**: confirm with V12 which options the generator uses. Expected: the HTTP `JsonOptions` (Web defaults, no enum
converter). If so, `GameServerBuilder` configures the HTTP JSON options with the enum converter of `GameServerJson`, after
the V12 inventory shows no framework endpoint whose runtime JSON changes; else an enum schema transformer.

**Why**: GM handlers read and write with `GameServerJson.Options` (camelCase enum strings); a document showing integer
enums would be wrong for every future game enum. Naming (camelCase) and number handling (`AllowReadingFromString`) already
match: the committed copy shows `type: ["integer","string"]` for `long` and `int` (FR-014 keeps it).

## R6 — Schema transformer: keywords, `required`, `reason`, `x-gm-options`

**Decision**: one `IOpenApiSchemaTransformer` in `GmOpenApi.Add`:
- **request-body set**: the types of `AcceptsMetadata.RequestType` on `/gm/**` endpoints (computed once from
  `EndpointDataSource` on first use);
- for a schema whose `context.JsonTypeInfo.Type` is in the set: `required` = JSON names with `[Required]` (+ `reason` when
  the type implements `IGmCommandBody`); for `reason`: `type: string`, `minLength: 1`, `maxLength: 500`, `pattern: "\\S"`;
- for each property schema (`context.JsonPropertyInfo`, V13): keywords from the R2 contract (`[Range]` → `minimum`/`maximum`
  as numbers; length attributes → string or collection keywords by the CLR type; `[RegularExpression]` → `pattern`;
  `[Description]` → `description`), overwriting the generator's values so the document equals what the server enforces;
  `[GmOptions]` → `x-gm-options` on the property schema or on its `items` for a collection.

**Why**: schema transformers see types, not usages; the set restricts the `required` rewrite to request bodies (response
records keep "always present"). Overwriting from the same contract makes V1's outcome irrelevant to correctness.

**Trap**: the generator may emit a `$ref` for a nested type and a property schema for the use site; keywords go on the
property schema (the use site), never on the shared component (another use could have other rules).

## R7 — Option routes; `ban-scopes` under `/gm/v1/players/`

**Decision**:
- `economy.resource_options` `GET /gm/v1/economy/resources` (`player.read`, Economy adapters): `ResourceCatalog.All`
  ordered by id → `GmOption(id, displayName)`.
- `identity.ban_scopes` `GET /gm/v1/players/ban-scopes` (`player.ban`, Identity adapters): an internal `BanScopes`
  singleton computed once from `ModuleRegistry.Commands` (first segment of each operation) + `login`, ordinal-sorted; used
  by the ban `toDomain` (today a local `groups` set in `IdentityModule.MapGmApi`) and by the option route.

**Why**: one definition per value set, so the list and the validation cannot disagree. The literal segment `ban-scopes`
wins over `{playerId}` in ASP.NET routing precedence, as `/gm/v1/players/search` and `/by-mapping/` already do; player
ids are generated and never equal `ban-scopes`.

**Alternatives**: query parameter on an existing route; options embedded in the document (`enum`), which would freeze
deployment-dependent values into the committed snapshot.

## R8 — Catalog route, cache, ETag

**Decision**: `gm.console_catalog` is declared in `GmRoutes` and mapped in `GmModule.MapGmApi`, excluded from the
description. The handler asks `GmCatalogDocument.GetAsync()`: a `Lazy<Task<CatalogBytes>>` (execution and publication);
a faulted generation is not cached (the next request retries) and is logged once per attempt. `CatalogBytes` =
`(byte[] Bytes, string ETag)` with `ETag = "\"" + Convert.ToHexStringLower(SHA256(bytes))[..32] + "\""`. `If-None-Match`:
split by comma, trim, strip `W/`, compare ordinal; `*` matches. Responses carry `ETag` and `Cache-Control: private,
no-cache`. One Information log line on generation: operations, bytes, elapsed ms.

**Why**: the document only changes with a deploy; console revalidation is one 304. `private` because it is behind a GM
session.

## R9 — Tests that pin what this slice changes (inventory to extend)

| Test | Pin | Change |
| --- | --- | --- |
| `tests/GameServer.Gm.Tests/OpenApiTests.cs` | committed copy via `JsonNode.DeepEquals`; per-endpoint permission/CSRF | Uses `GmOpenApiSnapshot`; adds extension checks |
| `tests/SampleGame.Tests/CatalogCompositionTests.cs:27-40` | every framework route name | + `gm.auth_methods`, `gm.console_catalog`, `economy.resource_options`, `identity.ban_scopes` |
| `tests/SampleGame.Tests/CatalogCompositionTests.cs:75` | dev document route mapped only with development tools | unchanged; add: catalog route mapped whenever GM is mapped |
| `tests/GameServer.Platform.Couchbase.IntegrationTests/SampleHostFlavorTests.cs:35, 66` | dev route 200 Local / 404 Beta | unchanged; add: catalog route 401 anonymous in every flavor |
| `tests/GameServer.Gm.Tests/GmVersionEndpointTests.cs:57+` | Public/player routes disclose no version | + `gm.auth_methods`; new test `/me` versions |
| `tests/GameServer.Platform.Web.Tests/DeclaredRouteMappingTests.cs:92-132` | platform routes and the dev route | unchanged (no platform route added) |
| `tests/GameServer.Platform.Web.Tests/LateEndpointValidationTests.cs:114` | dev route condition line | unchanged |
| `tests/GameServer.Architecture.Tests/TestKitArchitectureTests.cs` | kit internals allow-list | only if the snapshot uses an internal (plan: it does not) |
| GM tests asserting `400` bodies of GM commands | status/code | `errors` added, status/code unchanged |

The implementation re-greps (`src/`, `tests/`, `templates/`, `tools/`, `modules/`, `docs/`; never `samples/` as a root) for
`gm-openapi`, `GmOpenApi`, `GmMeResponse`, `"gm.` lists and `PublicEndpointAllowlist` before the first change (T006).

## R10 — Consumer fixture as a game with GM

**Decision**: `Fixture.Host` adds `GmModule`; `Fixture.Game` declares one GM command `fixture.note_set`
(`POST /gm/v1/players/{playerId}/note`, permission `fixture.note.edit`, touches the fixture's notes aggregate, one
audit) with body `SetNoteBody([property: Required, StringLength(140)] string? Text, [property: GmOptions("fixture.note_presets")] string? Preset, string? Reason)`,
and one option route `fixture.note_presets` (`GET /gm/v1/fixture/note-presets`, `fixture.note.edit`). A test
`FixtureGmOpenApiTests` runs `GmOpenApiSnapshot.DiffAsync(factory.Services, "<fixture>/docs/api/gm-openapi.json")` on
`LocalFlavorFactory<Program>` and a GM round trip (validation 400 with `errors`, commit 200).

**Traps to check first (T006)**: fixture tests that pin route lists or the wire-contract export (`FixtureCatalogTests`),
WireGen committed outputs of the fixture (GM surface generates nothing; `CommittedOutputTests` must stay green), the
fixture's Local config (GM needs no bootstrap staff to start), `Fixture.Host.csproj` project references through the
framework path, permissions catalog of the fixture module.

## R11 — `/me` versions

**Decision**: `GmMeResponse` keeps its positional members and gains `public string FrameworkVersion { get; init; } = "";`
and `GameVersion` likewise; `Me(http)` and `LoginAsync` set them from `GameServerRuntimeInfo` (the values of
`GmVersionResponse`). JSON order: the two new members last.

**Why**: additive for JSON and for C# callers (none outside the module).

## R12 — Presentation API placement and the `[GmOptions]` startup check

**Decision**: `GmPresentation.cs` in `src/GameServer.Platform.Web/Endpoints/` holds the enum, metadata records, attribute,
option records and the extension methods. The startup check is a function in `Validation/` called by
`EndpointValidation.Run` (pre-serving, joins the aggregated report): for each `/gm/**` endpoint with `AcceptsMetadata`,
walk the R2 contract; for each `GmOptionsAttribute`, require a catalog route with that name, method `GET`, surface
`GmApi`, whose condition holds on this host. Line:
`[endpoint POST /gm/v1/players/{playerId}/resources/adjust] body AdjustResourceBody property 'resource' names option route 'economy.resource_option', which is not a GM GET route of this host. Fix: declare the route (RouteDeclaration.Get(..., RouteSurface.GmApi)) or correct the name in [GmOptions] (FR-045).`

**Why**: ARC-01 — the attribute is used on adapter bodies, which already reference Platform.Web; Core and business
projects stay free of it.

## R13 — Framework body annotations (exact rules only)

| Body (file) | Annotation | Existing rule it states |
| --- | --- | --- |
| `AdjustResourceBody.Resource` (`EconomyModule.cs:67`) | `[property: Required]`, `[property: GmOptions("economy.resource_options")]` | `IsNullOrWhiteSpace` → 400 (`EconomyModule.cs:32-33`); `RequiredAttribute` refuses null, empty and white space |
| `BanBody.Scope` (`IdentityModule.cs:332`) | `[property: GmOptions("identity.ban_scopes")]` | values checked against the same set (R7); count/emptiness rules stay imperative (distinct-then-count is not expressible) |
| `MaintenanceBody.Message` (`GateEndpoints.cs:18`) | `[property: StringLength(500)]` | `Message.Length > 500` → 400 (`GateEndpoints.cs:47-50`) |
| `SetProgressionBody` (`SampleGameModule.cs:119`) | `Level`: `[property: Range(1, int.MaxValue)]`; `Xp`: `[property: Range(0, long.MaxValue)]`; `Stage`: `[property: Range(1, int.MaxValue)]` | `Level < 1 \|\| Xp < 0 \|\| Stage < 1` → 400 (`SampleGameModule.cs:49-52`); "at least one" stays |
| `reason` of every `IGmCommandBody` | schema only (FR-012); server rule unchanged | `GmCommandEndpointFactory.cs:172-175` |

Others (`CreateStaffBody`, `StaffGrantBody`, `MinClientVersionBody`, `UnbanBody`, `CancelDeletionBody`) keep their
imperative checks: trimming/lower-casing before the check or cross-field rules make an exact annotation impossible.
The doc of each module lists them.

## R14 — Summaries and typed confirmation

**Decision**: `WithSummary("…")` (ASP.NET `OpenApiRouteHandlerBuilderExtensions`) on every framework GM mapping: 29 `gm.*`
routes after the slice (minus `gm.ui` and the catalog, excluded), 3 identity commands + `identity.ban_scopes`, 2 economy
routes + `economy.resource_options`, and the sample's `progression.set`. `WithGmConfirm(GmConfirm.Typed)` on the five
Q7 routes. A framework test lists operations without summary (must be none for framework-owned routes); a game route
without summary is valid.

## Code facts used by the plan

- `GmOpenApi.Add/Map` (`src/GameServer.Platform.Web/OpenApi/GmOpenApi.cs:25-63`): one operation transformer for
  `x-gm-permission`/`x-gm-csrf`/`4XX`/`5XX`; `ShouldInclude` = relative path starts with `gm/`.
- `GmCommandEndpointFactory.MapGmCommandCore` (`:147-203`); `BuildResponse` (`:205-220`); `MaxReasonLength = 500` (`:71`).
- `ErrorResponseWriter.WriteAsync` (`src/GameServer.Platform.Web/Errors/ErrorResponseWriter.cs:24-90`): Problem Details
  members `type, title, status, code, codeNumber, requestId, retryable, retryAfterMs?, details?`.
- `ExceptionMiddleware` (`:27-41`) writes `ex.Code, ex.RetryAfterMs, ex.Details` for `GameServerException`.
- `GmHttp.ReadJsonAsync<T>` (`src/GameServer.Platform.Web/Endpoints/GmHttp.cs:54-67`), `ReadPageRequest` (`:22-32`),
  `PagedQuery.MaxPageSize` (200).
- `GmRoutes.All` (27 routes, `GmRoutes.cs:77-83`); `GmModule.MapGmApi` (`GmModule.cs:116-153`); `GmMeResponse`
  (`GmModule.cs:39`); `Me` (`:264-273`); local login answer (`:199-205`).
- `IdentityModule.MapGmApi` (`IdentityModule.cs:179-215`); `EconomyModule.MapGmApi` (`EconomyModule.cs:28-46`);
  `EconomyRoutes` (`src/GameServer.Modules.Economy/EconomyRoutes.cs`).
- `EndpointValidation.PublicTemplates` (`EndpointValidation.cs:48-58`); `EndpointSecurityValidator` allowlist (`:28, 58-63`).
- `WireCatalog.FindRouteName(method, template)` (`src/GameServer.Platform.Web/Catalog/WireCatalog.cs:91`).
- `GameServerRuntimeInfo` (`src/GameServer.Platform/GameServerRuntimeInfo.cs`), `GameServerVersion.FrameworkInformational`.
- TestKit: no test framework (`TestKitArchitectureTests`), `LocalFlavorFactory<T>` (WebApplicationFactory), `TestHost`.
