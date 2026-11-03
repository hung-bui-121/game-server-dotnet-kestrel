# Wire changes — 016 login-command

All changes are additive (API-03): new field numbers, one new message. No field is renamed, renumbered or given a new
meaning. The route catalog gains no route.

## 1. Proto

### 1.1 `proto/gameserver/core/v1/core.proto`

```proto
// A client-visible aggregate the response cannot carry (016, FG-029): absent from sections/updates, never sent empty.
message UnavailableSection {
  string id = 1;              // aggregateId
  string code = 2;            // AGGREGATE_VERSION_AHEAD | AGGREGATE_UNREADABLE
  int32 code_number = 3;
}

message SnapshotResponse {
  // fields 1-4 unchanged
  repeated UnavailableSection unavailable_sections = 5;   // 016
}

message CommandResponse {
  // fields 1-9 unchanged
  repeated UnavailableSection unavailable_sections = 10;  // 016: replay or post-commit mapping could not carry it
  map<string, string> details = 11;                       // 016: details of a business rejection (HTTP 200), e.g. part/bytes/limit
}
```

### 1.2 `src/GameServer.Modules.Identity.Adapters/Protos/identity.proto`

```proto
message LoginResponse {
  // fields 1-7 unchanged
  bool is_new_player = 8;                                          // 016: this login created the player
  repeated gameserver.core.v1.UnavailableSection unavailable_sections = 9;   // 016
}
```

`ErrorEnvelope` is unchanged. The new codes use `details["aggregate"]`, or `part`/`bytes`/`limit` for
`COMMAND_OUTPUT_TOO_LARGE`.

### 1.3 Regeneration

`tools/GameServer.WireGen generate` + `verify` for `sdk/GameServer.Client/wiregen.json` (changes `Core.cs`,
`Identity.cs` in `Runtime/Generated`), `samples/SampleGame/wiregen.json` and `tests/ConsumerFixture/tools/wiregen.json`
(both expected unchanged). `CommittedOutputTests` checks all three (research R17).

## 2. Values per response

| Response | `is_new_player` | `unavailable_sections` | `details` |
| --- | --- | --- | --- |
| `LoginResponse`, creating login | `true` | non-profile aggregates that failed to load or map | — |
| `LoginResponse`, other logins | `false` | same | — |
| `SnapshotResponse` (all, or explicit list) | — | requested sections that failed | — |
| `CommandResponse`, executed OK / committed code | — | touched sections whose new state could not be mapped (commit stands) | empty |
| `CommandResponse`, replayed | — | receipt aggregates that cannot be read now, or not mapped | empty |
| `CommandResponse`, business rejection | — | empty | rejection details (`COMMAND_OUTPUT_TOO_LARGE`: `part`, `bytes`, `limit`; module codes: what `Reject(code, details)` passed) |
| `ErrorEnvelope` `AGGREGATE_VERSION_AHEAD` | — | — | `aggregate`; `retryable = true`, `retry_after_ms = 1000`, HTTP 503 |
| `ErrorEnvelope` `AGGREGATE_UNREADABLE` | — | — | `aggregate`; HTTP 500 |

Rules:
- A section is never listed in `sections`/`updates` and in `unavailable_sections` of the same response.
- `unavailable_sections` is ordered by `id` (ordinal).
- Every section that is listed nowhere is byte-identical to an undamaged read (SC-004).
- A broken `profile` never appears in a login's `unavailable_sections`: a profile *load* failure fails the login. A
  profile *mapper* failure is listed like any other section, because the server still holds the state for its checks.

## 3. HTTP status

| Case | Status |
| --- | --- |
| snapshot / login with unavailable sections, even all of them | 200 |
| command refused because a touched or read aggregate is broken | 503 (`AGGREGATE_VERSION_AHEAD`) or 500 (`AGGREGATE_UNREADABLE`), error envelope |
| `COMMAND_OUTPUT_TOO_LARGE` | 200, command response |
| broken profile on login or bearer authentication | 503 / 500 envelope |
| initializer failure during a creating login | 500 `INTERNAL` envelope (D15) |
| creating login, ambiguous commit | 503 `COMMIT_UNKNOWN_RETRY` (was 500) |
| login, transaction expired or race rounds exhausted | 409 `CONCURRENT_UPDATE`, retryable (was 500) |

## 4. GM JSON (OpenAPI `docs/api/gm-openapi.json`)

### 4.1 Player view (`GET /gm/v1/players/{playerId}`)

```json
{
  "playerId": "…",
  "aggregates": [ { "id": "inventory", "exists": true, "…": "unchanged" } ],
  "deletion": null,
  "unreadable": [
    { "id": "wallet", "code": "AGGREGATE_VERSION_AHEAD", "reason": "version_ahead", "storedSchemaVersion": 3, "codeSchemaVersion": 2 }
  ]
}
```

- `unreadable` is always present (empty array when nothing failed) and ordered by `id`.
- A broken `profile` is listed there and gives `deletion: null`. The player still counts as existing (its document
  exists).

### 4.2 GM command response

Adds `details` (object, string values, empty for OK) and `unavailable` (array of `{ "id", "code" }`, empty normally).
`COMMAND_OUTPUT_TOO_LARGE` is a business code there as well (HTTP 200).

## 5. Compatibility

| Peer | Effect |
| --- | --- |
| SDK ≤ 0.7.0-before-016 against this server | ignores fields 8/9, 5, 10/11 (proto3 unknown fields); a broken section is simply absent from `sections` (BC-3); `AGGREGATE_VERSION_AHEAD` is retried by the existing retryable rule; `COMMAND_OUTPUT_TOO_LARGE` is returned as a business code |
| This SDK against a server before 016 | `IsNewPlayer` reads `false`; `unavailable_sections`/`details` empty; nothing else changes |
| Mixed rollout (old and new instances) | old instances never send the new fields; `is_new_player` from an old instance is `false` (presentation flag only, D1) |
| GM frontend | additive JSON members only |
