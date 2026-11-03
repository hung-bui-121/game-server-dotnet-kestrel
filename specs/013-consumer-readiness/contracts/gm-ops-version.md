# Contract — GM ops version read (013, FR-055)

Additive GM API endpoint. Published in `docs/pcr/gm.md` (permission table and ops route table) and in the generated
`docs/api/gm-openapi.json` (regenerate with `UPDATE_OPENAPI=1` while running `GameServer.Gm.Tests`, review, commit —
`OpenApiTests` fails until the committed copy matches the running host). Removing or renaming a field is a breaking change
(PCR-01).

## Endpoint

| | |
| --- | --- |
| Route | `GET /gm/v1/ops/version` (constant `GmModule.VersionRoute`) |
| Profile | `Gm` (`AsGmEndpoint(GmPermissions.OpsRead)`) |
| Permission | `ops.read` (existing; same as `GET /gm/v1/health`, `GET /gm/v1/ops/gate`) |
| CSRF | no (safe method) — `x-gm-csrf: false` |
| Mapped | whenever the GM API is mapped (`GameServer:Hosting:MapGmApi`, `GmModule` registered), every environment |
| Cache | `Cache-Control: no-store` (all GM responses) |
| Audit | none (read-only, no player data) |
| OpenAPI | `Produces<GmVersionResponse>()`, tag `GmModule`, `x-gm-permission: ops.read`, `x-gm-csrf: false` |

## Response 200 `application/json` (`GmVersionResponse`, camelCase like every GM response)

```json
{
  "gameId": "sample",
  "environmentId": "local",
  "instanceId": "host-1",
  "gameVersion": "0.7.0+9c1e2f0a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e",
  "frameworkVersion": "0.7.0+9c1e2f0a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e"
}
```

| Field | Type | Meaning |
| --- | --- | --- |
| `gameId` | string | `GameServer:GameId` of the deployment |
| `environmentId` | string | `GameServer:EnvironmentId` (`local`, `dev`, `beta`, `uat`, `live`, …) |
| `instanceId` | string | instance id of this process (`Observability:InstanceId` → `HOSTNAME` → machine name) — the same value as in log file names and `service.instance.id` |
| `gameVersion` | string | informational version of the host (game) entry assembly; `unknown` if unreadable |
| `frameworkVersion` | string | informational version of the framework assemblies: declared SemVer, `+{sha}` when built from git |

Values come from the `GameServerRuntimeInfo` singleton (data-model §4.2); the answer describes the instance that served
the request (behind a load balancer, repeat the call to see other instances).

## Errors (Problem Details, existing contract)

| Case | Status | Code |
| --- | --- | --- |
| no GM session / invalid cookie | 401 | `UNAUTHENTICATED` (or the session-specific codes of slice 007) |
| session without `ops.read` | 403 | `FORBIDDEN` |
| GM API not mapped on this host | 404 | `NOT_FOUND` |

## Non-disclosure rule

`/health/live`, `/health/ready`, `/v1/time`, every other Public endpoint and every player route carry no framework or
game version in body or headers (FR-055, OBS-03). `GmVersionEndpointTests` scans them.

## C# surface

```csharp
namespace GameServer.Modules.Gm;

public sealed record GmVersionResponse(
    string GameId,
    string EnvironmentId,
    string InstanceId,
    string GameVersion,
    string FrameworkVersion);

// added to the existing GmModule (GameServer.Modules.Gm.Adapters)
public const string VersionRoute = "/gm/v1/ops/version";
```
