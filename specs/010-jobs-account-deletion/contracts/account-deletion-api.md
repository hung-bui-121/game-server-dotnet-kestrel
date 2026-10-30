# Contract — account deletion (010)

Player API: Protobuf, `ErrorEnvelope` with `code` + `details` (API-05, API-11). GM API: JSON, Problem Details with
`code`; mutations need `X-Request-Id` (UUID), `reason` (1..500) and `X-CSRF-Token` (SEC-05).

## Player API

| Route | Profile | Request → Result | Errors |
| --- | --- | --- | --- |
| `POST /api/v1/identity/delete-account` | Player (command, `X-Request-Id`) | `RequestAccountDeletionRequest {}` → `CommandResponse` with `RequestAccountDeletionResult {requested_at_utc_ms, purge_after_utc_ms}` and a `profile` section update | 401 `ACCOUNT_DELETED` `{purgeAfter}` when already deleted (also the answer to a retry on the serving instance — the deletion is committed); 403 `ACCOUNT_SUSPENDED` for a login ban (authentication fails first); command-group bans do not block it |

```proto
// identity.proto (package GameServer.Modules.Identity.Protocol.V1), additive
message RequestAccountDeletionRequest {}
message RequestAccountDeletionResult {
  int64 requested_at_utc_ms = 1;
  int64 purge_after_utc_ms = 2;
}
```

Effects: one transaction {`profile.deleted = {requestedAtUtc, purgeAfterUtc}`, `sessionEpoch + 1`, receipt}. Every
session of the player answers `ACCOUNT_DELETED` at once on the serving instance, within 60 s elsewhere.

### `ACCOUNT_DELETED` (core 17, 401) — details (additive)

| Key | Present | Value |
| --- | --- | --- |
| `purgeAfter` | pending or purging | `yyyy-MM-ddTHH:mm:ssZ` (UTC) |

Returned by: guest login, provider login (before any `SWITCH_ACCOUNT` check), every authenticated request (bearer
check order deleted → suspended → replaced → unknown), and the delete-account command. After the purge, a token whose
session record is still cached answers `ACCOUNT_DELETED` without details; once removed, `UNAUTHENTICATED`. A guest or
provider credential of a purged player creates a **new** player.

Linking an identity of a pending player from another account: 409 `IDENTITY_CONFLICT` (unchanged).

## GM API

| Route | Permission | CSRF | Body → Response | Errors |
| --- | --- | --- | --- | --- |
| `GET /gm/v1/players/{playerId}` | `player.read` | — | `GmPlayerView` + `deletion` (below) | 404 when no aggregate and no purge record |
| `GET /gm/v1/players/by-mapping/{mappingKey}` | `player.read` | — | same; works while pending | 404 after purge (mapping removed) |
| `GET /gm/v1/players/search?displayName=` | `player.read` | — | works while pending | not found after purge |
| `POST /gm/v1/players/{playerId}/deletion/cancel` | `player.ban` | yes | `{reason}` → `GmCommandResponse` (`result = {before: DeletionInfo}`) | 403 without `player.ban`; 404 unknown player; 409 `DELETION_NOT_PENDING`; 409 `DELETION_GRACE_OVER` (`now >= purgeAfter`, purge started or purged); 409 `IDEMPOTENCY_PAYLOAD_MISMATCH` |

`deletion` object (null when the player has no deletion):

```json
{
  "state": "pending | purging | purged",
  "requestedAtUtc": "2026-10-30T10:00:00.000Z",
  "purgeAfterUtc": "2026-11-29T10:00:00.000Z",
  "purgeStartedAtUtc": null,
  "purgedAtUtc": null
}
```

For `purged` the view has `aggregates: []` and no personal data; the anonymous id is never returned for the old id.

Cancel effects: one transaction {clear `profile.deleted`, receipt, audit `gm_audit` with actor, permission
`player.ban`, action `identity.cancel_deletion`, target, reason, before (`DeletionInfo`)/after (`null`), time,
correlation}. Identities resolve to the player again; old sessions stay revoked; a later request starts a new 30-day
grace. Cancel and purge start race on `pa_profile`: exactly one commits.

Other GM mutations (`identity.ban`, `economy.adjust`, game GM commands) on a pending or purging player: 404 (unchanged).

## Error codes (identity range 14000–14099)

| Number | Name | HTTP | Retryable | Meaning |
| --- | --- | --- | --- | --- |
| 14005 | `DELETION_NOT_PENDING` | 409 | no | The player has no pending account deletion. |
| 14006 | `DELETION_GRACE_OVER` | 409 | no | The grace period is over; the account deletion can no longer be cancelled. |

## Unity SDK

```csharp
// GameServerClient
/// OK: the session and sections are cleared and AccountBlocked is raised with code ACCOUNT_DELETED, details purgeAfter.
/// GameServerException ACCOUNT_DELETED from this call also means the deletion is committed (retry after a lost response).
public Task<CommandResult<RequestAccountDeletionResult>> RequestAccountDeletionAsync(CancellationToken cancellationToken = default);
```

`GameServerError.Details["purgeAfter"]` is exposed for every `ACCOUNT_DELETED` (login or request) so the game can show
the erase date and point to support (cancel is a GM action only).

## Extension contracts (EXT-01, PCR platform)

- `CollectionSpec(..., Deletion: DeletionTreatment, PlayerField = "playerId")` — mandatory treatment per owned
  collection and per projection target.
- `ModuleDescriptor.DeletionSteps = [new DeletionStepRegistration(name, typeof(TStep))]`,
  `TStep : IAccountDeletionStep` — idempotent; gets `IPlayerDataEraser` (own collections only, fenced transactions).
- `ModuleDescriptor.Jobs = [new JobDescriptor(name, typeof(TJob), intervalSeconds)]`, `TJob : ILeaseJob`.
- `IGmTargetValidator.ValidateAsync(playerId, operation, ct)` — **breaking** for game implementations (operation added).
