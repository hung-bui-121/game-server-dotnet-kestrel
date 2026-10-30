# Research — 012 analytics-audit

Decisions for [spec.md](spec.md). Requirement IDs refer to `Requirements/Game_Server_System_Requirements_v1.2.md`.
Signatures are contracts for the implementer; bodies are deliberately absent. Items marked **[VERIFY]** are runtime or
store-API facts the implementer confirms on the pinned versions (.NET SDK 10.0.403, Google Play Developer API v3, App
Store Server JWS) before coding against them; each has a stated fallback.

Code facts this research relies on (verified 2026-10-31 on branch `012-analytics-audit`, base `ab17cc0` = 011 merged):

- `VerifiedPurchase(bool Valid, string Store, string ProductId, string ExternalReference, string? OrderId, string? Reason)`
  is a **public positional record** in the IAP business assembly (`IapModel.cs:15`). `ExternalReference` is the Google
  purchase token or the Apple `transactionId`; it is the claim reference (`extref::iap_{store}::{reference}`,
  `IapModel.cs:84`) and is copied into every ledger entry (`LedgerSource`, `IapModel.cs:89`).
- `GooglePlayReceiptVerifier` (`:76-87`) computes `isTest = purchaseType == 0`, refuses it in Production and **discards**
  it; it returns the token as `ExternalReference` and `orderId` (possibly null). It never reads `purchaseTimeMillis`.
- `AppleJwsReceiptVerifier` (`:102-113`) allows `Production` only in Production and `Production|Sandbox|Xcode|LocalTesting`
  elsewhere, then **discards** the environment; it returns `transactionId` as both `ExternalReference` and `OrderId`. It
  never reads `purchaseDate`.
- `FakeStoreReceiptVerifier`: payload `valid:{ref}` → valid, `ExternalReference = ref`, `OrderId = "GPA." + ref`; used
  for both stores whenever `Iap:UseFakeVerifier ?? AllowsDevelopmentTools` (`IapModule.cs:76`), refused outside
  Development/Test.
- `IapModule.VerifyAsync` verifies **before** the command (`IapModule.cs:49-66`); the handler is pure over
  `ICommandContext` (`IapPurchaseHandler.HandleAsync`, `IapModel.cs:76-99`). Rejections roll back everything staged,
  outbox included (`CommandOutcome.Reject`, `ICommandHandler.cs:63-68`).
- `CommandContext.AddOutbox(kind, payload)` (`CommandContext.cs:131-135`) serializes the payload with
  `GameServerJson.Options` (Web defaults = camelCase, **nulls written**) and assigns `EntryId = "{receiptId}:{index}"`.
- `CommandRunner` builds the receipt with `CommittedAtUtc = FormatTime(now)` (`CommandRunner.cs:398`; `now` is the
  attempt's server time, `yyyy-MM-ddTHH:mm:ss.fffZ`) and, after commit, **awaits** `relay.TryRelayAsync(receiptKey,
  receipt, CancellationToken.None)` before answering (`CommandRunner.cs:224-227`). Receipts above
  `Commands:ReceiptMaxBytes` (4096) fail the command (`CommandRunner.cs:330-334`).
- `OutboxRelay.TryRelayAsync` (`OutboxRelay.cs:111-154`) delivers **every** entry of the receipt on every attempt; any
  consumer failure or unknown kind (logged, `outbox_relay_inline_failures_total{kind}`) records a failed attempt with
  exponential backoff (`RetryDelaySeconds` 30 → `MaxRetryDelaySeconds` 3600) and dead-letters at `MaxRelayAttempts` (10).
  Consumers are one per kind; a duplicate kind fails startup. `QueryPendingOutboxAsync` excludes dead-lettered receipts
  (`InMemoryDocumentStore.cs:310-329`, `CouchbaseDocumentStore.cs:131`); nothing re-drives a dead letter (the GM receipt
  view only shows the flag, `GmPlayerEndpoints.cs:221`).
- Account purge (010): `PurgeRewrites.Anonymize` replaces every JSON string equal to the player id anywhere in a receipt,
  `ApplyReceiptRules` clears `requestId`, `bodyHash`, `resultCompact`, `correlationId`; `AccountPurgeService.RelayPendingAsync`
  then relays the rewritten receipts still pending (not dead-lettered) so projections carry the anonymous id.
- `ObservabilityOptions.ResolveInstanceId()` = `Observability:InstanceId` → `HOSTNAME` → machine name; log files are
  `{Sanitize(gameId)}-{Sanitize(instanceId)}-.log` with Serilog `RollingInterval.Day` (machine-local date). `LogSinkMonitor`
  measures free space of the mount holding the directory; `LogHealthCheck` returns Degraded, never Unhealthy.
- `ProfileEnforcementMiddleware` authenticates only non-Public endpoints, with `PlayerSchemes` for Player endpoints
  (`PlayerBearer` first, then `Test` in Development/Test) and `GmSchemes` for GM endpoints (`:69-84`). The bearer handler
  succeeds only after session valid → profile present → access checks → epoch current (`PlayerBearerAuthenticationHandler.cs:30-77`).
  `ProviderLoginService.CurrentPlayerAsync` validates the bearer token directly, not through the handler.
- `LoginCompletion.CompleteAsync` (`LoginCompletion.cs:76-100`) is the common end of guest, provider and confirmed switch
  logins: fresh profile read (`null` = first login) → access checks → epoch write → session → snapshot → success log.
- `IStartupCheck`s run in **both** serve and `--provision` mode (`ProvisioningRunner.RunAsync`); module options validated
  with `ValidateOnStart()` are merged into the serve report through `IStartupValidator` and are **not** run in provision
  mode (011 R4, `GameServerApplicationExtensions.cs:184-199`).
- `TestHost` uses content root `AppContext.BaseDirectory`, never calls `AddObservability`, and copies SampleHost's base
  `appsettings.json` (flavor files removed). `SampleHostSmokeTests` runs `WebApplicationFactory<Program>` with content
  root = `samples/SampleHost` and the `Local` flavor. Several tests switch `DeploymentEnvironment` to Staging/Production.
- `.gitignore` ignores `logs/` and `_database/` (unanchored patterns). `BannedSymbols.txt` bans `DateTime(Offset).(Utc)Now`;
  `TimeZoneInfo.Local` and `DateTime.Today` are not banned and are not used in `src/` or `modules/` today.
- Architecture tests: business assemblies may not depend on `GameServer.Platform*`; only Platform/Couchbase/Testing use
  `ITransactionContext`; only Platform.Observability uses Serilog/OpenTelemetry; modules never use
  `ITransactionalDocumentStore`; only Platform.Testing knows `FileDocumentStore`.

---

## R1 — Where the code lives (FR-002, FR-005, ARC-01)

**Decision.** No new project. Five homes, each owning one concern:

| Assembly | Owns | Why here |
| --- | --- | --- |
| `GameServer.Core` (`GameServer.Analytics`) | `AnalyticsEventType`, `AnalyticsOutboxEvent`, `AnalyticsOutbox.AddAnalyticsEvent` (outbox kind `analytics`) | Module business code (IAP handler) may only use Core (ARC-01); same precedent as `AuditExtensions.AddAudit` |
| `GameServer.Platform` (`GameServer.Platform.Analytics`) | options + validator, time zone, record formatter, sink seam, emitter, pipeline (queue + dispatcher), outbox consumer, metrics | Needed by every host and by Identity adapters; no ASP.NET dependency |
| `GameServer.Platform.Observability` (`…Observability.Analytics`) | JSONL file sink, single-writer lock, health check, `AddAnalyticsFiles()` | Ops file output next to the log files; already owns instance id resolution, disk-space probing and readiness checks |
| `GameServer.Platform.Testing` | `InMemoryAnalyticsSink` | Test doubles live with `FakeClock` / `InMemoryDocumentStore` |
| Modules | IAP business: payment event type, `transactionID`, fields; Identity adapters: `LoginAnalytics` (login + resume) | Each module owns the meaning of its own event |

**Alternatives.** (a) New `GameServer.Platform.Analytics` project — one more csproj for ~10 small files with no new
package; would need its own instance-id and disk-space code or a reference to Observability. (b) Everything in
Observability — the pipeline would then be missing from hosts that skip `AddObservability` (TestHost, future GM-only
hosts) while the IAP module still writes `analytics` outbox entries that need a consumer (R4). (c) File sink in Platform —
puts file I/O and health checks in the assembly every module adapter references.

**Trade-off.** The options type holds a few file-sink keys (directory, disk threshold) although the file sink lives in
Observability; accepted to keep one section (FR-031) and one validator.

---

## R2 — Payment event through one generic outbox kind (FR-003, FR-005, FR-011, FR-012, ANA-01)

**Decision.** The IAP handler adds one outbox entry of kind **`analytics`** (constant `AnalyticsOutbox.Kind`) after the
grants, in the same transaction:

```csharp
namespace GameServer.Analytics;

/// <summary>Analytics event type (ANA-01): stream/file name, schema version, owner-chosen name of the time field.</summary>
public sealed record AnalyticsEventType(string Name, int SchemaVersion, string TimeField)
{
    /// <summary><c>^[a-z][a-z0-9_]{0,31}$</c> (used in file names).</summary>
    public static bool IsValidName(string? name);

    /// <summary><c>^[a-z][A-Za-z0-9]{0,31}$</c>.</summary>
    public static bool IsValidTimeField(string? name);
}

/// <summary>Payload of an outbox entry of kind <see cref="AnalyticsOutbox.Kind"/>.</summary>
public sealed record AnalyticsOutboxEvent(string Event, int SchemaVersion, string TimeField, object Fields);

public static class AnalyticsOutbox
{
    public const string Kind = "analytics";

    /// <summary>
    /// Records an analytics event committed with the command (at-least-once). The platform adds the envelope at delivery:
    /// userID = the receipt's player, time = the receipt commit time, event id, game/environment, zone. Throws
    /// <see cref="ArgumentException"/> for an invalid type (a module bug; the command fails INTERNAL, nothing commits).
    /// </summary>
    public static void AddAnalyticsEvent(this ICommandContext context, AnalyticsEventType type, object fields);
}
```

The platform registers **one** `IOutboxConsumer` for kind `analytics` in `AddGameServer` (always, whether or not a sink
is registered — R4). It reads the self-describing payload and builds the envelope from the delivery
(`OutboxDelivery.PlayerId`, `CommittedAtUtc`, `ReceiptId`, `Entry.EntryId`).

Consequences that close spec points:

- **Replay / rejection (FR-011)**: a replay never runs the handler; a rejection rolls back staged outbox entries; a failed
  verification never reaches the command. No new mechanism.
- **Purge (FR-012, point 6)**: the payload holds **no player id** (userID comes from `receipt.playerId` at delivery), so
  the 010 generic anonymization of the receipt covers it. A receipt still pending at purge time is rewritten, then relayed
  by the purge (`RelayPendingAsync`) → its record carries the anonymous id. An inline/sweeper delivery racing the purge
  may still write the raw id; already-written records keep the raw id (owner Q11, FR-036). A dead-lettered receipt of a
  purged player, re-driven later, ships the anonymous id.
- **Size (FR-012)**: ≤ 512 B per entry (data-model §5); fields are written with `WhenWritingNull` so absent optionals
  cost nothing.
- **Visibility**: the entry appears in `GetReceipt` (CMD-09, owning player and GM `player.read`) like ledger entries; it
  holds nothing secret (R3).

**Extension seam (P1 ANA-01, US5-4).** The payload is self-describing, so another module command can call
`AddAnalyticsEvent(new AnalyticsEventType("tutorial_done", 1, "eventTime"), …)` and get at-least-once delivery and its
own file without any framework change; best-effort events from adapters use `IAnalyticsEmitter` (R5). Custom event
types are **not** published as a supported contract in this slice (PCR marks the seam "extension point, preview"); one
test proves the seam with a test-only event type.

**Alternatives.** (a) Kind per event (`analytics.payment`, as in the prior analysis) — each new event type needs a new
consumer registration on every instance and widens the rolling-deploy hazard (R12) per type. (b) Derive the payment from
the ledger entries — ledger has no store/product/test flag and one entry per grant. (c) Emit from the IAP adapter after
the command — loses atomicity with the grant (a crash after commit loses the record), violating FR-003.

**Trade-off.** Field names of custom events are a contract between the emitting module and the data team; the platform
only guarantees the envelope (data-model §3) and ignores fields that collide with envelope names (R6).

---

## R3 — `transactionID` (FR-008, LOG-05, point 1)

**Decision.** Computed in IAP business code (pure, deterministic), stored in the outbox payload:

| Store | Value | Example |
| --- | --- | --- |
| Apple | `apple:{transactionId}` | `apple:2000000748921311` |
| Google with `orderId` | `google:{orderId}` | `google:GPA.3391-5521-0937-41862` |
| Google without `orderId` (license-tester and some promo purchases) | `google:h:{hex(SHA-256(UTF-8(purchaseToken)))[0..32]}` | `google:h:9f2c0e…` (32 lowercase hex) |

```csharp
namespace GameServer.Modules.Iap;

public static class IapAnalytics
{
    public static AnalyticsEventType Payment { get; }          // ("payment", 1, "chargeTime")

    /// <summary>Store transaction id for the analytics record; never the Google purchase token (LOG-05).</summary>
    public static string TransactionId(VerifiedPurchase purchase);
}
```

Why plain SHA-256, not a keyed hash:

- **Non-reversible**: the input is a high-entropy random token (Google tokens are ~150+ random characters); SHA-256 preimage
  resistance makes the digest useless for recovering it, and there is no dictionary to try. A key adds no protection here.
- **Stable / dedupe-safe**: the same token always yields the same id on every instance, environment and redelivery, with
  no secret to distribute or rotate. A key (HMAC + pepper) would need a new entry in `GameServer:SecretKeys` (FR-034 forbids
  it) and would change ids on rotation, breaking `dropDuplicates`.
- **128 bits** (32 hex) is collision-free for any realistic purchase volume; the `h:` marker tells the data team the id is
  a digest, not an order id (no `google:h:` order id exists: Google order ids start with `GPA.`).

`orderId` stays a separate optional field (tracing with Play Console). Apple's `transactionId` is unique per
transaction (consumables included) and not a credential. The claim reference is unchanged (still the token for Google).

**Alternatives.** (a) The claim reference (spec input) — ships the Google token (LOG-05). (b) HMAC with a pepper —
rejected above. (c) Truncated digest of `orderId|token` — needless; `orderId` alone is unique for one-time products.

---

## R4 — Outbox consumer semantics, inline bound, rollback (FR-013, FR-014, JOB-02, points 3–4)

**Decision.** `AnalyticsOutboxConsumer.HandleAsync`:

| Situation | Result | Effect on the receipt |
| --- | --- | --- |
| Analytics **inactive** (`Enabled = false`, or no sink registered) | return (ack), nothing written, no metric | relayed normally (FR-014) |
| Active, payload valid | build envelope → `pipeline.DeliverAsync` → returns when **every** sink has written and flushed the line to the OS | relayed |
| Active, queue full | throws `AnalyticsDeliveryException("queue full")` immediately | attempt recorded, backoff, sweeper |
| Active, no ack within `DeliveryTimeoutMs` (default **500 ms**) | throws (`timeout`); the queued item is marked abandoned and skipped by the dispatcher if not yet taken | attempt recorded, sweeper |
| Active, a sink fails the batch | throws (`sink_error`) | attempt recorded, sweeper |
| Pipeline stopped (shutdown) | throws (`stopped`) | another instance's sweeper |
| Payload malformed (not an `AnalyticsOutboxEvent`) | throws, error log names the entry id | retried, then dead-lettered (module bug made visible) |

- **Bounded inline wait (FR-013).** The runner keeps awaiting the relay; the consumer bounds its own wait. Worst case a
  purchase response is delayed by `DeliveryTimeoutMs` (published in PCR); normal case < 1 ms (enqueue, dispatcher wake-up,
  buffered write, `Flush(false)`). The same bound applies to sweeper deliveries (the consumer cannot tell callers apart);
  a disk slower than 500 ms per batch is already an incident (readiness Degraded, R10).
- **At-least-once is preserved**: the ack is only returned after the line is handed to the OS (FR-013 last sentence);
  every non-ack leaves `outboxPending = true`. A timed-out item that the dispatcher already took may still be written →
  duplicate with identical identifying fields, removed by dedupe (contract).
- **Dead letter.** A sink failing for the whole backoff series (~3 h for 10 attempts) dead-letters the receipt; ledger
  entries were already delivered idempotently on the first attempt. No re-drive tool exists in the framework; the ops
  guide documents the manual re-drive (set `outboxDeadLetter = false`, `outboxAttempts = 0`, remove
  `outboxNextAttemptAtUtc` on the receipt; the sweeper picks it up). A GM/ops re-drive endpoint is deferred
  (ops-readiness slice).
- **Rolling deploy / rollback (FR-038).** A pre-012 build has no consumer for `analytics`: when its sweeper picks a
  pending 012 receipt it logs "No outbox consumer", **counts an attempt** and backs off. During a rolling deploy new
  instances relay inline and mark receipts relayed, so old sweepers rarely see them. After a **full rollback** the few
  receipts pending at that moment are dead-lettered by the old build after ~3 h (not "pending until roll-forward" as the
  spec edge case says) and need the manual re-drive after roll-forward. Deploy note + re-drive query in the guide.
- **`Enabled` must be uniform per deployment**: a disabled instance acks entries committed by an enabled one. Stated in PCR.

**Alternatives.** (a) Fire into the queue and ack at once — loses payments on crash or full queue (violates FR-003/JOB-02).
(b) Write synchronously on the request thread with a timeout — concurrent writers on one file need a lock; no group
flush. (c) A separate guaranteed queue with priority over logins — not needed at the target rates (SC-008: 60 logins/s);
one queue keeps one ordering and one drain path. (d) Runner drops `analytics` entries when disabled — makes FR-014
trivial but couples the runner to analytics and still leaves the rollback hazard once enabled.

---

## R5 — Pipeline: one bounded queue, one dispatcher, sinks get finished lines (FR-001, FR-002, FR-004, FR-026, FR-028)

**Decision.**

```csharp
namespace GameServer.Platform.Analytics;

/// <summary>Destination of analytics records (default: JSONL files; tests: in memory; P1: Couchbase, Kafka, HTTP).</summary>
public interface IAnalyticsSink
{
    /// <summary>Metric/health label, e.g. <c>file</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Writes the batch in order and returns once every record is handed to the destination (file: written and flushed to
    /// the OS). Throws on any failure; the whole batch then counts as failed for this sink.
    /// </summary>
    ValueTask WriteAsync(IReadOnlyList<AnalyticsRecord> batch, CancellationToken cancellationToken);
}

/// <summary>One finished record: UTF-8 JSON of one line, without the trailing newline.</summary>
public sealed record AnalyticsRecord(string Event, string EventId, ReadOnlyMemory<byte> Json);

/// <summary>Best-effort emission for code outside commands (login, resume). Never blocks, never throws.</summary>
public interface IAnalyticsEmitter
{
    /// <summary>False when analytics is disabled or no sink is registered (callers skip their own work).</summary>
    bool IsActive { get; }

    /// <summary>Queues the event; false when inactive or the queue is full (dropped and counted).</summary>
    bool TryEmit(AnalyticsEventType type, string userId, DateTimeOffset occurredAtUtc, object fields);
}

/// <summary>An event before formatting (envelope inputs + type-specific fields).</summary>
public sealed record AnalyticsEnvelope(
    AnalyticsEventType Type,
    string EventId,
    string UserId,
    DateTimeOffset OccurredAtUtc,
    string? ReceiptId,
    object Fields);

public sealed class AnalyticsPipeline : BackgroundService, IAnalyticsEmitter
{
    public bool IsActive { get; }

    public bool TryEmit(AnalyticsEventType type, string userId, DateTimeOffset occurredAtUtc, object fields);

    /// <summary>Guaranteed path (outbox): completes after every sink wrote the record; throws <see cref="AnalyticsDeliveryException"/>.</summary>
    public Task DeliverAsync(AnalyticsEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>Waits until everything queued before the call is written (tests, shutdown).</summary>
    public Task FlushAsync(CancellationToken cancellationToken);

    public AnalyticsStatus Status { get; }
}

public sealed record AnalyticsStatus(long SinkErrors, long Dropped, int QueueLength, DateTimeOffset? LastErrorAtUtc, string? LastError);

public sealed class AnalyticsDeliveryException(string reason, Exception? inner = null) : Exception(reason, inner);
```

- **Queue**: `Channel.CreateBounded<Pending>(QueueCapacity = 10 000, SingleReader = true, FullMode = Wait)`; producers use
  `TryWrite` only (never wait). A `Pending` carries the envelope and, for deliveries, a
  `TaskCompletionSource(RunContinuationsAsynchronously)` plus an abandoned flag.
- **Dispatcher** (`ExecuteAsync`): await one item, take up to 511 more without waiting (no linger → no added latency),
  skip abandoned items, format each once (`AnalyticsRecordFormatter`, R6), call every sink in registration order, then
  complete the deliveries (success only if every sink succeeded) and count `written`/`dropped{reason=sink_error}`.
  Formatting happens on the dispatcher, so the request path cost of a login is one `TryWrite` (SC-004).
- **Shutdown (FR-028, OPS-03)**: `StopAsync` completes the writer, drains the queue until the host's stop token fires,
  counts what is left as `dropped{reason=shutdown}` and fails pending deliveries. The pipeline is registered **before**
  `OutboxSweeper` in `AddGameServer`, so it stops after the sweeper and after Kestrel (reverse order); file handles are
  closed in the file sink's `StoppedAsync` (R9), after every `StopAsync`.
- **Inactive** = `Enabled == false` or zero `IAnalyticsSink` registrations. `TryEmit` returns false without counting;
  the consumer acks (R4). With `Enabled` and no sink, one startup warning ("analytics enabled but no sink registered").

**JOB-02 (point 5).** Login and resume events are in-memory events; their loss (queue full, sink error, crash, shutdown
overrun) is accepted and recorded in PCR platform §18 and identity PCR, with the drop counter as the signal. Rationale:
UL is computed from payments; `active_days` counts distinct local days, so a lost login rarely changes it (any other
login/resume of that day restores it).

**Alternatives.** (a) Sinks own their queues — every sink re-implements bounding, drop counting and shutdown, and the
"same guarantees for every sink" rule (US5-4) becomes per-sink. (b) Serilog as the writer — LOG-06 forbids audit-like
records depending on the log buffer, and Serilog rolls by machine-local time (R9). (c) Pass envelopes to sinks — every
sink would format; the in-memory sink could diverge from the file content (US5-3 wants identical content).

---

## R6 — Record format and event id (FR-006, FR-007, FR-015, FR-020, FR-033)

**Decision.** `AnalyticsRecordFormatter` writes one compact JSON object per record with `Utf8JsonWriter`
(`JavaScriptEncoder.Default`, no indentation), envelope first, in this order: `event`, `schemaVersion`, `eventId`,
`gameId`, `environmentId`, `userID`, `{timeField}`, `{timeField}Utc`, `timeZone`, `utcOffset`, `receiptId` (outbox events
only), then the type-specific fields in payload order. Null type-specific fields are omitted. A type-specific field whose
name equals an envelope name is ignored (envelope wins; one error log per event type and process).

Event id:

- **Outbox events (payment)**: `hex(SHA-256(UTF-8("{entryId}|{committedAtUtc}")))[0..32]`. Stable across every
  redelivery (both inputs are immutable receipt fields; the purge does not rewrite them) and unique even when CMD-07
  supersedes an expired receipt with the same `receiptId` (same request id reused after the 24 h window), which a raw
  `entryId` would not be. Still "derived from the outbox entry" (FR-003); `receiptId` is a separate field for tracing.
- **Emitted events (login, resume)**: `Guid.NewGuid().ToString("N")` at emission.

Both are 32 lowercase hex characters, one shape for the data team.

`gameId`/`environmentId` come from `GameServerOptions` only (FR-033). `userID` = `OutboxDelivery.PlayerId` (payment) or
the authenticated player id (login).

**Alternatives.** Raw `entryId` as event id (simplest; collides on CMD-07 supersede); JSON Schema files shipped with the
server (no consumer asked for it; PCR tables suffice).

---

## R7 — Time: zone resolution, per-event offset, formats (FR-020, FR-021, TIME-01, owner Q12)

**Decision.**

```csharp
namespace GameServer.Platform.Analytics;

/// <summary>Deployment zone of the analytics stream, resolved once at startup (FR-021).</summary>
public sealed class AnalyticsTimeZone
{
    /// <summary>Configured IANA name, or <c>UTC</c> when defaulted in Development/Test.</summary>
    public string Name { get; }

    public bool IsDefaulted { get; }

    /// <summary>The instant truncated to milliseconds, formatted three ways (same instant).</summary>
    public AnalyticsInstant Describe(DateTimeOffset utc);

    /// <summary>Local calendar date of the instant in the deployment zone (resume rule).</summary>
    public DateOnly LocalDate(DateTimeOffset utc);

    /// <summary>Resolution used by the validator and the constructor; never throws.</summary>
    public static bool TryResolve(string name, out TimeZoneInfo zone, out string error);
}

/// <summary><c>Utc</c> = <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>; <c>Local</c> = <c>yyyy-MM-ddTHH:mm:ss.fff+hh:mm</c>; <c>Offset</c> = <c>+hh:mm</c>.</summary>
public readonly record struct AnalyticsInstant(string Utc, string Local, string Offset);
```

- Offset per event: `TimeZoneInfo.ConvertTime(utcTruncated, zone)`; conversion **from UTC** is never ambiguous, so DST
  edges are exact (Europe/London `+00:00` in January, `+01:00` in July, correct on both sides of 01:00 UTC change instants).
  The instant is truncated to milliseconds **before** formatting, so `Local` and `Utc` always denote the same instant.
  `utcOffset` is `+00:00` for UTC (never `Z`); `Local` always carries the numeric offset. Invariant culture.
- Source of time: `IClock.UtcNow` for login/resume; the receipt's `committedAtUtc` for payment (owner Q9). Note:
  `committedAtUtc` is the server time of the transaction attempt that committed (`ICommandContext.Now`), at most the
  transaction expiration (10 s) before the actual commit — "server grant time" in the contract.
- **Validation (in `AnalyticsOptionsValidator`, only when analytics is active)**:
  - `TimeZone` set → `TryResolve`: `TimeZoneInfo.FindSystemTimeZoneById(name)` must succeed **and** the result must have
    `HasIanaId == true` (rejects Windows ids such as `SE Asia Standard Time`). Error text names
    `GameServer:Analytics:TimeZone`, the value, and the fix: "use an IANA name (e.g. Asia/Ho_Chi_Minh); container images
    need time zone data (tzdata / ICU) and must not run with InvariantGlobalization on Windows".
  - `TimeZone` missing → error in Staging/Production; `UTC` + one startup warning in Development/Test.
- Banned APIs: add `P:System.TimeZoneInfo.Local` and `P:System.DateTime.Today` to `BannedSymbols.txt` so no framework
  code can depend on the machine zone (US3-5).

**[VERIFY] V1** `HasIanaId` for a Windows id input: on Linux, `FindSystemTimeZoneById("SE Asia Standard Time")` may
convert and return a zone whose `HasIanaId` is true. Fallback: additionally require the configured value to match
`^(UTC|[A-Za-z_]+(/[A-Za-z0-9_+\-]+)+)$`. A unit test pins the behaviour on the developer OS and the Linux image.

**[VERIFY] V2** Container images: `mcr.microsoft.com/dotnet/aspnet:10.0` (Debian) ships `tzdata`; Alpine and chiseled
(`-chiseled`) images do not (the `-extra` chiseled variant does). The deploy checklist names the requirement; the fail-fast
message covers the miss.

**Alternatives.** Offset fixed at startup (wrong across DST — rejected by US3-2); per-player zone (GeoIP, out of scope);
NodaTime (new package; `TimeZoneInfo` with ICU/tzdata is sufficient on .NET 10).

---

## R8 — Options: own type bound from `GameServer:Analytics` (FR-021, FR-030, FR-031, FR-034)

**Decision.**

```csharp
namespace GameServer.Platform.Analytics;

/// <summary>Section <c>GameServer:Analytics</c> (OPS-01). No secrets (FR-034).</summary>
public sealed class AnalyticsOptions
{
    public const string SectionName = "GameServer:Analytics";

    public bool Enabled { get; set; } = true;

    /// <summary>IANA zone of the deployment; required in Staging/Production when analytics is active.</summary>
    public string? TimeZone { get; set; }

    /// <summary>File sink directory, relative to the content root; separate from the log directory.</summary>
    public string Directory { get; set; } = "analytics";

    public int QueueCapacity { get; set; } = 10_000;

    public int DeliveryTimeoutMs { get; set; } = 500;

    /// <summary>Players remembered per local day per instance for the resume rule (Identity).</summary>
    public int ResumeTrackerCapacity { get; set; } = 200_000;

    public long LowDiskBytes { get; set; } = 1024L * 1024 * 1024;

    public int ErrorWindowSeconds { get; set; } = 300;

    /// <summary>Optional: delete this instance's own files older than this many UTC days (FR-029). Null = never.</summary>
    public int? RetainLocalDays { get; set; }
}
```

Registered as `services.AddOptions<AnalyticsOptions>().BindConfiguration(SectionName).ValidateOnStart()` plus
`IValidateOptions<AnalyticsOptions>` (same pattern as `IapOptions`). Consequences:

- **Serve**: errors join the single startup report through `IStartupValidator` (011 R4) → exit 1 before traffic.
- **`--provision` (FR-030)**: validators are not run, no hosted service starts → no settings needed, no file, no lock.
  An `IStartupCheck` would have been run by `ProvisioningRunner` and is therefore **not** used for analytics.
- Ranges: `QueueCapacity` 100..1 000 000, `DeliveryTimeoutMs` 50..5 000, `ResumeTrackerCapacity` 1 000..10 000 000,
  `LowDiskBytes` ≥ 0, `ErrorWindowSeconds` 10..86 400, `RetainLocalDays` ≥ 1, `Directory` non-empty.

**Alternatives.** A property `GameServerOptions.Analytics` — bound by `GetSettings` without `IValidateOptions`, so the
zone rule would need an `IStartupCheck`, which provision mode runs (FR-030 conflict).

---

## R9 — File sink: names, UTC day, append safety, lock, retention (FR-022..FR-025, FR-029, LOG-01, LOG-03)

**Decision.**

```csharp
namespace GameServer.Platform.Observability.Analytics;

public static class AnalyticsFileExtensions
{
    /// <summary>
    /// Default sink (owner Q8): one JSONL file per instance, event type and UTC day under GameServer:Analytics:Directory,
    /// readiness check "analytics". Instance id = Observability:InstanceId → HOSTNAME → machine name (LOG-03).
    /// </summary>
    public static GameServerBuilder AddAnalyticsFiles(this GameServerBuilder gameServer);
}

public sealed class AnalyticsFileSink : IAnalyticsSink, IHostedLifecycleService, IDisposable
{
    public string Name { get; }                 // "file"

    public string DirectoryPath { get; }        // absolute

    public ValueTask WriteAsync(IReadOnlyList<AnalyticsRecord> batch, CancellationToken cancellationToken);

    /// <summary>Free bytes of the mount holding the directory; null when unknown.</summary>
    public long? FreeBytes();
}
```

- **Names**: `{gameId}-{instanceId}-{event}-{yyyyMMdd}.jsonl`, each part sanitized exactly like the log file name
  (`ObservabilityExtensions.Sanitize`, made `internal`). Lock file: `.{gameId}-{instanceId}.lock` in the same directory.
  Shippers parse from the right (event names contain no `-`), but consumers never derive data from names (contract).
- **Day**: `max(lastDay, UtcDate(clock.UtcNow))` computed once per batch — monotonic, so an NTP step back across 00:00
  UTC never reopens a closed day (FR-023, edge case). On a day change the sink closes every handle of the old day before
  writing. Serilog is not used: its `RollingInterval.Day` uses the machine-local date.
- **Append safety (FR-024)**: handles are opened `FileMode.Append, FileAccess.Write, FileShare.Read` (shippers read
  while we write). On open, if the file is non-empty and its last byte is not `\n`, write `\n` first (repairs a line cut
  by a crash). Each record is written as `json + "\n"`; after the batch, `Flush(flushToDisk: false)` (handed to the OS —
  survives process death, not power loss; same durability statement as 011 D5). On any `IOException`/
  `UnauthorizedAccessException`, every handle is closed (the next batch reopens and repairs) and the exception propagates
  to the pipeline (sink error). Consumers therefore must skip **any** unparseable line, not only the last one (spec
  deviation SD2).
- **Single writer (FR-025)**: `StartingAsync` creates the directory (error names `GameServer:Analytics:Directory` and the
  path), probes writability, and opens the lock file with `FileShare.None`. Held for the process lifetime; the OS releases
  it when the process dies, so a crashed holder never blocks the next start. A held lock fails startup with
  `GameServerStartupException` naming `Observability:InstanceId`, the resolved id and the directory → exit 1 (caught by
  `ServeAsync` like the 011 file-store lock).
- **Lifecycle**: `StartingAsync` (before any `StartAsync`, so before Kestrel listens) = directory + lock + retention
  pass; `StoppedAsync` (after every `StopAsync`, so after the pipeline drained) = flush, close, release lock.
- **Retention (FR-029)**: off by default. With `RetainLocalDays = N`, at start and at each day change the sink deletes
  only files whose name matches **its own** `{gameId}-{instanceId}-*-yyyyMMdd.jsonl` and whose date is more than N days
  before the current UTC day.

**[VERIFY] V3** `IHostedLifecycleService` ordering on .NET 10: every `StartingAsync` runs before any `StartAsync`
(including the web server's) and every `StoppedAsync` after all `StopAsync`. Fallback: acquire the lock in the
pipeline's `StartAsync` (registered before the sweeper) and close in its `StopAsync` after the drain.

**[VERIFY] V4** `FileShare.None` on Linux is an advisory `flock` per open file description: a second process — or a
second `FileStream` in the same process — fails. Network file systems (NFS/SMB) do not guarantee it: the guide requires
a local or block volume per pod (the usual persistent-volume setup), as for the logs.

**Alternatives.** Size-based splitting (out of scope); one file for all event types (spec requires per type); fsync per
batch (latency vs. a failure model — process death — that `Flush(false)` already covers).

---

## R10 — Metrics and health (FR-026, FR-027, OBS-01..03, LOG-06)

**Decision.** Meter `GameServer.Analytics` (exported by the existing `GameServer.*` prefix):

| Instrument | Kind | Tags |
| --- | --- | --- |
| `analytics_events_written_total` | counter | `event`, `sink` |
| `analytics_events_dropped_total` | counter | `event`, `reason` ∈ `queue_full`, `sink_error`, `shutdown` |
| `analytics_sink_errors_total` | counter | `sink` |
| `analytics_delivery_failures_total` | counter | `reason` ∈ `queue_full`, `timeout`, `sink_error`, `stopped`, `malformed` |
| `analytics_queue_entries` | observable gauge | — |
| `analytics_dir_free_bytes` | observable gauge (file sink) | — |
| `login_resume_tracker_resets_total` | counter (meter `GameServer.Identity`) | — |

No tag ever holds a player id, event id or transaction id (OBS-02). Inactive analytics records nothing (US5-2 "no
analytics metric reports errors"). Payment non-acks also appear in the existing `outbox_relay_inline_failures_total{kind="analytics"}`.

`AnalyticsHealthCheck` (`ready` tag, name `analytics`, registered by `AddAnalyticsFiles`): Degraded when
`free < LowDiskBytes` or a sink error happened within `ErrorWindowSeconds` (measured on `IClock`); never Unhealthy;
data `sinkErrors`, `dropped`, `queue`, `freeBytes`, `lastError` (exception type + message ≤ 500 chars, for the GM
`ops.read` detail; the public endpoint shows only the status word). Free-space probing is extracted from `LogSinkMonitor`
into `internal static DiskSpace.FreeBytes(string directory)` and shared.

---

## R11 — Login and resume (FR-015..FR-019, IDN-07, IDN-08, owner Q10)

**Decision.** One identity adapter class owns the login event and the resume rule:

```csharp
namespace GameServer.Modules.Identity;

/// <summary>Login analytics (Task 3): one record per successful login, one resume record per player, local day and instance.</summary>
public sealed class LoginAnalytics
{
    public static AnalyticsEventType Login { get; }   // ("login", 1, "activeTime")

    /// <summary>Called at the end of a successful login: emits source=login and marks the player seen today.</summary>
    public void LoggedIn(string playerId, string provider, bool createdPlayer);

    /// <summary>Called when the PlayerBearer scheme authenticated a request: emits source=resume once per local day.</summary>
    public void Authenticated(string playerId);
}

/// <summary>Players seen on one local day; replaced on a new day; cleared when full (each clear may cause one extra resume).</summary>
internal sealed class DailyPlayerSet
{
    /// <summary>True when the player was not yet seen on <paramref name="localDay"/> (and is now).</summary>
    public bool TryMark(string playerId, DateOnly localDay);

    public int Count { get; }
}
```

- **Login point**: `LoginCompletion.CompleteAsync`, after `snapshots.ReadAsync` succeeded and before `return` — the last
  step that can fail; refused/failed logins throw earlier and emit nothing (FR-016). `provider =
  ProviderIdentities.ProviderOf(mappingKey)` (`guest`, `pgs`, `gamecenter`, or an EXT provider name); `createdPlayer =
  profile is null` from the fresh read at the start (a purged account throws `ACCOUNT_DELETED` before). Confirmed switch
  logins reach the same point. Identity link is a command, not a login → nothing.
- **Resume point**: `PlayerBearerAuthenticationHandler.HandleAuthenticateAsync`, immediately before
  `AuthenticateResult.Success` (after session, profile, access and epoch checks — FR-017). Why the handler and not a
  middleware: the handler runs only for endpoints whose profile requires a player (`ProfileEnforcementMiddleware`), only
  for the PlayerBearer scheme (the development `Test` scheme is another handler; GM uses cookie schemes) → FR-019 by
  construction, with no endpoint or scheme bookkeeping in a middleware. `ProviderLoginService.CurrentPlayerAsync` does
  not go through the handler, so a provider login with a bearer header is not a resume. A request whose bearer
  authentication succeeded but which is then refused (gate, permission, body) still counts as a resume — the player was
  active (SD8).
- **Cost per request**: `IsActive` check, one `TimeZoneInfo.ConvertTime` (≈ 100 ns) for the local date, one concurrent
  dictionary lookup; emission only on the first request of the day.
- **Bound (FR-018)**: `ResumeTrackerCapacity` (200 000 ≈ 25 MB worst case at ~120 B per entry). Reaching it clears the
  set and increments `login_resume_tracker_resets_total`: each clear can produce at most one extra resume per active
  player (never a block, never a missing record). Restart loses the set: at most one extra resume per player and instance.
- **Day**: local date of `IClock.UtcNow` in the deployment zone; a new day atomically replaces the set (no timer).
- **Inactive analytics**: both methods return immediately (no memory used).

**Alternatives.** Middleware after authentication (needs to re-derive "PlayerBearer succeeded" and exclude Public/GM/Test
paths itself); `ExpiringCache` (TTL per entry, not "per local day"; unbounded between sweeps); persisting "seen today" in
the profile (a DB write per player per day; spec assumption excludes it).

---

## R12 — `VerifiedPurchase` test flag, store time and category (FR-009, FR-010, point 2)

**Decision.** Add **init-only properties** (not positional parameters) to the public record:

```csharp
public sealed record VerifiedPurchase(bool Valid, string Store, string ProductId, string ExternalReference, string? OrderId, string? Reason)
{
    /// <summary>Store test/sandbox purchase (Google purchaseType 0; Apple Sandbox/Xcode/LocalTesting; every fake verification).</summary>
    public bool IsTest { get; init; }

    /// <summary>Purchase time reported by the store (Google purchaseTimeMillis, Apple purchaseDate), when known.</summary>
    public DateTimeOffset? PurchasedAt { get; init; }

    /// <summary>Store purchase category when reported: "test", "promo", "rewarded" (Google purchaseType 0/1/2).</summary>
    public string? PurchaseCategory { get; init; }

    public static VerifiedPurchase Invalid(string store, string productId, string reason);
}
```

- Google: `IsTest = purchaseType == 0`, `PurchaseCategory` from `purchaseType` (absent → null), `PurchasedAt` from
  `purchaseTimeMillis` (int64 as string). Apple: `IsTest = environment != "Production"`, `PurchasedAt` from
  `purchaseDate` (ms), category null. Fake: `IsTest = true`, `PurchasedAt = null`; new payload convention
  `noorder:{reference}` → valid with `OrderId = null` (exercises the Google digest fallback over HTTP).
- **Compatibility**: source- and binary-compatible (no constructor, `Deconstruct` or `Invalid` change). Record equality now
  includes the new properties (no caller compares purchases). A game's own `IStoreReceiptVerifier` that does not set
  `IsTest` reports real purchases — PCR iap says so.

**[VERIFY] V5** Google `purchases.products.get`: `purchaseType` 0 = Test, 1 = Promo, 2 = Rewarded (absent for normal
purchases); `purchaseTimeMillis` is a string; `orderId` may be absent for test/promo. Fallback: if `purchaseTimeMillis`
is missing or unparsable, `PurchasedAt = null` (field omitted). **[VERIFY] V6** Apple JWS `purchaseDate` is a number (ms
since epoch); missing → null.

**Alternatives.** New positional parameters with defaults (binary-breaking constructor change for verifier authors and
tests); a separate `PurchaseFacts` object (one more type for three values).

---

## R13 — Registration, defaults and tests infrastructure (FR-030..FR-032, US5)

**Decision.**

- `AddGameServer` (always): `AnalyticsOptions` + validator, `AnalyticsTimeZone`, `AnalyticsMetrics`,
  `AnalyticsRecordFormatter`, `AnalyticsPipeline` (singleton, `IAnalyticsEmitter`, hosted — registered **before**
  `OutboxSweeper`), `IOutboxConsumer` → `AnalyticsOutboxConsumer`. Reserves outbox kind `analytics` (a game consumer or
  projection with that kind now fails startup with the existing duplicate-kind error).
- `AddAnalyticsFiles()` (explicit, like `AddObservability()`): file sink + health check. The sample host calls it, so the
  template gets files by default (FR-032) without making every test host write files and fight over locks.
- Identity module registers `LoginAnalytics` (singleton).
- `TestHost` registers an `InMemoryAnalyticsSink` (exposed as `TestHost.Analytics`) and sets
  `GameServer:Analytics:TimeZone = UTC` in `DefaultSettings()` — tests that switch to Staging/Production keep starting.
- `SampleHostSmokeTests` sets `GameServer:Analytics:Directory` to a folder under its temp folder.
- Sample flavor files: `GameServer:Analytics { Enabled: true, TimeZone: "Asia/Ho_Chi_Minh" }` in all five; `Directory`
  stays the code default `analytics`. `.gitignore` gains the **anchored** entry `/samples/SampleHost/analytics/`: an
  unanchored `analytics/` would, with `core.ignorecase=true` on Windows, also ignore the new `src/**/Analytics/` source
  folders.

```csharp
namespace GameServer.Platform.Testing;

/// <summary>In-memory sink for tests (FR-002, US5-3): same bytes as the file sink, parsed for assertions.</summary>
public sealed class InMemoryAnalyticsSink : IAnalyticsSink
{
    public string Name { get; }                                  // "memory"

    public IReadOnlyList<JsonObject> Records { get; }

    public IReadOnlyList<JsonObject> OfEvent(string eventName);

    /// <summary>Test seam: the next writes throw (sink error path).</summary>
    public bool FailWrites { get; set; }

    /// <summary>Test seam: each write waits this long (timeout path).</summary>
    public TimeSpan WriteDelay { get; set; }

    public ValueTask WriteAsync(IReadOnlyList<AnalyticsRecord> batch, CancellationToken cancellationToken);
}
```

**Alternatives.** File sink registered by `AddGameServer` with `Enabled` default on — every `TestHost` (hundreds per run,
parallel, same machine name and `bin/analytics`) would contend for the lock; would need test-only switches in framework
code. File sink inside `AddObservability()` — couples log and analytics on/off and still hits `ObservedHost`.

---

## R14 — Architecture rules and SC-006 scan (FR-005, FR-035, SC-006)

**Decision.**

- Existing ARC-01 rule already keeps `GameServer.Analytics` (Core) the only analytics dependency of business assemblies.
- New rule: no framework or module assembly other than `GameServer.Platform.Observability` depends on
  `AnalyticsFileSink` (hosts and tests excluded), mirroring the file-store rule.
- New banned symbols: `TimeZoneInfo.Local`, `DateTime.Today` (R7).
- FR-035/SC-006: (1) an end-to-end test drives guest login, provider login (fake provider), confirmed switch, resume,
  Google purchase with a known token, Google `noorder:` purchase and Apple purchase on one `TestHost`, then asserts that
  no record contains the session tokens, guest secret, provider credential, purchase token or receipt payload used, and
  that every record's key set is exactly the documented one; (2) the sample-host smoke test scans every `*.jsonl` file it
  produced for the same values. TestHost writes no files (in-memory sink), so "every analytics file of the full suite"
  is covered by (2) plus (1) on identical bytes (SD6).
