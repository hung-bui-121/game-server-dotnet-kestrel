# Release notes draft — slice 017 gm-api-contract (GM-1)

Framework `0.7.0` + branch `017-gm-api-contract` (unreleased). First slice of the GM tool track (GM-1 of GM-1..GM-4, design
`AgentMemories/features/gm-tool/01_design.md` §19). Started in T002 (2026-11-05, framework `e5851ec`); extended by the story
checkpoints T017, T026, T034, T043 and T050; final pass in T058.

**Backend contract only.** No cookie, CORS, SSO, hosting or frontend change (GM-2); no proto, Couchbase or Unity change; GM
JSON changes are additive. **Breaking:** BC-1 to BC-9 (table below), each additive or documentation-level with a mechanical
fix.

## US1 — every GM command is described in the GM document

- **Operation id = route name** of every operation (`RouteDeclarationMetadata`, else the route catalog by method + template),
  unique in the document; **tag = area** (route-name segment before the first dot, D1) unless the endpoint sets
  `WithTags(...)`. The document's top-level `tags` list is rebuilt from the operations (before: `GmModule` and the test host's
  application name).
- **GM commands** (every `MapGmCommand` overload) now carry `Accepts<TBody>("application/json")` and the new
  `GmRequestIdMetadata`: the document shows the request body (`$ref` to the body record), every template parameter as a
  required path parameter (`playerId` with the `^[A-Za-z0-9_-]{1,64}$` rule of `GmHttp.RequirePlayerId`, also on the GM
  queries that already listed it), the required `X-Request-Id` header (`format: uuid`) and `200` = `GmCommandResponse`.
  No request handling changed.
- **`x-gm-*` keys** on every operation: `x-gm-kind` (`command` / `query` for GET / `action`), `x-gm-operation` (commands),
  `x-gm-target` (`player` when the template has `{playerId}`, else `global`), `x-gm-confirm` (`simple` / `typed`, non-GET
  only), `x-gm-paged` (paged lists, US4). `x-gm-permission` and `x-gm-csrf` are unchanged.
- **Typed confirmation** (`WithGmConfirm(GmConfirm.Typed)`) on `identity.ban`, `identity.cancel_deletion`,
  `economy.adjust` (the two Q7 actions follow in US4).
- **No `servers` entry** (D4); `info.description` gains one sentence naming the `x-gm-*` keys.
- **Committed document** (`docs/api/gm-openapi.json`, framework test host): 33 operations; nothing removed except `servers`;
  35 changed members (33 operation tags, the top-level tag list, the info sentence); 139 added members (33 `operationId`,
  33 `x-gm-kind`, 33 `x-gm-target`, 15 `x-gm-confirm`, 5 command bodies with their parameters and `x-gm-operation`, the 5
  body schemas `BanBody`, `UnbanBody`, `CancelDeletionBody`, `AdjustResourceBody`, `SetProgressionBody`, 5 `playerId`
  patterns on existing GET parameters).
- **BC-1** in detail: generated client names that used tags or operation ids change; codegen keyed by path is unaffected.
  This is the MVP checkpoint of the slice: the document alone changed, no runtime answer did.

## US2 — GM body rules declared once, enforced by the server

- **Supported annotations** (`System.ComponentModel.DataAnnotations`): `[Required]`, `[Range]`, `[StringLength]`, `[MinLength]`,
  `[MaxLength]`, `[Length]`, `[RegularExpression]`, plus `[Description]` (`System.ComponentModel`, document only). Write them
  on the property (`[property: Range(1, 10)]`) or on the positional record parameter (`[Range(1, 10)]`): **both are read** by
  one walk over the System.Text.Json contract of the body (D6), nested objects and items of object lists included (depth 3).
  The same walk writes the document and runs on the server, so they cannot disagree (A6; an SC-002 matrix test checks 30
  boundary cases on a probe body).
- **Server**: every `MapGmCommand` validates `TBody` after the JSON read and before `toDomain`, the target validators and the
  runner; `GmHttp.ReadJsonAsync<T>` validates annotations too (D8; no `reason` rule there, framework actions keep theirs).
  `[Required]` on a non-nullable value type (`long Delta`) checks that the JSON had the property (an absent number reads as 0,
  D7; names matched without case like the GM JSON options). Nullable = optional: `[Range]` on `null` passes. Custom
  `ValidationAttribute`s are enforced but not shown in the document; cross-field rules stay in `toDomain` (400 without
  `errors`).
- **Answer** (BC-2): the existing `400 BAD_REQUEST` Problem Details plus `errors` — JSON path (`level`, `box.label`,
  `items[1].quantity`) → messages, ordinal key order, one answer for every failing field, the `reason` rule merged
  (`reason is required (1..500 characters, not only white space).`, A5). Messages are the attributes' English messages, no
  exception text. Carried by the new init property `GameServerException.Errors` (Core, BC-7), written by
  `ErrorResponseWriter` for Problem Details only (the Protobuf `ErrorEnvelope` is unchanged). Bad JSON, a missing
  `X-Request-Id` and `toDomain` rules keep today's shape. Counted and logged as every `BAD_REQUEST`
  (`http_errors_total{code="BAD_REQUEST"}`, Information line `Request rejected with BAD_REQUEST: Body validation failed: <keys>.`).
- **Document** (D5): request-body `required` lists exactly `[Required]` properties plus `reason` of `IGmCommandBody` types
  (`null` dropped from their `type`); `reason` = `string`, `minLength: 1`, `maxLength: 500`, `pattern: "\\S"`; keywords
  `minimum`/`maximum` (a `long.MaxValue` bound written exactly), `minLength`/`maxLength` (strings), `minItems`/`maxItems`
  (collections), `pattern`, `description`; written on use sites, never on a shared component. Response schemas keep the
  generator's `required`. Enums are `type: string` + `enum` = the camelCase strings `GameServerJson.Options` writes, written by
  the schema transformer; the host's HTTP JSON options are not changed (review r1 m1).
- **Framework bodies** (BC-3, exact rules only, A10): `AdjustResourceBody.Resource` `[Required]` (null/empty/white space →
  `errors.resource`, same 400 as before), `MaintenanceBody.Message` `[StringLength(500)]` (501 → `errors.message`, same 400).
  `SetProgressionBody` (sample) is the reference example: `[Range(1, int.MaxValue)]` level/stage, `[Range(0, long.MaxValue)]`
  xp; its `toDomain` keeps only "set at least one of level, xp, stage". Bodies whose rules trim, lower-case or combine fields
  keep imperative checks (`CreateStaffBody`, `StaffGrantBody`, `MinClientVersionBody`, `UnbanBody`, `CancelDeletionBody`,
  `GmLoginRequest`).
- **How to find affected GM bodies in a game** (BC-3, BC-9): search the game's records implementing `IGmCommandBody`, and the
  types read with `GmHttp.ReadJsonAsync<T>`, for `[` before a parameter or `[property:` (annotations) and for value-type members
  of GM bodies (`int`, `long`, `bool`, `double`, enum, `DateTimeOffset` without `?` and without a default). zh-pvp has none today.
- **Committed document**: `required` of 11 request bodies rewritten (6 lose the list: `CreateStaffBody`, `GmLoginRequest`,
  `MaintenanceBody`, `MinClientVersionBody`, `StaffGrantBody`, `StaffReasonBody`), `reason` rule on the 5 command bodies,
  `maxLength: 500` on `MaintenanceBody.message`, ranges on `SetProgressionBody`; nothing else.

## US3 — catalog, sign-in methods and versions for a console

- **GM document in every environment** (BC-6): `GameServerBuilder` registers `GmOpenApi.Add` and the new public singleton
  `GmCatalogDocument` (`GameServer.Platform.Web.OpenApi`) outside the development-tools block. The document is generated on
  the first catalog request or kit call (never at host start, D12) through the keyed `IOpenApiDocumentProvider` (V11: no HTTP
  request needed), and kept for the process: canonical bytes = OpenAPI 3.1 JSON re-written indented, LF, one final newline,
  UTF-8 without BOM; strong ETag = `"` + 32 hex of SHA-256 + `"`. Concurrent first calls share one generation; a failed one
  is kept for 30 s (`GmCatalogDocument.FailureHold`, every caller gets the same failure), then the next call retries. One Information line per generation (category `GameServer.Gm.Catalog`):
  `GM catalog generated: {Operations} operations, {Bytes} bytes, {ElapsedMs} ms.` The Development/Test route
  `platform.gm_openapi` (`/gm/v1/openapi/gm.json`) is unchanged and serves the same document.
- **`gm.console_catalog`** `GET /gm/v1/console/openapi.json` (Gm, `gm.access`, excluded from the document): `200`
  `application/json` with the bytes, `ETag`, `Cache-Control: private, no-cache`; `304` without a body when `If-None-Match`
  matches (comma lists, `W/` tags compared by value, `*`). Every `gm.access` staff gets the same bytes (no filtering, Q5). No
  version, no `servers`, no configuration value in the document (FR-008).
- **`gm.auth_methods`** `GET /gm/v1/auth/methods` (Public, GM timeout and limiter policies): `{ "local": bool, "sso": bool }`
  — `local` = the local login is mapped (development tools), `sso` = `Gm:Oidc` configured (not IdP reachability). Nothing
  else. **V10 result:** a Public route declared by a module is allowlisted through its declaration; it starts with the
  default `GameServer:PublicEndpointAllowlist` and with an explicit one that does not list it (permanent test
  `PublicDeclarationAllowlistTests`); games add no configuration.
- **Versions on `/me`** (BC-4, D9): `GmMeResponse` gains `frameworkVersion` (informational, as `ops/version`) and `gameVersion`
  as its last members, on `GET /gm/v1/me` and on the local login answer. Public and player routes still carry no version
  (`auth/methods` added to the OBS-03 test).
- **BC-5 (part)**: route names `gm.auth_methods` and `gm.console_catalog` (`gm.*` routes 27 → 29). Both are mapped whenever the
  GM API is mapped, in every flavor (Local/Dev/Beta/Uat/Live, in-memory composition test and Couchbase flavor test).
- **SC-003**: Beta and Local serve the same document except their conditional routes (local login only with development
  tools; SSO only with `Gm:Oidc`).
- **Committed document**: adds `gm.auth_methods` with the `GmAuthMethods` schema and the two `GmMeResponse` members; 34
  operations.

## US4 — lists, options, confirmations and summaries

- **Presentation API** (`GameServer.Platform.Web.Endpoints`, metadata only, FR-048): `WithGmConfirm(GmConfirm)`,
  `ProducesGmPage<T>()` (on `RouteHandlerBuilder`, one type argument, A3), `WithGmQueryParameter(name, required, maxLength,
  minLength)`, `WithGmRequestId()`, `[GmOptions("<route name>")]`, `GmOption(Value, Label)`, `GmOptionList(Items)`; metadata
  records `GmConfirmMetadata`, `GmPagedMetadata`, `GmQueryParameterMetadata`, `GmRequestIdMetadata`.
- **Paged lists** (GM-06 unchanged): `gm.player_search` (`GmPlayerSearchItem`, plus `displayName` required 1..64),
  `gm.player_receipts` (`GmReceiptSummary`), `gm.staff_list` (`StaffView`) typed; `gm.player_audit`, `gm.staff_audit` (D11)
  and `economy.ledger` free-form (`JsonObject` items: stored documents returned as they are, D10). Each documents `pageSize`
  (required, integer 1..200) and `cursor` (optional) and carries `x-gm-paged: true`. Handlers unchanged.
- **Option routes** (BC-5 complete): `economy.resource_options` `GET /gm/v1/economy/resources` (`player.read`; the resource
  catalog sorted by id, label = display name) and `identity.ban_scopes` `GET /gm/v1/players/ban-scopes` (`player.ban`;
  `login` + every command group, label = value). `BanScopes` is one singleton used by the ban command's validation and the
  route (A9), so the list and the accepted values cannot differ. `[GmOptions]` on `AdjustResourceBody.Resource` and on the
  items of `BanBody.Scope` (`x-gm-options`). The server still validates every submitted value.
- **Startup check** (BC-8, D13): a `[GmOptions]` on the body of a mapped GM endpoint must name a catalog route with method
  GET, surface GmApi, mapped on the host (not service-only, condition holding); otherwise one line per (endpoint, property)
  joins the startup report: `[endpoint POST /gm/v1/players/{playerId}/resources/adjust] body AdjustResourceBody property 'resource'
  names option route '…', which is not a GM GET route of this host. Fix: declare the route (RouteDeclaration.Get(...,
  RouteSurface.GmApi)) or correct the name in [GmOptions] (FR-045).` Nested properties are named by JSON path (`items[].pick`).
- **Confirmation (Q7)**: `typed` on `identity.ban`, `identity.cancel_deletion`, `economy.adjust`, `gm.ops_rotate_keys`,
  `gm.ops_gate_maintenance`; `simple` on every other mutation.
- **Request id header** (D3): `WithGmRequestId()` on the eight framework actions that refuse a request without `X-Request-Id`
  (they all go through `StaffEndpoints.Change`): `gm.staff_create`, `gm.staff_grant`, `gm.staff_disable`, `gm.staff_enable`,
  `gm.staff_revoke_sessions`, `gm.ops_gate_maintenance`, `gm.ops_gate_min_client_version`, `gm.ops_rotate_keys`.
- **Summaries** (R14): every framework GM route (modules `gm`, `identity`, `economy`, the sample's `progression.set` and the
  counter's GM read) has a one-sentence English `WithSummary`.
- **Committed document**: 36 operations (+ the two option routes); added `summary` everywhere, `x-gm-paged` + page/query
  parameters on six lists, typed 200 responses on the five lists that had none, `GmOption`, `GmOptionList` and four page
  schemas, `x-gm-options` ×2, `X-Request-Id` on five more actions; `x-gm-confirm` `typed` on rotate keys and maintenance.

## US5 — snapshot test for games (CI-01)

- **Kit call** `GmOpenApiSnapshot` (`GameServer.TestKit`, no test framework, only public framework API):
  `GetDocumentAsync(IServiceProvider)`, `WriteAsync(IServiceProvider, path)`, `DiffAsync(IServiceProvider, path)` and
  `WriteAsync`/`DiffAsync` overloads for `TestHost` (A4: over the host's services, so a game uses its real host through
  `LocalFlavorFactory<Program>`). `WriteAsync` creates the folder and replaces the file through a temporary one; `DiffAsync`
  returns `""` when equal (line ends ignored: a CRLF checkout is equal), else
  `GM OpenAPI document differs from <path> at line <n>:` + both lines (cut at 200 characters, `(end of file)` for the shorter
  side) + how to rewrite; a missing file is a message, not an exception.
- **Framework test**: `tests/GameServer.Gm.Tests/OpenApiTests.cs` keeps its per-endpoint permission/CSRF checks and compares
  the committed `docs/api/gm-openapi.json` through the kit (`UPDATE_OPENAPI=1` writes).
- **Fixture (D14)**: `tests/ConsumerFixture` composes `GmModule` like a game (`Fixture.Host` references
  `GameServer.Modules.Gm.Adapters`), adds the GM command `fixture.note_set` (`POST /gm/v1/players/{playerId}/note`,
  permission `fixture.note.edit`, body `SetNoteBody([Required, StringLength(140)] Text, [GmOptions("fixture.note_presets")]
  Preset, Reason)`), the option route `fixture.note_presets` (`GET /gm/v1/fixture/note-presets`), the committed
  `tests/ConsumerFixture/docs/api/gm-openapi.json` and `FixtureGmOpenApiTests` (snapshot, a changed bound reported at its
  line, a GM round trip with `errors`). WireGen outputs are unchanged (the GM surface generates nothing for Unity).
- **Game migration** (zh-pvp): after the bump add this test over the game's own host and commit the document:

  ```csharp
  [Fact]
  public async Task The_committed_gm_document_is_what_the_game_host_serves()
  {
      await using var factory = new LocalFlavorFactory<Program>();
      var committed = Path.Combine(RepositoryRoot(), "docs", "api", "gm-openapi.json");
      if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
      {
          await GmOpenApiSnapshot.WriteAsync(factory.Services, committed);
      }

      (await GmOpenApiSnapshot.DiffAsync(factory.Services, committed)).ShouldBeEmpty();
  }
  ```

- **Performance (SC-004, opt-in `GAMESERVER_PERF=1`, `GmCatalogPerformanceTests`)**: first catalog request 195 ms
  (generation included), warm p95 0.32 ms over 1 000 in-process requests (budgets 1 s and 5 ms).

## Verification items (FR-090)

- **V1** (generator keywords, `Microsoft.AspNetCore.OpenApi` 10.0.14): only `[property: …]` attributes become keywords;
  positional-parameter attributes are ignored, `[Required]` is never used, `required` lists every positional parameter, a
  `[Range]` on a nested-object property lands on the shared component, `long.MaxValue` loses precision. **Fallback applied**:
  the schema transformer writes every keyword from the same walk as the server and rewrites `required` (by design, A6).
- **V10** (Public module route vs allowlist): holds — declared module routes are allowlisted with the default and with an
  explicit `GameServer:PublicEndpointAllowlist`; permanent test `PublicDeclarationAllowlistTests`.
- **V11** (document without a request): holds — keyed `IOpenApiDocumentProvider` (`OpenApiDocumentService`) from the root
  provider, no `servers` without a request, JSON equal to the dev route minus `servers`.
- **V12** (generator JSON options): HTTP `JsonOptions` (Web defaults, no enum converter); only runtime user = the counter's GM
  read (no enum). First applied as an enum converter on the HTTP JSON options; review r1 m1 replaced it by an enum description
  in the schema transformer, so the host's options are unchanged.
- **V13** (positional parameters in the JSON contract): `JsonPropertyInfo.AssociatedParameter.AttributeProvider` and
  `context.JsonPropertyInfo` both available; no fallback.

## Docs touched

`docs/pcr/gm.md` (ownership header, endpoints, 29 routes, Public allowlist, new section "GM document và hợp đồng console"),
`docs/pcr/identity.md`, `docs/pcr/economy.md`, `docs/pcr/sample-game.md` (option routes, annotated bodies, typed confirmation),
`docs/pcr/platform.md` (§14 note, new §23: GM document in every environment, `errors`, presentation API, startup line,
kit, BC table), `docs/guides/module-authoring.md` + `templates/module/README.md` (GM route for the generic console),
`docs/guides/consumer-guide.md` (§14 GM API contract, §11 bump step, §5 kit call), `docs/html/02-them-command.html` §8,
`docs/html/01-gioi-thieu.html`, `docs/html/03-deploy.html` (ownership and "runtime OpenAPI only Dev/Test" lines, stamps),
`AgentMemories/features/p05-speckit/00_summary.md` (ownership lines). The Claude auto-memory entry `gm-frontend-out-of-scope.md`
(outside the repository) is the owner's.

## Breaking changes

| # | Change | Who is affected | How it surfaces | Fix |
| --- | --- | --- | --- | --- |
| BC-1 | The GM document changes shape: `operationId` everywhere, tags = area instead of the host's application name, request bodies/path/header parameters on GM commands, `required` of request bodies reduced to the real rules, `x-gm-*` keys, no `servers` entry | Tools that generate clients from or diff `docs/api/gm-openapi.json`; a game that already commits a copy | Diff in the committed file; generated client names change (tag/operation id based) | Regenerate and commit; codegen keyed by path is unaffected |
| BC-2 | Problem Details of a GM `BAD_REQUEST` from body validation (and the `reason` check) gain the member `errors` | GM clients that reject unknown members (none known) | New member | Ignore or show it |
| BC-3 | A GM body that carries DataAnnotations is now validated against them before `toDomain` (`MapGmCommand` and `GmHttp.ReadJsonAsync`). Framework bodies get only annotations they already enforced | A game whose GM body **already** carries annotations for another purpose (ignored before) | 400 `BAD_REQUEST` with `errors` where the command used to run | Remove the annotation or accept the rule (see "How to find annotated GM bodies") |
| BC-4 | `GmMeResponse` (on `/me` and on the local login answer) gains `frameworkVersion`, `gameVersion`: versions become visible to every staff with `gm.access`, not only `ops.read` | Owner policy on version disclosure; clients asserting the exact member set of `/me` | Two new members | None (additive); owner may restrict (owner question 1) |
| BC-5 | The route catalog gains four routes: `gm.auth_methods` (Public), `gm.console_catalog`, `economy.resource_options`, `identity.ban_scopes` | Tests or tools of a game that pin the full list of route names or of Public routes; a game route at one of the new templates | Pinned-list test fails; a template clash fails startup (014 check) | Add the names; rename the game route |
| BC-6 | The OpenAPI services are registered in every environment | Hosts that replaced or removed them (none known) | `IOpenApiDocumentProvider` registered outside Development/Test | None |
| BC-7 | `GameServerException` gains the optional init member `Errors` (field errors) | None at compile time | — | — |
| BC-8 | A `[GmOptions]` naming no declared GM GET route fails startup | New code only (new attribute) | One startup report line | Fix the route name or declare the route |
| BC-9 | Every non-nullable value type in a GM body without a default is required: absent → 400 `BAD_REQUEST` with `errors["<path>"]` (before: read as its default, e.g. `0`/`false`); the document lists it in `required` | A game GM body with value-type members (`int`, `long`, `bool`, enum) its clients may omit | 400 with `errors` where the call used to run | Make the member nullable (`int?`) or give its constructor parameter a default (`int Weight = 3`) to keep it optional |

Also from the review (T063): a GM command or action sent with a Content-Type its `Accepts` does not list (`text/plain`, form)
answers **415 `UNSUPPORTED_MEDIA_TYPE`** as before (routing's own 415 endpoint has no security profile and was answered 403;
`ProfileEnforcementMiddleware` now maps it like the 405 endpoint). The staff, gate and rotate-key bodies implement
`IGmCommandBody`, so the document lists their `reason` as required with the server rule (runtime unchanged); a `[Required]`
string shows `minLength: 1` and `pattern: "\\S"`; a `[Required]` object property is not nullable at its use site.

## Review round 1 (tech-lead) — changes

- **Pattern** (M1): `[RegularExpression("p")]` is written `pattern: "^(?:p)?$"` — .NET matches the whole value and lets `""`
  through, a JSON Schema `pattern` searches. Write `p` so it means the same in .NET and JavaScript regex (the console checks
  with JavaScript).
- **Enums** (m1): described by the schema transformer as the camelCase strings `GameServerJson.Options` writes; the host's
  HTTP JSON options stay as they were (the planned enum converter and its BC-9 are gone). The counter's GM read writes no enum.
- **Option-route permission** (m2): startup check — an option route needs `gm.access` or the permission of the endpoint whose
  body names it; one line otherwise. `economy.resource_options` now requires **`gm.access`** (was `player.read`; resource names
  are not sensitive). `identity.ban_scopes` stays `player.ban`.
- **Presence rule** (m3, m6; narrowed in r2 M1; BC-9) — **behaviour change**: every non-nullable value type of a GM body
  (`int`, `long`, `bool`, enum, nested ones included) that is read back from the JSON (setter or constructor parameter) and has
  no default value is required: the document lists it in `required` and the server answers 400 with
  `errors["<path>"] = ["The <name> field is required."]` when it is absent (before: read as the default). Nullable, a defaulted
  constructor parameter (`int Weight = 3`) or a computed get-only member = optional. Framework bodies affected:
  `AdjustResourceBody.Delta` (now also `[property: Required]`) and `MaintenanceBody.Enabled` (a missing `enabled` is a 400, not
  "maintenance off").
- **`economy.adjust` without `delta`** (m3; wording fixed in r2 m3): before, a missing `delta` read as `0` and the command
  answered **HTTP 200 with a stored `INVALID_QUANTITY` rejection** (receipt kept, replayed with the same answer on a retry of the
  same request id); now it is **`400 BAD_REQUEST` with `errors.delta`**, nothing stored. The error code on that path changes
  from `INVALID_QUANTITY` to `BAD_REQUEST`; `delta: 0` sent explicitly is still `INVALID_QUANTITY`.
- **Attribute probe** (m4): at startup each validation attribute of a GM body runs once; one that cannot run (invalid regex,
  unconvertible `[Range]` operand, illegal lengths) is one startup line instead of a 500 on the first request.
- **Catalog failure** (m5): a failed generation is kept 30 s before the next attempt.

## Review round 2 (tech-lead) — changes

- **Presence rule narrowed** (M1, BC-9): only value types read back from the JSON (setter or constructor parameter) whose
  constructor parameter has no default are required; a defaulted parameter (`int Weight = 3`) and a computed get-only member
  stay optional in the document and on the server. An explicit `[Required]` on a value type always checks presence, default
  or not.
- **Startup attribute probe** (m1): only the built-in `System.ComponentModel.DataAnnotations` attributes are probed; a game's
  own attribute that refuses `null` never fails startup.
- **Enum schema limits** (m2): `[Flags]` enums and properties with their own converter keep the generator's schema (no `enum`
  list); recorded in PCR gm.
- **`delta` wording** (m3): see "economy.adjust without delta" above (HTTP 200 stored `INVALID_QUANTITY` → `400 BAD_REQUEST`).

## First-consumer migration (zh-pvp-server)

Checked read-only on 2026-11-05 (`E:\ProjGits\zh-pvp-server`, framework pinned to `v0.7.0`): the game maps no GM command of
its own, and none of `src/ZhPvp.Host/appsettings*.json` sets `GameServer:PublicEndpointAllowlist` or a `GameServer:Hosting`
value. After the bump:

1. bump the framework reference;
2. add one test with `GmOpenApiSnapshot` over `LocalFlavorFactory<Program>`, run it once in write mode, commit
   `docs/api/gm-openapi.json` (input of the game's console codegen);
3. nothing for the allowlist: `gm.auth_methods` is allowlisted through its declaration (V10);
4. optionally add the first GM command with an annotated body and record friction if the contract does not fit;
5. check the game's tests for pinned route-name lists (BC-5) and GM bodies already carrying annotations (BC-3; none today).

## Decisions awaiting owner override

1. **D9 / BC-4** — versions in `/me` for every `gm.access` staff (wider than 013's `ops.read`): applied, owner confirmation
   pending.
2. **D5–D7** — honest `required` in request bodies, validation over the JSON contract, absent required number = missing:
   applied, owner confirmation pending.
3. **D10** — audit, staff audit and ledger items stay free-form in the document: applied, owner confirmation pending.
4. **D14** — the consumer fixture gains the GM module and a GM command: applied, owner confirmation pending.
5. **D15** — no version bump in the slice; next tag proposed `v0.8.0`: applied, owner confirmation pending.
6. **A3 / A4** — `ProducesGmPage<T>` (one type argument) and `GmOpenApiSnapshot` (over `IServiceProvider`) differ from design
   §5.2: applied, owner confirmation pending.
7. **Review M4** — the server validates bodies to depth 3 and does not validate items of a top-level array body, while the
   generator still shows `[property: …]` keywords on deeper types: documented as a limit (PCR gm, PCR platform §23), not
   changed. Owner: keep, or validate without a depth cut.
8. **Review m4** — `economy.resource_options` requires `player.read` (FR-043) while `economy.adjust` requires
   `resource.adjust`: a staff with only `resource.adjust` gets 403 on the option list. Owner: keep, or declare the route with
   `resource.adjust`.
9. **Review m2** — `GmOpenApiSnapshot.WriteAsync`/`DiffAsync` accept a relative path (resolved against the test output
   folder); docs now show an absolute path. Owner: keep, or refuse non-rooted paths.
10. **Review r1 m2** — `economy.resource_options` requires `gm.access` (default chosen by the orchestrator, awaiting owner
    override); item 8 above is superseded by it.
11. **Review r1 m3** — a missing `delta` of `economy.adjust` is a `400 BAD_REQUEST` with `errors.delta` instead of an HTTP 200
    stored `INVALID_QUANTITY` rejection (behaviour change, default chosen by the orchestrator, awaiting owner override); with it,
    every non-nullable value type of a GM body without a default is required (BC-9, `MaintenanceBody.enabled` too).
12. **Review r1 m5** — the failure hold uses the repository's `IClock` (TIME-01), not `TimeProvider`.

## Tests

New: `GmPresentationTests`, `GmBodyContractTests`, `GmBodyValidatorTests`, `GmProblemDetailsErrorsTests`,
`GmCatalogDocumentTests`, `GmOpenApiTransformerTests`, `GmOptionsCheckTests`, `GmOpenApiSnapshotTests`,
`PublicDeclarationAllowlistTests` (Platform.Web); `GmDocumentShapeTests`, `GmCommandValidationTests` (+ non-parallel
`GmValidationMetricTests`), `GmCatalogRouteTests`, `GmAuthMethodsTests`, `GmOptionRoutesTests`, `GmCatalogPerformanceTests`
(opt-in) and the test-only `ProbeGmModule` (Gm); `FixtureGmOpenApiTests` (consumer fixture). Extended: `OpenApiTests` (kit),
`GmVersionEndpointTests`, `GmAdjustTests`, `GateAndBanTests`, `CatalogCompositionTests`, `SampleHostFlavorTests`. Counts of
the final run: see tasks.md T060/T061 notes (the full Couchbase suite is the owner's final run).

## Next tag

Proposed `v0.8.0` (additive changes). Not part of this slice (D15): no version bump, tag or push here.
