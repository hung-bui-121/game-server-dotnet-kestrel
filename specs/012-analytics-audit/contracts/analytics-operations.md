# Contract — analytics operations: settings, files, metrics, health, rollout (012)

Operations-facing contract, published in PCR platform §18 and `docs/guides/environments-and-local-run.md` (deploy
checklist). Removing or renaming a key, metric, health check or file-name part below is a breaking change (PCR-01).

## 1. Settings (`GameServer:Analytics`, environment variables `GameServer__Analytics__<Key>`)

See [data-model §1](../data-model.md#1-configuration--section-gameserveranalytics-analyticsoptions-r8) for types,
defaults and ranges. Operator-relevant rules:

- `TimeZone` (IANA) is **required** in Staging/Production when analytics is active; unknown names, Windows zone ids and
  images without time zone data fail startup (exit 1) with a message naming the key and the fix.
- `Enabled` must be the same on every instance of a deployment (a disabled instance acknowledges payment entries
  committed by enabled instances without writing them).
- Instance id: `Observability:InstanceId` → `HOSTNAME` → machine name. Two processes with the same id and the same
  analytics directory: the second exits 1 at startup naming `Observability:InstanceId`.
- `--provision` reads no analytics setting and creates no file, directory or lock.

## 2. Host composition

```csharp
gameServer.AddObservability();      // logs, OTel, /health
gameServer.AddAnalyticsFiles();     // JSONL analytics files + readiness check "analytics" (sample host default)
```

Without `AddAnalyticsFiles()` (and no other sink) analytics is inactive: one startup warning, nothing written, payment
outbox entries acknowledged.

## 3. Volume and shipping (LOG-03 policy)

- Mount the analytics directory on a **persistent volume** per pod, or run a shipper (Vector, Fluent Bit, Filebeat)
  with ≤ 60 s lag. Local or block volumes only (the single-writer lock is not reliable on NFS/SMB).
- Ship files of past UTC dates as complete; the current day's file may be tailed (lines are appended whole and flushed
  per batch). A shipper must tolerate and skip an unparseable line.
- Size: ≈ 0.4 GB per million logins per day uncompressed; payments are negligible in comparison.
- The server never deletes analytics files unless `RetainLocalDays` is set (then only its own files older than N UTC
  days). Shipped data: 25 months (data store policy).
- Container image: needs time zone data (`tzdata`; Debian-based `aspnet` images have it, Alpine and plain chiseled images
  do not — use the `-extra` chiseled variant or install `tzdata`).

## 4. Metrics (meter `GameServer.Analytics`; labels never hold ids)

| Metric | Labels | Alert idea |
| --- | --- | --- |
| `analytics_events_written_total` | `event`, `sink` | rate drop to 0 with traffic |
| `analytics_events_dropped_total` | `event`, `reason` (`queue_full`, `sink_error`, `shutdown`) | `login` drops sustained > 5 min (payments are never dropped here: they stay in the outbox and count in `analytics_delivery_failures_total`) |
| `analytics_sink_errors_total` | `sink` | > 0 |
| `analytics_delivery_failures_total` | `reason` (`queue_full`, `timeout`, `sink_error`, `stopped`, `malformed`) | > 0 sustained |
| `analytics_queue_entries` | — | > 50 % of `QueueCapacity` |
| `analytics_dir_free_bytes` | — | < `LowDiskBytes` |
| `login_resume_tracker_resets_total` (meter `GameServer.Identity`) | — | frequent → raise `ResumeTrackerCapacity` |
| existing `outbox_relay_inline_failures_total{kind="analytics"}`, `outbox_dead_letter_total` | | dead letter > 0 |

## 5. Health

- Readiness check `analytics` (tag `ready`): **Degraded** when the analytics directory has less free space than
  `LowDiskBytes` or a sink error happened within `ErrorWindowSeconds`; never Unhealthy (gameplay continues). Liveness
  unaffected. Detail (GM `ops.read`): `sinkErrors`, `dropped`, `queue`, `freeBytes`, `lastError`.
- While the sink fails: logins succeed (records dropped and counted); purchases succeed (the payment record waits in the
  receipt outbox and is retried by the sweeper with backoff 30 s → 1 h).

## 6. Inline bound (FR-013)

A purchase response waits for its payment record at most `DeliveryTimeoutMs` (default **500 ms**) beyond the existing
outbox relay; normally < 1 ms. On timeout the purchase still succeeds and the record is delivered by the sweeper.

## 7. Rollout, rollback, dead letters (FR-038)

- **Rolling deploy**: safe. Receipts of the new build carry outbox kind `analytics`; old instances that sweep one log
  "No outbox consumer for kind analytics" and back off (one attempt counted); new instances deliver it.
- **Full rollback to a pre-012 build**: receipts still pending at that moment (inline relay failed shortly before) are
  retried by the old build and **dead-lettered after `Outbox:MaxRelayAttempts` (10, ≈ 3 h)**. After rolling forward,
  re-drive them (below). Receipts already relayed are unaffected.
- **Dead-lettered receipt (any cause)**: the payment record is missing until re-driven. Manual re-drive (no tool in this
  slice), Couchbase example:

  ```sql
  UPDATE `<bucket>`.`g_<gameId>`.`receipts` r
  SET r.outboxDeadLetter = false, r.outboxAttempts = 0
  UNSET r.outboxNextAttemptAtUtc
  WHERE r.outboxPending = true AND r.outboxDeadLetter = true
    AND ANY e IN r.outbox SATISFIES e.kind = "analytics" END;
  ```

  The sweeper then relays them; ledger entries are idempotent, analytics copies are deduplicated downstream. The query
  touches only the outbox bookkeeping fields that the relay itself updates outside transactions
  (`MarkOutboxFailedAsync`), and only on dead-lettered receipts, which no command transaction writes any more. A GM/ops
  re-drive endpoint is deferred to the ops-readiness slice.
- Outbox kind `analytics` is reserved: a game consumer or projection with that kind fails startup.
