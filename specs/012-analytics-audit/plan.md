# Implementation Plan: Analytics audit log for Login and Payment (slice 012 analytics-audit)

**Branch**: `012-analytics-audit` | **Date**: 2026-10-31 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/012-analytics-audit/spec.md`

## Summary

The framework gains an analytics event stream (first, narrow part of ANA-01) with two event types. **Payment**: the
`iap.purchase` handler adds one outbox entry of a new generic kind `analytics` (Core helper `AddAnalyticsEvent`, no
player id in the payload) in the grant transaction; one platform consumer turns it into a record whose `userID`,
`chargeTime`, `receiptId` and `eventId` come from the receipt, and acknowledges only after every sink has handed the line
to the OS — at-least-once, bounded inline wait (500 ms), retried by the existing sweeper. `transactionID` is the store's
own id (`apple:{transactionId}`, `google:{orderId}`, `google:h:{SHA-256 of token}` fallback); `VerifiedPurchase` gains
init-only `IsTest`, `PurchasedAt`, `PurchaseCategory`. **Login**: `LoginCompletion` emits `source=login` on success; the
PlayerBearer handler emits `source=resume` once per player, local day and instance — best effort through the same bounded
queue (loss accepted, JOB-02). A single dispatcher formats each record once and writes batches to every registered sink.
The default sink (`AddAnalyticsFiles()`, Platform.Observability) writes `{gameId}-{instanceId}-{event}-{yyyyMMdd}.jsonl`
by UTC day of `IClock`, guarded by a per-instance lock file, with metrics and a Degraded-only readiness check. Every
record carries UTC, local time with per-instant offset and the IANA zone of the deployment (`GameServer:Analytics:TimeZone`,
fail-fast). Decisions: [research.md](research.md); schemas and settings: [data-model.md](data-model.md); contracts:
[contracts/analytics-records.md](contracts/analytics-records.md) (data team),
[contracts/analytics-operations.md](contracts/analytics-operations.md) (operations).

## Technical Context

**Language/Version**: C# / .NET 10 (SDK 10.0.403)

**Primary Dependencies**: unchanged; no new package (`System.Threading.Channels`, `System.Text.Json`,
`TimeZoneInfo` with ICU/tzdata, `IHostedLifecycleService` are in the shared framework)

**Storage**: no new collection, index or deletion treatment; one new outbox kind inside existing receipts; new
append-only JSONL files per instance in `GameServer:Analytics:Directory`

**Testing**: xUnit v3 + Shouldly; `TestHost` with `InMemoryAnalyticsSink` + `FakeClock`; file sink tests on temp folders;
`WebApplicationFactory<Program>` smoke test on the local flavor; architecture tests; opt-in performance comparison
(`GAMESERVER_PERF=1`) and load-tool purchase step; one Couchbase integration case (sweeper delivery)

**Target Platform**: Linux containers (beta/uat/live), Windows/macOS/Linux developer machines

**Project Type**: web-service framework + sample host

**Performance Goals**: login p95 +≤ 1 ms, purchase p95 +≤ 100 ms on the dev machine (SC-004 as amended 2026-10-31: one fsync per payment batch; was +≤ 5 ms); 0 login drops at 60 logins/s with the default
queue (SC-008); inline payment wait ≤ `DeliveryTimeoutMs` (500 ms) worst case

**Constraints**: ANA-01, JOB-02 (at-least-once for payment; in-memory loss recorded in PCR for login), LOG-01/03/05/06,
OBS-01..03, TIME-01, ARC-01 (module code uses Core only), PRIV-01 + owner Q11 (raw id, 25 months), DAT-04/D2 (receipt
≤ 4 KiB), PERF-01 (no blocking, bounded queues), PCR-01

**Scale/Scope**: 2 event types; 9 new settings; 1 new outbox kind; 1 new sink + 1 test sink; 7 metrics; 1 readiness check;
no player-facing or GM API change

## Overview

```text
 iap.purchase handler (IAP business, Core API only)
   └─ ctx.AddAnalyticsEvent(payment, fields) ──► receipt.outbox[kind=analytics]   (same transaction as the grant)
                                                       │ inline relay (awaited) / sweeper / purge relay
                                                       ▼
                                   AnalyticsOutboxConsumer (Platform) ── inactive? ack
                                                       │ DeliverAsync (wait ≤ DeliveryTimeoutMs)
 LoginCompletion ──► LoginAnalytics.LoggedIn ─┐        ▼
 PlayerBearer handler ─► LoginAnalytics       ├─► AnalyticsPipeline (Platform): bounded Channel (10 000)
     .Authenticated (DailyPlayerSet)          │      TryEmit = best effort   │ single dispatcher, batch ≤ 512
                                IAnalyticsEmitter    AnalyticsRecordFormatter ─ AnalyticsTimeZone (IANA, per-instant offset)
                                                       │ AnalyticsRecord(event, eventId, UTF-8 line)
                                       ┌───────────────┴───────────────┐
                         AnalyticsFileSink (Observability)     InMemoryAnalyticsSink (Testing)   [P1: Couchbase, Kafka, HTTP]
                           .lock · {game}-{inst}-{event}-{yyyyMMdd}.jsonl
                           AnalyticsHealthCheck (ready, Degraded only) · metrics GameServer.Analytics
```

Style: unchanged modular monolith. Transactional outbox for the authoritative event, in-process bounded queue for the
best-effort events, ports-and-adapters sink seam, explicit host composition (`AddAnalyticsFiles()`).

## Module breakdown

| Module (assembly / namespace) | Responsibility | Depends on | Does NOT own |
| --- | --- | --- | --- |
| Core `GameServer.Analytics` | Event type value + outbox helper (payload shape, kind `analytics`) | `ICommandContext` | formatting, delivery, time zone, files |
| Platform `GameServer.Platform.Analytics` — `AnalyticsOptions`/validator | Settings and their validation | `GameServerOptions`, sinks (to know "active") | directory I/O |
| — `AnalyticsTimeZone` | Resolve zone once; format an instant three ways; local date | options | clock reads |
| — `AnalyticsRecordFormatter` | Envelope + fields → one UTF-8 JSON line | time zone, `GameServerOptions` | transport |
| — `AnalyticsPipeline` (`IAnalyticsEmitter`) | Bounded queue, dispatcher, batches to sinks, acks, drain, status | formatter, sinks, metrics | what an event means; file layout |
| — `AnalyticsOutboxConsumer` | Outbox entry → envelope → `DeliverAsync`; ack rules | pipeline | relay retry/backoff (OutboxRelay) |
| — `AnalyticsMetrics` | Meter `GameServer.Analytics` | — | health decisions |
| Observability `…Observability.Analytics` — `AnalyticsFileSink` | JSONL files, UTC day roll, newline repair, lock, retention | `IClock`, options, instance id | queueing, formatting |
| — `AnalyticsHealthCheck` | Degraded on recent sink error / low disk | pipeline status, file sink | Unhealthy (never) |
| Testing `InMemoryAnalyticsSink` | Capture records for tests | — | — |
| IAP business `IapAnalytics` | Payment type, `transactionID`, payment fields | Core | envelope, delivery |
| IAP adapters (verifiers) | Fill `IsTest`, `PurchasedAt`, `PurchaseCategory` | store responses | analytics |
| Identity adapters `LoginAnalytics` | Login/resume emission, per-day seen set | `IAnalyticsEmitter`, `AnalyticsTimeZone`, `IClock` | queue, sinks |

No circular dependency: Core ← Platform ← Platform.Web ← Observability; modules' business → Core; adapters → Platform.Web.

## Constitution Check

| Principle | How this slice meets it | Result |
| --- | --- | --- |
| I Server authoritative, one transaction per command | Payment record is an outbox entry committed in the purchase receipt (same transaction as grant + claim); handler stays pure (`AddAnalyticsEvent` only buffers); relay + sweeper unchanged | PASS |
| II Explicit registration | Core analytics registered by `AddGameServer`; file sink only via explicit `AddAnalyticsFiles()`; sinks via DI registration; no discovery; outbox kind `analytics` reserved (duplicate kind fails startup) | PASS |
| III Security deny-by-default | No new endpoint; records exclude tokens, secrets, purchase tokens, payloads, IPs (FR-035 scan); settings hold no secret | PASS |
| IV Async, bounded load | Queue 10 000, batch 512, inline wait 500 ms, tracker 200 000 — all numeric, in PCR; producers never wait; login emission is non-authoritative best effort (allowed), payment is outbox-backed | PASS |
| V Stable contract | JSONL schema v1 + versioning rule + dedupe + time semantics in PCR §18 and contracts; breaking items listed below | PASS |
| VI Multi-layer tests | Unit (zone/DST, formatter, pipeline, consumer, validator), host tests (payment/login/resume/purge/provision), file sink tests, arch rule, smoke test; Couchbase IT case for sweeper delivery. **Gap carried, not introduced:** no CI pipeline (CI-01) | PASS (CI gap pre-existing) |
| VII Observability | 7 metrics without id labels, readiness check, startup warnings, error logs on sink failures | PASS |

Re-check after design: unchanged.

## Design decisions (details in research.md)

| # | Decision | Research |
| --- | --- | --- |
| D1 | No new project: Core (`GameServer.Analytics`), Platform (`Platform.Analytics`), Observability (file sink), Testing (memory sink), modules own their event meaning | R1 |
| D2 | One generic outbox kind `analytics` with self-describing payload `{event, schemaVersion, timeField, fields}`; one platform consumer; envelope derived from the receipt at delivery; payload holds no player id | R2 |
| D3 | `transactionID` = `apple:{transactionId}` / `google:{orderId}` / `google:h:{hex(SHA-256(token))[0..32]}` — plain SHA-256 (high-entropy input, stable, no secret, no rotation); computed in IAP business | R3 |
| D4 | `VerifiedPurchase` gains init-only `IsTest`, `PurchasedAt`, `PurchaseCategory` (source/binary compatible); Google `purchaseType`/`purchaseTimeMillis`, Apple `environment`/`purchaseDate`; fake verifier = test, new `noorder:{ref}` convention | R12 |
| D5 | Consumer: inactive → ack; active → `DeliverAsync`, ack only after all sinks flushed the line to the OS; queue full / timeout (500 ms) / sink error / stopped / malformed → throw → existing relay backoff and dead letter | R4 |
| D6 | One bounded `Channel` (10 000, `TryWrite` only), one dispatcher, batch ≤ 512 without linger, format once, sinks get finished UTF-8 lines; timed-out deliveries marked abandoned and skipped if not yet taken; `dropped` counted only for best-effort events | R5 |
| D7 | `eventId`: payment `hex(SHA-256("{entryId}\|{committedAtUtc}"))[0..32]` (stable, safe against CMD-07 supersede); login random GUID `N` | R6 |
| D8 | Envelope order and names fixed (data-model §3); nulls omitted; field colliding with envelope ignored | R6 |
| D9 | `AnalyticsTimeZone`: `FindSystemTimeZoneById` + `HasIanaId`; offset per instant from UTC; ms truncation before formatting; Staging/Production require zone when active, Development/Test default UTC + warning | R7 |
| D10 | `AnalyticsOptions` is its own options type bound from `GameServer:Analytics` with `ValidateOnStart` → merged in the serve report, not run by `--provision` (no `IStartupCheck`) | R8 |
| D11 | File sink: names `{gameId}-{instanceId}-{event}-{yyyyMMdd}.jsonl`, monotonic UTC day from `IClock` per batch, append + newline repair on open, `Flush(false)` per batch, close all handles on error | R9 |
| D12 | Single writer: `.{gameId}-{instanceId}.lock` with `FileShare.None` in `StartingAsync`; failure names `Observability:InstanceId`; instance id = `ObservabilityOptions.ResolveInstanceId()`; close in `StoppedAsync` | R9 |
| D13 | Optional `RetainLocalDays` deletes only this instance's files; off by default | R9 |
| D14 | Metrics (meter `GameServer.Analytics`) + readiness check `analytics` Degraded on low disk / recent sink error, never Unhealthy; `DiskSpace` helper extracted from `LogSinkMonitor` | R10 |
| D15 | Login emitted at the end of `LoginCompletion.CompleteAsync` (after snapshot read) with `provider` and `isNewUser = profile was null` | R11 |
| D16 | Resume emitted in `PlayerBearerAuthenticationHandler` just before `Success`; `DailyPlayerSet` per deployment-local day, bounded (200 000), cleared when full (+counter) | R11 |
| D17 | `AddGameServer` always registers the core (pipeline before `OutboxSweeper`); `AddAnalyticsFiles()` explicit; `TestHost` registers `InMemoryAnalyticsSink` and zone `UTC`; "enabled but no sink" = inactive + warning in Development/Test, startup error in Staging/Production (review T066) | R13 |
| D18 | Sample host: `AddAnalyticsFiles()`, flavor files `Enabled=true`, `TimeZone=Asia/Ho_Chi_Minh`; anchored `.gitignore` `/samples/SampleHost/analytics/`; smoke test uses a temp directory | R13 |
| D19 | Arch rule: only Platform.Observability depends on `AnalyticsFileSink`; banned `TimeZoneInfo.Local`, `DateTime.Today` | R14 |
| D20 | Rollback/dead letter: documented manual re-drive; `Enabled` uniform per deployment; outbox kind `analytics` reserved | R4 |

## Spec points resolved (input list)

| # | Point | Resolution |
| --- | --- | --- |
| 1 | Google claim reference = purchase token | D3: store id, digest fallback `google:h:` (plain SHA-256, 128 bits) |
| 2 | No test flag in `VerifiedPurchase` | D4: init-only properties, non-breaking; verifiers + fake fill them |
| 3 | Runner awaits inline relay | D5/D6: consumer waits ≤ 500 ms for the dispatcher's ack; slower → throw → sweeper; never fails the purchase |
| 4 | Relay acks only if all consumers succeed; disabled; rollback | D5: inactive → ack; failure → retry/backoff/dead letter; rollback dead-letters after ~3 h on the old build → re-drive (D20) |
| 5 | JOB-02 in-memory events | Login/resume loss accepted, counted, recorded in PCR platform §18 + identity |
| 6 | 4 KiB receipt; purge | Entry ≤ 512 B; no player id in payload; purge anonymizes `receipt.playerId` → late record carries anonymous id; written records keep raw id (Q11) |
| 7 | Instance id | `ObservabilityOptions.ResolveInstanceId()`, same sanitization as log files |
| — | Resume detection point, cache bound | Bearer handler (FR-019 by construction); `DailyPlayerSet` 200 000, clear-on-full |
| — | UTC day roll | Own writer, `IClock` UTC date, monotonic; Serilog not used |
| — | Single writer, Degraded, zone fail-fast/DST, P1 seam, metrics, placement, flavors | D12, D14, D9, D2 + emitter, D14, D1, D18 |

## Spec deviations

Where the code or a requirement contradicts the spec, the plan decides as follows (no silent change):

**Status: resolved in spec (2026-10-31).** spec.md now states every row below (Clarifications session 2026-10-31;
FR-003, FR-009, FR-013, FR-017, FR-021, FR-024, FR-031, FR-032, FR-035, FR-038, SC-004, SC-006, edge cases; new FR-039
for SD13 and FR-040 for the reserved kind of D20).

| # | Spec says | Plan does | Why |
| --- | --- | --- | --- |
| SD1 | FR-003 event id "derived from the outbox entry" (prior analysis: `eventId = entryId`) | `eventId = hex(SHA-256("{entryId}\|{committedAtUtc}"))[0..32]`; `receiptId` kept as a field | CMD-07 supersedes an expired receipt with the same `receiptId` when a request id is reused after 24 h: the raw entry id would then name two different purchases |
| SD2 | FR-024 "Consumers skip an incomplete last line" | Contract: skip **any** unparseable line | After a crash the sink writes `\n` before appending, so the torn line ends up in the middle of the file |
| SD3 | Edge case: full rollback "leaves such receipts pending until roll-forward" | Old builds count attempts and dead-letter after `MaxRelayAttempts` (~3 h); dead letters are never swept again; manual re-drive documented | `OutboxRelay` records a failed attempt for an unknown kind; `QueryPendingOutboxAsync` excludes dead letters |
| SD4 | Edge case: dead letter "until operations re-drives the receipt" | No re-drive tool exists; ops guide gives the manual query; GM/ops endpoint deferred to ops-readiness | Out of scope for an analytics slice; re-drive concerns every outbox kind |
| SD5 | FR-021 zone required "with analytics enabled" | Required when analytics is **active** (enabled **and** a sink registered). Review T066: enabled **without** a sink fails Staging/Production starts (`AnalyticsOptionsValidator`, message names `AddAnalyticsFiles()` / `Enabled=false`); Development/Test warn only | Core is registered in every host; the zone check must not fire without a sink, but an enabled host without a sink on a shared environment acknowledges payments without records |
| SD6 | SC-006 "scan of every analytics file produced by the full test suite" | E2E scan test over all flows (identical bytes to files) + smoke test scanning its real files | `TestHost` uses the in-memory sink (no files) to avoid lock contention across parallel hosts |
| SD7 | FR-031 "one section `GameServer:Analytics`" | Same section, bound as its own `AnalyticsOptions` type, not a `GameServerOptions` property | Needs `ValidateOnStart` (merged in serve, skipped in `--provision`, FR-030) |
| SD8 | FR-017 resume = "request authenticated by the player bearer scheme" | Any request whose PlayerBearer authentication succeeded, even if later refused (gate, permission, body limit) | The player was demonstrably active; checking final outcomes would need a second hook after the pipeline |
| SD9 | FR-009 `chargeTime` = "commit time of the grant" | Receipt `committedAtUtc`, which is the server time of the committing attempt (≤ 10 s before the commit) | Single source on the receipt; the exact commit instant is not recorded anywhere |
| SD10 | FR-032 "local flavor's directory is ignored by git" | Anchored `/samples/SampleHost/analytics/` | An unanchored `analytics/` matches `src/**/Analytics/` on Windows (`core.ignorecase`) |
| SD11 | SC-004 "on the local load script" | `tools/GameServer.LoadTest` gains a purchase step (fake verifier) + opt-in in-process comparison | The load tool has no purchase scenario today |
| SD12 | FR-013 bound "recorded in PCR" (no value) | `DeliveryTimeoutMs` = 500 ms (50..5 000), same for sweeper deliveries | IAP purchases already include a store round trip; a smaller bound risks dead letters on a briefly slow disk |
| SD13 | Spec is silent on mixed configuration | `Enabled` must be uniform per deployment (PCR) | A disabled instance acknowledges entries committed by enabled ones |

## Data flow

**Purchase.** Client → `IapModule.VerifyAsync` (store; `IsTest`, `PurchasedAt`, category) → runner transaction →
handler: claim → grants (ledger entries 0..n-1) → `AddAnalyticsEvent(payment, PaymentFields)` (entry n) → commit receipt →
inline relay: ledger consumer (insert-if-absent) → analytics consumer → `DeliverAsync` → dispatcher formats (envelope
from receipt: `playerId`, `committedAtUtc`, `receiptId`, `eventId`) → file sink appends + flushes → ack → receipt marked
relayed → response. Failure/timeout → receipt pending → sweeper (any instance, backoff) repeats delivery of all entries.

**Login.** Guest/provider/switch → `LoginCompletion.CompleteAsync` → checks → epoch → session → snapshot →
`LoginAnalytics.LoggedIn` (mark seen today, `TryEmit`) → response. Dispatcher writes asynchronously.

**Resume.** Player endpoint → `ProfileEnforcementMiddleware` → PlayerBearer handler → session/profile/access/epoch OK →
`LoginAnalytics.Authenticated` → `DailyPlayerSet.TryMark(player, localDay)` → first time today: `TryEmit(source=resume)`.

**Start.** Serve: `UseGameServer` merges `AnalyticsOptionsValidator` errors → `StartingAsync`: file sink creates
directory, takes lock, retention pass → pipeline starts (warnings) → sweeper → Kestrel. Provision: nothing analytics runs.

**Stop.** Readiness 503 → Kestrel drains requests (inline deliveries complete) → sweeper stops → pipeline drains queue
until the stop token (rest counted `shutdown`) → `StoppedAsync`: flush, close, release lock.

**State ownership.** See data-model §7–§8: the receipt is the source of truth for payments (records are projections);
the queue, day set and file handles are per-process and disposable.

## Performance

- Login request path: `IsActive` + one `Channel.TryWrite` (no formatting, no I/O). Resume path: one `ConvertTime`
  (~100 ns) + one dictionary lookup per authenticated request; emission once per player per day.
- Purchase: normal added latency = thread-pool hop + buffered write + `Flush(false)` of a batch (< 1 ms); worst case
  500 ms. TCS uses `RunContinuationsAsynchronously` so request continuations never run on the dispatcher.
- Allocation: one pooled `ArrayBufferWriter<byte>` per batch on the dispatcher; one `byte[]` per record (~400 B); no
  per-record `JsonObject` for login (fields serialized directly from the record type).
- Memory bounds: queue ≤ 10 000 items (~6 MB worst case); day set ≤ 200 000 ids (~25 MB worst case); file handles = event
  types.
- Group flush: all records in a batch share one flush per file, so throughput scales with burst size.

## Extension points

- **New sink** (P1 Couchbase/Kafka/HTTP): register another `IAnalyticsSink`; same events, same ack rule (all sinks must
  succeed for a payment ack). Emission points untouched (US5-4).
- **New event from a command** (P1 ANA-01): `ctx.AddAnalyticsEvent(new AnalyticsEventType(name, version, timeField), fields)`
  → at-least-once, own file. Marked "extension point, preview" in PCR; proven by one seam test, not a published contract yet.
- **New best-effort event from an adapter**: `IAnalyticsEmitter.TryEmit`.
- **Deferred**: refund/revoke events (iap-notifications slice, same seam), outbox re-drive tool (ops-readiness),
  per-player zone, price/currency, compression/size splitting, server-side upload — no current requirement.

## Project Structure

### Documentation (this feature)

```text
specs/012-analytics-audit/
├── spec.md
├── plan.md                              # this file
├── research.md                          # R1..R14
├── data-model.md                        # settings, schemas v1, outbox entry, files, in-memory state
├── contracts/analytics-records.md       # data-team contract (fields, time, dedupe, versioning, privacy)
├── contracts/analytics-operations.md    # settings, volume, metrics, health, inline bound, rollout/re-drive
├── checklists/requirements.md
└── tasks.md                             # next stage (/speckit-tasks)
```

### Source Code (repository root)

```text
src/GameServer.Core/Analytics/AnalyticsEvents.cs              # NEW AnalyticsEventType, AnalyticsOutboxEvent, AnalyticsOutbox.AddAnalyticsEvent
src/GameServer.Platform/Analytics/
├── AnalyticsOptions.cs                   # NEW options + AnalyticsOptionsValidator
├── AnalyticsTimeZone.cs                  # NEW zone resolution, AnalyticsInstant
├── AnalyticsRecordFormatter.cs           # NEW envelope + fields → UTF-8 line, event ids
├── AnalyticsSinks.cs                     # NEW IAnalyticsSink, AnalyticsRecord, IAnalyticsEmitter, AnalyticsEnvelope, AnalyticsStatus, AnalyticsDeliveryException
├── AnalyticsPipeline.cs                  # NEW queue, dispatcher, drain
├── AnalyticsOutboxConsumer.cs            # NEW IOutboxConsumer kind "analytics"
└── AnalyticsMetrics.cs                   # NEW meter GameServer.Analytics
src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs      # register analytics core (pipeline before OutboxSweeper), consumer, options
src/GameServer.Platform.Observability/
├── Analytics/AnalyticsFileExtensions.cs  # NEW AddAnalyticsFiles()
├── Analytics/AnalyticsFileSink.cs        # NEW sink + IHostedLifecycleService (lock, roll, repair, retention)
├── Analytics/AnalyticsFileNames.cs       # NEW naming/parsing
├── Analytics/AnalyticsHealthCheck.cs     # NEW readiness "analytics"
├── DiskSpace.cs                          # NEW free-space helper (extracted)
├── LogSinkMonitor.cs                     # use DiskSpace
└── ObservabilityExtensions.cs            # Sanitize → internal
src/GameServer.Platform.Testing/InMemoryAnalyticsSink.cs      # NEW
src/GameServer.Modules.Iap/IapModel.cs                        # VerifiedPurchase init props; handler AddAnalyticsEvent after grants
src/GameServer.Modules.Iap/IapAnalytics.cs                    # NEW Payment type, TransactionId, PaymentFields
src/GameServer.Modules.Iap.Adapters/
├── GooglePlayReceiptVerifier.cs          # IsTest, PurchaseCategory, PurchasedAt
├── AppleJwsReceiptVerifier.cs            # IsTest (environment), PurchasedAt
└── FakeStoreReceiptVerifier.cs           # IsTest = true; noorder:{ref}
src/GameServer.Modules.Identity.Adapters/
├── LoginAnalytics.cs                     # NEW LoginAnalytics, DailyPlayerSet, LoginFields
├── LoginCompletion.cs                    # call LoggedIn at the end (provider, created)
├── PlayerBearerAuthenticationHandler.cs  # call Authenticated before Success
├── IdentityModule.cs                     # register LoginAnalytics
└── IdentityOptions.cs                    # IdentityMetrics: login_resume_tracker_resets_total
BannedSymbols.txt                                             # TimeZoneInfo.Local, DateTime.Today
samples/SampleHost/Program.cs                                 # gameServer.AddAnalyticsFiles()
samples/SampleHost/appsettings.{Local,Dev,Beta,Uat,Live}.json # GameServer:Analytics { Enabled, TimeZone }
.gitignore                                                    # /samples/SampleHost/analytics/
tools/GameServer.LoadTest/{Program.cs,VirtualPlayer.cs}        # optional purchase step (fake verifier) for SC-004
docs/pcr/platform.md                      # §18 analytics (contract, JOB-02 loss, bound, metrics, health, breaking)
docs/pcr/iap.md                           # payment event, transactionID, isTest, fake conventions, custom verifier note
docs/pcr/identity.md                      # login/resume events, resume rule and bound
docs/guides/analytics.md                  # NEW data-team + game-team guide (records contract, queries, seam)
docs/guides/environments-and-local-run.md # settings per flavor, deploy checklist, rollback/re-drive
README.md                                 # pointer to the analytics guide
tests/Shared/TestHost.cs                  # InMemoryAnalyticsSink by default (TestHost.Analytics), TimeZone=UTC
tests/GameServer.Platform.Tests/Analytics/
├── AnalyticsTimeZoneTests.cs             # 3 zones, DST both sides, midnight local/UTC, ms truncation, IANA/Windows ids
├── AnalyticsOptionsValidatorTests.cs     # missing zone per environment, unknown zone, ranges, inactive → no zone rule
├── AnalyticsRecordFormatterTests.cs      # field order/names, nulls omitted, collisions, event ids
├── AnalyticsPipelineTests.cs             # queue full, ack after write, timeout + abandon, multi-sink, inactive, drain, errors
└── AnalyticsOutboxConsumerTests.cs       # envelope from receipt, stable eventId, anonymized receipt, malformed payload
tests/GameServer.Observability.Tests/
├── AnalyticsFileSinkTests.cs             # names, UTC roll w/ FakeClock, clock back, restart append + repair, lock, read-only dir, retention
└── AnalyticsHealthTests.cs               # Degraded on error window / low disk, never Unhealthy
tests/SampleGame.Tests/
├── IapAnalyticsTests.cs                  # US1 matrix: N purchases, replays, ALREADY_GRANTED, invalid, relay failure → sweeper on 2nd instance, isTest, noorder digest, receipt size
├── AnalyticsPrivacyScanTests.cs          # SC-006 E2E secret scan + exact key sets
└── SampleHostConfigurationFileTests.cs   # every flavor sets Analytics Enabled + TimeZone
tests/GameServer.Platform.Web.Tests/
├── LoginAnalyticsTests.cs                # US2: guest/provider/switch, refusals, resume once/day/instance, day change, test scheme/GM/public excluded, tracker clear
├── AnalyticsSeamTests.cs                 # US5-4: custom event via test module, second sink receives everything
├── AccountPurgeTests.cs                  # pending purchase receipt purged → late record carries anonymous id
└── GameServerCommandLineTests.cs         # --provision in Production without zone: no error, no directory, no lock
tests/Sample.Counter.Tests/SampleHostSmokeTests.cs            # temp analytics dir; files present; scan for secrets
tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseOutboxTests.cs (or existing outbox IT) # payment record delivered by sweeper on Couchbase
tests/GameServer.Architecture.Tests/ArchitectureTests.cs      # AnalyticsFileSink only in Platform.Observability
```

**Structure Decision**: no new project (research R1). Contracts that module business code needs go to Core; the
generic pipeline to Platform; file I/O and readiness to Platform.Observability next to the log files; the test double to
Platform.Testing; each module owns its event meaning.

## Test strategy

| Story / rule | Tests |
| --- | --- |
| US1 payment (FR-007..FR-014, SC-001) | `IapAnalyticsTests` on two `TestHost`s sharing store and clock: N purchases / M players, replay (same request id), resubmit (ALREADY_GRANTED), invalid / unavailable / unknown product, forced sink failure on host A (`FailWrites`) → host B sweeper delivers same `eventId`/`transactionID`/`chargeTime`; dedupe by `transactionID` = N, distinct `userID` = M; `isTest` true (fake); Google `noorder:` → `google:h:` 32 hex, never the token; inline timeout (`WriteDelay` > bound) → purchase OK, record later; analytics disabled → receipts relayed, no record, no error metric; receipt size test (3 grants, 300-char token, 128-char product) < 4 KiB |
| Verifiers (FR-010, FR-009) | Google: `purchaseType` 0/1/2/absent → `IsTest`/category; `purchaseTimeMillis` → `PurchasedAt` (stub HTTP handler, as existing verifier tests); Apple: Sandbox/Xcode/LocalTesting → test, `purchaseDate` → time |
| US2 login/resume (FR-015..FR-019, SC-002) | `LoginAnalyticsTests`: one record per guest/provider/confirmed switch with provider + `isNewUser`; none for banned, deleted, purged, suspended, wrong secret, invalid proof, rate limited, SWITCH_ACCOUNT, identity link; bearer requests on two local days → one resume per day; login then requests same day → no resume; Test scheme, GM cookie and public endpoints → none; tracker clear at capacity → counter + at most one extra |
| US3 time (FR-020, FR-021, SC-003) | `AnalyticsTimeZoneTests` (Asia/Ho_Chi_Minh, Asia/Taipei, Europe/London Jan/Jul and ±1 ms around both 2026 changes, local/UTC midnight); validator matrix; machine-zone independence: file day and records under `FakeClock` with the process `TZ` irrelevant (no `TimeZoneInfo.Local` by ban) |
| US4 files (FR-022..FR-030, SC-005, SC-007) | `AnalyticsFileSinkTests`: names, 00:00 UTC roll, clock back across midnight → no reopen, restart append + torn-line repair, second sink on same (game, instance, dir) → startup error naming `Observability:InstanceId`, disposed holder releases, read-only dir → sink error + Degraded + logins/purchases OK, retention own files only; `AnalyticsHealthTests`; provision test; graceful stop drains |
| US5 defaults/seam (SC-*) | Smoke test (local flavor, temp dir): records written with no game code; `AnalyticsSeamTests`; `SampleHostConfigurationFileTests` |
| US6 docs (SC-009) | Key-existence test: every key named in PCR §18 and the guides exists on `AnalyticsOptions`; analyst walkthrough on smoke-test output (manual, recorded in tasks) |
| Privacy (FR-035, SC-006) | `AnalyticsPrivacyScanTests` + smoke test file scan |
| Performance (SC-004, SC-008) | opt-in `GAMESERVER_PERF=1`: p95 login/purchase with analytics on vs off on `TestHost`; burst 180 logins/s × 60 s → 0 drops; blocked sink → bounded memory, drops counted; load tool purchase step on the local flavor (manual record) |
| Arch | `AnalyticsFileSink` dependency rule; banned symbols compile-time |

## Implementation phases (input for tasks.md)

1. **Foundations (blocking)** — Core `AnalyticsEvents.cs`; Platform `Analytics/*` (options + validator, time zone,
   formatter, sink contracts, pipeline, consumer, metrics); registration in `AddGameServer` (before `OutboxSweeper`);
   `InMemoryAnalyticsSink`; `TestHost` defaults; banned symbols; unit tests of zone, validator, formatter, pipeline, consumer.
2. **US1 payment** — `VerifiedPurchase` properties; Google/Apple/fake verifiers; `IapAnalytics`; handler emission;
   `IapAnalyticsTests`, verifier tests, receipt size test; purge case in `AccountPurgeTests`.
3. **US2 login/resume** — `LoginAnalytics`/`DailyPlayerSet`; hooks in `LoginCompletion` and the bearer handler;
   identity metric; `LoginAnalyticsTests`.
4. **US4 file sink** — `DiskSpace` extraction; `AnalyticsFileSink`, names, lock, retention, lifecycle; health check;
   `AddAnalyticsFiles`; file sink and health tests; provision test; arch rule.
5. **US5 sample host** — `Program.cs`, flavor files, `.gitignore`, smoke test, configuration file test, seam test,
   privacy scan test, Couchbase IT case.
6. **US6 docs and close** — PCR platform §18, iap, identity; `docs/guides/analytics.md`; environments guide (settings
   per flavor, volume/shipper, tzdata, distinct instance ids, rollout/rollback, re-drive); README; load-tool purchase
   step + SC-004/SC-008 measurements; full suite (in-memory, file store switch, Couchbase) → tech-lead review (LOG-05
   focus) → resume file `AgentMemories/features/p05-speckit/00_summary.md`.

Phases 2, 3 and 4 depend only on 1 and can run in parallel (disjoint files); 5 needs 2–4; 6 last.

## Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Container image without tzdata/ICU or Windows id configured | Live fails to start | Fail-fast message names key and fix; deploy checklist; [VERIFY] V1/V2 tests on the Linux image |
| Pod replaced without persistent volume/shipper | Revenue records lost (payments already acked) | LOG-03 policy in deploy checklist; same volume decision as logs |
| Full rollback to pre-012 build | Pending payment receipts dead-lettered after ~3 h | Deploy note; documented re-drive; affected set = receipts pending at rollback (inline relay failed) |
| Disk slower than 500 ms or failing for hours | Payment receipts retried, eventually dead-lettered | Degraded readiness + `analytics_sink_errors_total` + existing dead-letter alert; re-drive |
| Purchase receipts near 4 KiB (many grants × long Google token, pre-existing) | +≤ 0.5 KiB could push one over → INTERNAL | Size test; PCR iap notes the per-grant token cost; trimming ledger references is a separate change |
| `IHostedLifecycleService` ordering or `HasIanaId` differ from research | Lock taken after Kestrel listens / Windows id accepted | [VERIFY] V1/V3 with stated fallbacks; tests pin behaviour |
| Lock not honoured on NFS/SMB | Two writers on one file | Guide: local/block volume per pod only |
| Mixed `Enabled` across instances | Payments acked without a record | PCR rule "uniform per deployment" |
| Raw `userID` in shipped files after account deletion (PRIV-01) | Legal exposure | Owner Q11 accepted; documented in PCR, guide and privacy-policy input |
| Hundreds of test hosts collecting records in memory | Test memory growth | In-memory sink per host, disposed with it; records ~400 B |

## Breaking changes (to announce in PCR)

| Change | Who is affected |
| --- | --- |
| Outbox kind `analytics` is reserved (consumer registered by the framework) | A game registering a consumer/projection named `analytics` fails startup |
| `iap.purchase` receipts carry one more outbox entry (≤ 512 B) | Receipt size budget; pre-012 builds relaying such receipts (rollback hazard above) |
| New startup failures when analytics is active: zone missing in Staging/Production, unknown/Windows zone, unwritable directory, duplicate instance id on one directory | Deployments calling `AddAnalyticsFiles()` (sample host and games copying it) must set `GameServer:Analytics:TimeZone` and distinct instance ids |
| `VerifiedPurchase` gains `IsTest`, `PurchasedAt`, `PurchaseCategory` (init-only) | Source/binary compatible; record equality includes them; custom verifiers that do not set `IsTest` report purchases as real |
| Fake verifier marks every purchase as test; new payload convention `noorder:{ref}` | Tests/tools using the fake verifier |
| New semantic contract: JSONL schema v1, file names, time semantics, dedupe rules | Data pipelines (PCR §18) |
| Test infrastructure: `TestHost` registers `InMemoryAnalyticsSink` and `TimeZone=UTC`; `WebApplicationFactory` users of the sample host must override `GameServer:Analytics:Directory` | Game test suites built on the sample |
| Banned APIs: `TimeZoneInfo.Local`, `DateTime.Today` in framework/module code | Module authors (compile error) |

## Complexity Tracking

| Item | Why needed | Simpler alternative rejected because |
| --- | --- | --- |
| New public Core API `AnalyticsOutbox.AddAnalyticsEvent` | IAP business may only use Core (ARC-01) and needs to stage the payment entry; same pattern as `AddAudit` | Raw `AddOutbox("analytics", …)` in the module duplicates the payload contract per module and cannot validate names |
| Self-describing payload (`timeField` in the entry) | Lets the single consumer serve payment now and future events without a registry | Kind per event type multiplies consumer registrations and rollback hazards (R2) |
