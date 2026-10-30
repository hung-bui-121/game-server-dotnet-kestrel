# Tasks: Analytics audit log for Login and Payment (P0.5 batch, slice 012 analytics-audit)

**Input**: [plan.md](plan.md) (authoritative; deviations SD1–SD13 are now stated in the spec, decisions D1–D20),
[spec.md](spec.md) (FR-001..FR-040, SC-001..SC-009, reconciled 2026-10-31), [research.md](research.md) (R1–R14),
[data-model.md](data-model.md), [contracts/analytics-records.md](contracts/analytics-records.md),
[contracts/analytics-operations.md](contracts/analytics-operations.md)

**Tests**: mandatory (constitution VI). Each phase has its own tests. Couchbase tests skip with a message when
`COUCHBASE_CONNSTR` is not set and run locally against EE 7.6.12 before merge (no CI, CI-01); the Couchbase suite takes
about 19 minutes. Performance tests are opt-in (`GAMESERVER_PERF=1`). Scenario ids (`US1-3`) refer to the spec's
acceptance scenarios.

**Format**: `- [ ] T0NN [P] [USx] description with paths`, then `Done when:`. `[P]` = different files, no open
dependency.

**[VERIFY]**: a runtime or store-API fact from research (V1–V6) to confirm on the pinned versions (.NET SDK 10.0.403,
Google Play Developer API v3, App Store Server JWS) **before** coding the rest of that task. Record the result as a
`Note:` on the task. If the fact is wrong, apply the stated fallback and record any change to keys, messages or docs as a
deviation.

| Item | Fact | Task |
| --- | --- | --- |
| V1 | `HasIanaId` refuses a Windows zone id on Windows and Linux | T002, T006, T015 |
| V2 | Debian `aspnet:10.0` ships `tzdata`; Alpine and plain chiseled images do not | T058 |
| V3 | every `IHostedLifecycleService.StartingAsync` runs before any `StartAsync` (Kestrel included); `StoppedAsync` after every `StopAsync` | T002, T038 |
| V4 | `FileShare.None` refuses a second open in another process and in the same process | T040 |
| V5 | Google `purchaseType` 0/1/2/absent, `purchaseTimeMillis` string, `orderId` may be absent | T023 |
| V6 | Apple JWS `purchaseDate` is a number (ms since epoch) | T024 |

**Environment**: `"$HOME/.dotnet10/dotnet.exe"`; Couchbase variables as in `AgentMemories/features/p05-speckit/00_summary.md`
(password from the user variable `COUCHBASE_PW`, never printed). Never grep recursively over `samples/` (Unity
`Library/`); targeted paths are fine. `.specify/feature.json` already points to `specs/012-analytics-audit`. Docs are
written in the language of the existing repository docs (Vietnamese); keys, metric names, field names and commands stay
literal.

## Phase 1: Setup

- [ ] T001 Baseline on branch `012-analytics-audit` (base `ab17cc0`): Release build of `GameServer.slnx`; full suite in
  memory (no `COUCHBASE_*`).
  Done when: 0 warnings, 0 failures; the total/passed/skipped counts are recorded as a Note (reference for T065).
- [ ] T002 **[VERIFY] V1 and V3** with a throw-away console or xUnit scratch test (not committed) on the developer OS,
  and on Linux when WSL or Docker is available:
  - V1: `TimeZoneInfo.FindSystemTimeZoneById` for `Asia/Ho_Chi_Minh`, `Asia/Taipei`, `Europe/London`, `UTC` and
    `SE Asia Standard Time`; print `Id` and `HasIanaId`.
  - V3: a generic host with two `IHostedLifecycleService`s and a `BackgroundService` plus Kestrel on port 0: log the
    order of `StartingAsync`, `StartAsync`, `ApplicationStarted`, `StopAsync`, `StoppedAsync`.

  Fallbacks: V1 wrong → the validator also requires `^(UTC|[A-Za-z_]+(/[A-Za-z0-9_+\-]+)+)$` (T007); V3 wrong → the file
  sink lock moves to the pipeline's `StartAsync` (registered before the sweeper) and handles close in its `StopAsync`
  after the drain (T038).
  Done when: a Note records both results per OS and the path chosen for T006/T007 and T038.

**Checkpoint**: baseline counts known; V1/V3 decided.

## Phase 2: Foundational (blocks every story)

- [ ] T003 [P] Core contracts in a new file `src/GameServer.Core/Analytics/AnalyticsEvents.cs` (research R2):
  `AnalyticsEventType(Name, SchemaVersion, TimeField)` with `IsValidName` (`^[a-z][a-z0-9_]{0,31}$`) and
  `IsValidTimeField` (`^[a-z][A-Za-z0-9]{0,31}$`); `AnalyticsOutboxEvent(Event, SchemaVersion, TimeField, Fields)`;
  `AnalyticsOutbox.Kind = "analytics"` and `AddAnalyticsEvent(this ICommandContext, AnalyticsEventType, object fields)`
  (invalid type or schema version < 1 → `ArgumentException`). `CommandContext.AddOutbox` serializes with
  `GameServerJson.Options`, which **writes nulls**: the helper must leave null fields out of the payload (serialize
  `fields` to a `JsonObject` without nulls, or rely on `[JsonIgnore(WhenWritingNull)]` on the field types and assert it).
  Tests in a new file `tests/GameServer.Platform.Tests/Analytics/AnalyticsOutboxTests.cs` (with `RunnerHarness`): valid
  and invalid names/time fields; the staged entry has kind `analytics`, the `{event, schemaVersion, timeField, fields}`
  shape, no null field and no player id; an invalid type fails the command `INTERNAL` with nothing committed; a
  rejection rolls the entry back (FR-011).
  Done when: the tests are green and `GameServer.Core` gains no reference.
- [ ] T004 [P] Sink seam in a new file `src/GameServer.Platform/Analytics/AnalyticsSinks.cs` (research R5):
  `IAnalyticsSink` (`Name`, `WriteAsync(batch, ct)`), `AnalyticsRecord(Event, EventId, Json)`, `IAnalyticsEmitter`
  (`IsActive`, `TryEmit`), `AnalyticsEnvelope`, `AnalyticsStatus`, `AnalyticsDeliveryException(reason, inner)`.
  Done when: the solution builds; XML docs state "never blocks, never throws" for `TryEmit` and the ack rule for
  `WriteAsync`.
- [ ] T005 [P] Metrics in a new file `src/GameServer.Platform/Analytics/AnalyticsMetrics.cs` (research R10): meter
  `GameServer.Analytics`; counters `analytics_events_written_total{event,sink}`,
  `analytics_events_dropped_total{event,reason}`, `analytics_sink_errors_total{sink}`,
  `analytics_delivery_failures_total{reason}`; observable gauge `analytics_queue_entries` (callback set by the
  pipeline). `analytics_dir_free_bytes` is registered by the file sink (T041).
  Done when: the solution builds; no tag can carry a player, event or transaction id (OBS-02) — tags are typed as the
  enumerated reason/event/sink values only.
- [ ] T006 Time zone in a new file `src/GameServer.Platform/Analytics/AnalyticsTimeZone.cs` (research R7, after T002):
  `AnalyticsTimeZone` (`Name`, `IsDefaulted`, `Describe(utc)`, `LocalDate(utc)`, static `TryResolve(name, out zone, out
  error)` that never throws) and `AnalyticsInstant(Utc, Local, Offset)`. Truncate to milliseconds **before** converting;
  convert from UTC with `TimeZoneInfo.ConvertTime`; invariant culture; `utcOffset` `+00:00` for UTC (never `Z`).
  `TryResolve` applies the V1 result (and the regex fallback if V1 failed). The error text names
  `GameServer:Analytics:TimeZone`, the value and the fix (IANA name such as `Asia/Ho_Chi_Minh`; tzdata/ICU in the image;
  no InvariantGlobalization).
  Done when: the solution builds; T015 covers it.
- [ ] T007 Options in a new file `src/GameServer.Platform/Analytics/AnalyticsOptions.cs` (research R8, data-model §1–§2,
  after T004, T006): `AnalyticsOptions` (`SectionName = "GameServer:Analytics"`, the 9 keys with their defaults) and
  `AnalyticsOptionsValidator : IValidateOptions<AnalyticsOptions>`:
  - ranges: `QueueCapacity` 100..1 000 000, `DeliveryTimeoutMs` 50..5 000, `ResumeTrackerCapacity` 1 000..10 000 000,
    `LowDiskBytes` ≥ 0, `ErrorWindowSeconds` 10..86 400, `RetainLocalDays` ≥ 1 when set, `Directory` non-empty — each
    message names the key and the range;
  - zone rules **only when active** (`Enabled` and at least one `IAnalyticsSink` registration; decide "active" once,
    in one place shared with the pipeline — e.g. an `AnalyticsActivation` singleton built from the options and the
    registered sink count — so validator, pipeline, consumer and login analytics agree): missing zone → error in
    Staging/Production naming the key and the deployment environment; set zone → `AnalyticsTimeZone.TryResolve`.
  Done when: the solution builds; no key is added to `GameServer:SecretKeys` (FR-034); T016 covers it.
- [ ] T008 Formatter in a new file `src/GameServer.Platform/Analytics/AnalyticsRecordFormatter.cs` (research R6,
  data-model §3, after T003, T004, T006): one compact UTF-8 line with `Utf8JsonWriter` (`JavaScriptEncoder.Default`), keys
  in the envelope order `event, schemaVersion, eventId, gameId, environmentId, userID, {timeField}, {timeField}Utc,
  timeZone, utcOffset, receiptId?`, then the type fields in payload order; null type fields omitted; a type field whose
  name equals an envelope name is ignored with one error log per event type and process. Event ids: payment =
  `hex(SHA-256(UTF-8("{entryId}|{committedAtUtc}")))[0..32]` (lowercase), login = `Guid.NewGuid().ToString("N")`.
  `gameId`/`environmentId` from `GameServerOptions` only (FR-033). Login fields are written directly from the record
  type (no per-record `JsonObject`).
  Done when: the solution builds; T017 covers it.
- [ ] T009 Pipeline in a new file `src/GameServer.Platform/Analytics/AnalyticsPipeline.cs` (research R5, after T004,
  T005, T007, T008): `AnalyticsPipeline : BackgroundService, IAnalyticsEmitter` with
  - `Channel.CreateBounded` (`QueueCapacity`, `SingleReader`, `FullMode = Wait`), producers use `TryWrite` only;
  - `TryEmit`: false without counting when inactive; full queue → `dropped{reason=queue_full}`;
  - `DeliverAsync`: `TaskCompletionSource(RunContinuationsAsynchronously)`; full queue → `AnalyticsDeliveryException`
    (`queue full`) at once; no ack within `DeliveryTimeoutMs` → item marked abandoned and `timeout` thrown; stopped →
    `stopped`; each failure counts `analytics_delivery_failures_total{reason}`;
  - dispatcher: await one item, take up to 511 more without waiting, skip abandoned items, format once, call every sink
    in registration order, complete deliveries only if every sink succeeded, count `written{event,sink}`,
    `sink_errors{sink}` and `dropped{reason=sink_error}` for best-effort records, keep `LastError`/`LastErrorAtUtc`
    (`IClock`);
  - `FlushAsync`; `StopAsync`: complete the writer, drain until the stop token, count the rest `dropped{reason=shutdown}`,
    fail pending deliveries;
  - start warnings, once: "analytics enabled but no sink registered (AddAnalyticsFiles)" and "time zone defaulted to
    UTC" (Development/Test).
  Done when: the solution builds; T018 covers it.
- [ ] T010 Outbox consumer in a new file `src/GameServer.Platform/Analytics/AnalyticsOutboxConsumer.cs` (research R4,
  after T009): kind `analytics`; inactive → return (ack, no metric); otherwise parse the payload as
  `AnalyticsOutboxEvent` (malformed → error log naming the entry id, `delivery_failures{reason=malformed}`, throw), build
  the envelope from the delivery (`PlayerId`, `CommittedAtUtc`, `ReceiptId`, `Entry.EntryId`) and await
  `DeliverAsync`; any `AnalyticsDeliveryException` propagates to the relay (attempt, backoff, dead letter).
  Done when: the solution builds; T019 covers it.
- [ ] T011 Registration in `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs` (research R13, after T007–T010):
  `AddOptions<AnalyticsOptions>().BindConfiguration(SectionName).ValidateOnStart()` + the validator;
  `AnalyticsTimeZone` (resolved lazily so an invalid zone is reported by the validator, not by a constructor), metrics,
  formatter, the activation decision, `AnalyticsPipeline` as singleton + `IAnalyticsEmitter` + hosted service
  registered **before** `OutboxSweeper`, `IOutboxConsumer` → `AnalyticsOutboxConsumer` (this reserves kind `analytics`
  through the existing duplicate-kind check, FR-040).
  Done when: a host built without any sink starts with the "no sink" warning; `--provision` (011 path) still runs no
  options validation for analytics (checked in T043); the full existing suite is green.
- [ ] T012 [P] Test sink in a new file `src/GameServer.Platform.Testing/InMemoryAnalyticsSink.cs` (research R13, after
  T004): `Name = "memory"`, `Records` (parsed `JsonObject`s) **and** the raw lines (byte-identical to what the file sink
  writes, US5-3), `OfEvent(name)`, `FailWrites`, `WriteDelay`, thread-safe.
  Done when: the solution builds.
- [ ] T013 Test host defaults in `tests/Shared/TestHost.cs` (after T011, T012): register an `InMemoryAnalyticsSink`
  exposed as `TestHost.Analytics`; `DefaultSettings()` adds `GameServer:Analytics:TimeZone = UTC` so tests that switch to
  Staging/Production keep starting. Analytics is now active in every `TestHost`, so every purchase receipt carries one
  more outbox entry and every login emits a record.
  Done when: the full existing suite is green in memory. Any test that asserted the outbox entries of an `iap.purchase`
  receipt, the GM receipt view, startup warning counts or log lines is fixed and listed in a Note.
- [ ] T014 [P] `BannedSymbols.txt`: add `P:System.TimeZoneInfo.Local` and `P:System.DateTime.Today` with the reason
  "Use IClock + AnalyticsTimeZone (TIME-01)" (research R7, D19).
  Done when: Release build has 0 warnings (no framework or module code uses them today).
- [ ] T015 Unit tests in a new file `tests/GameServer.Platform.Tests/Analytics/AnalyticsTimeZoneTests.cs` (after T006;
  US3-1, US3-2, SC-003):
  - `Asia/Ho_Chi_Minh` `+07:00`, `Asia/Taipei` `+08:00`;
  - `Europe/London` in January `+00:00` and July `+01:00`; ±1 ms around both 2026 changes (2026-03-29T01:00Z,
    2026-10-25T01:00Z) gives the offset of the instant;
  - local midnight and UTC midnight: `LocalDate` follows the zone;
  - ms truncation: an instant with ticks below 1 ms → `Local` and `Utc` parse to the same instant;
  - formats: `Utc` ends with `Z`, `Local` and `Offset` carry `±hh:mm`, `UTC` zone gives `+00:00`;
  - **V1 pinned**: `SE Asia Standard Time` refused, an unknown name refused, IANA names accepted; error text names the
    key, the value and the fix.
  Done when: green on the developer OS (and on Linux if T002 ran there).
- [ ] T016 [P] Unit tests in a new file `tests/GameServer.Platform.Tests/Analytics/AnalyticsOptionsValidatorTests.cs`
  (after T007): every range boundary (in and out); missing zone with a sink in Staging and Production → error naming the
  key and the environment; Development/Test → valid (defaulted); unknown zone → error; inactive (disabled, or no sink)
  in Production without zone → valid (SD5).
  Done when: green.
- [ ] T017 [P] Unit tests in a new file `tests/GameServer.Platform.Tests/Analytics/AnalyticsRecordFormatterTests.cs`
  (after T008): exact key order and names for `payment` and `login` (data-model §3.1/§3.2 examples reproduced
  byte-for-byte from fixed inputs); nulls omitted; colliding field ignored and logged once; payment `eventId` stable for
  the same `(entryId, committedAtUtc)` and different for the same `entryId` with another `committedAtUtc` (CMD-07
  supersede, SD1); login ids unique, 32 lowercase hex; `gameId`/`environmentId` from options; no trailing newline.
  Done when: green.
- [ ] T018 Unit tests in a new file `tests/GameServer.Platform.Tests/Analytics/AnalyticsPipelineTests.cs` (after T009,
  T012), with `FakeClock` and one or two `InMemoryAnalyticsSink`s:
  - `TryEmit` inactive → false, nothing counted; full queue (capacity 100, blocked sink) → false + `dropped{queue_full}`;
  - `DeliverAsync` completes only after every sink wrote; one sink failing → throws `sink_error`, the other sink still
    got the batch, best-effort records counted `dropped{sink_error}`;
  - `WriteDelay` > `DeliveryTimeoutMs` → `timeout` within bound + margin; an abandoned item not yet taken is skipped (not
    written); one already taken may be written (documented duplicate);
  - batches ≤ 512, order preserved; format once (both sinks receive identical bytes);
  - `StopAsync` drains queued records, counts the rest `shutdown`, pending deliveries fail `stopped`;
  - blocked sink: queue length never exceeds `QueueCapacity` (memory bound, SC-008 second half);
  - metrics observed with a `MeterListener` filtered to this pipeline's meter instance (process-wide listener trap of
    010/011: run in a non-parallel collection if needed).
  Done when: green in 3 consecutive runs.
- [ ] T019 Unit tests in a new file `tests/GameServer.Platform.Tests/Analytics/AnalyticsOutboxConsumerTests.cs` (after
  T010): envelope from the delivery (`userID` = receipt player, time = `committedAtUtc`, `receiptId`, digest `eventId`);
  same delivery twice → identical lines; a delivery whose receipt was anonymized (player id `anon_…`) → `userID` is the
  anonymous id, `eventId` unchanged; inactive → returns without writing and without metrics; malformed payload → throws
  and logs the entry id; queue full / timeout / sink error / stopped → throws.
  Done when: green.

**Checkpoint**: Release build has 0 warnings. The full existing suite is green in memory. Every `TestHost` has active
analytics with the in-memory sink; no story code exists yet.

## Phase 3: US1 — Data team counts paying users per month (P1) MVP

- [ ] T020 [US1] `VerifiedPurchase` gains init-only `IsTest`, `PurchasedAt` (`DateTimeOffset?`) and `PurchaseCategory`
  (`string?`) in `src/GameServer.Modules.Iap/IapModel.cs` (research R12). Positional parameters, `Deconstruct` and
  `Invalid` stay unchanged (source and binary compatible).
  Done when: the solution builds with no change to any caller; existing IAP tests green.
- [ ] T021 [US1] New file `src/GameServer.Modules.Iap/IapAnalytics.cs` (research R3, data-model §3.1/§4, after T003,
  T020): `IapAnalytics.Payment = ("payment", 1, "chargeTime")`; `TransactionId(VerifiedPurchase)` → `apple:{ExternalReference}`,
  `google:{OrderId}`, or `google:h:{hex(SHA-256(UTF-8(token)))[0..32]}` when Google gives no order id; `PaymentFields`
  with explicit `[JsonPropertyName]` (`transactionID`, `store`, `productId`, `orderId`, `isTest`, `purchaseCategory`,
  `storePurchaseTimeUtc` as `yyyy-MM-ddTHH:mm:ss.fffZ`) and nulls not written. Uses Core only (ARC-01).
  Done when: unit tests in a new file `tests/SampleGame.Tests/IapAnalyticsUnitTests.cs` (or a section of T027's file):
  Apple, Google with order id, Google without order id → 32 lowercase hex, deterministic, never contains the token;
  an unknown store → `ArgumentException`.
- [ ] T022 [US1] `IapPurchaseHandler.HandleAsync` in `src/GameServer.Modules.Iap/IapModel.cs` (after T021, same file as
  T020): after the grants, `ctx.AddAnalyticsEvent(IapAnalytics.Payment, IapAnalytics.Fields(purchase))` — entry index =
  number of grants, deterministic across transaction attempts; nothing for rejected paths (the rejection rolls back).
  Done when: existing `tests/SampleGame.Tests/IapTests.cs`, `ShopTests.cs`, `SampleGameEndToEndTests.cs` and
  `GameServer.Gm.Tests` are green (fix and list in a Note any assertion on the receipt's outbox entries); the
  architecture tests stay green (FR-005, US5-5).
- [ ] T023 [P] [US1] **[VERIFY] V5** against the Google Play Developer API v3 reference for `purchases.products.get`
  (`purchaseType` 0 = Test, 1 = Promo, 2 = Rewarded, absent for normal purchases; `purchaseTimeMillis` int64 as string;
  `orderId` may be absent). Then in `src/GameServer.Modules.Iap.Adapters/GooglePlayReceiptVerifier.cs` return
  `IsTest = purchaseType == 0`, `PurchaseCategory` = `test`/`promo`/`rewarded`/null, `PurchasedAt` from
  `purchaseTimeMillis` (missing or unparsable → null). The Production refusal of test purchases is unchanged.
  Done when: the Note records the V5 result (source and date); T026 covers it.
- [ ] T024 [P] [US1] **[VERIFY] V6** against the App Store Server API reference (`JWSTransactionDecodedPayload.purchaseDate`
  is a number in ms since epoch; `environment` values). Then in `src/GameServer.Modules.Iap.Adapters/AppleJwsReceiptVerifier.cs`
  return `IsTest = environment != "Production"` and `PurchasedAt` from `purchaseDate` (missing → null); category null.
  Done when: the Note records the V6 result; T026 covers it.
- [ ] T025 [P] [US1] `src/GameServer.Modules.Iap.Adapters/FakeStoreReceiptVerifier.cs`: every valid result has
  `IsTest = true`, `PurchasedAt = null`; new payload convention `noorder:{ref}` → valid with `ExternalReference = ref` and
  `OrderId = null` (exercises the Google digest fallback over HTTP). `valid:{ref}` is unchanged.
  Done when: existing IAP tests green; the convention is in the class XML doc.
- [ ] T026 [US1] Verifier tests in `tests/SampleGame.Tests/StoreVerifierTests.cs` (existing file with stub HTTP
  handlers, after T023–T025): Google `purchaseType` 0/1/2/absent → `IsTest`/category; `purchaseTimeMillis` present,
  absent and unparsable; `orderId` absent → `OrderId` null; Apple `Sandbox`, `Xcode`, `LocalTesting` → test,
  `Production` → not test; `purchaseDate` → `PurchasedAt`, absent → null; fake `valid:` and `noorder:` (FR-010, US1-5,
  US1-7).
  Done when: green.
- [ ] T027 [US1] Payment host tests in a new file `tests/SampleGame.Tests/IapAnalyticsTests.cs` (after T013, T022,
  T025). Two `TestHost`s share one store and one `FakeClock`; `TestHost` disables the background sweeper
  (`Outbox:SweeperEnabled=false`), so redelivery uses `OutboxSweeper.SweepOnceAsync` on host B after advancing the clock
  past the relay backoff (≥ 30 s). Cases:
  - US1-1, SC-001 matrix: N purchases for M players (some players twice); replays with the same request id (US1-2);
    resubmissions with new request ids → `ALREADY_GRANTED` (US1-3); invalid receipt, unknown product, store failure →
    nothing; host A `FailWrites` for some purchases → purchase OK, receipt pending, host B sweep delivers (US1-4). Over
    the records of both sinks: distinct `transactionID` = N, `COUNT(DISTINCT userID)` = M, 0 records for rejected or
    replayed requests; the redelivered record has the `eventId`, `transactionID`, `chargeTime` computed from the
    receipt (`committedAtUtc`, entry id).
  - Required fields and exact key set (data-model §3.1); `chargeTime`/`chargeTimeUtc` = receipt `committedAtUtc`
    (FR-009, SD9); `isTest` true (fake); `receiptId` = receipt id; no `storePurchaseTimeUtc` for the fake.
  - Google `noorder:` → `google:h:` + 32 hex, the token appears nowhere in the line (US1-6, FR-008).
  - Inline bound (FR-013, SD12): `DeliveryTimeoutMs=50`, `WriteDelay` 2 s on host A → the purchase answers OK within
    bound + margin; the record arrives later (sweep) exactly once per `eventId` after dedupe.
  - Partial relay duplicate: delivery taken by the dispatcher but timed out → a second copy after the sweep has
    identical identifying fields (US1-4 last sentence).
  - Disabled (`Enabled=false`) → receipts relayed (`outboxPending` false), no record, no analytics error metric (FR-014,
    US5-2).
  - Receipt size (FR-012): a Google purchase with 3 grants, a 300-character token and a 128-character product id (add the
    product to the test catalog under `tests/Shared/config` if none fits) stays below 4 KiB and its analytics entry is
    ≤ 512 B.
  - Commit-unknown (edge case, CMD-04): an ambiguous commit followed by a client retry with the same request id → one
    record (use the existing `StoreFaultInjector` ambiguous-commit fault if available; otherwise record why not in a
    Note).
  Done when: green in 3 consecutive runs.
- [ ] T028 [US1] Purge interplay in `tests/GameServer.Platform.Web.Tests/AccountPurgeTests.cs` (abstract base, runs on the
  in-memory and file stores since 011; after T022): a purchase whose inline delivery failed (`FailWrites`) leaves its
  receipt pending; the player is purged (`FakeClock` past `purgeAfter`, `RunOnceAsync`); the purge's
  `RelayPendingAsync` delivers it → the record carries the anonymous id (`anon_…`) and the same `eventId` as before the
  purge; a record written before the purge keeps the raw id (FR-012, FR-036, edge case "account purge"). The existing
  full-store scan still finds the old id only in `account_deletions`.
  Done when: both subclasses green.

**Checkpoint**: US1 deliverable alone — payment records with the full contract in memory; full suite green.

## Phase 4: US2 — Data team measures retention and active days (P1)

- [ ] T029 [US2] New file `src/GameServer.Modules.Identity.Adapters/LoginAnalytics.cs` (research R11, after Phase 2):
  `LoginAnalytics` (`Login = ("login", 1, "activeTime")`, `LoggedIn(playerId, provider, createdPlayer)`,
  `Authenticated(playerId)`), internal `DailyPlayerSet` (`TryMark(playerId, localDay)`, `Count`; a new local day
  atomically replaces the set; reaching `ResumeTrackerCapacity` clears it) and `LoginFields` (`source`, `provider?`,
  `isNewUser`). Both methods return at once when analytics is inactive (no memory used). Time from `IClock`, local day
  from `AnalyticsTimeZone`. Counter `login_resume_tracker_resets_total` on `IdentityMetrics` in
  `src/GameServer.Modules.Identity.Adapters/IdentityOptions.cs`; register `LoginAnalytics` as singleton in
  `src/GameServer.Modules.Identity.Adapters/IdentityModule.cs`.
  Done when: the solution builds; the architecture tests stay green.
- [ ] T030 [US2] Login hook in `src/GameServer.Modules.Identity.Adapters/LoginCompletion.cs` (after T029): at the end of
  `CompleteAsync`, after the snapshot read succeeded and before `return`, call
  `LoggedIn(playerId, ProviderIdentities.ProviderOf(mappingKey), profile is null)` using the fresh profile read at the
  start. Nothing on any earlier throw (FR-016).
  Done when: existing identity tests green.
- [ ] T031 [US2] Resume hook in `src/GameServer.Modules.Identity.Adapters/PlayerBearerAuthenticationHandler.cs` (after
  T029): call `Authenticated(playerId)` immediately before `AuthenticateResult.Success` (after session, profile, access
  and epoch checks). No change to the Test scheme, GM schemes or `ProviderLoginService.CurrentPlayerAsync` (FR-019 by
  construction).
  Done when: existing session, ban and deletion tests green.
- [ ] T032 [US2] Login tests in a new file `tests/GameServer.Platform.Web.Tests/LoginAnalyticsTests.cs` (after T030):
  - one record per guest login (first: `isNewUser` true; second: false), provider login (fake Play Games and Game
    Center providers as in the existing identity tests: `provider` `pgs`/`gamecenter`), confirmed switch login; exact
    key set (data-model §3.2), `source = login` (US2-1, FR-015);
  - no record for: deleted (pending deletion), purged, suspended (login ban), wrong guest secret, invalid provider
    proof, rate limited, unconfirmed `SWITCH_ACCOUNT`, identity link (US2-2, FR-016);
  - sink failing or queue full → every login still 200, drops counted (US2-6, FR-004).
  Done when: green.
- [ ] T033 [US2] Resume tests in the same file (after T031, T032), `GameServer:Analytics:TimeZone = Asia/Ho_Chi_Minh` so
  local and UTC midnight differ:
  - a stored-session player calls commands on two local days → exactly one `resume` per local day; the boundary is
    local midnight (17:00Z), not 00:00Z (US2-3, FR-017, FR-018);
  - login then commands the same local day → no resume (US2-4);
  - expired, replaced (new epoch), deleted or suspended session → no resume (US2-5);
  - bearer authentication succeeded but the request is refused afterwards (gate or permission) → one resume (SD8);
  - Test-scheme requests (Development), GM cookie requests and public endpoints → none (FR-019);
  - two hosts → at most one resume per player, local day and instance;
  - `DailyPlayerSet` (internal, `InternalsVisibleTo GameServer.Platform.Web.Tests` already exists): capacity reached →
    cleared + `login_resume_tracker_resets_total` + at most one extra resume per player; restart → at most one extra;
  - inactive analytics → no record and an empty set.
  Done when: green in 3 consecutive runs. SC-002 is met by T032 + T033.

**Checkpoint**: US1 + US2 in memory; full suite green.

## Phase 5: US3 — Every record states its time unambiguously (P1)

The zone resolution and its unit tests are foundational (T006, T007, T015, T016). This phase proves the rules at host
level and on real records.

- [ ] T034 [US3] Startup tests in a new file `tests/GameServer.Platform.Web.Tests/AnalyticsStartupTests.cs` (after
  T011, T013), with `TestHost.StartAsync(environmentName:)`:
  - unknown zone (`Asia/Hanoi_typo`) and a Windows id (`SE Asia Standard Time`, per V1) → one startup exception naming
    `GameServer:Analytics:TimeZone`, the value and the fix, before any request is served (US3-3, SC-007);
  - Staging and Production with a sink and no zone → error naming the key (US3-4); Development and Test without zone →
    start, `timeZone` `UTC` in records, the warning logged exactly once under `GameServer.Startup` or the pipeline
    category;
  - no sink registered (a `TestHost` variant without the in-memory sink) in Production without zone → starts, "no sink"
    warning, no record (SD5); `Enabled=false` → no warning;
  - a range violation (`QueueCapacity=5`) → error naming the key, merged into the one startup report with other module
    errors (011 `IStartupValidator` path).
  Done when: green.
- [ ] T035 [US3] Record time tests in a new file `tests/SampleGame.Tests/AnalyticsTimeTests.cs` (after T027, T033):
  hosts configured with `Asia/Ho_Chi_Minh`, `Asia/Taipei` and `Europe/London`; with `FakeClock` produce a purchase and a
  login at 16:59:59.999Z / 17:00:00.000Z (Ho Chi Minh midnight), 23:59:59.999Z / 00:00:00.000Z (UTC midnight) and ±1 ms
  around both 2026 London changes. For **every** record: `{time}` and `{time}Utc` parse to the same instant, `utcOffset`
  equals the offset inside `{time}`, `timeZone` is the configured name, millisecond precision (US3-1, US3-2, SC-003).
  The process time zone is irrelevant by construction (`TimeZoneInfo.Local` banned, T014); the test also asserts that
  no record or file-name date is computed from `DateTime.Now`-style local values by comparing against values computed
  from UTC only (US3-5).
  Done when: green.

**Checkpoint**: US1–US3 (all P1) complete in memory.

## Phase 6: US4 — Operations runs the analytics files safely (P2)

- [ ] T036 [P] [US4] Observability internals (research R10): extract free-space probing from
  `src/GameServer.Platform.Observability/LogSinkMonitor.cs` into a new `src/GameServer.Platform.Observability/DiskSpace.cs`
  (`internal static long? FreeBytes(string directory)`); `LogSinkMonitor` uses it. Make
  `ObservabilityExtensions.Sanitize` `internal`. Add `<InternalsVisibleTo Include="GameServer.Observability.Tests" />`
  to `src/GameServer.Platform.Observability/GameServer.Platform.Observability.csproj` (none exists today).
  Done when: `GameServer.Observability.Tests` green, unchanged.
- [ ] T037 [US4] File names in a new file `src/GameServer.Platform.Observability/Analytics/AnalyticsFileNames.cs`
  (research R9, data-model §6, after T036): `{gameId}-{instanceId}-{event}-{yyyyMMdd}.jsonl` and
  `.{gameId}-{instanceId}.lock` with the log-file sanitization; parse from the right (`event` has no `-`); own-file
  matcher for retention.
  Done when: covered by T040 (names, parse round trip, instance ids containing `-`).
- [ ] T038 [US4] File sink in a new file `src/GameServer.Platform.Observability/Analytics/AnalyticsFileSink.cs`
  (research R9, after T002, T037): `IAnalyticsSink` (`Name = "file"`) + `IHostedLifecycleService` + `IDisposable`:
  - lifecycle per the V3 result (T002): `StartingAsync` = resolve the directory against the content root, create it
    (failure → `GameServerStartupException` naming `GameServer:Analytics:Directory` and the absolute path), probe
    writability, open the lock with `FileShare.None` (held → `GameServerStartupException` naming
    `Observability:InstanceId`, the resolved id and the directory), retention pass; `StoppedAsync` = flush, close,
    release the lock;
  - instance id = `ObservabilityOptions.ResolveInstanceId()`; day = `max(lastDay, UtcDate(clock.UtcNow))` once per
    batch; on a day change close every handle of the old day;
  - handles `FileMode.Append, FileAccess.Write, FileShare.Read`; on open, if the file is non-empty and its last byte is
    not `\n`, write `\n` first; each record `json + "\n"`; `Flush(flushToDisk: false)` once per batch;
  - on `IOException`/`UnauthorizedAccessException`: close every handle and rethrow (pipeline counts a sink error);
  - `RetainLocalDays = N`: at start and at each day change delete only this instance's own files older than N UTC days;
  - `FreeBytes()` via `DiskSpace`.
  Inactive analytics (`Enabled=false`) → no directory, no lock, no file (US5-2).
  Done when: the solution builds; T040 covers it.
- [ ] T039 [US4] Health and composition (research R10, R13, after T038): new files
  `src/GameServer.Platform.Observability/Analytics/AnalyticsHealthCheck.cs` (name `analytics`, tag `ready`; Degraded
  when free space < `LowDiskBytes` or a sink error within `ErrorWindowSeconds` on `IClock`; never Unhealthy; data
  `sinkErrors`, `dropped`, `queue`, `freeBytes`, `lastError` ≤ 500 chars) and
  `src/GameServer.Platform.Observability/Analytics/AnalyticsFileExtensions.cs` (`AddAnalyticsFiles(this
  GameServerBuilder)`: one sink instance registered as `IAnalyticsSink` and as hosted lifecycle service, the health
  check, gauge `analytics_dir_free_bytes`). State is per host (no static monitor like `LogSinkMonitor.Instance`), so
  parallel test hosts do not share it.
  Done when: the solution builds; T041 covers it.
- [ ] T040 [US4] File sink tests in a new file `tests/GameServer.Observability.Tests/AnalyticsFileSinkTests.cs` (after
  T038), temp folders, `FakeClock`:
  - names and parse round trip; one file per event type (US4-1, FR-022);
  - 00:00 UTC roll: 23:59:59.999Z → file of day D, 00:00:00.000Z → day D+1, the D file never written again (US4-2,
    FR-023, SC-005); clock stepped back across midnight → stays on D+1 (edge case);
  - restart the same day → appends; a torn last line (bytes without `\n` written by the test) → `\n` inserted before
    the next record; a reader that skips non-JSON lines gets every acknowledged record (US4-3, FR-024, SD2);
  - **[VERIFY] V4**: a second sink with the same (game, instance, directory) fails `StartingAsync` naming
    `Observability:InstanceId`; a raw second `FileStream` on the lock in the same process fails too; after the holder
    is disposed the next start succeeds (US4-4, FR-025, SC-007); record the V4 result as a Note;
  - uncreatable directory (path under a file) → startup error naming `GameServer:Analytics:Directory` (SC-007);
  - write failure (directory replaced by a file, or read-only attribute where supported) → `WriteAsync` throws, handles
    closed, the next batch after the fix reopens and repairs (US4-5);
  - retention: own files older than N days deleted; other instances' files and newer files kept (FR-029); default →
    nothing deleted.
  Done when: green in 3 consecutive runs.
- [ ] T041 [US4] Health tests in a new file `tests/GameServer.Observability.Tests/AnalyticsHealthTests.cs` (after
  T039): sink error → Degraded, Healthy again after `ErrorWindowSeconds` (`FakeClock`); `LowDiskBytes = long.MaxValue`
  → Degraded (US4-6); never Unhealthy; liveness unaffected; public `/health` shows only the status word, the GM detail
  carries the data fields (FR-027).
  Done when: green.
- [ ] T042 [US4] Host tests in a new file `tests/GameServer.Observability.Tests/AnalyticsFileHostTests.cs` (after T039,
  T022, T030), using `ObservedHost` (or `TestHost` with `b.AddObservability()`) plus `b.AddAnalyticsFiles()` on a temp
  directory and a unique `Observability:InstanceId`:
  - guest login + fake purchase → `*-login-*.jsonl` and `*-payment-*.jsonl` exist, lines identical to the in-memory
    sink's lines (US5-3);
  - analytics directory made unwritable while running → 100% of logins and purchases succeed (SC-004 second half),
    login records dropped and counted, payment receipts pending, `analytics_sink_errors_total` up, readiness Degraded
    (US4-5); directory restored + clock past backoff + sweep → payment records written once;
  - graceful stop with queued login records (slow sink) → written and flushed before exit within the drain budget,
    the rest counted `shutdown` (US4-7, FR-028).
  Done when: green in 3 consecutive runs.
- [ ] T043 [US4] Provision test in `tests/GameServer.Platform.Web.Tests/GameServerCommandLineTests.cs` (after T039):
  Production, `AddAnalyticsFiles()` registered, no `GameServer:Analytics:*` settings, analytics directory pointing at a
  non-existent temp path, fake `IStoreProvisioner` → exit 0, the directory and lock file do not exist afterwards
  (US4-8, FR-030).
  Done when: green.
- [ ] T044 [P] [US4] Architecture rule in `tests/GameServer.Architecture.Tests/ArchitectureTests.cs` (after T038): no
  framework or module assembly other than `GameServer.Platform.Observability` depends on `AnalyticsFileSink` (hosts and
  tests excluded, as the `FileDocumentStore` rule); the existing ARC-01 rules stay green with `GameServer.Analytics` in
  Core (FR-005, D19).
  Done when: green; temporarily referencing `AnalyticsFileSink` from a module fails the rule (verified, reverted).

**Checkpoint**: file sink, lock, roll, repair, retention and health proven; full suite green.

## Phase 7: US5 — Game teams get analytics by default and can extend it (P2)

- [ ] T045 [US5] `samples/SampleHost/Program.cs`: `gameServer.AddAnalyticsFiles();` right after
  `gameServer.AddObservability();` (D18, contracts/operations §2).
  Done when: the solution builds. Do not run the WAF-based suites before T047 (they would write into the content root
  and collide on the lock).
- [ ] T046 [P] [US5] Flavor files `samples/SampleHost/appsettings.{Local,Dev,Beta,Uat,Live}.json`: add
  `"Analytics": { "Enabled": true, "TimeZone": "Asia/Ho_Chi_Minh" }` under `GameServer` (data-model §1); the base
  `appsettings.json` gets no analytics key (FR-032).
  Done when: the JSON parses; T049 covers it.
- [ ] T047 [US5] Every `WebApplicationFactory<Program>` user gets its own analytics directory and instance id (after
  T045), because the sample host now writes files under its content root with the machine name as instance id, and
  parallel test classes would fail on the lock:
  - `tests/Sample.Counter.Tests/SampleHostSmokeTests.cs` (temp folder already used for the file store: add
    `GameServer:Analytics:Directory` = `<temp>/analytics`);
  - `tests/SampleGame.Tests/SampleHostConfigurationFileTests.cs` (host case) and
    `tests/SampleGame.Tests/SampleHostEntryPointTests.cs`;
  - `tests/GameServer.Platform.Couchbase.IntegrationTests/Infrastructure/SampleHostFactory.cs` (`SampleHostFlavorTests`,
    Beta/Live flavors).
  Each sets `GameServer:Analytics:Directory` to a unique temp folder (deleted on dispose) and a unique
  `Observability:InstanceId`, or `GameServer:Analytics:Enabled=false` where the test does not care about analytics.
  Done when: these classes are green running in parallel, and after a full run no `samples/SampleHost/analytics/` folder
  and no `analytics/` folder under any `tests/**/bin/` exists.
- [ ] T048 [P] [US5] `.gitignore`: add the **anchored** entry `/samples/SampleHost/analytics/` next to `logs/` (SD10).
  Done when: `git check-ignore -v samples/SampleHost/analytics/x.jsonl` reports the new rule, and
  `git -c core.ignorecase=true check-ignore src/GameServer.Platform/Analytics/AnalyticsPipeline.cs` and
  `git check-ignore src/GameServer.Platform.Observability/Analytics/AnalyticsFileSink.cs` report nothing.
- [ ] T049 [US5] `tests/SampleGame.Tests/SampleHostConfigurationFileTests.cs` (after T046, T047): every standard flavor
  file sets `GameServer:Analytics:Enabled = true` and a `TimeZone` that `AnalyticsTimeZone.TryResolve` accepts; the base
  file has no `GameServer:Analytics` key; no analytics key is a secret key (FR-032, FR-034, US5-1).
  Done when: green.
- [ ] T050 [US5] Smoke test in `tests/Sample.Counter.Tests/SampleHostSmokeTests.cs` (after T047): Local flavor with no
  game code beyond the sample → guest login and a fake purchase (`valid:` and `noorder:`) → the `login` and `payment`
  files exist in the temp analytics folder with one valid line each per event and the documented key sets (US5-1); scan
  every `*.jsonl` file of the folder for the session token, guest secret, purchase tokens and receipt payloads used
  (SC-006 second half, FR-035); a second case with `GameServer:Analytics:Enabled=false` → no folder, no lock, purchase
  receipt relayed (US5-2). Keep one case with the outbox sweeper on (011 trap: the sweeper does not await store init).
  Done when: green; the temp folder is deleted at the end.
- [ ] T051 [US5] Seam tests in a new file `tests/GameServer.Platform.Web.Tests/AnalyticsSeamTests.cs` (after T022,
  T030):
  - a test-only module whose command calls `ctx.AddAnalyticsEvent(new AnalyticsEventType("test_event", 1, "eventTime"),
    fields)` → one record with the envelope (`eventTime`/`eventTimeUtc`, `receiptId`) and the payload fields, at least
    once via the outbox (preview extension point, plan "Extension points");
  - a second registered `IAnalyticsSink` receives every payment, login, resume and custom record with bytes identical to
    the first (US5-4, FR-002); the second sink failing → payment not acknowledged (receipt pending), logins still 200;
  - a test module registering a projection or consumer with kind `analytics` → host start fails with the duplicate-kind
    message (FR-040);
  - the IAP purchase handler and the test module compile against Core only (the ARC-01 architecture test is the proof;
    reference it in a Note, US5-5).
  Done when: green.
- [ ] T052 [US5] Privacy scan in a new file `tests/SampleGame.Tests/AnalyticsPrivacyScanTests.cs` (research R14, after
  T027, T033): one `TestHost` drives guest login, provider login (fake provider), confirmed switch, resume, a Google
  purchase with a known 300-character token, a Google `noorder:` purchase and an Apple purchase; collect every secret
  used (session tokens, guest secret, provider credential/auth code, provider subject id, purchase tokens, receipt
  payloads, request bodies, client IP of the test server); assert that no raw line of the in-memory sink contains any
  of them, and that each record's key set is exactly the documented one (data-model §3) (SC-006, FR-035).
  Done when: green; the assertion fails when a token is injected into a field on purpose (verified, reverted).
- [ ] T053 [US5] Couchbase integration in a new file
  `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseAnalyticsTests.cs` (the plan names a
  `CouchbaseOutboxTests.cs` that does not exist), using `CouchbaseGameHost` (full sample game on Couchbase; its
  `services` hook registers an `InMemoryAnalyticsSink`, because only `TestHost` does that by default) with a
  `FakeClock` shared by two hosts (after T027):
  - host A purchase with `FailWrites` → receipt `outboxPending` in Couchbase; host B `SweepOnceAsync` after the backoff
    → one record whose `eventId`, `transactionID` and `chargeTime` match the receipt document; receipt relayed (US1-4 on
    real transactions);
  - dead letter + the **documented manual re-drive**: `Outbox:MaxRelayAttempts=1` and a failing sink → dead-lettered;
    run the exact N1QL statement of contracts/analytics-operations §7 (scope/collection of the test) → the next sweep
    relays it and writes the record (validates SD4's documentation);
  - receipt size on Couchbase for the T027 3-grant purchase (< 4 KiB).
  Done when: green on EE 7.6.12; no test scope or `gs-it-*` user left on the cluster.

**Checkpoint**: sample host writes files by default; seam, privacy and Couchbase delivery proven; full suite green in
memory and with `GAMESERVER_TEST_STORE=File`.

## Phase 8: US6 — Data team and operations find one documented contract (P3)

Write the docs in Vietnamese, like the existing README, guides and PCR; keys, field names, metric names, SQL and
commands stay literal.

- [ ] T054 [P] [US6] PCR `docs/pcr/platform.md` new **§18 Analytics** (FR-037, FR-006, FR-039, FR-040, plan "Breaking
  changes"): event types and envelope; schema v1 tables of both types (contracts/analytics-records §3–§5); versioning
  rule; delivery per type (payment at-least-once through the outbox; login/resume best effort — **JOB-02 accepted loss**
  with its causes and the drop counter); dedupe keys; file naming, UTC day rule and "skip any non-JSON line" (SD2);
  settings with defaults, ranges and per-flavor values; `Enabled` uniform per deployment; metrics and health; inline
  bound 500 ms (SD12); outbox kind `analytics` reserved; the extension seam marked "extension point, preview"; privacy
  (raw `userID`, 25 months, purge does not rewrite files); breaking items; perf numbers from T063/T064. The version line
  names slice 012.
  Done when: every item of FR-037 is present (checklist in a Note); T061 is green on §18.
- [ ] T055 [P] [US6] PCR `docs/pcr/iap.md`: the payment event, `transactionID` rule (never the purchase token, LOG-05),
  `isTest`/`purchaseCategory`/`storePurchaseTimeUtc`, `VerifiedPurchase` init properties (custom verifiers that do not
  set `IsTest` report real purchases), fake verifier conventions (`isTest` always true, `noorder:{ref}`), receipt size
  note (per-grant token cost) (FR-037).
  Done when: the items are present.
- [ ] T056 [P] [US6] PCR `docs/pcr/identity.md`: login and resume events, the resume rule (per player, deployment-local
  day and instance; counted once bearer authentication succeeded, SD8), `ResumeTrackerCapacity` and
  `login_resume_tracker_resets_total`, JOB-02 accepted loss (FR-037).
  Done when: the items are present.
- [ ] T057 [P] [US6] New guide `docs/guides/analytics.md` for the data team and game teams: contracts/analytics-records
  in full (files, fields, time semantics with `log_month`/`log_date`, dedupe, UL and active-days queries, `environmentId`
  and `isTest` filters, promo purchases without revenue, never derive dates from file names, skip non-JSON lines,
  25-month retention and privacy-policy input), plus the game-team part (host composition `AddAnalyticsFiles()`,
  in-memory sink in tests, second sink, preview `AddAnalyticsEvent` seam) (US6-1, FR-036).
  Done when: an analyst can follow it without the code (checked in T062).
- [ ] T058 [P] [US6] `docs/guides/environments-and-local-run.md`: analytics settings per flavor (data-model §1), deploy
  checklist items — persistent volume or shipper with the LOG-03 policy and ≤ 60 s lag, local/block volumes only (lock
  not reliable on NFS/SMB, V4), container time zone data (**[VERIFY] V2**: confirm from the .NET image docs, or with
  `docker run --rm mcr.microsoft.com/dotnet/aspnet:10.0 ls /usr/share/zoneinfo/Asia/Ho_Chi_Minh` when Docker is available;
  record the source in a Note), distinct instance ids for processes sharing a directory, `Enabled` uniform per
  deployment (FR-039), size estimate (≈ 0.4 GB per million logins per day), rolling deploy and full-rollback note
  (dead letter after ~3 h, SD3), the manual re-drive statement of contracts §7 (SD4) (FR-038, US6-2).
  Done when: the items are present; T061 is green on the guide.
- [ ] T059 [P] [US6] `README.md`: one short paragraph pointing to `docs/guides/analytics.md` and the analytics settings
  in the environments guide; the local flavor writes `samples/SampleHost/analytics/` (git-ignored).
  Done when: T061 is green on the README.
- [ ] T060 [P] Load tool purchase step in `tools/GameServer.LoadTest/Program.cs` and `tools/GameServer.LoadTest/VirtualPlayer.cs`
  (SD11): a `--purchase-every <n>` option for the `burst` scenario (allowed-option list in `Options.Parse`); each
  virtual player sends `iap.purchase` with the fake verifier payload `valid:{unique}` every n-th action; `Recorder`
  reports purchase p50/p95/p99 separately.
  Done when: `burst --purchase-every 5` against the local flavor (`tools/local/run-local.ps1`) completes and prints
  purchase percentiles; the help text lists the option.

## Phase 9: Polish and close

- [ ] T061 [US6] Docs key test in `tests/SampleGame.Tests/DocumentedConfigurationKeysTests.cs` (after T054, T057–T059;
  SC-009 key part). Today the section map sends every `GameServer:` key to `GameServerOptions`, so
  `GameServer:Analytics:TimeZone` (bound to its own `AnalyticsOptions`, SD7) would fail the test as soon as the docs name
  it. Change: resolve by the **longest** section prefix with `GameServer:Analytics` → `AnalyticsOptions`; add
  `docs/guides/analytics.md` and PCR `## 18.` to the documents; add "the operator docs (environments guide, PCR §18) name
  every bound `AnalyticsOptions` key".
  Done when: green; injecting `GameServer:Analytics:Bogus` into the guide fails the test (verified, reverted).
- [ ] T062 [US6] Contract walkthrough test in a new file `tests/SampleGame.Tests/AnalyticsContractTests.cs` (after T027,
  T033, T057; SC-009): with `FakeClock` and zone `Asia/Ho_Chi_Minh`, generate purchases for known players across a local
  month boundary (including one at 23:59:59 local verified / 00:00:01 granted), duplicates by redelivery, and logins +
  resumes across several local days; read the raw lines only and apply the guide's rules literally (dedupe by
  `transactionID`, `environmentId`/`isTest` filters, month = first 7 chars of `chargeTime`, active days = distinct first
  10 chars of `activeTime`) → UL per month and active days per player equal the expected values. A manual analyst
  walkthrough on the T050 smoke files is recorded as a Note (who, date, result).
  Done when: green; Note recorded.
- [ ] T063 Opt-in performance tests in a new file `tests/SampleGame.Tests/AnalyticsPerformanceTests.cs`
  (`GAMESERVER_PERF=1`, otherwise skip with a message; after T042):
  - p95 login and purchase latency on `TestHost` with analytics on (file sink on a temp folder on the data drive) vs
    off: login +≤ 1 ms, purchase +≤ 5 ms (SC-004);
  - burst 180 logins/s for 60 s with the default queue → 0 dropped (SC-008);
  - sink blocked (`WriteDelay` = 1 h) → queue length ≤ `QueueCapacity`, drops counted, working set growth bounded.
  Done when: the numbers are printed and recorded in a Note for PCR §18 (T054).
- [ ] T064 SC-004 measurement with the load tool (after T060): run `burst` with and without `--purchase-every` against
  the local flavor with `GameServer__Analytics__Enabled=true` and `=false`; record login/purchase p95 deltas in a Note
  and in PCR §18. The SC-008 rate (60 logins/s) is reachable with the tool's defaults.
  Done when: Note recorded; deltas within SC-004 or the miss is explained and reported to the owner.
- [ ] T065 Pre-review full run, recording counts as a Note:
  - Release build of `GameServer.slnx`: 0 warnings;
  - full suite in memory;
  - full suite with `GAMESERVER_TEST_STORE=File`;
  - full suite with Couchbase (`COUCHBASE_*`, ≈ 19 min) including T053;
  - `GAMESERVER_PERF=1` run of T063.
  After the runs: `git status --porcelain` shows only intended changes (no `analytics/` folder in the repository, no
  `*.jsonl`), no test scope or `gs-it-*` user left on the cluster, no `SampleHost` process.
  Done when: all green, or every failure is fixed or listed with its reason.
- [ ] T066 Run tech-lead-reviewer on the slice (request + plan + this file + changed files). Focus: LOG-05 (no token in
  any record, log line or metric tag), the payment ack path (ack only after all sinks flushed; timeout/abandon
  duplicates), lifecycle ordering (lock before Kestrel listens, drain before close), purge interplay, `TestHost`
  defaults, docs ↔ options. Fix Blocker and Major findings.
  Done when: the review summary (counts, fixes, deferred items) is appended to this file.
- [ ] T067 Update `AgentMemories/features/p05-speckit/00_summary.md`: the 012 analytics-audit row, technical notes
  (outbox kind `analytics`, `AddAnalyticsFiles()`, `TestHost.Analytics`, zone rule, `VerifiedPurchase` init properties,
  docs-key test section map) and open items (re-drive tool → ops-readiness, refund/revoke events → iap-notifications,
  CI-01, V2 if unverified, perf numbers if not run).
  Done when: the file names 012 as the latest slice and the next step.
- [ ] T068 Final gate:
  - `build GameServer.slnx -c Release` has 0 warnings;
  - `test --solution GameServer.slnx -c Release --no-build` is all green, **including Couchbase** (`COUCHBASE_*`
    variables, ≈ 19 min) and `GameServer.Architecture.Tests`;
  - one extra pass with `GAMESERVER_TEST_STORE=File` is green.

  Then commit `feat:` with no signature lines, only when the owner asks for the commit.
  Done when: counts recorded; commit hash noted.

## Dependencies

- Phase 1 blocks everything; T002 must finish before T006, T007 and T038.
- Phase 2 blocks every story. Inside Phase 2:
  - T003, T004, T005 and T014 run in parallel; T012 follows T004.
  - T006 needs T002. T007 needs T004 and T006. T008 needs T003, T004 and T006.
  - T009 needs T005, T007 and T008. T010 needs T009. T011 needs T007–T010. T013 needs T011 and T012.
  - Tests: T015 after T006, T016 after T007, T017 after T008, T018 after T009 and T012, T019 after T010.
- **Phase 3 (US1) ∥ Phase 4 (US2) ∥ Phase 6 (US4)** after Phase 2. They touch disjoint files: IAP business/adapters and
  `SampleGame.Tests` (US1); Identity adapters and `LoginAnalyticsTests.cs` (US2); Platform.Observability and
  `GameServer.Observability.Tests` (US4).
  - Phase 3: T020 → T021 → T022; T023, T024, T025 in parallel → T026; T027 after T022 and T025; T028 after T022.
  - Phase 4: T029 → T030, T031 → T032 → T033.
  - Phase 6: T036 → T037 → T038 → T039; T040 after T038; T041 after T039; T043 after T039; T044 after T038.
    **T042 also needs T022 and T030** (it drives purchases and logins).
- Phase 5 (US3): T034 needs only Phase 2; T035 needs T027 and T033.
- Phase 7 (US5) needs Phases 3, 4 and 6: T045 → T047 → T049, T050; T046 and T048 are [P]; T051 after T022 and T030;
  T052 after T027 and T033; T053 after T027.
- Phase 8 (US6) after Phase 7 (the docs describe the final behaviour); T054–T060 are [P]. T060 only needs Phase 2.
- Phase 9: T061 after T054 and T057–T059; T062 after T027, T033 and T057; T063 after T042; T064 after T045 and T060.
  Then T065 → T066 → T067 → T068.
- Shared files that force order:
  - `src/GameServer.Modules.Iap/IapModel.cs`: T020 → T022.
  - `tests/GameServer.Platform.Web.Tests/LoginAnalyticsTests.cs`: T032 → T033.
  - `tests/Sample.Counter.Tests/SampleHostSmokeTests.cs`: T047 → T050.
  - `tests/SampleGame.Tests/SampleHostConfigurationFileTests.cs`: T047 → T049.
  - `src/GameServer.Platform.Observability/LogSinkMonitor.cs` and `.csproj`: T036 only; `GameServerBuilder.cs`: T011
    only; `tests/Shared/TestHost.cs`: T013 only.
  - `samples/SampleHost/Program.cs` (T045) must land together with T047: running the WAF suites in between writes
    analytics files into `samples/SampleHost/` and fails on the lock.
- Parallel examples: T003 ∥ T004 ∥ T005 ∥ T014; T015 ∥ T016 ∥ T017; T023 ∥ T024 ∥ T025 ∥ T029 ∥ T036 (three lanes);
  T040 ∥ T041 ∥ T044; T046 ∥ T048; T054 ∥ T055 ∥ T056 ∥ T057 ∥ T058 ∥ T059 ∥ T060.

## Implementation strategy

1. **MVP (Phases 1–3)**: payment records with the full contract, delivered at least once, proven in memory (SC-001).
   This is the UL axis of owner task 3. Stop and check the checkpoint before continuing.
2. **Login (Phase 4)**: login and resume records (SC-002) — completes the data of owner task 3.
3. **Time proof (Phase 5)** and **files (Phase 6)**: required before any shared environment (beta/uat/live) receives
   the build.
4. **Template default (Phase 7)**: the sample host writes files by default; seam, privacy scan and Couchbase delivery.
5. **Contract and close (Phases 8–9)**: PCR, guides, docs-key test, walkthrough, performance, full runs, review,
   resume file.

With one implementer run the phases in order. With three, split after Phase 2 into the US1, US2 and US4 lanes (disjoint
files) and merge before T035/T042, then continue with Phase 7.

**Never deploy an intermediate build** (after Phase 3 but before Phase 7) to an environment whose data matters: without
`AddAnalyticsFiles()` analytics is inactive, and the consumer acknowledges payment entries without writing them — those
payments would never get a record.

## Implementation risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| `TestHost` now activates analytics (memory sink, extra outbox entry per purchase, extra records per login) for 800+ tests | Medium | T013 runs the full suite and lists every fixed assertion; checkpoint after Phase 2 |
| The four `WebApplicationFactory<Program>` users start writing files into the sample content root with the machine name as instance id → lock failures between parallel classes, stray files in the repository | High (red suite) | T045 lands with T047; T048 anchored ignore; T065 checks `git status` and `tests/**/bin/` |
| `DocumentedConfigurationKeysTests` maps every `GameServer:` key to `GameServerOptions`, so analytics keys in the docs fail it | Medium | T061 adds the longest-prefix section map; docs tasks name it in their Done when |
| Process-wide `MeterListener` cross-talk between parallel test classes (seen in 010/011) | Medium | Filter by meter instance or run the metric tests in a non-parallel collection (T018, T027) |
| V1/V3/V4 runtime facts differ from research | Medium | T002 runs first with stated fallbacks; T015/T040 pin the behaviour |
| Timing tests (inline bound, drain, burst) flaky on a busy Windows machine | Medium | Assert bound + margin, prefer `FakeClock`, 3 consecutive green runs; perf only opt-in |
| Purchase receipts near the 4 KiB limit (many grants × long Google token) | Medium | Size tests T027/T053; PCR iap note (T055) |
| Full rollback dead-letters pending receipts; the documented re-drive query could be wrong | High in production | T053 runs the exact query on Couchbase; T058 deploy note |
| Intermediate build deployed without a sink acknowledges payments without records | High | Strategy rule above; T065/T066 confirm the sample host composition |
| Couchbase suite (≈ 19 min) hits cluster-manager 503s | Low | Existing `CouchbaseManagementRetry`; run in T065 and T068 only |

## FR coverage

| FR | Tasks |
| --- | --- |
| FR-001 | T004, T009, T011, T038, T045 |
| FR-002 | T004, T012, T039, T051 |
| FR-003 | T003, T008, T010, T017, T019, T022, T027, T053 |
| FR-004 | T009, T018, T029, T032, T054 |
| FR-005 | T003, T021, T022, T044, T051 |
| FR-006 | T003, T008, T054, T057 |
| FR-007 | T021, T022, T027 |
| FR-008 | T021, T027, T052 |
| FR-009 | T020, T023, T024, T027 |
| FR-010 | T020, T023, T024, T025, T026, T027 |
| FR-011 | T003, T022, T027 |
| FR-012 | T003, T027, T028, T053 |
| FR-013 | T009, T010, T018, T027 |
| FR-014 | T010, T019, T027, T050 |
| FR-015 | T029, T030, T032 |
| FR-016 | T030, T032 |
| FR-017 | T029, T031, T033 |
| FR-018 | T029, T033 |
| FR-019 | T031, T033 |
| FR-020 | T006, T008, T015, T035 |
| FR-021 | T006, T007, T015, T016, T034 |
| FR-022 | T037, T038, T040, T042 |
| FR-023 | T038, T040 |
| FR-024 | T038, T040, T054, T057 |
| FR-025 | T038, T040 |
| FR-026 | T005, T009, T018, T042 |
| FR-027 | T039, T041, T042 |
| FR-028 | T009, T018, T042 |
| FR-029 | T038, T040, T054, T057 |
| FR-030 | T011, T043 |
| FR-031 | T007, T011, T016, T038, T061 |
| FR-032 | T045, T046, T048, T049 |
| FR-033 | T008, T017 |
| FR-034 | T007, T049 |
| FR-035 | T050, T052 |
| FR-036 | T028, T054, T057 |
| FR-037 | T054, T055, T056 |
| FR-038 | T053, T058 |
| FR-039 | T054, T058 |
| FR-040 | T011, T051, T054 |

| Success criterion | Tasks |
| --- | --- |
| SC-001 | T027, T053 |
| SC-002 | T032, T033 |
| SC-003 | T015, T035 |
| SC-004 | T042 (unwritable directory), T060, T063, T064 |
| SC-005 | T040 |
| SC-006 | T050, T052 |
| SC-007 | T016, T034, T040 |
| SC-008 | T018, T063, T064 |
| SC-009 | T057, T061, T062 |

Scenarios:
- US1: US1-1, US1-2, US1-3 → T027; US1-4 → T027, T053; US1-5 → T026, T027; US1-6 → T021, T027, T052; US1-7 → T026, T027.
- US2: US2-1, US2-2, US2-6 → T032; US2-3, US2-4, US2-5 → T033.
- US3: US3-1, US3-2 → T015, T035; US3-3, US3-4 → T016, T034; US3-5 → T014, T035.
- US4: US4-1..US4-4 → T040; US4-5 → T040, T042; US4-6 → T041; US4-7 → T042; US4-8 → T043.
- US5: US5-1 → T049, T050; US5-2 → T027, T050; US5-3 → T012, T042; US5-4 → T051; US5-5 → T022, T044, T051.
- US6: US6-1 → T054, T057, T062; US6-2 → T058, T061.

[VERIFY] items: V1 → T002, T006, T015; V2 → T058; V3 → T002, T038; V4 → T040; V5 → T023; V6 → T024.

## Planning notes (gaps between spec, plan and code, resolved here)

1. **Docs-key test.** `tests/SampleGame.Tests/DocumentedConfigurationKeysTests.cs` (011 T053) resolves every
   `GameServer:` key against `GameServerOptions`; analytics settings bind to their own `AnalyticsOptions` (SD7). T061
   changes the section map before the docs can name analytics keys without turning the suite red.
2. **`WebApplicationFactory<Program>` users.** Four test classes run the real sample entry point
   (`SampleHostSmokeTests`, `SampleHostConfigurationFileTests`, `SampleHostEntryPointTests`, Couchbase
   `SampleHostFactory`). The plan only names the smoke test; T047 covers all four.
3. **Sweeper in tests.** `TestHost` sets `Outbox:SweeperEnabled=false`; redelivery tests call
   `OutboxSweeper.SweepOnceAsync` after moving `FakeClock` past the backoff (T027, T042, T053).
4. **Paths in the plan that do not exist.** `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseOutboxTests.cs`
   does not exist; T053 creates `CouchbaseAnalyticsTests.cs` on `CouchbaseGameHost`, whose `services` hook must register
   the in-memory sink (only `TestHost` does it by default). Verifier tests live in the existing
   `tests/SampleGame.Tests/StoreVerifierTests.cs` (T026).
5. **Purge tests are abstract since 011.** `AccountPurgeTests` runs on the in-memory and file stores; the purge case
   (T028) runs on both.
6. **Nulls in outbox payloads.** `CommandContext.AddOutbox` serializes with `GameServerJson.Options`, which writes nulls;
   T003 keeps null fields out of the payload (receipt size, FR-012).
7. **Internals.** `GameServer.Platform.Observability` has no `InternalsVisibleTo`; T036 adds one for
   `GameServer.Observability.Tests`. `GameServer.Platform.Tests` does not reference the Identity adapters, so
   `DailyPlayerSet` is tested in `GameServer.Platform.Web.Tests` (T033; `InternalsVisibleTo` exists).
8. **Static health state.** `LogSinkMonitor.Instance` is a process-wide static; the analytics health state must be per
   host so parallel test hosts do not see each other's errors (T039).
9. **Machine time zone (US3-5).** The process time zone cannot be changed reliably inside a Windows test run; the
   requirement is enforced by construction (banned `TimeZoneInfo.Local`/`DateTime.Today`, T014) and checked by comparing
   records and file names with UTC-only computations (T035, T040).
10. **Re-drive query.** The manual re-drive of contracts/operations §7 is executed against Couchbase in T053 so the
    documented statement is known to work (SD4).
11. **Load tool.** It has only the `burst` and `contention` scenarios; T060 adds a purchase option to `burst` (SD11).
12. **New FRs.** FR-039 (uniform `Enabled`, SD13) and FR-040 (reserved outbox kind, D20) were added to the spec during
    reconciliation; T051/T054/T058 cover them.
13. **Docs language.** README, guides and PCR are Vietnamese; Phase 8 follows it.
