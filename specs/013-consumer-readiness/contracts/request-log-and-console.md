# Contract — request event, request outcome and console sink (013, FR-080..FR-089)

Operations- and consumer-facing contract, published in PCR platform §13/§19 and
`docs/guides/environments-and-local-run.md`. Renaming or removing a property, the category, the event id, a fixed result
code or a setting is a breaking change (PCR-01). Field types and examples: [data-model §1–§3](../data-model.md).

## 1. Request event

- Exactly one event per HTTP request the host answers or the client aborts, for every route (player, GM, public, Dev;
  commands, queries, login, snapshot, config; 404/405 and every other error), written when the framework pipeline has
  completed.
- Not written for `/health/live`, `/health/ready` and paths in `GameServer:RequestLog:ExcludedPaths`; not written when
  request logging is off for the deployment (§4).
- Category `GameServer.Requests`, event id `1` / `Request`; properties `HttpMethod`, `Route`, `Profile`, `Operation`,
  `ActorType`, `ActorId`, `RequestId`, `TraceId`, `Status`, `ResultCode`, `Replayed`, `NotModified`, `Aborted`,
  `DurationMs` (+ `GameId`, `EnvironmentId`, `InstanceId`, `SourceContext` from the operational log enrichment).
- `ResultCode` = the code the client received (API-11): `OK` when none; the business code of a command response
  (committed with a code or rejected at HTTP 200); the `ErrorEnvelope` / Problem Details code; `ABORTED` when the client
  went away before the response started (then `Status` is absent and the event never claims a commit it did not see);
  `INTERNAL` for unhandled failures; `HTTP_{status}` for a non-2xx response written outside the framework without a code.
  For command routes the event and `command_results_total{code}` agree.
- Levels: Information (< 400), Warning (4xx, aborted), Error (5xx).
- Privacy (LOG-05): no body, query string, header value, cookie, token, password, guest secret, provider token, CSRF
  token, purchase token or IP address. `Route` is the endpoint template; unmatched paths are cleaned (control characters
  → `?`) and cut at 120 characters. `ActorId` comes from the authenticated principal only, cleaned and cut at 64.
  Player and staff ids are internal ids under the existing LOG-04/TBD-08 policy.
- Loss (LOG-06): the event goes through the bounded, dropping operational log pipeline; when a sink cannot keep up the
  event is dropped and counted (`log_dropped_total`); logging never delays or fails a request; a failure while building
  the event is swallowed (counted as a sink error) and the response is unchanged.

## 2. Request outcome feature (semantic contract with consumer code)

```csharp
app.Use(async (context, next) =>          // consumer middleware registered BEFORE app.UseGameServer()
{
    await next();
    var outcome = context.RequestOutcome(); // GameServer.Platform.Web.Requests
    // outcome.Completed == true, outcome.ResultCode != null, StatusCode null only when Aborted
});
app.UseGameServer();
```

- Set for every response the framework writes (commands, rejections, errors, config, every other framework route) and
  finalized after the framework pipeline (same values as the request event, whether or not the event is enabled).
- Readable by consumer endpoints and `afterExecute` hooks during the request (values set so far) and by consumer
  middleware outside the framework pipeline after it returned (final values). Covers rejections and non-command routes,
  which the `afterExecute` hook of `MapPlayerCommand` does not.
- A consumer endpoint may set `ResultCode`, `Operation`, `Replayed`, `NotModified` for its own responses; the framework
  does not overwrite a code a consumer endpoint set, except `ABORTED`/`INTERNAL` at finalization.
- Reading it never buffers, copies or parses the response body.
- Not a wire contract: clients never see it.

## 3. Console sink

- `Observability:Logging:Console:Enabled` (default `false`; the sample host's `Local` and `Dev` flavor files set `true`),
  `MinimumLevel` (default `Information`), `BufferSize` (default 1 000).
- One human-readable line per event at or above `MinimumLevel`: request events in the compact request shape of
  data-model §1, other operational events as `time level message [category]` with the exception below. Colours only when
  stdout is a terminal.
- Bounded and dropping like the file sink (never the default console provider, which waits when its queue is full);
  drops counted in `log_dropped_total`; a stopped or redirected stdout never blocks requests.
- In addition to the file sink, never instead of it; the file content is unchanged; operational logs still go to no other
  provider.
- Enabling it outside Development/Test is allowed and produces one startup warning.

## 4. Settings and defaults per flavor

| Flavor (deployment environment) | Request event | Console sink (sample host) |
| --- | --- | --- |
| local, dev (Development) | on (default) | on (flavor file) |
| framework/consumer test hosts (Development/Test) | on (captured by the test logger) | off (`LocalFlavorFactory` forces off) |
| beta, uat (Staging) | **off** (default); `GameServer:RequestLog:Enabled=true` turns it on (+ startup warning) | off |
| live (Production) | **off** (default); same switch | off |

Level per deployment: `Observability:Logging:LevelOverrides:GameServer.Requests` (`Warning` = only 4xx/5xx/aborts,
`Fatal` = effectively none). Startup validation (exit 1) for invalid `ExcludedPaths` entries and console levels/buffer.

Volume when on: ~300–400 B per event (≈ 13–17 GB per instance per day at 500 requests/s). With the default file limits
(100 MiB × 30 files) that is ~4–5 hours of retained operational logs at 500 requests/s; raise
`Observability:Logging:FileSizeLimitBytes`/`RetainedFileCountLimit` or ship the files before enabling it in production.
(FR-086 decision: orchestrator default 2026-11-03, owner may override.)
