# Data model — 013 consumer-readiness

Log event schema, configuration keys, the request outcome, version information and the test-kit settings set. No new
Couchbase collection, index, document format or player wire message. Decisions and rationale: [research.md](research.md);
contracts: [contracts/](contracts/).

## 1. Request event (FR-080..FR-083, R11)

One event per HTTP request the host answers or the client aborts, except excluded paths. Logger category
`GameServer.Requests`, `EventId` 1, name `Request`. Message template (stored as `@mt` in the compact JSON file):
`{HttpMethod} {Route} -> {Status} {ResultCode} in {DurationMs} ms`. Every field below is an event property with this
exact name (PascalCase like the existing `GameId`, `InstanceId`, `RequestId` properties).

| Property | Type | Value | Source |
| --- | --- | --- | --- |
| `HttpMethod` | string | `GET`, `POST`, … | `Request.Method` |
| `Route` | string | route template (`/api/v1/player/receipts/{receiptId}`); unmatched: cleaned path (control chars → `?`, ≤ 120 chars + `...`), never the query string | `RouteEndpoint.RoutePattern.RawText` / `Request.Path` |
| `Profile` | string? | `Public`, `Player`, `Gm`, `Dev`; null when no endpoint matched | endpoint `SecurityProfileMetadata` |
| `Operation` | string? | command operation (`counter.increment`), null for non-command routes | outcome feature / `CommandOperationMetadata` |
| `ActorType` | string? | `player`, `gm`; null when not authenticated | `ActorClaims.GetActorType(User)` |
| `ActorId` | string? | player id or staff id, cleaned and cut at 64 chars (test-auth headers become claims, so they are sanitized too) | claim `gs:actor_id` |
| `RequestId` | string | correlation id (`X-Request-Id`, client UUID or server generated) | `CorrelationMiddleware.GetCorrelationId` |
| `TraceId` | string? | W3C trace id (32 hex) of the request activity; null when no activity | `Activity.Current` |
| `Status` | int? | HTTP status sent; null when aborted | outcome feature |
| `ResultCode` | string | code the client received: `OK`; business code at HTTP 200 (committed or rejected); `ErrorEnvelope`/Problem Details code; `ABORTED`; `INTERNAL` for unhandled failures; `HTTP_{status}` for a non-framework non-2xx response without code | outcome feature |
| `Replayed` | bool | command answered from a receipt | outcome feature |
| `NotModified` | bool | config get answered `not_modified` | outcome feature |
| `Aborted` | bool | client gone before the response started | outcome feature |
| `DurationMs` | double | wall time of the whole framework pipeline, rounded to 0.1 ms | `Stopwatch` in the outermost middleware |

Added by the existing enrichers when `AddObservability()` is used: `GameId`, `EnvironmentId`, `InstanceId`,
`SourceContext`. Never present: bodies, query strings, header values, cookies, tokens, passwords, guest secrets, provider
tokens, CSRF tokens, purchase tokens, IP addresses (FR-082).

Levels: Information when `Status` < 400 (including business codes at HTTP 200), Warning for 4xx and `Aborted`, Error for
5xx. `Observability:Logging:LevelOverrides:GameServer.Requests` filters them (e.g. `Warning` = failures only).

Example (compact JSON file line, wrapped here):

```json
{"@t":"2026-11-03T08:15:02.1234567Z","@mt":"{HttpMethod} {Route} -> {Status} {ResultCode} in {DurationMs} ms",
 "@tr":"4bf92f3577b34da6a3ce929d0e0e4736","HttpMethod":"POST","Route":"/api/v1/counter/increment","Profile":"Player",
 "Operation":"counter.increment","ActorType":"player","ActorId":"p_7f3a9c1e2b4d","RequestId":"1a2b3c4d-…",
 "TraceId":"4bf92f3577b34da6a3ce929d0e0e4736","Status":200,"ResultCode":"OK","Replayed":false,"NotModified":false,
 "Aborted":false,"DurationMs":12.4,"SourceContext":"GameServer.Requests","EventId":{"Id":1,"Name":"Request"},
 "InstanceId":"host-1","GameId":"sample","EnvironmentId":"local"}
```

Volume: ~300–400 bytes per event in the file (≈ 13–17 GB per instance per day at 500 requests/s, ≈ 1.3–1.7 GB at
50 requests/s, uncompressed). With the default file limits (100 MiB × 30 files ≈ 3 GiB per instance, LOG-02) 500
requests/s keep only ~4–5 hours of operational logs: operations that enable the event in Staging/Production raise the
file limits or ship logs (environments guide, PCR).

### Console line (request events, R12)

```text
08:15:02.123 INF POST /api/v1/counter/increment (counter.increment) player=p_7f3a9c1e2b4d req=1a2b3c4d -> 200 OK 12.4 ms
08:15:03.010 INF POST /api/v1/iap/purchase (iap.purchase) player=p_7f3a9c1e2b4d req=9f8e7d6c -> 200 ALREADY_GRANTED replayed 3.1 ms
08:15:04.500 WRN GET /api/v1/nope req=0c1d2e3f -> 404 NOT_FOUND 0.4 ms
08:15:05.000 INF POST /gm/v1/players/{playerId}/ban staff=admin req=77aa88bb -> 200 OK 8.0 ms
```

Shape: `{time} {LVL} {HttpMethod} {Route}[ ({Operation})][ player=|staff={ActorId}] req={RequestId[0..8]} -> {Status|-}
{ResultCode}[ replayed][ not_modified] {DurationMs} ms` (`-` and `ABORTED` when the client went away). Other events:
`{time} {LVL} {rendered message} [{SourceContext}]`, exception on the following lines.

## 2. Request outcome (FR-084, R11)

Per-request record on `HttpContext.Features` (`IRequestOutcomeFeature`, public). Installed by the outermost framework
middleware, mutated by framework writers, finalized after the pipeline.

| Member | Type | Set by | Readable |
| --- | --- | --- | --- |
| `ResultCode` | string? (get/set) | `ErrorResponseWriter`, command endpoints (player, GM), consumer endpoints; finalized (`OK`, `ABORTED`, `INTERNAL`, `HTTP_{n}`) | during the request: what was set so far; after completion: never null |
| `Operation` | string? (get/set) | command endpoints; `CommandOperationMetadata` at completion | always |
| `Replayed` | bool (get/set) | command endpoints | always |
| `NotModified` | bool (get/set) | config endpoint | always |
| `StatusCode` | int? (get) | finalization (`null` when aborted) | after completion |
| `Aborted` | bool (get) | finalization | after completion |
| `Completed` | bool (get) | finalization | always |

State ownership: the feature instance lives exactly as long as the `HttpContext`; the framework is the only writer
except a consumer endpoint/hook setting `ResultCode`/flags for its own responses. Reading it never touches the body.

## 3. Configuration keys

### 3.1 `GameServer:RequestLog` (`RequestLogSettings`, property `GameServerOptions.RequestLog`)

| Key | Type | Default | Valid | Notes |
| --- | --- | --- | --- | --- |
| `Enabled` | bool? | unset = on in Development/Test (local, dev, test hosts), off in Staging/Production (beta, uat, live) | — | explicit `true` outside Development/Test → startup warning with the volume estimate (FR-086 decision 2026-11-03) |
| `ExcludedPaths` | string[] | `[]` | ≤ 50 entries; each starts with `/`, ≤ 200 chars, no `?` | segment-prefix, case-insensitive; `/health/live` and `/health/ready` are always excluded and need no entry; arrays are replaced, not merged |

Validation errors join the aggregated startup report (`[request-log] …`, exit 1).

### 3.2 `Observability:Logging:Console` (`ConsoleLoggingSettings`, property `LoggingSettings.Console`)

| Key | Type | Default | Valid |
| --- | --- | --- | --- |
| `Enabled` | bool | `false` | — (outside Development/Test → startup warning) |
| `MinimumLevel` | string | `Information` | Serilog level name (same rule as `Logging:MinimumLevel`) |
| `BufferSize` | int | `1000` | ≥ 1; full queue → event dropped and counted in `log_dropped_total` |

The file sink (`FileEnabled`, `Directory`, limits) is unchanged; console output is in addition to files (FR-088).

### 3.3 Sample host flavor values

| Flavor file | `Observability:Logging:Console:Enabled` | `GameServer:RequestLog:Enabled` |
| --- | --- | --- |
| `appsettings.json` (base) | not set (false) | not set |
| `appsettings.Local.json` | `true` | not set (on: Development) |
| `appsettings.Dev.json` | `true` | not set (on: Development) |
| `appsettings.{Beta,Uat,Live}.json` | not set | not set (off: Staging/Production) |

`SampleHostConfigurationFileTests` pins this table.

### 3.4 Local-flavor settings set (`LocalFlavorSettings`, FR-012, R8)

| Key | Value applied by `LocalFlavorFactory` |
| --- | --- |
| `FileStore:Folder` | `{root}/data` |
| `GameServer:Outbox:SweeperEnabled` | `false` (opt-in) |
| `GameServer:Jobs:Enabled` | `false` |
| `GameServer:Hosting:DrainDelaySeconds` | `0` |
| `GameServer:Analytics:Directory` | `{root}/analytics` |
| `Observability:InstanceId` | `lf-{8 hex}`, unique per factory |
| `Observability:Logging:Directory` | `{root}/logs` |
| `Observability:Logging:Console:Enabled` | `false` |

`{root}` = `%TEMP%/gs-local-{guid}` (deleted on dispose) or `LocalFlavorOptions.Folder` (kept). Test overrides in
`LocalFlavorOptions.Settings` are applied last.

### 3.5 Kit `TestHost.DefaultSettings()`

| Key | Value |
| --- | --- |
| `GameServer:GameId` | `test` |
| `GameServer:EnvironmentId` | `test` |
| `GameServer:DeploymentEnvironment` | `Development` |
| `GameServer:Outbox:SweeperEnabled` | `false` |
| `GameServer:Jobs:Enabled` | `false` |
| `GameServer:Hosting:DrainDelaySeconds` | `0` |
| `GameServer:Analytics:TimeZone` | `UTC` |
| `Identity:CacheSeconds` | `0` |

No `GameServer:ConfigPath`: without it (and without the `config` argument) the host uses `TestGameConfig.Empty`
(label `test`). The framework's `SampleTestHost` adds `GameServer:ConfigPath = {BaseDirectory}/config` (sample config).

## 4. Version information (FR-050..FR-055, R14)

### 4.1 Build properties (framework `Directory.Build.props`)

| Property | Value | Effect |
| --- | --- | --- |
| `GameServerFrameworkVersion` | `0.7.0` | single declaration; equals `sdk/unity/com.gameserver.client/package.json` `version` (test) |
| `Version` | `$(GameServerFrameworkVersion)` | `AssemblyVersion` `0.7.0.0`, `FileVersion` `0.7.0.0`, `InformationalVersion` `0.7.0+{40-hex sha}` (no suffix without git metadata) |

Release rule: the annotated tag for a declared version `X.Y.Z` is `vX.Y.Z`; builds between tags are told apart by the sha.

### 4.2 `GameServerRuntimeInfo` (Platform, singleton, `GameServerBuilder.Runtime`)

| Member | Example | Source |
| --- | --- | --- |
| `GameId` | `sample` | `GameServer:GameId` |
| `EnvironmentId` | `local` | `GameServer:EnvironmentId` |
| `DeploymentEnvironment` | `Development` | resolved deployment environment |
| `InstanceId` | `host-1` | `Observability:InstanceId` → `HOSTNAME` → machine name (one implementation) |
| `GameAssembly` | `SampleHost` | `IHostEnvironment.ApplicationName` |
| `GameVersion` | `0.7.0+9c1e…` / `1.2.3` | informational version of the game assembly (else `AssemblyName.Version` 3 parts, else `unknown`) |
| `FrameworkVersion` | `0.7.0+9c1e…` | `GameServerVersion.FrameworkInformational` |

### 4.3 Where it appears

| Channel | Content |
| --- | --- |
| Startup log (Information, `GameServer.Startup`) | `Starting {GameId} {GameVersion} on framework {FrameworkVersion}: environment {EnvironmentId} ({DeploymentEnvironment}), instance {InstanceId}.` |
| OTel resource | `service.name` `game-server-{gameId}` (unchanged), `service.version` = `GameVersion`, `service.instance.id` = `InstanceId` (unchanged), `gameserver.framework.version` = `FrameworkVersion`, `deployment.environment.name` (unchanged) |
| GM `GET /gm/v1/ops/version` (`ops.read`) | [contracts/gm-ops-version.md](contracts/gm-ops-version.md) |
| Public endpoints, `/health/*` | nothing |

## 5. Error-code bands (FR-060..FR-062, R16)

| Range | Owner |
| --- | --- |
| 0–999 | core |
| 10000–10099 | sample.counter |
| 11000–11099 | economy |
| 12000–12099 | iap |
| 13000–13099 | gm |
| 14000–14099 | identity |
| 40000–40099 | sample.game |
| **50000–89999** | **reserved for game (consumer) modules** (`ErrorCodeBands.GameStart`/`GameEnd`) — first consumer uses 50000–50999 |
| 90000–90099 | template (docs example) |
| 90000–99999 | framework territory |

## 6. Command-context double state (R9)

| State | Owner | Lifetime |
| --- | --- | --- |
| Configuration (actor, now, config, receipt id, seeds, seeded claims) | test via fluent methods | until first `ICommandContext` call, then frozen |
| Inner `CommandContext` (rules, buffered states, claims, outbox) | Platform (shared with the runner) | one double = one handler run |
| Recorded lists (writes, outbox, claims) | double, appended after successful inner calls | readable any time |

Defaults: actor player `p_test` (`ActorType.Player`, game `test`, environment `test`, correlation id
`00000000-0000-4000-8000-000000000001`) for Player/Dev commands; GM commands default to staff `staff_test` holding the
descriptor's permission and target player `p_test`; `Now` = `2026-01-01T00:00:00Z`; config `TestGameConfig.Empty`;
request id `00000000-0000-4000-8000-000000000001` → receipt id `ReceiptIds.Compute(actorId, playerId, operation,
requestId)`.

## 7. `run-local` resolution output (R15)

`GameServer.LocalRun describe` prints `key=value` lines (UTF-8, one per key, values without newlines):

| Key | Example | Rule |
| --- | --- | --- |
| `project` | `E:/game/src/ZhPvp.Host/ZhPvp.Host.csproj` | file, or the folder's single `*.csproj` |
| `projectDir` | `E:/game/src/ZhPvp.Host` | content root of `dotnet run` |
| `projectName` | `ZhPvp.Host` | default display name |
| `isSample` | `false` | project = framework `samples/SampleHost/SampleHost.csproj` |
| `flavor` | `Local` | profile `environmentVariables.ASPNETCORE_ENVIRONMENT` (required) |
| `port` | `5180` | first `http://` URL of `applicationUrl`; none → `5080` |
| `portNote` | `` / `no http URL in profile 'local'; using 5080` | banner note |
| `dataFolder` | `E:/game/.local/data` | `FileStore:Folder` of base + flavor + env vars, relative to `projectDir` |
| `logFolder` | `E:/game/src/ZhPvp.Host/logs` | `Observability:Logging:Directory` (default `logs`), relative to `projectDir` |
| `sdkVersion` | `10.0.403` | `sdk.version` of the nearest upward `global.json` |
| `globalJson` | `E:/game/global.json` | that file |

Errors (exit 2): project path missing / folder with 0 or > 1 projects; no `Properties/launchSettings.json`; profile not
found (lists the defined profiles); profile without `ASPNETCORE_ENVIRONMENT`; unreadable JSON (file and position);
no `global.json` above the project.
