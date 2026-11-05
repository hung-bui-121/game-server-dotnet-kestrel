# Contract: the GM document and the console bootstrap routes (slice 017, GM-1)

Consumers: the GM console of GM-2..GM-4 (catalog parser, nav, generic forms), TypeScript codegen (`openapi-typescript`
on the committed file), a game's CI (snapshot). Rules are in [data-model.md](../data-model.md) §1–§5; this file shows the
resulting shapes and the compatibility promise.

## 1. A GM command operation (example: `economy.adjust` on the framework test host)

```json
"/gm/v1/players/{playerId}/resources/adjust": {
  "post": {
    "tags": ["economy"],
    "summary": "Adjust a resource balance of the player (GM-03).",
    "operationId": "economy.adjust",
    "parameters": [
      { "name": "playerId", "in": "path", "required": true,
        "schema": { "pattern": "^[A-Za-z0-9_-]{1,64}$", "type": "string" } },
      { "name": "X-Request-Id", "in": "header", "required": true,
        "schema": { "type": "string", "format": "uuid" } }
    ],
    "requestBody": {
      "content": { "application/json": { "schema": { "$ref": "#/components/schemas/AdjustResourceBody" } } },
      "required": true
    },
    "responses": {
      "200": { "description": "OK", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/GmCommandResponse" } } } },
      "4XX": { "description": "Problem Details (application/problem+json) with `code` and `codeNumber` from the error-code catalog." },
      "5XX": { "description": "Problem Details; `retryable` tells whether the same request may be retried." }
    },
    "x-gm-permission": "resource.adjust",
    "x-gm-csrf": true,
    "x-gm-kind": "command",
    "x-gm-operation": "economy.adjust",
    "x-gm-target": "player",
    "x-gm-confirm": "typed"
  }
}
```

Member order inside an operation is whatever the OpenAPI writer produces; consumers must not rely on it. The exact
committed text is the reference (`docs/api/gm-openapi.json`).

```json
"AdjustResourceBody": {
  "required": ["resource", "reason"],
  "type": "object",
  "properties": {
    "resource": { "type": "string", "x-gm-options": "economy.resource_options" },
    "delta": { "pattern": "^-?(?:0|[1-9]\\d*)$", "type": ["integer", "string"], "format": "int64" },
    "reason": { "maxLength": 500, "minLength": 1, "pattern": "\\S", "type": "string" }
  }
}
```

`resource` is `string?` in C#, but `[Required]` refuses null and blank, so the schema drops `null` from its type and lists
it in `required`. A console renders `required` as "must be filled".

## 2. Kinds, targets and confirmations of the framework operations

| Route | Kind | Target | Confirm | Paged | Notes |
| --- | --- | --- | --- | --- | --- |
| `identity.ban` | command | player | typed | — | `scope` items `x-gm-options: identity.ban_scopes` |
| `identity.unban` | command | player | simple | — | |
| `identity.cancel_deletion` | command | player | typed | — | |
| `economy.adjust` | command | player | typed | — | `resource` `x-gm-options: economy.resource_options` |
| `progression.set` (sample) | command | player | simple | — | `level`/`xp`/`stage` `minimum` |
| `gm.ops_rotate_keys` | action | global | typed | — | header `X-Request-Id` |
| `gm.ops_gate_maintenance` | action | global | typed | — | `message` `maxLength: 500`; header |
| `gm.ops_gate_min_client_version` | action | global | simple | — | header |
| `gm.staff_create`, `gm.staff_grant`, `gm.staff_disable`, `gm.staff_enable`, `gm.staff_revoke_sessions` | action | global | simple | — | header |
| `gm.auth_login` (Dev/Test), `gm.auth_logout` | action | global | simple | — | `x-gm-permission: public` / `gm.access` |
| `gm.player_search` | query | global | — | yes | `displayName` required 1..64 |
| `gm.player_receipts`, `gm.player_audit`, `economy.ledger` | query | player | — | yes | audit/ledger items free-form |
| `gm.staff_list`, `gm.staff_audit` | query | global | — | yes | staff audit items free-form |
| `gm.player`, `gm.player_by_mapping`, `gm.receipt` | query | player / global / global | — | — | |
| `economy.resource_options`, `identity.ban_scopes` | query | global | — | — | `GmOptionList` |
| `gm.me`, `gm.health`, `gm.ops_version`, `gm.ops_gate`, `gm.staff`, `gm.auth_csrf`, `gm.auth_methods`, SSO routes | query | global | — | — | |

`gm.player_by_mapping` has no `{playerId}` (it resolves one), so its target is `global`.

## 3. Bootstrap routes for a console

Order a console follows (design §10): `GET /gm/v1/auth/methods` → sign-in → `GET /gm/v1/auth/csrf` → `GET /gm/v1/me` →
`GET /gm/v1/console/openapi.json` (with `If-None-Match` when it has a copy).

| Route | Auth | Answer | Cache |
| --- | --- | --- | --- |
| `GET /gm/v1/auth/methods` | Public | `{ local, sso }` | none |
| `GET /gm/v1/me` | `gm.access` | 013 members + `frameworkVersion`, `gameVersion` | none |
| `GET /gm/v1/console/openapi.json` | `gm.access` | the GM document (canonical bytes) | `ETag`, `Cache-Control: private, no-cache`, 304 |
| `GET /gm/v1/openapi/gm.json` | Public, Dev/Test only | the same document | none (unchanged) |

Cross-origin access to these routes (CORS headers, `Access-Control-Expose-Headers: ETag`) is GM-2.

## 4. Validation answer

See data-model §3.1. A console maps `errors["<path>"]` to the form field with that JSON path; `reason` errors to the
reason box; a 400 without `errors` is shown as a general message (cross-field rules, bad JSON, missing request id).

## 5. Compatibility promise (PCR gm)

- Within `/gm/v1`, document changes are additive: new operations, new optional members, new `x-gm-*` values. Removing or
  renaming an operation id, a path, a required member or changing a kind/target of an existing route is a breaking
  change recorded in the release note (ARC-08).
- `x-gm-*` keys are framework-owned; a game adds none (it uses the presentation API).
- The committed document of a game equals what its host serves (`GmOpenApiSnapshot.DiffAsync` empty in the game's CI).
