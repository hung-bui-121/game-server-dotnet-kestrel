# Feature Specification: GM API contract — a complete, machine-readable GM catalog served in every environment, declared input rules enforced by the server, option sources, typed lists, console bootstrap endpoints and a snapshot test for games (slice 017, GM-1)

**Feature Branch**: `017-gm-api-contract`

**Created**: 2026-11-05

**Status**: Draft

**Input**: GM tool design `AgentMemories/features/gm-tool/01_design.md` (revision 1, 2026-11-05), **§19 = the scope of this
slice** ("GM-1 `gm-api-contract`"), with §0, §1 (H1–H16), §2, §5 (contract), §7.4–§7.5, §8, §9.2, §13 (D1–D13), §14, §17
(Q1–Q21) and §18 (V1, V10). Summary `AgentMemories/features/gm-tool/00_summary.md`. Requirements
`Requirements/Game_Server_System_Requirements_v1.2.md`: GM-01, GM-02, GM-03, GM-06, SEC-07, SEC-08, SEC-09, SEC-13,
API-02, API-04, CI-01, PCR-01, TBD-21.

Owner decisions already taken (2026-11-05, not reopened here):

- The framework ships the GM **backend base and a frontend shell**; a game extends both. Generic forms are generated from
  the GM OpenAPI document of the running host.
- TBD-21: React + TypeScript + Vite + Mantine, console on a **separate static host** (case A: same site, other origin).
  None of that is built here: this slice is the **backend contract only**. Cookies, CORS, SSO return, hosting, the console
  shell and the frontend are GM-2 (`gm-console-hosting-shell`), GM-3 (`gm-generic-forms`) and GM-4
  (`gm-console-extension`).
- The architect's defaults Q5–Q15 of the design are accepted (catalog for every `gm.access` staff without filtering, hide
  what the staff may not do, typed confirm list, server-checked DataAnnotations, per-IP limit only on auth endpoints, GM-1
  before giftcode). Q16–Q21 are open and do not affect this slice.
- Slices run autonomously; there is no owner review stop. The repository lines saying that "another developer" owns the GM
  frontend are outdated and are corrected by this slice.

Decisions taken by default in this spec are marked **[DEFAULT]** and listed again under Assumptions, so the owner can
override them.

## Scope note

- Verified on `main` = `59d37f6` (2026-11-05, branch `017-gm-api-contract` created from it, tag `v0.7.0` at `eeb8e83`).
  The facts of design §1 hold; some need precision (see "Design claims re-checked" at the end).
- **Already in place (004, 007, 008, 010, 013, 014, 016):**
  - GM JSON API under `/gm/v1/**`: cookie session with server-side ticket, CSRF token bound to the session, Origin and
    `Sec-Fetch-Site` checks, CORS exact-origin allowlist (007); permission + `gameId`/`environmentId` scope on every request
    (SEC-07); one security profile per endpoint, Public only from the allowlist (SEC-08, SEC-09).
  - GM commands mapped by `MapGmCommand<TBody, TRequest, TResult>` run through `ICommandRunner` (receipt, idempotency on
    `X-Request-Id`, audit in the receipt outbox; SEC-14) and answer `GmCommandResponse` (with `details` and `unavailable`
    since 016). The body implements `IGmCommandBody` (`Reason`, checked 1..500 by hand).
  - Lists (`search`, `receipts`, `audit`, `ledger`, `staff`, `staff audit`) are keyset-paged with a mandatory `pageSize`
    (1..200) and an opaque `cursor` (GM-06, ACC-24), answering `GmPage<T>(items, nextCursor)`.
  - The GM OpenAPI document (`Microsoft.AspNetCore.OpenApi` 10.0.14, OpenAPI `3.1.1`) has `x-gm-permission` and
    `x-gm-csrf` on every operation. It is registered and served (`platform.gm_openapi`,
    `/gm/v1/openapi/{documentName}.json`, Public) **only where development tools are allowed** (Development/Test). A test
    compares the committed copy `docs/api/gm-openapi.json` with the running framework test host.
  - Route catalog (014): every endpoint is mapped from a `RouteDeclaration` listed by a module, the platform or the host;
    Public declarations of modules and the platform are allowlisted through the declaration (no config entry); the
    endpoint check runs before Kestrel listens.
  - `GET /gm/v1/me` (`gm.access`) answers staff id, display name, game, environment and permissions.
    `GET /gm/v1/ops/version` (`ops.read`) answers the instance's game and framework versions (013, FR-055).
  - Consumer test kit `GameServer.TestKit` (013): `TestHost`, `LocalFlavorFactory<TEntryPoint>`, `GmBrowser`, no
    unit-test-framework dependency.
- **Gaps this slice closes (design §1):**
  - H1: no GM command has a `requestBody`, a `playerId` path parameter or the `X-Request-Id` header in the document.
  - H2: beta/uat/live have no catalog at run time.
  - H3: input rules are imperative code in `toDomain`; the schema carries none of them.
  - H4: no `operationId`; tags are the host's application name (`GameServer.Gm.Tests` in the committed copy).
  - H5: list parameters (`pageSize`, `cursor`, `displayName`) are read by hand and undocumented; four lists have no
    response schema.
  - H7 (contract part only): the document has no notion of a GM operation's kind, target or confirmation level.
  - H8: a game has no standard way to generate and diff its own GM document (CI-01).
  - H11: `BanBody.Scope` and `AdjustResourceBody.Resource` take values from a set known only at run time.
  - H12: repository lines say "another developer" owns the GM frontend.
  - The login page of a console cannot ask which sign-in methods exist, and `/me` carries no version for the console's
    compatibility check (design §7.5).
- **Not included:** anything of GM-2..GM-4 (cookie `SameSite`/`__Host-`, CORS changes, CORS headers on error responses,
  `Access-Control-Expose-Headers`, SSO return to the console, `Gm:Console:*` options, `/gm/v1/ui` restriction, the
  console workspace, shell, generic forms, extension registry), `MapGmAction` (giftcode slice), game role templates (H10,
  Q11), the per-IP GM limiter of SEC-13 (Q9), typed item schemas for audit and ledger pages, and edits of the
  `Requirements/` file (owner's, gitignored; TBD-05a/SEC-04/SEC-06/TBD-21 wording belongs to GM-2 per design §11). See
  Out of Scope.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Every GM command is fully described in the GM document (Priority: P1) — H1, H4, H7

A console developer (and later the generic form of GM-3) reads the GM OpenAPI document of a host and finds, for every GM
command: a stable operation id (the route name), a tag (the area), the JSON body schema with the mandatory `reason`, the
path parameters of its template, the mandatory `X-Request-Id` header, the `200` response schema, and `x-gm-*` keys that
say what kind of operation it is, which command it runs, whether it targets a player and how strongly it must be
confirmed. A game that maps its own GM command with `MapGmCommand` gets the same description with no extra code.

**Why this priority**: it is the foundation of the whole GM tool strategy (design D1). Today the document says nothing
about any GM command body, so neither a typed client nor a generic form can be produced from it, and the stable key the
console needs (`operationId`) does not exist.

**Independent Test**:
- Start the framework test host (sample modules) and read the GM document: `economy.adjust`, `identity.ban`,
  `identity.unban`, `identity.cancel_deletion` and `progression.set` each have `operationId` = route name, `tags` = first
  route-name segment, `requestBody` (`application/json`, `$ref` to the body schema), path parameter `playerId`, header
  `X-Request-Id` (required, `format: uuid`), `200` → `GmCommandResponse`, and `x-gm-kind: command`, `x-gm-operation`,
  `x-gm-target: player`, `x-gm-confirm`.
- Map a GM command of a test module through the string-pattern overload (no declaration attached): the description is
  the same, the operation id comes from the route catalog.

**Acceptance Scenarios**:

1. **Given** any endpoint of the GM document, **When** it is read, **Then** its `operationId` is the name of its route
   declaration and is unique in the document.
2. **Given** an endpoint without an explicit `WithTags(...)`, **When** it is read, **Then** `tags` is `[<area>]`, the
   segment of the route name before the first `.` (`economy.adjust` → `economy`, `gm.player_search` → `gm`); an endpoint
   with `WithTags(...)` keeps its tags.
3. **Given** a GM command, **When** it is read, **Then** it has the body schema, every `{name}` of its template as a
   required path parameter, the required `X-Request-Id` header (uuid), the `GmCommandResponse` response, and
   `x-gm-kind: command`, `x-gm-operation: <operation>`, `x-gm-target: player`, `x-gm-confirm: simple|typed`.
4. **Given** a body schema of an `IGmCommandBody`, **When** it is read, **Then** `reason` is required, a string of 1..500
   characters that is not only white space, and the other properties are required only when the server requires them.
5. **Given** any GM GET endpoint, **When** it is read, **Then** `x-gm-kind: query` and no `x-gm-confirm`; any other GM
   mutation has `x-gm-kind: action` and `x-gm-confirm`; `x-gm-target` is `player` exactly when the template has
   `{playerId}`.
6. **Given** the five framework mutations of Q7 (ban, cancel deletion, rotate keys, maintenance, economy adjust), **When**
   they are read, **Then** `x-gm-confirm` is `typed`; every other mutation is `simple`.

---

### User Story 2 - A GM body's input rules are declared once and enforced by the server (Priority: P2) — H3, Q8

A module author puts DataAnnotations (`[Required]`, `[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`,
`[RegularExpression]`, `[Length]`, `[Description]`) on the properties of a GM body. The GM document shows them as JSON
Schema keywords, and the server rejects a body that breaks them with `400 BAD_REQUEST` Problem Details whose additive
`errors` member names each failing JSON property, **before** `toDomain` runs and before the command runner. A body
without annotations behaves exactly as today.

**Why this priority**: a rule written once is shown by the form (GM-3) and enforced by the server, with no copy in
TypeScript that can drift (design D3). It is also what lets a game add a GM command with zero frontend code.

**Independent Test**:
- Annotate the sample `SetProgressionBody` (`level ≥ 1`, `xp ≥ 0`, `stage ≥ 1`), send `level: 0, xp: -1`: 400,
  `code = BAD_REQUEST`, `errors = { "level": [...], "xp": [...] }`, no receipt, `toDomain` and the handler never run.
  The document shows `minimum` on the three properties.
- Send a valid body: the command commits as before. Send a body of a framework GM command that has no annotation
  besides `reason`: identical status, code and response as on `main`.

**Acceptance Scenarios**:

1. **Given** a GM body property with a supported annotation, **When** the document is read, **Then** the property schema
   carries the matching keyword(s) (`minimum`/`maximum`, `minLength`/`maxLength`, `minItems`/`maxItems`, `pattern`,
   `description`), and the property is in `required` exactly when it carries `[Required]`.
2. **Given** a body that breaks one or more rules, **When** it is posted, **Then** the answer is `400 BAD_REQUEST`
   Problem Details with `errors: { "<jsonName>": ["<message>", ...] }` (JSON property names, every failing property
   listed, messages from the attributes), nothing is written, no receipt, no audit, and the command runner is not called.
3. **Given** an annotation written on a positional record parameter (no `property:` target) or on the property, **When**
   the body is validated, **Then** the server enforces it in both cases, exactly as the document shows it.
4. **Given** a body whose annotated property is a nested object or a list of objects, **When** an inner rule fails,
   **Then** the `errors` key is the JSON path (`items[1].quantity`), up to the nesting the document can show.
5. **Given** a missing or blank `reason`, **When** a GM command is posted, **Then** the answer stays `400 BAD_REQUEST`
   (as today) and now also lists `errors: { "reason": [...] }`.
6. **Given** a GM body read through `GmHttp.ReadJsonAsync<T>` (framework actions such as maintenance, and a game's own
   non-command GM endpoints), **When** it breaks an annotation, **Then** the same 400 with `errors` is answered.
7. **Given** a framework GM body, **When** this slice annotates it, **Then** each annotation is exactly a rule the endpoint
   already enforced (same accepted and refused values, same status and code); rules that cannot be written exactly as an
   annotation stay imperative.

---

### User Story 3 - A console finds the catalog, the sign-in methods and the server versions in every environment (Priority: P3) — H2, D2, D8, §7.5

A console (any environment, including live) asks `GET /gm/v1/auth/methods` before sign-in to know which buttons to show,
reads `GET /gm/v1/me` after sign-in (now with `frameworkVersion` and `gameVersion`), and loads the GM document from
`GET /gm/v1/console/openapi.json` with its session cookie. The catalog answers the same document every staff with
`gm.access` gets, with an `ETag`, and `304 Not Modified` for a matching `If-None-Match`.

**Why this priority**: a game that adds a GM command and deploys its server must see the new form in beta/uat/live
without rebuilding the console (design D2); today the document exists only in Development/Test.

**Independent Test**:
- Start the sample host on the Beta flavor (OIDC configured): `/gm/v1/console/openapi.json` without a session → 401;
  with a session of a staff with `gm.access` only → 200 `application/json`, same bytes as the Development document of
  the same composition, `ETag` present; again with `If-None-Match` → 304 with an empty body. The old dev route
  `/gm/v1/openapi/gm.json` → 404 on Beta, 200 on Local.
- `GET /gm/v1/auth/methods` anonymously → `{ "local": false, "sso": true }` on Beta, `{ "local": true, "sso": false }`
  on Local without OIDC; no version, staff or configuration value in it.
- `GET /gm/v1/me` → the 013 fields plus `frameworkVersion` and `gameVersion` equal to `GET /gm/v1/ops/version`.

**Acceptance Scenarios**:

1. **Given** any environment with the GM API mapped, **When** a staff with `gm.access` reads the catalog route, **Then**
   it gets the GM document of the host, identical for every such staff (no permission filtering, Q5), with `ETag`,
   `Cache-Control: private, no-cache` and `Content-Type: application/json`.
2. **Given** a request without a GM session or without `gm.access`, **When** it reads the catalog route, **Then** it gets
   401 or 403 Problem Details as any GM endpoint.
3. **Given** a request whose `If-None-Match` equals the current `ETag`, **When** it reads the catalog route, **Then** the
   answer is `304` with the `ETag` and no body.
4. **Given** the document, **When** it is read, **Then** it contains no framework or game version, no server URL and no
   configuration value, so its bytes are the same in every environment for the same composition and the committed
   snapshot does not change on every release.
5. **Given** Development/Test, **When** a tool reads `/gm/v1/openapi/gm.json`, **Then** it gets the same document as
   today (route `platform.gm_openapi` kept, Public, Dev/Test only).
6. **Given** an anonymous caller, **When** it reads `GET /gm/v1/auth/methods`, **Then** it gets `{ local, sso }`:
   `local` is true exactly when the local login route is mapped, `sso` exactly when `Gm:Oidc` is configured.
7. **Given** a game host whose configuration lists `GameServer:PublicEndpointAllowlist` or does not, **When** it starts
   with this slice, **Then** `gm.auth_methods` is allowlisted through its declaration and the game adds nothing (V10).
8. **Given** a signed-in staff, **When** it reads `/me` or signs in locally, **Then** the answer carries
   `frameworkVersion` and `gameVersion` (additive); Public and player routes still disclose no version (OBS-03).

---

### User Story 4 - Lists, choices and confirmations are described, not guessed (Priority: P4) — H5, H11, Q7

A console developer reads the document and finds: each paged list with `x-gm-paged: true`, its `pageSize` (required,
1..200) and `cursor` query parameters and a typed `GmPage<T>` response; `displayName` of player search as a documented
query parameter; `x-gm-options` on body properties whose values come from another GM route; two option routes of the
framework (`economy.resource_options`, `identity.ban_scopes`) answering `GmOptionList`; and a summary on every framework
GM route.

**Why this priority**: GM-06 lists and option-driven fields are most of what a staff does; without this the generic list
and form of GM-3 cannot work, and a game would copy these shapes by hand.

**Independent Test**:
- Read the document: `gm.player_search`, `gm.player_receipts`, `gm.player_audit`, `economy.ledger`, `gm.staff_list`,
  `gm.staff_audit` each have `x-gm-paged: true`, `pageSize`/`cursor` parameters and a `GmPage…` response schema;
  `gm.player_search` also documents `displayName` (required, 1..64).
- `AdjustResourceBody.resource` has `x-gm-options: "economy.resource_options"`; `BanBody.scope` items have
  `x-gm-options: "identity.ban_scopes"`.
- `GET /gm/v1/economy/resources` (`player.read`) lists the resource catalog; `GET /gm/v1/players/ban-scopes`
  (`player.ban`) lists `login` and every command group of the deployment, exactly the values the ban command accepts.
- Every framework GM operation has a non-empty `summary`.

**Acceptance Scenarios**:

1. **Given** an endpoint annotated with `ProducesGmPage<T>()`, **When** the document is read, **Then** it has
   `x-gm-paged: true`, the `200` schema `GmPage<T>` (`items`, `nextCursor`), `pageSize` (query, required, integer
   1..200) and `cursor` (query, optional, string); calling it without `pageSize` is still 400 (GM-06, ACC-24).
2. **Given** an endpoint annotated with `WithGmQueryParameter(name, required, maxLength)`, **When** the document is read,
   **Then** the query parameter is listed with that rule, and the handler's own check is unchanged.
3. **Given** a body property with `[GmOptions("<route name>")]`, **When** the document is read, **Then** its schema (or,
   for a list, its item schema) carries `x-gm-options: "<route name>"`.
4. **Given** a `[GmOptions]` that names no declared GM `GET` route of the host, **When** the host starts, **Then** startup
   fails with one report line naming the body type, the property and the route name (deny silent typos).
5. **Given** `GET /gm/v1/economy/resources`, **When** a staff with `player.read` calls it, **Then** the answer is
   `{ items: [{ value: <resource id>, label: <display name> }] }` sorted by `value` (ordinal), and without `player.read`
   it is 403.
6. **Given** `GET /gm/v1/players/ban-scopes`, **When** a staff with `player.ban` calls it, **Then** the answer lists
   `login` and every command group, the same set the ban command validates against (one shared definition).
7. **Given** a GM route of the framework, **When** the document is read, **Then** it has a `summary`; a game route without
   one is valid (the console shows the route name).

---

### User Story 5 - A game proves its GM contract in CI with one test (Priority: P5) — H8, CI-01

A game adds one test that uses `GmOpenApiSnapshot` from the test kit against its own host (for example
`LocalFlavorFactory<Program>`): it fails with the first differing line when the host's GM document differs from the
game's committed `docs/api/gm-openapi.json`, and the same call in "write" mode regenerates the file. The framework's own
document test and the consumer fixture use the same call.

**Why this priority**: CI-01 requires an OpenAPI diff for GM; the committed document is also the input of the console's
TypeScript codegen (design §8). Without a kit call each game copies the framework test.

**Independent Test**:
- In the consumer fixture (game-shaped repo), add the GM module and one annotated GM command, commit its document, run
  the snapshot test: green; change a `[Range]` bound: red with a message naming the first differing line; run in write
  mode: file rewritten, test green.
- The framework test `OpenApiTests` uses the kit call; its permission/CSRF checks per endpoint stay.

**Acceptance Scenarios**:

1. **Given** a host and a committed path, **When** `GmOpenApiSnapshot.DiffAsync` runs, **Then** it returns an empty string
   when the committed file equals the document the catalog route serves, else a message with the 1-based line number,
   the committed line and the host line.
2. **Given** a missing committed file, **When** `DiffAsync` runs, **Then** it returns a message telling how to create it
   (write mode), never throws for that case.
3. **Given** `WriteAsync`, **When** it runs, **Then** the file holds exactly the bytes the catalog route serves (UTF-8
   without BOM, LF line ends, one final newline), and the folder is created if needed.
4. **Given** a committed file checked out with CRLF line ends, **When** `DiffAsync` runs, **Then** line ends are ignored.
5. **Given** the kit, **When** its references are checked, **Then** it still has no unit-test-framework dependency.

---

### User Story 6 - The docs tell module authors and games how to declare a GM route for the generic console (Priority: P6) — H12, PCR-01

A module author reads the module-authoring guide and `docs/html/02-them-command.html` §8 and learns how to map a GM
command or query so the console can render it (summary, annotations, options, paging, confirmation, request id), how the
catalog and the snapshot work; a game reads the consumer guide; the PCRs list the new routes, the Public allowlist entry,
the `x-gm-*` keys and the validation answer; the release note draft lists every change for the next tag; and no
repository line still says another developer owns the GM frontend.

**Why this priority**: the contract is only useful when module authors follow it; the stale ownership lines mislead every
reader of the GM docs.

**Independent Test**: a reviewer agent with no context beyond the docs adds a GM command with an annotated body and an
option source to the consumer fixture, regenerates the fixture's GM document and passes its snapshot test.

**Acceptance Scenarios**:

1. **Given** the PCR gm, **When** it is read, **Then** it lists the four new routes, `gm.auth_methods` in the Public
   allowlist (SEC-09), the catalog route and its caching, every `x-gm-*` key, the `errors` member of a validation answer,
   and the annotation conventions.
2. **Given** the repository after this slice, **When** it is searched for the ownership lines of H12 (and the matching
   lines in the HTML pages), **Then** none remains; they say the owner builds the console as framework GM-2..GM-4.
3. **Given** the release notes draft, **When** it is read, **Then** it lists every breaking change of this slice with its
   fix and the game's migration steps (snapshot test, committed document, nothing for the allowlist).

---

### Edge Cases

**Document generation**

- **An endpoint with no declaration metadata** (mapped through the string-pattern `MapGmCommand` overloads, which bind to
  their declaration by method + template in the 014 endpoint check): the operation id comes from the route catalog
  lookup (`WireCatalog.FindRouteName`). An endpoint the catalog cannot name would already fail the 014 endpoint check
  before the document is ever generated.
- **Two endpoints, one route name**: impossible after the 014 checks (one declaration per name, one method per GM
  declaration); the document test still asserts operation ids are unique.
- **A route name without a `.`** (allowed for host routes): the tag is the whole name.
- **Public GM routes** (`gm.auth_csrf`, `gm.auth_login`, `gm.auth_methods`, SSO): described like any route, with
  `x-gm-permission: public`; GM-3 filters on that key, so no extra kind is introduced.
- **Endpoints excluded from description** (`gm.ui`, the catalog route itself, the dev document route): not in the document,
  as today for `gm.ui` and the dev route.
- **The first catalog request after start** generates the document (lazy, once per process); concurrent first requests
  wait for the same generation. A transformer bug surfaces as a 500 on that route only; the framework and game snapshot
  tests run the same generation, so such a bug is caught in CI.
- **Hosting without the GM API** (`GameServer:Hosting:MapGmApi = false`): no catalog route, no `gm.auth_methods`, as for
  every GM route.
- **The document of a composition differs between flavors** only by conditional routes (`gm.auth_login` only with
  development tools, SSO routes only with `Gm:Oidc`); each host serves its own document. The committed snapshot of a game
  is produced from one chosen flavor (Local in the fixture), as today.

**Validation**

- **A body that is not JSON or not an object**: `400 BAD_REQUEST` as today (no `errors`), before validation.
- **`[Required]` on a value type** (`long Delta`): JSON Schema shows it required; the server checks that the property was
  present in the JSON, not that it is non-zero (a missing number deserializes to 0, which `[Required]` alone cannot see).
  [DEFAULT] The validator records which JSON properties were present and treats an absent `[Required]` value-type
  property as missing (plan research R3).
- **`[Range]` on a nullable property**: null passes (the property is optional), a value is checked.
- **A cross-field rule** ("set at least one of level, xp, stage"): stays in `toDomain`, answered 400 without `errors`
  as today; the document cannot show it (design D3 trade-off).
- **Annotations the document cannot express** (a custom `ValidationAttribute`): the server enforces it, the document
  shows nothing for it; the module-authoring guide says to prefer the supported set.
- **`IValidatableObject` on a body**: not called in this slice (the server would enforce a rule the document never
  shows); listed under Out of Scope.
- **Reason with only white space**: refused (as today); the schema shows `pattern: "\\S"`.

**Catalog route and console bootstrap**

- **`If-None-Match` with a list or a weak tag** (`W/"…"`): matched by the opaque value; any match → 304.
- **Instances of one deployment on different framework builds** (rolling deploy): each answers its own document and ETag;
  the console revalidates and gets 200 with the other document. Accepted (design §7.5).
- **`gm.auth_methods` when SSO is configured but the IdP is down**: `sso: true` (configuration, not reachability).
- **Version strings**: `frameworkVersion` is the informational version (`0.7.0+{sha}` style) exactly as `ops/version`;
  `gameVersion` is the host entry assembly's informational version or `unknown`.

**Options**

- **Resource catalog empty or not loaded**: `items: []` (an economy host without `resources.json` already fails startup
  when the catalog is required; nothing new).
- **A `[GmOptions]` route that the staff may not call**: the document still names it; the console shows a free-text
  field when the options route answers 403 (GM-3). The server still validates the submitted value.
- **`ban-scopes` and a module added later**: the list is computed from the registered commands at startup, the same
  instance the ban command uses, so both always agree within one process.

## Requirements *(mandatory)*

### Functional Requirements

**GM document: operations (H1, H4, H7; GM-01, API-02, CI-01)**

- **FR-001** (H4): Every operation of the GM document MUST have `operationId` = the name of the endpoint's route
  declaration, resolved from `RouteDeclarationMetadata` or, when absent, from the route catalog by method and template.
  Operation ids MUST be unique in the document.
- **FR-002** (H4) **[DEFAULT]**: An operation without explicit tags metadata MUST be tagged `[<area>]`, the route-name
  segment before the first `.` (the whole name when there is none). Explicit `WithTags(...)` wins.
- **FR-003** (H1): Every GM command mapped by any `MapGmCommand` overload MUST be described with: `requestBody`
  (`application/json`, required, schema of `TBody`); every template parameter as a required path parameter
  (`playerId`: string, pattern of `GmHttp.RequirePlayerId`); the header `X-Request-Id` (required, string,
  `format: uuid`); response `200` = `GmCommandResponse`. No request or response behaviour changes.
- **FR-004** (H7): Every operation MUST carry `x-gm-kind` = `command` (endpoint has `CommandOperationMetadata`), `query`
  (GET) or `action` (any other method); `x-gm-target` = `player` when the template contains `{playerId}`, else
  `global`; a command also carries `x-gm-operation` = its operation name.
- **FR-005** (Q7) **[DEFAULT]**: Every non-GET operation MUST carry `x-gm-confirm` = `typed` when the endpoint has
  `WithGmConfirm(GmConfirm.Typed)`, else `simple`; GET operations MUST NOT carry it.
- **FR-006** (H1) **[DEFAULT]**: A GM mutation that requires `X-Request-Id` outside `MapGmCommand` (framework staff
  writes, gate writes, key rotation) MUST document the header through the same metadata (`WithGmRequestId()`), so every
  operation that answers 400 without the header documents it.
- **FR-007**: The existing `x-gm-permission` and `x-gm-csrf` keys, the `4XX`/`5XX` responses and the document `info`
  MUST stay as they are (the `info.description` may gain one sentence about `x-gm-*`).
- **FR-008** **[DEFAULT]**: The document MUST NOT contain a `servers` entry, a framework or game version, or any
  configuration value, so that the same composition yields byte-identical documents in every environment (design §5.2,
  §7.5).
- **FR-009**: Endpoints excluded from description today stay excluded; the catalog route of FR-030 is excluded too.

**GM document: schemas (H3, H11)**

- **FR-010** (H3): For a body type of a GM endpoint (`AcceptsMetadata`), the property schemas MUST carry the JSON Schema
  keywords of its DataAnnotations: `[Range]` → `minimum`/`maximum`; `[StringLength]`, `[MinLength]`, `[MaxLength]`,
  `[Length]` → `minLength`/`maxLength` (strings) or `minItems`/`maxItems` (collections); `[RegularExpression]` →
  `pattern`; `[Description]` → `description`; whether the attribute is written on the property or on the positional
  record parameter. Which keywords the generator emits by itself is checked first (V1); the slice's schema transformer
  adds what is missing.
- **FR-011** **[DEFAULT]**: For a body type of a GM endpoint, `required` MUST list exactly the properties the server
  requires: those with `[Required]` (whose schema type then excludes `null`), plus `reason` for an `IGmCommandBody`.
  (Today the generator lists every positional record parameter as required although the server accepts their absence.)
  Response schemas are unchanged.
- **FR-012** (SEC-14): The `reason` property of every `IGmCommandBody` schema MUST be `type: string`, `minLength: 1`,
  `maxLength: 500`, `pattern: "\\S"`, matching the server rule `GmCommandEndpointFactory.MaxReasonLength`.
- **FR-013** (H11): A body property (or the items of a list property) with `[GmOptions("<route name>")]` MUST carry
  `x-gm-options: "<route name>"` in its schema.
- **FR-014** (V1): `long`/`int` properties keep the representation the generator gives them with the GM JSON options
  (today `type: ["integer","string"]`, `format`, `pattern`); enum properties MUST be described as the camelCase strings
  `GameServerJson.Options` reads and writes. Any difference found by V1 is fixed in the document, never by changing the
  wire format.

**Server-side validation (H3, Q8; API-04, GM-03, SEC-14)**

- **FR-020** (H3, Q8): `MapGmCommand` MUST validate the deserialized `TBody` against its DataAnnotations after the JSON
  read and before `toDomain`, the target validators and the command runner. A body with no annotation (besides the
  existing `reason` rule) MUST behave exactly as today.
- **FR-021** **[DEFAULT]**: Validation MUST apply the attributes the document shows: those on each JSON property of the
  body's System.Text.Json contract (property or matching positional parameter), recursively into nested objects and
  items of object lists, up to the depth the document shows. JSON names (naming policy and `[JsonPropertyName]`) are used
  in the answer.
- **FR-022** (API-04): A failing body MUST be answered `400 BAD_REQUEST` Problem Details with the existing members plus
  `errors`: an object from JSON path to a non-empty array of messages, listing every failing property. Messages come from
  the attributes (no exception text, no stack trace, no submitted value beyond what the attribute's message format
  includes).
- **FR-023**: The existing `reason` check MUST answer the same `400 BAD_REQUEST` and add `errors: { "reason": [...] }`.
- **FR-024** **[DEFAULT]**: `GmHttp.ReadJsonAsync<T>` (used by framework GM actions and available to game GM endpoints)
  MUST apply the same validation and answer.
- **FR-025**: The validation failure MUST be counted and logged as any `BAD_REQUEST` today (Information line of
  `ExceptionMiddleware`, `http_errors_total{code="BAD_REQUEST"}`); no new metric.
- **FR-026** (Core, additive): `GameServerException` MUST gain an optional per-field errors member that
  `ErrorResponseWriter` writes as `errors` in Problem Details only; the Protobuf `ErrorEnvelope` is unchanged.
- **FR-027**: Framework GM bodies MUST be annotated only with rules their endpoint already enforces exactly (plan lists
  them); every framework GM endpoint MUST answer the same status and code for the same input as on `main`. The sample
  `SetProgressionBody` MUST carry `[Range]` annotations as the reference example (the "at least one field" rule stays in
  `toDomain`).

**Catalog route, sign-in methods, versions (H2, D2, D8, §7.5; SEC-07, SEC-09, OBS-03)**

- **FR-030** (H2, D2): The GM document MUST be generated in every environment, and a new route `gm.console_catalog`
  (`GET /gm/v1/console/openapi.json`, profile Gm, permission `gm.access`, surface GmApi) MUST serve it to every caller
  with `gm.access`, without permission filtering (Q5).
- **FR-031** (§14): The catalog route MUST generate the document once per process on first use, keep its bytes, answer
  them with a strong `ETag` (hash of the bytes), `Cache-Control: private, no-cache`, `Content-Type: application/json`,
  and answer `304` (no body) when `If-None-Match` matches.
- **FR-032**: The catalog bytes MUST be the canonical form: the JSON of the document, indented, UTF-8 without BOM, LF line
  ends and one final newline; the same bytes `GmOpenApiSnapshot` writes (FR-050).
- **FR-033** (BC): The Development/Test route `platform.gm_openapi` (`/gm/v1/openapi/{documentName}.json`, Public) MUST stay
  as it is, condition `development_tools`, serving the same document.
- **FR-034** (D8, SEC-09): A new Public route `gm.auth_methods` (`GET /gm/v1/auth/methods`, surface GmApi, same timeout
  and limiter policies as `gm.auth_csrf`) MUST answer `GmAuthMethods { local, sso }`: `local` = the local login route is
  mapped on this host, `sso` = `Gm:Oidc` is configured. It MUST answer nothing else (no version, staff or configuration
  value).
- **FR-035** (V10): `gm.auth_methods` MUST be allowlisted through its declaration (as every Public module route, 014); a
  game MUST NOT need a configuration change for it. The PCR allowlist (SEC-09) MUST list it.
- **FR-036** (§7.5) **[DEFAULT]**: `GmMeResponse` MUST gain the additive members `frameworkVersion` and `gameVersion`, with
  the values of `GET /gm/v1/ops/version`, on `/gm/v1/me` and on the local login answer (same record). Public and player
  routes still carry no version (OBS-03); the catalog carries none (FR-008).

**Lists, options, confirmation, summaries (H5, H11, Q7; GM-06)**

- **FR-040** (H5, GM-06): `ProducesGmPage<T>()` MUST add `Produces<GmPage<T>>`, a `GmPagedMetadata`, and document
  `pageSize` (query, required, integer 1..`PagedQuery.MaxPageSize`) and `cursor` (query, optional, string). It MUST NOT
  change the handler's checks.
- **FR-041** (H5): `WithGmQueryParameter(name, required, maxLength)` MUST document a query parameter a handler reads by
  hand. Player search MUST document `displayName` (required, 1..64).
- **FR-042** (H5) **[DEFAULT]**: The framework lists `gm.player_search` (`GmPlayerSearchItem`), `gm.player_receipts`
  (`GmReceiptSummary`), `gm.staff_list` (`StaffView`) MUST be annotated with typed pages; `gm.player_audit`,
  `gm.staff_audit` and `economy.ledger`, whose items are stored documents returned as they are, MUST be annotated with a
  free-form object item schema.
- **FR-043** (H11): Two framework option routes MUST be added: `economy.resource_options`
  (`GET /gm/v1/economy/resources`, `player.read`) answering the resource catalog as `GmOptionList` (value = id,
  label = display name, sorted by value), and `identity.ban_scopes` (`GET /gm/v1/players/ban-scopes`, `player.ban`)
  answering `login` plus every command group (label = value), computed by the same definition the ban command validates
  against.
- **FR-044** (H11): `[GmOptions("economy.resource_options")]` MUST be on `AdjustResourceBody.Resource` and
  `[GmOptions("identity.ban_scopes")]` on `BanBody.Scope`.
- **FR-045** **[DEFAULT]**: Startup MUST fail with one report line per `[GmOptions]` on the body type of a mapped GM
  endpoint whose route name is not a declared `GET` route of surface GmApi on this host (joining the one aggregated
  startup report, 011 FR-009).
- **FR-046** (Q7): `WithGmConfirm(GmConfirm.Typed)` MUST be on `identity.ban`, `identity.cancel_deletion`,
  `gm.ops_rotate_keys`, `gm.ops_gate_maintenance` and `economy.adjust`.
- **FR-047**: Every framework GM route (modules `gm`, `identity`, `economy`, platform) and the sample's
  `progression.set` MUST have a `WithSummary(...)` (English, one sentence).
- **FR-048**: The presentation API (`GmConfirm`, `GmConfirmMetadata`, `GmPagedMetadata`, `GmQueryParameterMetadata`,
  `GmRequestIdMetadata`, `GmOptionsAttribute`, `GmOption`, `GmOptionList`, `GmPresentationExtensions`) MUST live in
  `GameServer.Platform.Web.Endpoints`, add no dependency to Core or business projects (ARC-01), and be metadata only (no
  runtime behaviour besides the startup check of FR-045).

**Test kit snapshot (H8, CI-01)**

- **FR-050** (CI-01): The test kit MUST offer `GmOpenApiSnapshot` with `GetDocumentAsync`, `WriteAsync` and `DiffAsync`
  over a started host's services (and convenience overloads for `TestHost`), producing the canonical bytes of FR-032.
- **FR-051**: `DiffAsync` MUST return an empty string when equal, else a message with the first differing line (number,
  committed text, host text) and the write-mode hint; a missing file is a message, not an exception; line ends are
  ignored.
- **FR-052**: The kit MUST keep no unit-test-framework dependency; the allow-list guard of the kit is extended only if a
  new internal is used.
- **FR-053**: The framework document test MUST use `GmOpenApiSnapshot` (`UPDATE_OPENAPI=1` keeps meaning "write"), keep its
  per-endpoint permission and CSRF checks, and add checks for FR-001..FR-006 and FR-040..FR-047.
- **FR-054** **[DEFAULT]**: The consumer fixture MUST add the GM module, one GM command of its own with an annotated body
  and one option route, a committed `tests/ConsumerFixture/docs/api/gm-openapi.json`, and a snapshot test, as a game
  would.
- **FR-055**: `docs/api/gm-openapi.json` MUST be regenerated from the framework test host and committed.

**Documentation (H12, PCR-01)**

- **FR-060** (PCR-01, SEC-09): PCR gm MUST list the new routes, the allowlist entry, the catalog route and caching, every
  `x-gm-*` key with its rule, the validation answer, the `/me` fields and the annotation conventions; PCR identity and
  economy MUST list their option routes and annotated bodies; PCR platform MUST record the GM document in every
  environment, the `errors` member of Problem Details and the presentation API.
- **FR-061**: `docs/guides/module-authoring.md` MUST explain how to map a GM command or query for the generic console
  (summary, annotations and their limits, `[GmOptions]` + option route, paging, confirmation, request id).
- **FR-062**: `docs/guides/consumer-guide.md` MUST gain a GM section (catalog, snapshot test, committed document, bump
  steps) and its bump checklist the GM step.
- **FR-063**: `docs/html/02-them-command.html` §8 MUST describe the declared GM command (`MapGmCommand(route, toDomain)`),
  annotations, the snapshot, with the page stamp updated for the unreleased version.
- **FR-064** (H12): The outdated ownership lines MUST be corrected in `docs/pcr/gm.md`,
  `AgentMemories/features/p05-speckit/00_summary.md`, `docs/html/01-gioi-thieu.html` and `docs/html/03-deploy.html`
  (including "the runtime OpenAPI route exists only in Dev/Test").
- **FR-065**: `specs/017-gm-api-contract/release-notes-draft.md` MUST list every change, breaking change and migration
  step of this slice.

**Verification and close**

- **FR-090**: The VERIFY items V1 and V10 (design §18) and the slice items V11–V13 (research) MUST be checked before the
  tasks that depend on them, with the stated fallback applied and recorded when a fact is wrong.
- **FR-091**: Debug and Release builds MUST have 0 warnings; the full suite (in memory, File store, Couchbase) MUST pass.

### Key Entities

- **GM document (catalog)**: the OpenAPI `3.1.1` document of the host's `/gm/**` endpoints, with `x-gm-*` keys; canonical
  bytes, one per composition, served by `gm.console_catalog` and committed as `docs/api/gm-openapi.json`.
- **Operation extensions**: `operationId`, `tags`, `x-gm-permission`, `x-gm-csrf` (existing), `x-gm-kind`,
  `x-gm-operation`, `x-gm-target`, `x-gm-confirm`, `x-gm-paged`; property `x-gm-options`.
- **Presentation metadata**: endpoint metadata objects read only by the document transformers and the options check.
- **Field errors**: JSON path → messages, carried by `GameServerException` and written as Problem Details `errors`.
- **Option list**: `GmOptionList { items: [{ value, label }] }` answered by an option route.
- **Sign-in methods**: `GmAuthMethods { local, sso }`.
- **Snapshot**: the committed copy of the GM document of a host and the kit call that writes or diffs it.

## Backward compatibility and breaking changes (to consumers of `v0.7.0`)

Every API change is additive: no route, method, permission, request field or response field changes meaning; no
persisted document changes; no proto change (API-03 untouched); the Unity package is not touched. A GM body without
annotations is validated exactly as today. The old Development/Test document route stays.

| # | Change | Who is affected | How it is surfaced | Fix |
| --- | --- | --- | --- | --- |
| BC-1 | The GM document changes shape: `operationId` everywhere, tags = area instead of the host's application name, request bodies/path/header parameters on GM commands, `required` of request bodies reduced to the real rules, `x-gm-*` keys, no `servers` entry (FR-001..FR-014) | Tools that generate clients or diff the committed `docs/api/gm-openapi.json`; a game that already commits a copy | Diff in the committed file; generated client names change (tag/operation id based) | Regenerate and commit; codegen keyed by path (design D5) is unaffected |
| BC-2 | Problem Details of a GM `BAD_REQUEST` from body validation (and the `reason` check) gain the member `errors` (FR-022, FR-023) | GM clients that reject unknown members (none known) | New member | Ignore or show it |
| BC-3 | A GM body that carries DataAnnotations is now validated against them before `toDomain` (FR-020, FR-024). Framework bodies get only annotations they already enforced (FR-027) | A game whose GM body **already** carries annotations for another purpose (they were ignored before) | 400 `BAD_REQUEST` with `errors` where the command used to run | Remove the annotation or accept the rule; the release note lists how to find such bodies |
| BC-4 | `GmMeResponse` (on `/me` and on the local login answer) gains `frameworkVersion`, `gameVersion` (FR-036): versions become visible to every staff with `gm.access`, not only `ops.read` (013 default) | Owner policy on version disclosure; clients asserting the exact member set of `/me` | Two new members | None (additive); owner may restrict (Owner confirmations 2) |
| BC-5 | The route catalog gains four routes: `gm.auth_methods` (Public), `gm.console_catalog`, `economy.resource_options`, `identity.ban_scopes` (FR-030, FR-034, FR-043) | Tests or tools of a game that pin the full list of route names or of Public routes; a game that declares a route at one of the new templates | Pinned-list test fails; a template clash fails startup (014 check) | Add the names; rename the game route |
| BC-6 | The OpenAPI services are registered in every environment (FR-030) | Hosts that replaced or removed them (none known) | `IOpenApiDocumentProvider` registered outside Development/Test | None |
| BC-7 | `GameServerException` gains an optional member for field errors (FR-026, Core) | None at compile time (init property) | — | — |
| BC-8 | A `[GmOptions]` naming no declared GM GET route fails startup (FR-045) | New code only (new attribute) | One startup report line | Fix the route name or declare the route |
| BC-9 | (review r1/r2, FR-021) Every non-nullable value type in a GM body without a default is required: absent → 400 `BAD_REQUEST` with `errors["<path>"]` (before: read as its default, e.g. `0`/`false`); the document lists it in `required` | A game GM body with value-type members (`int`, `long`, `bool`, enum) its clients may omit | 400 with `errors` where the call used to run | Make the member nullable (`int?`) or give its constructor parameter a default (`int Weight = 3`) to keep it optional |

## First-consumer migration (zh-pvp-server after the next tag)

Checked read-only on 2026-11-05 (`E:\ProjGits\zh-pvp-server`, framework pinned to `v0.7.0`): the game maps no GM command
of its own (design §1 note), and `src/ZhPvp.Host/appsettings*.json` set no `GameServer:PublicEndpointAllowlist` and no
`GameServer:Hosting` value. After the bump the game:

1. adds one test with `GmOpenApiSnapshot` over `LocalFlavorFactory<Program>` and commits its `docs/api/gm-openapi.json`
   (the input of its future console codegen; consumer guide GM section);
2. changes nothing for `gm.auth_methods` (V10: Public module routes are allowlisted through their declaration);
3. optionally adds its first GM command with an annotated body (design §16 GM-1 row) and records a friction entry if the
   contract does not fit;
4. checks its tests for pinned route-name lists (BC-5) and for annotations already on GM bodies (BC-3; none today).

## Dependencies and seams with other slices

- **007 gm-security**: CSRF, cookie, CORS and SSO are untouched; the catalog route is an ordinary Gm endpoint (cookie +
  CSRF rules unchanged, GET needs no token); `gm.auth_methods` is a GET Public route (no CSRF needed, SEC-05 check of
  Public GM mutations unaffected).
- **013 consumer-readiness**: `GmOpenApiSnapshot` joins the test kit (no test framework); `/me` reuses the
  `GameServerRuntimeInfo` values of FR-055; the fixture is the game-shaped dogfood.
- **014 wire-contract**: operation ids are route names; the four new routes are declared in `GmRoutes`, `EconomyRoutes`,
  `IdentityRoutes` (module owners); GM surface routes generate no Unity constants, so WireGen outputs do not change; the
  wire-contract export lists the new routes.
- **016 login-command**: `GmCommandResponse.details`/`unavailable` are already in the response schema; unaffected.
- **GM-2** (next): reads `/gm/v1/auth/methods`, `/me` versions and the catalog route cross-origin; adds CORS expose
  headers (`ETag`), error-response CORS headers and the cookie changes. Nothing in this slice depends on GM-2.
- **GM-3**: consumes every `x-gm-*` key, the `errors` member, the option routes and the paged lists.
- **Giftcode slice**: `MapGmAction` will set `x-gm-kind: action` + `x-gm-target: global` through the same metadata.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the framework test host's document, 100% of the 5 GM commands have `requestBody`, every template
  parameter, `X-Request-Id`, `GmCommandResponse`, and all of `x-gm-kind`, `x-gm-operation`, `x-gm-target`,
  `x-gm-confirm`; 100% of operations have a unique `operationId` equal to the route name and an area tag.
- **SC-002**: For every supported annotation (8 attribute types × property and positional-parameter targets), the
  document keyword and the server answer agree: a boundary value accepted by the schema is accepted by the server and a
  value one step outside is refused with the property in `errors`, in 100% of the cases; a body without annotations gets
  byte-identical responses to `main` for the existing GM tests.
- **SC-003**: On Beta, Uat and Live flavors of the sample host (GM API on), the catalog route answers 401 without a
  session, 200 for a `gm.access` staff with bytes identical to the Local document of the same composition except for the
  conditional routes, and 304 for a matching `If-None-Match`, in 100% of runs.
- **SC-004**: The catalog route answers a warm request (bytes cached) in ≤ 5 ms p95 in-process and the first request in
  ≤ 1 s on the reference machine; no host start becomes slower by more than measurement noise (the document is not
  generated at startup).
- **SC-005**: `GmOpenApiSnapshot.DiffAsync` reports the first differing line for a one-character change anywhere in the
  committed file, returns empty for the regenerated file (also when checked out with CRLF), and the kit has 0 test
  framework references.
- **SC-006**: The consumer fixture's snapshot test is green on the committed document and red with the expected line when
  one `[Range]` bound changes, with no framework code change (consumer path).
- **SC-007**: `gm.auth_methods` starts and answers anonymously on a host with the default allowlist and on a host with an
  explicit `GameServer:PublicEndpointAllowlist` that does not list it (0 configuration lines for the game).
- **SC-008**: After the slice, a search of the repository (excluding `specs/` history and release notes of past slices)
  finds 0 lines saying another developer owns the GM frontend.

## Assumptions

Decisions taken by default (owner may override; each is referenced where it applies):

- **D1 — tags = area, the route-name segment before the first dot** (FR-002): route names are already `<area>.<name>`
  (014 naming rule), so menus group by module without a new declaration field.
- **D2 — `x-gm-confirm` only on mutations, `simple` unless typed** (FR-005): reading is never confirmed; Q7's typed list
  is applied as written.
- **D3 — the X-Request-Id header is documented on every GM mutation that requires it, not only on commands** (FR-006):
  staff writes, gate writes and key rotation already refuse a request without it; leaving it out would document a call
  the server refuses.
- **D4 — no `servers` entry** (FR-008): the console builds URLs from its `config.json` `apiBaseUrl` (design §7), and a
  host-derived server URL would make the bytes differ per environment and per request host.
- **D5 — `required` of request bodies rewritten to the real rules** (FR-011): the generator marks every positional record
  parameter required (committed copy: `StaffGrantBody`, `MaintenanceBody`, …), but the server accepts their absence; a
  generic form would force optional fields. Response schemas keep the generator's `required` (the server always writes
  every member).
- **D6 — validation reads the System.Text.Json contract, not `Validator.TryValidateObject`** (FR-021): every GM body is a
  positional record, and an attribute on a positional parameter without the `property:` target is invisible to
  `Validator` while the document generator may show it; one walker over the JSON contract guarantees "the server enforces
  what the document shows". Plan research R2.
- **D7 — absent `[Required]` value-type properties count as missing** (Edge Cases): otherwise `[Required] long` would be
  shown required and never enforced. Plan research R3.
- **D8 — `GmHttp.ReadJsonAsync` validates too** (FR-024): framework actions and a game's own non-command GM endpoints get the
  same contract; bodies without annotations behave as today.
- **D9 — versions in `/me` (and the local login answer)** (FR-036): design §7.5 needs them for the console's skew warning;
  this widens the 013 default "version only via `ops.read`" to every `gm.access` staff (still never Public, OBS-03).
- **D10 — audit, staff audit and ledger pages keep free-form items** (FR-042): their items are stored documents returned
  as they are; a typed item schema would promise a shape the handler does not enforce. Typed views are a later change.
- **D11 — `gm.staff_audit` is paged like the other lists** (FR-042): design §19 lists search, receipts, audit, ledger and
  staff list; staff audit is the same keyset-paged list and is included.
- **D12 — the document is generated lazily on the first catalog request** (FR-031): generating it at startup would add its
  cost to every host start (about 2 750 tests start hosts); CI generates it through the snapshot tests.
- **D13 — an unknown `[GmOptions]` route fails startup** (FR-045): consistent with every other declaration fault (014),
  and cheap because the route catalog is known before serving.
- **D14 — the consumer fixture dogfoods the GM contract** (FR-054): the fixture is the game-shaped repo of the framework
  and the snapshot call is consumer API, as 016 did for initializers.
- **D15 — no version bump or tag in this slice**: the next tag (proposed `v0.8.0`, additive changes) is a release step
  after the slice; the release note is a slice draft (FR-065).

Other assumptions:

- **Requirements wording**: SEC-09 lists Public routes "explicitly in the PCR allowlist"; the 014 rule (declaration =
  allowlist entry, PCR lists it) is kept and the PCR gains `gm.auth_methods`. SEC-13's per-IP GM limit is not implemented
  here (Q9: auth endpoints only, later). `Requirements/` (TBD-21 still "Mở" in v1.2) is edited by the owner/GM-2 per design
  §11, not by this slice.
- The reference machine and timings are those of 013–016.
- Messages in `errors` are English attribute messages (the GM API is English; the console may localize by keyword).

## Owner confirmations

No `[NEEDS CLARIFICATION]` remains; every open point has a default. The owner should confirm:

1. **D5 / D6 / D7**: the document's `required` of request bodies reflects real rules; validation reads the JSON contract;
   an absent required number is "missing".
2. **D9 / BC-4**: versions visible to every `gm.access` staff (wider than the 013 default "ops.read only").
3. **D10**: audit, staff audit and ledger items stay free-form objects in the document.
4. **D14**: the consumer fixture gains the GM module and a GM command.
5. **D15**: no version bump in the slice; next tag proposed `v0.8.0`.

## Plan traps (for the plan, 2026-11-05)

Numbering used by research.md.

1. Positional record attributes: parameter target vs property target, for both the generator and the validator → R2.
2. `[Required]` on value types and JSON presence → R3.
3. Document generation without an HTTP request (catalog bytes, snapshot) and the `servers` entry → R4, V11.
4. JSON options of the generator vs `GameServerJson.Options` (enum strings, number handling, naming) → R5, V1.
5. Operation-id resolution for endpoints mapped by template only → R1.
6. Request-body type set for the `required` rewrite (schema transformers see types, not usages) → R6.
7. Route-name and Public-route inventories pinned by tests (`CatalogCompositionTests`, `DeclaredRouteMappingTests`,
   `GmVersionEndpointTests`) → R9.
8. `ban-scopes` literal segment under `/gm/v1/players/{playerId}` → R7.
9. Fixture gaining the GM module: fixture tests pinning routes, config and outputs → R10.

## Out of Scope

- Everything of GM-2 (cookie `SameSite` split, `__Host-` cookie, CORS expose headers and headers on error responses, SSO
  return URL and `Gm:Console:*`, `/gm/v1/ui` restriction, requirement edits TBD-05a/SEC-04/SEC-06/TBD-21), GM-3 (generic
  forms, confirmation dialogs, request-id handling in the browser) and GM-4 (extension registry, templates).
- `MapGmAction` and global GM operations (giftcode slice); the contract (`x-gm-kind: action`, `x-gm-target: global`) is
  ready for it.
- `IValidatableObject` and cross-field rules in the document; custom attribute keywords; localization of messages.
- Typed item schemas of audit, staff audit and ledger pages; changes to list handlers.
- Game role templates (H10, Q11); the per-IP GM rate limit of SEC-13 (Q9).
- Version bump, tag and push; edits of `Requirements/` and of the game repositories (the game session bumps after the tag).

## Design claims re-checked on `main` (`59d37f6`)

- **H1** holds: `GmCommandEndpointFactory.cs:162` maps `MapPost(pattern, async (HttpContext http) => …)` and reads the
  body by hand (`:170-171`); only `CommandOperationMetadata`, `Produces<GmCommandResponse>` and the GM profile are attached
  (`:200-202`). The committed document has no `requestBody` or parameter for `ban`, `unban`, `deletion/cancel`,
  `resources/adjust`, `progression`.
- **H2** holds: `GameServerBuilder.cs:252-256` calls `GmOpenApi.Add` only when `AllowsDevelopmentTools`;
  `GameServerApplicationExtensions.cs:100-103` maps it only then; `PlatformRoutes.cs:27-32` declares it Public with
  condition `development_tools`.
- **H3** holds: `IdentityModule.cs:186-205` (ban scope), `EconomyModule.cs:30-34` (resource), `SampleGameModule.cs:44-55`
  (progression) validate in `toDomain`; the reason check is `GmCommandEndpointFactory.cs:172-175`.
- **H4** holds: no `operationId`; tags are `GameServer.Gm.Tests` or `GmModule` in `docs/api/gm-openapi.json`.
- **H5** holds: `GmHttp.ReadPageRequest` (`GmHttp.cs:22-32`) reads `pageSize`/`cursor`; search reads `displayName`
  (`GmPlayerEndpoints.cs:127-131`); search, receipts, audit, ledger and staff audit have no `Produces` (only staff list
  has `Produces<GmPage<StaffView>>`, `StaffEndpoints.cs:56`). **Also**: `gm.staff_audit` (`StaffEndpoints.cs:64-66`) is
  paged and not in §19's list (D11).
- **H8** holds: the committed document comes from `tests/GameServer.Gm.Tests/OpenApiTests.cs` on `GmTestHost` (sample
  modules, counter included).
- **H11** holds: `BanBody(string? Reason, DateTimeOffset? UntilUtc, List<string>? Scope)` (`IdentityModule.cs:332`),
  `AdjustResourceBody(string? Resource, long Delta, string? Reason)` (`EconomyModule.cs:67`).
- **H12** holds and is wider: `docs/pcr/gm.md:4-5`, `AgentMemories/features/p05-speckit/00_summary.md:9-10` and `:389`,
  plus `docs/html/01-gioi-thieu.html:526, 661, 687` and `docs/html/03-deploy.html:733` (also "runtime OpenAPI route only
  Dev/Test").
- **§5.2 / §19 "`SetProgressionBody` as the example"**: the body (`samples/SampleGame/SampleGame.Adapters/SampleGameModule.cs:119`)
  has **no** annotation today; it becomes the reference example in this slice (FR-027). The handler file
  `samples/SampleGame/SampleGame/GmProgressionHandler.cs` holds the command and handler only.
- **§5.2 `Validator.TryValidateObject`**: every GM body is a positional record (`IdentityModule.cs:332-337`,
  `EconomyModule.cs:67`, `SampleGameModule.cs:119`, `GateEndpoints.cs:18-20`, `StaffEndpoints.cs:26-38`), so the design's
  call would miss parameter-targeted attributes (D6).
- **§5.1 "errors" member**: Problem Details today have only `details` (string → string, `ErrorResponseWriter.cs:67-84`)
  and `GameServerException` has no field for per-field errors (`src/GameServer.Core/Errors/GameServerException.cs`), so a
  Core change (additive) is needed (FR-026); design §4 does not list it.
- **§8 "int64 → number"**: the committed document already shows `long`/`int` as `type: ["integer","string"]` with
  `format` and `pattern` (Web defaults allow reading numbers from strings), e.g. `GmAggregateUpdate.baseRev`. V1 confirms it
  with annotations.
- **V10 by reading**: `EndpointValidation.PublicTemplates` (`src/GameServer.Platform.Web/Hosting/EndpointValidation.cs:48-58`)
  adds the templates of Public module and platform declarations to the allowlist checked by
  `EndpointSecurityValidator.cs:28, 58-63`; `GameServerOptions.PublicEndpointAllowlist` defaults to `["/v1/time"]`
  (`GameServerOptions.cs:46`). The test of V10 confirms it on a host.
- **Versions**: `GmVersionResponse` (`GmModule.cs:44`) and `GmVersionEndpointTests` (013) pin `ops/version`;
  `specs/013-consumer-readiness/spec.md:492, 697-698` made `ops.read` the operator channel for versions (D9 widens it).
- **Pinned lists**: `tests/SampleGame.Tests/CatalogCompositionTests.cs:27-40` (every framework route name) and `:75`
  (dev document route only with development tools), `tests/GameServer.Platform.Web.Tests/DeclaredRouteMappingTests.cs:126`,
  `tests/GameServer.Platform.Couchbase.IntegrationTests/SampleHostFlavorTests.cs:35, 66` (dev route 200/404).
