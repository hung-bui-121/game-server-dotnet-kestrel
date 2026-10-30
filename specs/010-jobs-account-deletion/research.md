# Research — 010 jobs-account-deletion

Decisions for [spec.md](spec.md). Requirement IDs refer to `Requirements/Game_Server_System_Requirements_v1.2.md`.
Signatures are contracts for the implementer; bodies are deliberately absent.

Code facts this research relies on (verified 2026-10-30 on branch `010-jobs-account-deletion`):

- `ITransactionContext` has `GetManyAsync`, `InsertAsync`, `ReplaceAsync` only: **no transactional remove**.
- `IPagedQueryStore` supports one equality filter + one string order field; documents missing the order field never
  match. `ITransactionalDocumentStore.QueryPendingOutboxAsync` is the precedent for a store-specific job query.
- `PlayerSnapshotReader`/`ProfileCache` return the **default state** for a missing `pa_profile` (`SessionEpoch = 0`,
  `Deleted = null`). `PlayerBearerAuthenticationHandler` accepts a session when `session.Epoch >= profile.SessionEpoch`,
  so today a cached session of a player whose profile was removed would authenticate (gap closed by R9).
- `LoginCompletion` creates the profile through `IPlayerAggregateWriter.UpdateAsync` when it is missing (first login).
- `ProviderLoginService` runs the `SWITCH_ACCOUNT` check **before** the account checks of the mapped player.
- `AccountChecks.CheckAccess` returns `ACCOUNT_DELETED` without details.
- `IGmTargetValidator.ValidateAsync(playerId, ct)` refuses a target pending deletion with 404 and has no notion of the
  operation, so it would also refuse the cancel command.
- `GmModule` re-declares `receipts` (index `ix_receipts_player`) and `pa_profile` (display-name index) as
  `CollectionSpec`s only to provision indexes; `gm_audit` is a platform collection also declared by `gm`.
- Outbox projections are inserted by `ProjectionOutboxConsumer` with plain `InsertIfAbsentAsync`; the sweeper marks
  receipts with KV `MutateIn` + CAS.
- The arch test forbids every assembly except Platform/Couchbase/Testing from depending on `ITransactionContext`, and
  every module assembly from depending on `ITransactionalDocumentStore`. Business assemblies may not depend on
  `GameServer.Platform` at all.
- The codebase clock is `GameServer.Time.IClock` (`FakeClock` in tests), not `TimeProvider`.

---

## R1 — Lease job runner (minimal JOB-01, OPS-05)

**Decision.** A platform `LeaseJobRunner` (`BackgroundService`) runs every job declared in a `ModuleDescriptor.Jobs`
entry. Coordination is one KV document per job, written with CAS only, no Couchbase expiry (DAT-03):

- Collection `leases` (platform-owned), key `leases::{jobName}` (JOB-01 naming, like `rcpt::` in `receipts`).
- Content `{jobName, owner, fencingToken, leaseUntilUtc, nextRunAtUtc, acquiredAtUtc, renewedAtUtc, lastRun{startedAtUtc,
  completedAtUtc, result}, gameId, environmentId}`; `owner = null` when released.
- **Acquire** (CAS): document missing → `Insert` with `fencingToken = 1`; otherwise `Replace` with the read CAS when
  either (a) `owner == null && nextRunAtUtc <= now` (regular due run) or (b) `owner != null && leaseUntilUtc <= now`
  (holder crashed → immediate takeover, US4-2). Every acquisition sets `fencingToken = previous + 1`,
  `leaseUntilUtc = now + LeaseSeconds`. A failed insert/CAS = another instance won; no retry in this cycle.
- **Renew**: a `PeriodicTimer` every `RenewSeconds` replaces the document with the CAS of the last successful write,
  `leaseUntilUtc = now + LeaseSeconds`, same token. CAS mismatch → re-read: same `owner` and `fencingToken` means our own
  ambiguous write landed (continue); anything else → **lease lost**. A renewal that cannot succeed before
  `leaseUntilUtc − 5 s` also counts as lost. Loss cancels the run's token and increments `job_lease_losses_total`.
- **Release** (CAS, best effort, 2 s budget, `CancellationToken.None`): `owner = null`, `leaseUntilUtc = now`,
  `nextRunAtUtc = now + interval` after a finished run, `= now` after an interrupted run (shutdown, lease lost to
  cancellation) so another instance continues at once (US4-4, FR-004).
- The interval is therefore **cluster-wide** (`nextRunAtUtc` lives in the lease), not per instance. Non-holders poll the
  lease every `PollSeconds` (± 20 % jitter): one KV get per job per instance per poll.
- **Fencing token semantics** (FR-003): the token identifies the acquisition. It is *checked at the storage of
  progress*, not at the lease: every progress document a job writes stores the highest token that wrote it, and a write
  carrying a smaller token is refused (`LeaseLostException`) and the job stops. For the purge job the progress document
  is the deletion record (R6); the check happens inside the same transaction as the data it guards. A stale holder
  that has not yet been overtaken on a given record may still finish a step there — harmless, steps are idempotent.
- **Clock**: all lease times from `IClock` (TIME-01). `LeaseSeconds` (60) ≫ tolerated skew (NTP, < 1 s, OPS-01).
- **Shutdown** (OPS-03): `stoppingToken` cancels the job context; jobs check it between transactions only (the commit
  itself never takes the token, PERF-02), so the worst stop latency is one transaction (≤ 10 s) + release (≤ 2 s),
  inside `ShutdownTimeoutSeconds = 25`.
- **Start**: the runner awaits `IStoreInitializer.EnsureInitializedAsync` (pattern of `KeyRingService`) before the first
  lease read. No sync-over-async.
- **Tests**: `RunOnceAsync(jobName, ct)` performs acquire → run → release synchronously (like
  `OutboxSweeper.SweepOnceAsync`); `GameServer:Jobs:Enabled = false` in `TestHost` defaults stops the background loop.
  Lease expiry is driven by `FakeClock`; renewal by calling `LeaseStore.TryRenewAsync` directly.

**Registration** (FR-001, constitution II): jobs are declared in the descriptor, validated with every other
registration rule (duplicate name → startup fail; type must implement `ILeaseJob`; name pattern). Per-job enable flag
and interval come from configuration.

```csharp
namespace GameServer.Jobs;                      // GameServer.Core

public interface ILeaseJob
{
    /// <summary>One run. Must be idempotent and stop when context.Cancellation fires.</summary>
    Task RunAsync(IJobContext context);
}

public interface IJobContext
{
    string JobName { get; }
    long FencingToken { get; }
    /// <summary>Cancelled on shutdown or lease loss.</summary>
    CancellationToken Cancellation { get; }
}

namespace GameServer.Modules;                   // GameServer.Core, ModuleDescriptor gains: IReadOnlyList<JobDescriptor> Jobs
public sealed record JobDescriptor(string Name, Type JobType, int DefaultIntervalSeconds);

namespace GameServer.Platform.Jobs;             // GameServer.Platform
public sealed record JobLease(string JobName, string Owner, long FencingToken, DateTimeOffset LeaseUntil, StoredDocument Document);

public sealed class LeaseStore                  // IKeyValueStore + IClock
{
    public Task<JobLease?> TryAcquireAsync(string jobName, string owner, CancellationToken cancellationToken);
    public Task<JobLease?> TryRenewAsync(JobLease lease, CancellationToken cancellationToken);
    public Task ReleaseAsync(JobLease lease, DateTimeOffset nextRunAt, string result, CancellationToken cancellationToken);
}

public enum JobRunResult { NotDue, HeldElsewhere, Completed, Failed, LeaseLost, Cancelled }

public sealed class LeaseJobRunner : BackgroundService
{
    public Task<JobRunResult> RunOnceAsync(string jobName, CancellationToken cancellationToken);
}

public sealed class LeaseLostException(string jobName, long token) : Exception;
```

`GameServerOptions.Jobs` (`JobSettings`): `Enabled` (true), `LeaseSeconds` (60, 30..600, ≥ 3 × transaction expiration),
`RenewSeconds` (20, ≤ LeaseSeconds / 3), `PollSeconds` (30, 5..LeaseSeconds), `Settings[jobName] {Enabled?,
IntervalSeconds?}` overriding the descriptor default. Owner id = `{machineName}:{processId}:{8 random hex}` per process.

**Alternatives.** (a) Lease held permanently by one instance and renewed between runs — simpler loop, but interval
becomes per-holder and a quiet holder blocks failover tuning; rejected. (b) Couchbase document expiry as the lease
timeout — forbidden by DAT-03 and loses the fencing counter. (c) Hangfire/Quartz — forbidden by the constitution.
(d) Generic durable progress documents + job outbox (full JOB-01/JOB-02) — P1; the purge keeps its progress in its own
record, which is the only progress this slice needs.

**P1 boundary.** Not built: generic progress documents, insert-only job result markers as a framework service, job
outbox consumer (JOB-02 P1 part), GM job pages (GM-05), ACC-15. The runner's public surface (`ILeaseJob`,
`IJobContext`, `JobDescriptor`, `LeaseStore`) is what P1 extends; the receipt-retention job (CMD-08, 180 days) should be
its next user (slice 011 or 013).

---

## R2 — Deletion request command (PRIV-01, FR-006..FR-010)

**Decision.** Identity module command `identity.request_deletion`, Player profile, `Touches = [profile]`, no claim
markers, mapped at `POST /api/v1/identity/delete-account` (Protobuf, `X-Request-Id` required like every command).

- Handler (pure): `profile.Deleted is not null` → `Reject(ACCOUNT_DELETED, {purgeAfter})` (401 envelope, no receipt —
  a second request id of an already deleted account never creates a second receipt). Otherwise
  `Deleted = {RequestedAtUtc = ctx.Now, PurgeAfterUtc = ctx.Now + GraceDays, PurgeStartedAtUtc = null}`,
  `SessionEpoch + 1`; result `{requestedAtUtc, purgeAfterUtc}`. Receipt written by the runner (same transaction).
- **Idempotency** (US1-5): same request id → receipt replay (CMD-04). On the serving instance the bearer check runs first
  and returns `ACCOUNT_DELETED` for the retry (cache refreshed, below); on another instance within the cache window the
  runner replays. Both are documented SDK outcomes (R10).
- **Bans** (FR-006, US1-6): `BanCommandGuard` exempts `identity.request_deletion`, so a command-group ban (even on group
  `identity`) does not block it. A login ban already fails authentication (assumption: support handles those).
- **Revocation on the serving instance** (FR-007): the endpoint refreshes `ProfileCache` for the player after the runner
  returns (committed or replayed). This needs a small platform hook: a `MapPlayerCommand` overload with
  `Func<CommandExecution, HttpContext, Task> afterExecute`. Other instances: ≤ `Identity:CacheSeconds` (60).
- **Commands during grace** (FR-009): enforced by the existing per-request bearer check (deleted → `ACCOUNT_DELETED`,
  IDN-10 order). No new runner guard: the spec accepts the ≤ 60 s window on other instances, and the edge case
  "a command already past authentication commits just after the deletion" is explicitly allowed.
- **Grace** (FR-010): `Identity:Deletion:GraceDays` = 30 (1..90), injected into the handler as
  `AccountDeletionPolicy(int GraceDays)` (business assembly cannot read options).
- `DeletionInfo` gains `PurgeStartedAtUtc` (nullable). Additive JSON field, missing = null: `schemaVersion` of `profile`
  stays 1, no upgrader.

```csharp
namespace GameServer.Modules.Identity;          // business assembly

public sealed record DeletionInfo
{
    public DateTimeOffset RequestedAtUtc { get; init; }
    public DateTimeOffset PurgeAfterUtc { get; init; }
    /// <summary>Set by the purge job when it marks the player purging; from then on cancel is refused.</summary>
    public DateTimeOffset? PurgeStartedAtUtc { get; init; }
}

public sealed record AccountDeletionPolicy(int GraceDays);
public sealed record RequestDeletionCommand;
public sealed record RequestDeletionResult(DateTimeOffset RequestedAtUtc, DateTimeOffset PurgeAfterUtc);
public sealed class RequestDeletionHandler(AccountDeletionPolicy policy) : ICommandHandler<RequestDeletionCommand, RequestDeletionResult>
{
    public const string Operation = "identity.request_deletion";
    public static CommandDescriptor Descriptor { get; }
}
```

**Alternatives.** Removing the identity mappings at request time (literal "xoá") — rejected in the spec's assumptions
(identity could create a new account during grace; cancel impossible). A platform-wide "deleted" runner guard reading
`profile` in every transaction — extra read per command for a window the requirement already accepts.

---

## R3 — `ACCOUNT_DELETED` at login and session (FR-008, IDN-10)

**Decision.**

- `AccountChecks.CheckAccess` returns `ACCOUNT_DELETED` with details `{purgeAfter: "yyyy-MM-ddTHH:mm:ssZ"}` (same
  format as the suspension `until`). Used by bearer authentication, guest login, provider login and the
  `LoginCompletion` re-check inside the epoch transaction. Order stays deleted → suspended → replaced → unknown token.
- **Guest login**: unchanged chain (rate limits → mapping lookup + secret verification → `LoginCompletion`); the
  deleted check is the first check in `LoginCompletion`, so a wrong secret still answers `UNAUTHENTICATED` (no account
  state leaks without the credential, IDN-02). No session, no new player: the mapping exists and resolves to the
  deleted player.
- **Provider login**: after verification and mapping lookup, if the identity is mapped, the mapped player's account
  checks run **before** the `SWITCH_ACCOUNT` check (code change; today the switch check runs first). Reason: offering a
  switch into an account that can only answer `ACCOUNT_DELETED` is wrong, and US1-3 requires `ACCOUNT_DELETED`.
- **Link by another player** (US1-4): unchanged — the mapping exists, `TryClaimAsync` returns false →
  `IDENTITY_CONFLICT`. The mapping stays reserved until purge.

---

## R4 — Due-player discovery without a full scan (FR-013, PERF-07)

**Decision.** Two bounded queries per run, both served by partial indexes that contain only players being deleted:

1. **Not started**: `pa_profile` where `state.deleted.purgeAfterUtc <= now` and `state.deleted.purgeStartedAtUtc` is not
   valued, ordered by `purgeAfterUtc`. Index declared by the identity module on its own aggregate collection:
   `ix_profile_deletion_due` ON `pa_profile(state.deleted.purgeAfterUtc)` WHERE
   `state.deleted.purgeStartedAtUtc IS NOT VALUED`. Profiles without a deletion have a MISSING leading key and are not
   indexed, so the index holds only pending players.
2. **Resume** (started, unfinished, retry due): `account_deletions` where `nextAttemptAtUtc <= now` and `purgedAtUtc`
   is not valued, ordered by `nextAttemptAtUtc`. Platform index `ix_account_deletions_due` ON
   `account_deletions(nextAttemptAtUtc)` WHERE `purgedAtUtc IS NOT VALUED`.

Resume candidates are processed first, then new ones, up to `MaxPlayersPerRun`. A failing player gets
`nextAttemptAtUtc` in the future (R7), so failures never starve the queue head.

Generic store query (reusable by the receipt-retention job: `receipts.committedAtUtc <= cutoff`):

```csharp
namespace GameServer.Platform.Persistence;

/// <summary>Documents whose <paramref name="DueField"/> (ISO-8601 UTC string) is &lt;= <paramref name="Now"/> and whose
/// <paramref name="UnsetField"/> is missing or null; oldest first; RequestPlus. Needs a partial index with the same predicate.</summary>
public sealed record DueQuery(string Collection, string DueField, DateTimeOffset Now, int Limit, string? UnsetField = null);

/// <summary>Keys of documents whose <paramref name="Field"/> equals <paramref name="Value"/>; ordered by key; RequestPlus.</summary>
public sealed record ReferenceQuery(string Collection, string Field, string Value, int Limit);

/// <summary>Bounded maintenance queries for jobs (PERF-07: N1QL only outside the hot path). Couchbase + in-memory.</summary>
public interface IMaintenanceQueryStore
{
    Task<IReadOnlyList<StoredDocument>> QueryDueAsync(DueQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<DocumentKey>> QueryReferencesAsync(ReferenceQuery query, CancellationToken cancellationToken);
}
```

Field paths are validated with `PagedQuery.IsValidField`; collections with the existing collection pattern.
Time strings: `DeletionInfo` times are written by System.Text.Json (`2026-11-29T10:00:00+00:00`, always offset 0 because
they come from `IClock.UtcNow`), record times by `AggregateDocumentSerializer.FormatTime`. The query parameter is
formatted with the same serializer as the field it compares to, so lexical order equals time order to the second; the
job tolerates ± 1 s.

**Alternatives.** (a) A queue document written at request time — a Player command can only insert claim markers, and a
re-request after cancel would collide with the first marker; a second source of truth for "pending". (b) Full scan of
`pa_profile` — forbidden by FR-013/PERF-07. (c) Extending `PagedQuery` with ranges — it is the GM pagination contract
(GM-06) and its "order field must exist" rule would silently skip documents of mixed shapes (identity mappings vs link
claim markers have different time fields).

---

## R5 — Collection deletion treatments and startup validation (FR-019, ARC-06, EXT-01)

**Decision.** `CollectionSpec` gains two optional members; `ModuleRegistry.Validate` gains `ValidateDeletion`.

```csharp
namespace GameServer.Modules;                   // GameServer.Core

public enum DeletionTreatment
{
    Unspecified = 0,    // allowed only on a re-declaration of a collection owned elsewhere (index-only)
    NoPlayerData,
    Anonymize,          // overwrite the player id with the anonymous id; keep the record
    Remove,             // remove every record that references the player
    ModuleStep,         // the declaring module's IAccountDeletionStep handles it
}

public sealed record CollectionSpec(
    string Name,
    IReadOnlyList<IndexSpec>? Indexes = null,
    bool TransactionOnly = false,
    DeletionTreatment Deletion = DeletionTreatment.Unspecified,
    string PlayerField = "playerId");
```

Rules (each violation is a startup error naming module and collection, ACC-26 style):

1. **Platform-owned collections** have fixed treatments and may be re-declared by modules only with `Unspecified` or
   the same value: `receipts` → Anonymize (receipt rules, R8), `pa_*` → Remove (by key), `gm_audit` → Anonymize,
   `dataprotection`, `gate`, `leases` → NoPlayerData, `account_deletions` → purge record (excluded, holds the old id by
   design, FR-018).
2. Every other collection in `RequiredCollections()` (module collections **and** outbox projection targets) needs
   exactly one explicit treatment across all its declarations. None → "declares no deletion treatment; fix:
   `CollectionSpec(..., Deletion: ...)`". Two different → conflict error. A projection target without any
   `CollectionSpec` → error (the platform cannot guess what a payload holds).
3. `ModuleStep` requires at least one `DeletionStepRegistration` in the **same** module descriptor.
4. `Anonymize`/`Remove` require a valid `PlayerField` path.
5. Deletion step names are unique across modules; step types implement `IAccountDeletionStep`.

Built-in declarations added in this slice: identity `identities` → Remove, `sessions` → Remove, `pa_profile` (index
only); economy `ledger` → Anonymize, `extref_claims` → Anonymize; gm `gm_staff`, `gm_staff_identities`, `gm_sessions` →
NoPlayerData (and `receipts`/`gm_audit`/`pa_profile` stay index-only); sample counter `counter_log` → Anonymize.
`ix_receipts_player` moves from the gm descriptor to platform provisioning (the purge needs it even without the gm
module).

**Reference indexes.** For every Anonymize/Remove collection the provisioner creates `ix_{collection}_player_ref` ON
(`PlayerField`) unless a declared index of that collection already has `PlayerField` as its leading key (ledger,
gm_audit, receipts already do). `ModuleRegistry.DeletionPlan()` exposes the resolved list.

```csharp
namespace GameServer.Platform.Registry;
public sealed record CollectionDeletion(string Collection, DeletionTreatment Treatment, string PlayerField, string Owner);
// ModuleRegistry: public IReadOnlyList<CollectionDeletion> DeletionPlan();   // registration order, platform first
```

**Alternatives.** A separate `ModuleDescriptor.DeletionTreatments` map — a second list to keep in sync with
`Collections`, easy to forget (the very failure the requirement targets). An attribute on state types — not every
collection has a CLR type.

---

## R6 — Deletion record, anonymous id, purge writer (FR-014..FR-018, DAT-09, DAT-04(c))

**Decision.** Platform collection `account_deletions` (TransactionOnly), one document per deleted player, key
`deletion::{playerId}`. It is the purge's progress document and fencing anchor, and after completion the purge record.
Shape in [data-model.md](data-model.md). The anonymous id lives in it only while `state = purging`.

All purge writes go through one platform class — the DAT-09 "`IPlayerAggregateWriter` (purge)" path:

```csharp
namespace GameServer.Platform.Privacy;          // GameServer.Platform

public sealed record PurgeTicket(string PlayerId, string AnonymousId, long FencingToken, IReadOnlySet<string> CompletedSteps);

/// <summary>Profile change that marks the player purging, plus the dates copied into the record.</summary>
public sealed record PurgeStart<TProfile>(TProfile Profile, DateTimeOffset RequestedAtUtc, DateTimeOffset PurgeAfterUtc);

public enum PurgeAction { Anonymize, Remove }

/// <summary>
/// The only writer of the purge (DAT-09): every call is one transaction that also replaces the deletion record, after
/// checking record.fencingToken &lt;= ticket.FencingToken and storing the ticket's token (FR-003, FR-017).
/// Never creates pa_* or receipt documents; never takes the caller's token into the commit (PERF-02).
/// </summary>
public interface IAccountPurgeWriter
{
    /// <summary>
    /// No record: loads profile + record in one transaction, calls <paramref name="start"/> (pure; null = not eligible),
    /// writes the profile (rev+1) and inserts the record (state purging, new random anonymous id, nextAttemptAtUtc = now).
    /// Record purging: claims it for this token. Record purged: null.
    /// </summary>
    Task<PurgeTicket?> BeginAsync<TProfile>(string playerId, long fencingToken, Func<TProfile, PurgeStart<TProfile>?> start, CancellationToken cancellationToken)
        where TProfile : class;

    /// <summary>Applies the action to up to BatchSize documents (missing = already done) and adds the count to the record.</summary>
    Task<int> ApplyAsync(PurgeTicket ticket, IReadOnlyList<DocumentKey> keys, PurgeAction action, CancellationToken cancellationToken);

    Task<PurgeTicket> CompleteStepAsync(PurgeTicket ticket, string step, CancellationToken cancellationToken);

    /// <summary>Removes pa_profile and turns the record into the purge record (state purged, anonymousId and progress dropped).</summary>
    Task CompleteAsync(PurgeTicket ticket, CancellationToken cancellationToken);

    /// <summary>attempts+1, lastFailure {step, errorType, atUtc}, nextAttemptAtUtc = now + backoff. Fenced like every write.</summary>
    Task RecordFailureAsync(PurgeTicket ticket, string step, Exception error, CancellationToken cancellationToken);
}

public sealed class PurgeFencedOutException(string playerId, long token, long current) : Exception;

public static class AnonymousIds
{
    /// <summary><c>anon_</c> + 26 base32 characters of 128 random bits; never derived from the player id (FR-018).</summary>
    public static string New();
}

/// <summary>Read side for GM and the target validator (no schema knowledge outside Platform).</summary>
public sealed class AccountDeletionRecords
{
    public Task<AccountDeletionRecord?> GetAsync(string playerId, CancellationToken cancellationToken);
}
```

Transactions: one per call, `record + ≤ BatchSize (25)` documents — ≈ 2 × 26 + 3 KV operations, well inside the 10 s
expiration (DAT-04). Removal needs a new transactional primitive:

```csharp
// ITransactionContext (GameServer.Platform.Persistence) gains:
Task RemoveAsync(StoredDocument existing);       // Couchbase: AttemptContext.RemoveAsync; in-memory: staged delete with version check
```

`ApplyAsync` refuses: `pa_profile` (only `CompleteAsync` removes it), Anonymize on `pa_*`, any key whose collection's
treatment does not match the action, and inserts of any kind. Writes through `ITransactionalDocumentStore` directly, so
`AuditedStore`'s "audit entries are immutable" guard does not apply: the purge writer is the single, field-level
exception (Complexity Tracking).

**Anonymize rule** (generic, FR-015): every JSON string value anywhere in the document that equals the player id
exactly is replaced by the anonymous id (covers `playerId`, `actorId` when it is the player, audit `targetPlayerId`,
claim marker `playerId`, projection `payload.*`). Player ids (`p_` + 26 base32) do not collide with other values.
Keys are not changed (they are hashes or external references, never the raw player id outside `pa_*`, which are
removed).

**Fencing.** `record.fencingToken > ticket.FencingToken` → `PurgeFencedOutException` → the job stops the run (FR-003).
A stale holder can therefore never revert a `purged` record (which would resurrect the anonymous-id link) or regress
progress. Two holders on the same record serialize on the record's write-write conflict.

**Alternatives.** (a) Progress inside `profile.deleted` — the profile is removed at the end, so a separate purge record
is needed anyway, and the platform would have to know the profile JSON shape. (b) Adding the purge methods to
`IPlayerAggregateWriter` itself — that interface stays the single-aggregate writer used by login; a separate interface
keeps both small (naming deviation noted in plan.md). (c) KV writes for anonymization of projections (ledger/audit are
KV-inserted) — would bypass fencing; every purge write is transactional so one rule covers all.

---

## R7 — Purge pipeline order, idempotency, retries (FR-013..FR-020)

**Decision.** Two classes:

- `AccountPurgeJob : ILeaseJob` — identity adapters. Owns *when*: discovery (R4), the start decision on
  `ProfileState` (pure: deleted, not started, `PurgeAfterUtc <= now` → set `PurgeStartedAtUtc = now`), per-run limits,
  the overdue gauge.
- `AccountPurgeService` — platform. Owns *how*: the ordered steps across every module's data.

```csharp
namespace GameServer.Platform.Privacy;
public enum PurgeOutcome { Completed, Incomplete, Failed }
public sealed class PurgeBudget(int documents) { public int Remaining { get; } }
public sealed class AccountPurgeService
{
    public Task<PurgeOutcome> RunAsync(PurgeTicket ticket, PurgeBudget budget, CancellationToken cancellationToken);
}

namespace GameServer.Modules.Identity;          // adapters
public sealed class AccountPurgeJob : ILeaseJob
{
    public const string JobName = "identity.account-purge";
}
```

Steps, each recorded in `record.completedSteps` when finished; a resumed run skips finished steps and re-runs the
current one from the start (every step is a query-until-empty loop, so repeating it is harmless):

| # | Step id | Action | Data |
| --- | --- | --- | --- |
| 0 | (begin) | mark purging | `pa_profile.deleted.purgeStartedAtUtc` + insert record — one transaction; cancel refused from here |
| 1 | `receipts` | Anonymize (receipt rules, R8) | `ReferenceQuery(receipts, playerId, old)` until empty; rewritten receipts still `outboxPending` (not dead-lettered) are relayed at once after the commit, so new projections carry the anonymous id |
| 2 | `anonymize:{collection}` | Anonymize | each Anonymize collection of the deletion plan (gm_audit, ledger, extref_claims, counter_log, …), registration order |
| 3 | `step:{name}` | module step | each `IAccountDeletionStep`, module registration order (FR-020: before identities and profile) |
| 4 | `remove:{collection}` | Remove | each Remove collection (sessions, identities, module collections) — mappings released here (FR-016) |
| 5 | `verify` | re-query 1, 2, 4 | catches projections inserted late by an in-flight relay; processes what it finds; up to 3 rounds, then failure |
| 6 | `aggregates` | Remove by key | every registered `pa_*` except `pa_profile`, one transaction (≤ 15 removes) |
| 7 | (complete) | remove `pa_profile`, record → purged | one transaction; anonymous id dropped (FR-018) |

- **Budget** (edge case "large history"): every transaction subtracts its document count from
  `MaxDocumentsPerRun` (5000). At 0 the service returns `Incomplete`; the record keeps `nextAttemptAtUtc <= now`, so the
  next run resumes this player first. No transaction exceeds `BatchSize` documents.
- **Failure** (FR-017): any exception other than fencing/cancellation → `RecordFailureAsync` (attempts+1, step,
  exception type only — no message, it may hold data), `nextAttemptAtUtc = now + RetryDelayMinutes × 2^(attempts−1)`
  capped at `MaxRetryDelayMinutes`; metric `account_purge_step_failures_total{step}`; `LogError` with player id, step
  and exception. The player stays purging (login still refused, cancel refused) and the job continues with the next
  player.
- **Cancellation/fencing**: the job stops the run; nothing is recorded as failure.
- **Missing documents** count as done (never-created aggregates, already removed mappings).

**Why this order.** Receipts first so any later relay produces anonymized projections; projections next; module steps
before identities and the profile (FR-020: a step may still need to resolve the player); identities and sessions before
the profile (R9 relies on "mapping removed ⇒ profile removal may follow"); the profile last because its presence keeps
every credential and stale token refused while the purge runs (US2-5).

**Alternatives.** Whole pipeline in identity (fewer types) — the generic part needs the registry, receipt schema and
the purge writer, all platform knowledge; splitting by *when* (identity) vs *how* (platform) keeps each side single-
purpose. Whole pipeline in platform with an `IAccountDeletionSource` callback for the profile decision — one more
interface for no gain.

---

## R8 — Receipts, ledger and audit rewrite vs DAT-09 / CMD-08 / SEC-14 (requirement conflicts)

**Decision.**

1. **Receipt rewrite vs DAT-09 ("pa_* and rcpt::* only through the runner or `IPlayerAggregateWriter` (purge)")** —
   no conflict once the purge writer is that path: DAT-09 itself names "purge" as a sanctioned writer and the rule's
   reason (V15: never mix KV and transactional writes on the same document) is preserved — receipts are only ever
   written in transactions. The sweeper's existing KV `MutateIn` of `outboxPending` is the pre-existing exception; a
   concurrent purge transaction can at worst make the sweeper's flag write lost (one extra idempotent relay) — it can
   never restore the old player id because the sweeper never writes `playerId`.
2. **CMD-08 / SEC-14 immutability ("ledger and audit never deleted"; `AuditedStore` refuses audit replacement) vs
   PRIV-01 ("anonymize playerId in receipt/ledger/audit by overwriting the field")** — PRIV-01 is the later, more
   specific rule and CMD-09 states it explicitly ("ẩn danh PRIV-01 = ghi đè trường"). Records are never deleted; only
   values equal to the player id change. Amounts, balances, timestamps, receipt references, correlation ids, external
   references are untouched. The exception is confined to `AccountPurgeWriter` (one class, transaction-only, fenced).
3. **Receipt-specific rules** (beyond the generic anonymize): `resultCompact` and `bodyHash` are cleared (empty
   string) and `claimKeys` entries pointing into a Remove-treated collection become `"{collection}/redacted"`.
   Reason: `resultCompact` can carry personal data (e.g. the display name in `identity.set_display_name`), `bodyHash`
   of a low-entropy body (a name) is dictionary-attackable, claim keys of `identities` carry provider/local ids; all
   three exist only for the 24 h replay window (CMD-07), long past at purge time (≥ 30 days after the last command).
   This goes beyond FR-015 "other fields unchanged" — see plan.md, Spec deviations.
4. **Delete vs hide identity (PRIV-01 "xoá/ẩn", ACC-37 "identity ẩn")** — hide during grace (mapping kept, login
   refused with `ACCOUNT_DELETED`), delete at purge (step 4). As in the spec's assumptions.
5. **Receipt keys** (`rcpt::{hash(actor|player|op|requestId)}`) are kept: not reversible without the random request id.

---

## R9 — No credential or old token can ever authenticate as the purged id (FR-016, US2-5)

Two holes exist in today's code once `pa_profile` can disappear (see code facts):

1. **Stale session**: a cached session + a missing profile (default state, epoch 0) would pass.
   **Fix — invariant "a session's player has a profile document"**: `ProfileCache` caches `ProfileState?` (null =
   no document); `PlayerBearerAuthenticationHandler` answers `ACCOUNT_DELETED` (no details) when it is null. Every
   session is created by `LoginCompletion` *after* the profile write, so the invariant holds for every live player.
   `ProviderLoginService.CurrentPlayerAsync` treats null as "no current player"; `BanCommandGuard` treats null as
   "no ban" (the bearer already refused).
2. **Login racing the purge**: login reads the mapping (exists), the purge then removes mapping and profile, login's
   epoch transaction finds no profile and *creates* one → a session for the purged id. Couchbase transactions do not
   detect read-write conflicts, so reading the mapping in that transaction is not enough.
   **Fix — creation anchor**: the profile-creating transaction must also *write* the mapping document, so it
   write-conflicts with the purge's removal.

```csharp
namespace GameServer.Platform.Aggregates;
// IPlayerAggregateWriter gains an overload; the existing signature delegates with creationAnchor: null.
Task<(TState State, long Rev)> UpdateAsync<TState>(
    string playerId,
    string aggregateId,
    Func<TState, TState> mutate,
    DocumentKey? creationAnchor,
    CancellationToken cancellationToken)
    where TState : class;
// When the aggregate document is missing: the anchor is read in the same transaction; missing, or its "playerId" differs
// → AggregateAnchorMissingException (nothing written); otherwise it is replaced with identical content (write lock).
// When the aggregate exists the anchor is not touched (no extra cost for returning players).

public sealed class AggregateAnchorMissingException(DocumentKey anchor) : Exception;
```

`LoginCompletion.CompleteAsync(playerId, mappingKey, ct)` passes `identities/{mappingKey}` and maps the exception to
`ACCOUNT_DELETED`. Proof sketch: the purge removes the mapping (step 4) before the profile (step 7). A login transaction
either (a) sees the profile (it exists until step 7, carrying `deleted`) → refused, or (b) sees no profile → must
replace the mapping, which is either already removed (refused) or conflicts with the removal (one retries and lands in
(a) or the "removed" case). A first login whose earlier attempt crashed after inserting the mapping still works (anchor
exists, no record). After the purge, the same credential finds no mapping → a **new** player id (US2-3).

**Alternatives.** Checking the purge record during login — read-only, same race. Timing arguments ("purge waits longer
than a login can take") — break under GC pauses, the failure mode fencing exists for. Keeping `pa_profile` as a
tombstone — violates FR-014/SC-002.

---

## R10 — Module deletion steps (FR-020, EXT-01) and the "modules may not open transactions" rule

**Decision.** Steps are declared in the descriptor (like command handlers) and activated from DI per run. A step never
sees a transaction or the document store: it receives a fenced eraser implemented by the platform purge writer and
scoped to the collections its own module declares (never `pa_*`, `receipts`, `gm_audit`).

```csharp
namespace GameServer.Privacy;                   // GameServer.Core

public interface IAccountDeletionStep
{
    /// <summary>Must be idempotent: a resumed purge runs it again from the start.</summary>
    ValueTask RunAsync(IPlayerDataEraser eraser, CancellationToken cancellationToken);
}

public interface IPlayerDataEraser
{
    string PlayerId { get; }
    string AnonymousId { get; }
    /// <summary>Maximum keys per call of RewriteAsync.</summary>
    int BatchSize { get; }

    /// <summary>Keys (≤ limit) of the module's collection whose field equals value.</summary>
    Task<IReadOnlyList<string>> FindAsync(string collection, string field, string value, int limit, CancellationToken cancellationToken);

    /// <summary>One fenced transaction: rewrite returns the new content, or null to remove the document.</summary>
    Task RewriteAsync(string collection, IReadOnlyList<string> keys, Func<JsonObject, JsonObject?> rewrite, CancellationToken cancellationToken);

    /// <summary>Rewrites payloads of the module's own projection kind inside the player's (already anonymized) receipts.</summary>
    Task ScrubOutboxAsync(string kind, Func<JsonNode?, JsonNode?> scrub, CancellationToken cancellationToken);
}

namespace GameServer.Modules;
public sealed record DeletionStepRegistration(string Name, Type StepType);   // ModuleDescriptor.DeletionSteps
```

Order: module registration order, then declaration order (deterministic, constitution II). The purge completes only
after every step returned (FR-020); an exception is a step failure (R7). Arch tests: unchanged rules already forbid
modules from depending on `ITransactionContext`/`ITransactionalDocumentStore`; `IPlayerDataEraser` is Core so business
assemblies can implement steps without Platform.

**Alternatives.** Let steps open transactions through `ClaimStore`-like services — breaks fencing and the arch rule.
Steps registered only in DI — the registry could not validate "ModuleStep without a step" at startup.

---

## R11 — GM view and cancel (FR-011, FR-012, GM-02, GM-07, SEC-14)

**Decision.**

- **Cancel**: identity command `identity.cancel_deletion`, Gm profile, permission `player.ban`, `Touches = [profile]`,
  route `POST /gm/v1/players/{playerId}/deletion/cancel` via `MapGmCommand` (reason 1..500, `X-Request-Id`, CSRF by the
  GM pipeline, audit 7 fields). Handler: `Deleted == null` → `Reject(DELETION_NOT_PENDING)` (409);
  `PurgeStartedAtUtc != null || ctx.Now >= PurgeAfterUtc` → `Reject(DELETION_GRACE_OVER)` (409); otherwise clear
  `Deleted`, `AddAudit(player.ban, identity.cancel_deletion, reason, before: Deleted, after: null)`. Sessions stay
  revoked (epoch unchanged).
- **Race with purge start** (US3-6): both write `pa_profile` in a transaction → write-write conflict → exactly one
  commits; the loser's retry sees the winner's state (cancel → 409 grace over; purge start → not eligible). Time alone
  already separates them (`now < purgeAfter` vs `now >= purgeAfter`); the conflict covers clock skew.
- **Target validator**: `IGmTargetValidator.ValidateAsync(string playerId, string operation, CancellationToken)` —
  the operation lets `GmTargetValidator` allow `identity.cancel_deletion` on a pending player while every other GM
  mutation keeps the existing 404 "pending deletion". For a purged player (no profile, record `purged`) the cancel
  answers 409 `DELETION_GRACE_OVER`; other operations 404.
- **After commit**: the GM endpoint refreshes `ProfileCache` on the serving instance (same hook as R2, GM overload);
  other instances within 60 s. Login always reads the profile fresh, so the player can log in at once anywhere.
- **View**: `GmPlayerView` gains `Deletion` (`null` when none):
  `{state: pending|purging|purged, requestedAtUtc, purgeAfterUtc, purgeStartedAtUtc?, purgedAtUtc?}` from
  `profile.deleted` (+ record while purging). `GET /gm/v1/players/{playerId}` for a purged player returns 200 with no
  aggregates and `deletion.state = purged` (today: 404). Lookup by mapping key and display name works while pending
  (mapping and profile exist); after purge they return 404 — no path from personal data to the old id. The anonymous
  id is never shown for the old id (FR-012).
- **OpenAPI**: `docs/api/gm-openapi.json` regenerated; `OpenApiTests` keeps it in sync.

---

## R12 — Error codes, metrics, logs (FR-022, FR-023, OBS-01/02, LOG-05)

- Identity range 14000–14099: `DELETION_NOT_PENDING` (14005, 409, not retryable), `DELETION_GRACE_OVER` (14006, 409,
  not retryable). `ACCOUNT_DELETED` (core 17) gains optional details `purgeAfter` — additive, non-breaking.
- Metrics (no player id labels, OBS-02):
  - identity: `account_deletion_requests_total`, `account_deletion_cancellations_total`;
  - platform: `account_purges_total{result=completed|incomplete|failed|fenced_out}`,
    `account_purge_step_failures_total{step}` (bounded set: step ids of R7),
    `account_purge_documents_total{action=anonymize|remove}`, `account_purge_overdue_seconds` (observable gauge: now −
    oldest `purgeAfterUtc` among due players seen by the last run on this instance; 0 when none; alert > 24 h, SC-006),
    `job_runs_total{job,result}`, `job_run_duration_seconds{job}`, `job_lease_acquisitions_total{job}`,
    `job_lease_losses_total{job}`.
- Logs: player id only as a structured property where needed to trace (request, purge start/complete/failure, cancel);
  **the anonymous id is never logged** (a log line holding both would persist the link that FR-018 discards);
  failure logs carry exception type and step, not document contents; lease events carry job, owner, token.
- Tracing: one activity per job run (`job {name}`) and per player purge (`purge`, attribute `gs.player_id`).

---

## R13 — SDK (FR-021, SDK-02)

`Task<CommandResult<RequestDeletionResult>> RequestAccountDeletionAsync(CancellationToken ct = default)` on
`GameServerClient`: a normal command (same bytes on retry). On `OK`: clear the session and sections, raise
`AccountBlocked` with a client-built `GameServerError` code `ACCOUNT_DELETED`, details `purgeAfter`. On
`ACCOUNT_DELETED` from the server (a retry after a lost response): the existing `Notify` path already clears the session
and raises `AccountBlocked`; the method throws `GameServerException` whose details carry `purgeAfter` — documented as
"the deletion is committed". No new event type (the spec's "account-deleted event" = `AccountBlocked` with code
`ACCOUNT_DELETED`). Proto: `RequestAccountDeletionRequest {}` → `RequestAccountDeletionResult { int64
requested_at_utc_ms = 1; int64 purge_after_utc_ms = 2; }`.
