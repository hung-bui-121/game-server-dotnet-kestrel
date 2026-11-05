# Data model: GM API contract (slice 017 gm-api-contract, GM-1)

**Date**: 2026-11-05 · **Spec**: [spec.md](spec.md) · **Research**: [research.md](research.md)

No persisted document, collection, index, proto message or Couchbase shape changes. Everything below is in-process
metadata, HTTP JSON shapes or a committed text file.

## 1. GM document (catalog)

| Item | Value |
| --- | --- |
| Format | OpenAPI `3.1.1` JSON, document name `gm`, `ShouldInclude` = relative path starts with `gm/` (unchanged) |
| Canonical bytes | `JsonNode` of the serialized document, written indented by System.Text.Json (default encoder), UTF-8 without BOM, `\n` line ends, one final `\n` (research R4) |
| Variable parts removed | `servers` (document transformer); no version anywhere (`info.version` stays `v1`) |
| Lifetime | Generated on the first catalog request (or kit call), kept for the process (`GmCatalogDocument`); a failed generation is not kept |
| ETag | `"` + first 32 lowercase hex chars of SHA-256(bytes) + `"` (strong) |
| Served by | `gm.console_catalog` (`GET /gm/v1/console/openapi.json`, every environment) and `platform.gm_openapi` (Dev/Test, unchanged path, same document) |
| Committed copies | `docs/api/gm-openapi.json` (framework test host), `tests/ConsumerFixture/docs/api/gm-openapi.json` (fixture), `<game>/docs/api/gm-openapi.json` (each game) |

### 1.1 Operation members

| Member | Rule | Source | Present on |
| --- | --- | --- | --- |
| `operationId` | route declaration `Name` | `RouteDeclarationMetadata`, else `WireCatalog.FindRouteName(method, template)` | every operation |
| `tags` | explicit `ITagsMetadata`, else `[area]`, `area` = `Name` before the first `.` | declaration | every operation |
| `summary` / `description` | `WithSummary` / `WithDescription` | module mapping | framework routes (always), game routes (optional) |
| `parameters` (path) | every `{name}` of the template, `required: true`, `schema: {type: string}`; `playerId` also `pattern: ^[A-Za-z0-9_-]{1,64}$` | template | every operation with template parameters |
| `parameters` (header) | `X-Request-Id`, `required: true`, `schema: {type: string, format: uuid}` | `GmRequestIdMetadata` | GM commands, framework actions that require it |
| `parameters` (query) | `pageSize` (required, integer, 1..200), `cursor` (optional, string); declared ones (`displayName`: required, string, 1..64) | `GmPagedMetadata`, `GmQueryParameterMetadata` | paged lists, documented query parameters |
| `requestBody` | `application/json`, `required: true`, `$ref` to the body schema | `AcceptsMetadata` (commands: added by `MapGmCommand`) | mutations with a body |
| `responses.200` | typed schema (`GmCommandResponse`, `GmPage…`, `GmOptionList`, …) | `Produces…` | every operation with a JSON answer |
| `responses.4XX`, `5XX` | unchanged descriptions | existing transformer | every operation |
| `x-gm-permission` | permission or `public` | existing | every operation |
| `x-gm-csrf` | unsafe method and (Gm profile or `RequireCsrf`) | existing | every operation |
| `x-gm-kind` | `command` (has `CommandOperationMetadata`) · `query` (GET) · `action` (other) | metadata + method | every operation |
| `x-gm-operation` | command operation name | `CommandOperationMetadata` | `x-gm-kind = command` |
| `x-gm-target` | `player` when the template contains `{playerId}`, else `global` | template | every operation |
| `x-gm-confirm` | `typed` (`GmConfirmMetadata`) else `simple` | metadata | non-GET operations |
| `x-gm-paged` | `true` | `GmPagedMetadata` | paged lists |

### 1.2 Schema members (request bodies of `/gm/**` endpoints)

| Member | Rule |
| --- | --- |
| `required` | JSON names with `[Required]`, plus `reason` for `IGmCommandBody` types (replaces the generator's list); `null` is removed from the `type` of a `[Required]` property |
| `reason` | `type: string`, `minLength: 1`, `maxLength: 500`, `pattern: "\\S"` |
| Keywords | `[Range]` → `minimum`/`maximum`; `[StringLength]`/`[MinLength]`/`[MaxLength]`/`[Length]` → `minLength`/`maxLength` (string) or `minItems`/`maxItems` (collection); `[RegularExpression]` → `pattern`; `[Description]` → `description` |
| `x-gm-options` | route name of `[GmOptions]`; on the property schema, or on `items` for a collection |
| Numbers | as generated with the GM JSON options (`long`: `type: ["integer","string"]`, `format: int64`, `pattern`) |
| Enums | camelCase string values (V12) |

Response schemas are left as generated.

## 2. Presentation metadata (in-process, `GameServer.Platform.Web.Endpoints`)

| Type | Fields | Added by | Read by |
| --- | --- | --- | --- |
| `GmConfirm` | `Simple`, `Typed` | — | — |
| `GmConfirmMetadata` | `Confirm` | `WithGmConfirm` | operation transformer |
| `GmPagedMetadata` | `ItemType` | `ProducesGmPage<T>` | operation transformer |
| `GmQueryParameterMetadata` | `Name`, `Required`, `MinLength?`, `MaxLength?`, `Description?` | `WithGmQueryParameter`, `ProducesGmPage<T>` (`pageSize`, `cursor` as integer/string specials) | operation transformer |
| `GmRequestIdMetadata` | — (marker) | `MapGmCommand`, `WithGmRequestId` | operation transformer |
| `GmOptionsAttribute` | `RouteName` | module code on body properties/parameters | schema transformer, startup check |

## 3. HTTP shapes

### 3.1 Validation answer (Problem Details, `400 BAD_REQUEST`)

```json
{
  "type": "https://errors.gameserver/bad_request",
  "title": "The request is invalid.",
  "status": 400,
  "code": "BAD_REQUEST",
  "codeNumber": 1,
  "requestId": "<correlation id>",
  "retryable": false,
  "errors": {
    "level": ["The field level must be between 1 and 2147483647."],
    "reason": ["reason is required (1..500 characters, not only white space)."]
  }
}
```

- `errors` keys: JSON paths (`name`, `outer.inner`, `items[2].quantity`), ordinal order; values: non-empty arrays,
  attribute order.
- Present only when a body rule failed; other `BAD_REQUEST`s (bad JSON, missing request id, `toDomain` rules) keep the
  current shape. `codeNumber` and `title` are whatever the catalog says today for `BAD_REQUEST`.

### 3.2 `GET /gm/v1/auth/methods` (Public)

```json
{ "local": true, "sso": false }
```

### 3.3 `GET /gm/v1/me` (and the local login answer)

```json
{ "staffId": "admin", "displayName": "Staff admin", "gameId": "sample", "environmentId": "test",
  "permissions": ["gm.access", "..."], "frameworkVersion": "0.7.0+abc1234", "gameVersion": "1.0.0" }
```

### 3.4 Option list (`economy.resource_options`, `identity.ban_scopes`, game option routes)

```json
{ "items": [ { "value": "gold", "label": "Gold" }, { "value": "gem", "label": "Gem" } ] }
```

`identity.ban_scopes`: `label = value` (`login`, `battle`, `economy`, …).

### 3.5 Catalog route

| Request | Answer |
| --- | --- |
| no session | 401 Problem Details (`UNAUTHENTICATED`) |
| session without `gm.access` or other game/env scope | 403 (`FORBIDDEN`) |
| `gm.access`, no or other `If-None-Match` | 200, body = canonical bytes, `Content-Type: application/json`, `ETag`, `Cache-Control: private, no-cache` |
| `gm.access`, matching `If-None-Match` (`"…"`, `W/"…"`, list, `*`) | 304, no body, `ETag`, `Cache-Control` |
| generation failed | 500 `INTERNAL` (logged once), next request retries |

## 4. Routes added

| Route | Method | Template | Profile | Permission | Surface | Owner | Condition | In document |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `gm.auth_methods` | GET | `/gm/v1/auth/methods` | Public | — | GmApi | gm | — | yes |
| `gm.console_catalog` | GET | `/gm/v1/console/openapi.json` | Gm | `gm.access` | GmApi | gm | — | no (excluded) |
| `economy.resource_options` | GET | `/gm/v1/economy/resources` | Gm | `player.read` | GmApi | economy | — | yes |
| `identity.ban_scopes` | GET | `/gm/v1/players/ban-scopes` | Gm | `player.ban` | GmApi | identity | — | yes |
| `fixture.note_set` (fixture only) | POST | `/gm/v1/players/{playerId}/note` | Gm | `fixture.note.edit` | GmApi | fixture | — | yes |
| `fixture.note_presets` (fixture only) | GET | `/gm/v1/fixture/note-presets` | Gm | `fixture.note.edit` | GmApi | fixture | — | yes |

`gm.*` routes: 27 → 29. GM surface routes generate no Unity constants (014), so WireGen outputs do not change.

## 5. Snapshot file

| Item | Rule |
| --- | --- |
| Content | canonical bytes (§1) of the host's document |
| Write | `GmOpenApiSnapshot.WriteAsync`: creates the folder, writes the bytes (temp file + replace) |
| Compare | `DiffAsync`: both sides split on `\n` after removing `\r`; first differing line → message; equal → `""` |
| Message | `GM OpenAPI document differs from <path> at line <n>:\n  committed: <text>\n  host:      <text>\nReview the change, then rewrite the file with GmOpenApiSnapshot.WriteAsync (framework tests: UPDATE_OPENAPI=1).` (texts cut at 200 chars; "end of file" when one side is shorter) |
| Missing file | `GM OpenAPI document <path> does not exist. Create it with GmOpenApiSnapshot.WriteAsync (framework tests: UPDATE_OPENAPI=1).` |

## 6. Startup report line (FR-045)

`[endpoint <METHOD> <template>] body <Type> property '<jsonPath>' names option route '<name>', which is not a GM GET route of this host. Fix: declare the route (RouteDeclaration.Get(..., RouteSurface.GmApi)) or correct the name in [GmOptions] (FR-045).`

One line per (endpoint, property); joins the aggregated startup report; the host exits 1 as for every endpoint fault.

## 7. Ownership

| Thing | Owner |
| --- | --- |
| Document transformers, catalog bytes, validator, presentation API | Platform.Web (`OpenApi/`, `Endpoints/`, `Commands/`) |
| `errors` member on the exception | Core (`Errors/GameServerException.cs`) |
| `gm.auth_methods`, `gm.console_catalog`, `/me` versions | Gm.Adapters |
| `identity.ban_scopes`, `BanScopes` | Identity.Adapters |
| `economy.resource_options` | Economy.Adapters |
| `GmOpenApiSnapshot` | TestKit |
| Committed documents | each repository (framework, fixture, game) |
