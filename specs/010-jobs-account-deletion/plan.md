# Implementation Plan: Lease jobs and account deletion (P0.5 slice 010)

**Branch**: `010-jobs-account-deletion` | **Date**: 2026-10-30 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/010-jobs-account-deletion/spec.md`

## Summary

A minimal lease job runner in Platform (one KV lease document per job, CAS acquire/renew/release, fencing token,
cluster-wide interval, graceful release) runs the identity module's `identity.account-purge` job. Players request
deletion with the command `identity.request_deletion` (one transaction: `profile.deleted`, `sessionEpoch + 1`,
receipt); every credential and token then answers `ACCOUNT_DELETED {purgeAfter}`. GM cancels with
`identity.cancel_deletion` (`player.ban`, audited) while `now < purgeAfter` and the purge has not started. After grace
the job marks the player purging and a platform `AccountPurgeService` erases the player's data in fenced, bounded
transactions through `IAccountPurgeWriter`: anonymize receipts → anonymize every Anonymize-treated collection → module
steps → remove identities/sessions/Remove-treated collections → verify → remove `pa_*` → remove `pa_profile` and keep
a purge record without the anonymous id. Every declared collection must state a deletion treatment or the host fails
to start. Detailed decisions: [research.md](research.md); documents and config: [data-model.md](data-model.md); API:
[contracts/account-deletion-api.md](contracts/account-deletion-api.md).

## Technical Context

**Language/Version**: C# / .NET 10 (SDK 10.0.403); SDK client C# 9 / netstandard2.1

**Primary Dependencies**: unchanged (ASP.NET Core Minimal API, CouchbaseNetClient 3.10.0 pinned, Google.Protobuf pinned);
no new package (`BackgroundService`, `PeriodicTimer`, `RandomNumberGenerator` are in the shared framework)

**Storage**: Couchbase / in-memory. New platform collections `leases` (KV), `account_deletions` (TX). New indexes:
`ix_profile_deletion_due`, `ix_account_deletions_due`, reference indexes for Anonymize/Remove collections. New store
primitives: `ITransactionContext.RemoveAsync`, `IMaintenanceQueryStore` (due query, reference query)

**Testing**: xUnit v3 + Shouldly; `InMemoryDocumentStore` + `FakeClock`; `TestHost` with two instances on one store;
`GmTestHost`; Couchbase integration (`COUCHBASE_CONNSTR`), including `EXPLAIN` of the new queries; SDK harness;
arch tests

**Target Platform**: Linux server, Unity client

**Project Type**: web-service framework + client SDK

**Performance Goals**: no change on the hot path except one extra KV write per **first** login (creation anchor,
research R9); purge transactions ≤ 26 documents (≪ 10 s); purge throughput ≈ 7 200 players/day at defaults; non-holder
instances cost one KV get per job per 30 s

**Constraints**: DAT-03 (no expiry on leases/records), DAT-04 (bounded transactions), DAT-09 (pa_*/receipts only
through runner/writer), PERF-02 (commit never takes the job/request token), PERF-07 (N1QL only in jobs, partial
indexes, `EXPLAIN` checked), OPS-03 (stop ≤ 25 s), IDN-08 (≤ 60 s revocation), LOG-05 (no credential, no anonymous id
in logs)

**Scale/Scope**: one job in this slice; framework collections listed in data-model.md; game modules declare their own

## Overview

```text
                 Player API                                   GM API
 POST /identity/delete-account                 POST /gm/v1/players/{id}/deletion/cancel     GET /gm/v1/players/{id}
        │ (bearer: deleted→suspended→replaced)            │ (player.ban, CSRF, target validator(op))     │ player.read
        ▼                                                 ▼                                               ▼
  ICommandRunner ── identity.request_deletion       ICommandRunner ── identity.cancel_deletion     GmPlayerEndpoints
        │  pa_profile.deleted, epoch+1, receipt            │  clear deleted, receipt, audit              │ profile.deleted
        ▼                                                 ▼                                               │ + AccountDeletionRecords
  ProfileCache refresh (serving instance)          ProfileCache refresh                                   ▼
                                                                                             deletion {state, dates}
 ┌──────────────────────────── every instance ────────────────────────────────────────────────────────────┐
 │ LeaseJobRunner (Platform, BackgroundService) ── LeaseStore (KV CAS leases/leases::{job})                │
 │        │ holder only, IJobContext{FencingToken, Cancellation}                                            │
 │        ▼                                                                                                 │
 │ AccountPurgeJob (identity) ── IMaintenanceQueryStore.QueryDueAsync (pa_profile due / records due)       │
 │        │ BeginAsync<ProfileState>(start decision) ──────────────┐                                        │
 │        ▼                                                         ▼                                        │
 │ AccountPurgeService (Platform: ordered steps, budget)  ──►  IAccountPurgeWriter (Platform, fenced TX)    │
 │        │ IAccountDeletionStep (modules) ── IPlayerDataEraser ──┘        │                                │
 │        └──────────── IOutboxRelay (relay rewritten pending receipts) ◄──┘                                │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────────┘
 Login: Guest/ProviderLoginService ─► LoginCompletion ─► IPlayerAggregateWriter.UpdateAsync(..., creationAnchor = mapping)
```

Style: modular monolith, explicit registration (descriptor), background services coordinated by lease documents,
transactional writes through platform writers only.

## Constitution Check

| Principle | How this slice meets it | Result |
| --- | --- | --- |
| I Server authoritative, one transaction per command | Request and cancel are commands (profile + receipt, + audit for GM) in one transaction. The purge is a job (DAT-04(c)): every write is a bounded transaction through the platform purge writer (DAT-09 "purge" path), fenced by the record; no KV write ever touches `pa_*`/`receipts`/`account_deletions`. Login creation anchor keeps the profile creation transactional | PASS, with the audit/ledger overwrite exception in Complexity Tracking |
| II Explicit module registration | Jobs (`ModuleDescriptor.Jobs`), deletion steps (`DeletionSteps`) and treatments (`CollectionSpec.Deletion`) are declared in descriptors; duplicate job/step names, missing treatment, ModuleStep without step → startup fail. Core changes add the extension point once; afterwards modules need no Core change | PASS |
| III Security deny-by-default | Delete-account = Player command; cancel = GM command with `player.ban`, CSRF, audit 7 fields; target player from route; profile-missing invariant closes stale-token access; no credential/anonymous id in logs | PASS |
| IV Async, bounded load | All I/O async; lease renewal on `PeriodicTimer`; every limit numeric and in PCR (lease 60/20/30 s, interval 600 s, 50 players, 25 docs/tx, 5000 docs/run, retry 10 min → 24 h); no fire-and-forget mutation | PASS |
| V Stable contract | New codes 14005/14006 in the catalog; proto additive; `ACCOUNT_DELETED` details additive; GM OpenAPI regenerated; PCR identity/platform/gm/unity-sdk/economy/sample-counter updated. `IGmTargetValidator` gains a parameter — breaking for game implementations, announced in PCR platform | PASS (breaking extension point noted) |
| VI Multi-layer tests | In-memory tests per user story, two-instance tests, Couchbase integration (lease contention, purge, race, `EXPLAIN`), SDK tests, arch tests; ACC-37 covered end-to-end | PASS |
| VII Observability | Metrics of research R12 (no player id labels), structured logs, one trace per job run and per player purge | PASS |

Re-check after design: unchanged.

## Key decisions (one line each; details in research.md)

1. **R1** Lease = KV document `leases/leases::{job}` with CAS; token +1 per acquisition; cluster-wide `nextRunAtUtc`; takeover after `leaseUntilUtc`; release on shutdown with `nextRunAtUtc = now`.
2. **R1** Fencing is enforced where progress is stored: the purge record's `fencingToken` is checked inside every purge transaction.
3. **R1** Jobs declared in `ModuleDescriptor.Jobs`; `ILeaseJob`/`IJobContext` in Core; `LeaseJobRunner.RunOnceAsync` for tests; `IClock` for time.
4. **R2** `identity.request_deletion` (Touches profile) with grace from `Identity:Deletion:GraceDays` (30); exempt from command-group bans; serving-instance `ProfileCache` refresh via a new `afterExecute` hook.
5. **R3** `ACCOUNT_DELETED` carries `purgeAfter`; provider login checks the mapped account before `SWITCH_ACCOUNT`.
6. **R4** Due discovery = two partial-index range queries (pending profiles; purging records due) via new `IMaintenanceQueryStore`.
7. **R5** `CollectionSpec.Deletion` + `PlayerField`; registry validation; platform-owned treatments; auto reference indexes; `ix_receipts_player` becomes platform-provisioned.
8. **R6** `account_deletions/deletion::{playerId}` = progress + fence + purge record; anonymous id `anon_…` stored only while purging.
9. **R6** `IAccountPurgeWriter` is the only purge writer; `ITransactionContext.RemoveAsync` added; batches ≤ 25 documents.
10. **R7** Step order receipts → anonymize collections → module steps → remove collections → verify → `pa_*` → profile + record; resumable, budgeted, exponential retry per player.
11. **R8** Receipt/ledger/audit overwrite only values equal to the player id; receipts also lose `resultCompact`/`bodyHash` and redact claim keys into removed collections.
12. **R9** "Session ⇒ profile exists" invariant at the bearer; profile creation at login anchored on the mapping document (write conflict with the purge's removal).
13. **R10** Module steps get a fenced `IPlayerDataEraser` limited to their own collections; never a transaction.
14. **R11** Cancel = `identity.cancel_deletion` (409 `DELETION_NOT_PENDING` / `DELETION_GRACE_OVER`); race with purge start resolved by the `pa_profile` write conflict; `IGmTargetValidator` gets the operation; GM view gains `deletion`, purged players return 200.
15. **R13** SDK `RequestAccountDeletionAsync`; success and `ACCOUNT_DELETED` both mean committed; `AccountBlocked` carries `purgeAfter`.

## Data flow

**Request (US1).** Player → bearer (cache) → runner transaction {profile.deleted, epoch+1, receipt} → endpoint hook
`ProfileCache.ReadFreshAsync` → response `purgeAfter`. State: `pa_profile` (owner identity).

**Login while pending (US1-3).** Guest: mapping + secret → `LoginCompletion` → `AccountChecks` → 401
`ACCOUNT_DELETED {purgeAfter}`. Provider: verify → mapping → account checks of the mapped player → 401. No mapping
write, no session.

**Cancel (US3).** GM → CSRF/permission → `GmTargetValidator(playerId, identity.cancel_deletion)` → runner transaction
{clear deleted, receipt, audit} → cache refresh → player logs in with the same credentials (fresh profile read).

**Purge run (US2, US4, US5).** Runner on the lease holder → `AccountPurgeJob`: resume list (records due), then start
list (pending profiles due), ≤ 50 players → per player `BeginAsync<ProfileState>` (start transaction or claim) →
`AccountPurgeService.RunAsync(ticket, budget)` → steps of research R7, each a loop of `QueryReferencesAsync` +
`ApplyAsync`/module step until empty, each transaction fenced → `CompleteAsync`. Failure → `RecordFailureAsync`
(retry later). Fenced out or cancelled → stop the run; the runner releases the lease.

**State ownership.** Access and cancel eligibility: `pa_profile.deleted` (identity). Purge progress, fence, anonymous id,
purge record: `account_deletions` (platform). Job schedule and holder: `leases` (platform). Retained financial/audit
records: their owning collections, rewritten in place.

## Spec deviations

Where the code or a requirement contradicts the spec, the plan decides as follows (no silent change):

| # | Spec says | Plan does | Why |
| --- | --- | --- | --- |
| D1 | FR-015: retained records keep "their other fields unchanged" | Receipts additionally get `resultCompact = ""`, `bodyHash = ""`, and `claimKeys` into Remove-treated collections → `"{collection}/redacted"` | `resultCompact` can hold personal data (display name result), a hash of a low-entropy body is attackable, identity claim keys hold provider ids; all only serve the 24 h replay window, expired ≥ 30 days earlier. Amounts, timestamps, receipt references and correlation ids — the fields the edge case lists — stay unchanged |
| D2 | FR-018: purge record kept "for answering stale tokens" | Stale tokens are answered from the **missing profile** (`ACCOUNT_DELETED`, no details) while the session record exists or is cached; after the purge removes sessions they are unknown tokens (`UNAUTHENTICATED`). The record serves GM and the cancel-after-purge 409 | No extra KV read on every authenticated request; keeping a token index after purge would retain device-linked data. Access is refused in both cases |
| D3 | FR-005/DAT-09 name `IPlayerAggregateWriter` as the purge path | Purge writes go through `IAccountPurgeWriter` (Platform, same guarantees); `IPlayerAggregateWriter` only gains the creation-anchor overload | Keeps the single-aggregate writer small (interface segregation); the requirement's substance (transactional, platform-only) is unchanged |
| D4 | FR-021: "raises the account-deleted event" | Raises the existing `AccountBlocked` event with code `ACCOUNT_DELETED` and `purgeAfter` | The SDK already routes `ACCOUNT_DELETED` there (Scope note); a second event would duplicate it |
| D5 | Scope note: deleted→suspended→replaced order already at login | Not true for provider login: `SWITCH_ACCOUNT` is checked first today. Plan moves the mapped player's account checks before the switch check | US1-3 requires `ACCOUNT_DELETED` for provider login |
| D6 | Scope note: "GM mutations refuse a target pending deletion" | True, but `IGmTargetValidator` cannot distinguish the cancel command; its signature gains `operation` (breaking for game validators) | Cancel must pass the validator (FR-011) |
| D7 | Scope note: GM view shows the profile | After purge the current lookup returns 404 (no aggregates). Plan returns 200 with `deletion.state = purged` | US2-7, FR-012 |
| D8 | FR-007 "cache refreshed" on the serving instance | No refresh hook exists for commands; plan adds `afterExecute` to `MapPlayerCommand`/`MapGmCommand` | FR-007 immediate revocation |
| D9 | (implicit) purged profile = absent document | Today an absent profile reads as a valid default state; a cached session of a purged player would authenticate | Closed by research R9 (invariant + creation anchor) |

Note (not a spec deviation): time in jobs uses the codebase's `IClock`/`FakeClock` (TIME-01), not `TimeProvider`;
only the poll/renew timers wait on real time, and tests drive runs through `RunOnceAsync`.

## Project Structure

### Documentation (this feature)

```text
specs/010-jobs-account-deletion/
├── spec.md
├── plan.md                          # this file
├── research.md                      # decisions R1..R13
├── data-model.md                    # documents, treatments, config
├── contracts/account-deletion-api.md
├── checklists/requirements.md
└── tasks.md                         # next stage (/speckit-tasks)
```

### Source Code (repository root)

```text
src/GameServer.Core/
├── Modules/ModuleDescriptor.cs            # DeletionTreatment, CollectionSpec(+Deletion, PlayerField), JobDescriptor, DeletionStepRegistration, descriptor lists
├── Jobs/ILeaseJob.cs                      # ILeaseJob, IJobContext
└── Privacy/IAccountDeletionStep.cs        # IAccountDeletionStep, IPlayerDataEraser
src/GameServer.Platform/
├── Persistence/DocumentStore.cs           # ITransactionContext.RemoveAsync
├── Persistence/MaintenanceQueries.cs      # DueQuery, ReferenceQuery, IMaintenanceQueryStore
├── Aggregates/PlayerAggregateWriter.cs    # creation-anchor overload, AggregateAnchorMissingException
├── Jobs/{LeaseStore,LeaseJobRunner,JobSettings,JobContext}.cs
├── Privacy/{AccountPurgeWriter,AccountPurgeService,AccountDeletionRecords,PurgeTicket,AnonymousIds,PurgeSettings,PlayerDataEraser}.cs
├── Registry/ModuleRegistry.cs             # ValidateDeletion, ValidateJobs, DeletionPlan(), platform collections leases/account_deletions
├── Diagnostics/PlatformMetrics.cs         # job_* and account_purge_* instruments
└── GameServerOptions.cs                   # Jobs, Purge sections
src/GameServer.Platform.Couchbase/
├── CouchbaseDocumentStore.cs              # RemoveAsync in transaction, IMaintenanceQueryStore
└── CouchbaseProvisioner.cs                # ix_receipts_player, ix_account_deletions_due, reference indexes
src/GameServer.Platform.Testing/InMemoryDocumentStore.cs   # staged remove, IMaintenanceQueryStore
src/GameServer.Platform.Web/
├── Commands/CommandEndpointFactory.cs     # afterExecute overload
├── Commands/GmCommandEndpointFactory.cs   # IGmTargetValidator(playerId, operation), afterExecute
└── Hosting/GameServerBuilder.cs           # register LeaseStore, LeaseJobRunner, purge services, IMaintenanceQueryStore
src/GameServer.Modules.Identity/
├── ProfileState.cs                        # DeletionInfo.PurgeStartedAtUtc
├── ProfileCommands.cs                     # error codes 14005/14006
└── AccountDeletion.cs                     # AccountDeletionPolicy, RequestDeletionHandler, CancelDeletionHandler, start decision
src/GameServer.Modules.Identity.Adapters/
├── IdentityModule.cs                      # commands, routes, job, treatments, profile due index, metrics
├── AccountPurgeJob.cs
├── IdentityOptions.cs                     # Deletion:GraceDays
├── GuestCredentials.cs / AccountChecks    # purgeAfter details
├── ProfileCache.cs                        # ProfileState? (missing document)
├── PlayerBearerAuthenticationHandler.cs   # missing profile → ACCOUNT_DELETED
├── LoginCompletion.cs                     # creation anchor
├── ProviderLoginService.cs                # account checks before SWITCH_ACCOUNT
├── BanCommandGuard.cs                     # exempt identity.request_deletion
└── Protos/identity.proto                  # RequestAccountDeletionRequest/Result
src/GameServer.Modules.Gm.Adapters/
├── GmModule.cs                            # treatments; receipts index removed (platform owns it)
├── GmPlayerEndpoints.cs                   # GmDeletionView, purged view
└── GmTargetValidator.cs                   # operation-aware, purge record
src/GameServer.Modules.Economy/EconomyModuleDefinition.cs   # ledger/extref_claims treatments
modules/Sample.Counter/Sample.Counter/CounterModuleDefinition.cs  # counter_log treatment
sdk/unity/com.gameserver.client/Runtime/Core/GameServerClient.cs  # RequestAccountDeletionAsync
docs/api/gm-openapi.json
docs/pcr/{platform,identity,gm,unity-sdk,economy,sample-counter}.md
tests/GameServer.Platform.Tests/{Jobs/LeaseTests.cs, Privacy/AccountPurgeWriterTests.cs, Registry/ModuleRegistryTests.cs, Persistence/StoreConformanceTests.cs}
tests/GameServer.Platform.Web.Tests/{AccountDeletionTests.cs, AccountPurgeTests.cs, LeaseJobHostTests.cs}
tests/GameServer.Gm.Tests/{DeletionGmTests.cs, OpenApiTests.cs}
tests/Sample.Counter.Tests/DeletionExtensionTests.cs
tests/GameServer.Client.Tests/AccountDeletionClientTests.cs
tests/GameServer.Platform.Couchbase.IntegrationTests/{CouchbaseStoreConformanceTests.cs, CouchbaseLeaseAndPurgeTests.cs}
tests/GameServer.Architecture.Tests/ArchitectureTests.cs
```

**Structure Decision**: keep the 2-project module layout (ARC-01). Generic mechanisms (lease runner, purge writer and
service, store primitives, treatment validation) live in Platform because they need the registry, the receipt schema
and transactions; account semantics (request, cancel, start decision, login behavior, job schedule) live in the
identity module, which owns `profile`. GM view/validator stay in the gm module.

## Test strategy

| Story / rule | In-memory tests (no infrastructure) | Couchbase integration |
| --- | --- | --- |
| US1 request | request → `purgeAfter`, receipt count 1, epoch +1; old token 401 `ACCOUNT_DELETED {purgeAfter}` on the same instance at once, on a second `TestHost` instance after `FakeClock` + 60 s; guest and provider login 401, no session, no new player; link by another player 409; retry same request id → replay or `ACCOUNT_DELETED`, still one receipt; command-group ban allows, login ban 403 `ACCOUNT_SUSPENDED`; provider login with another player's session → `ACCOUNT_DELETED` not `SWITCH_ACCOUNT` | request + login refusal on Couchbase |
| US2 purge | `FakeClock` past `purgeAfter` → `RunOnceAsync` → scan **every** collection of the store: 0 `pa_*` of the player, 0 occurrences of the old id outside `account_deletions` (SC-002); receipt and ledger counts and per-resource sums equal under the anonymous id (SC-003); claim markers, gm_audit, counter_log anonymized; guest + provider login create a new player id; not-yet-due player untouched; GM sees purged view; anonymous id absent from captured logs | full purge + `EXPLAIN` of due and reference queries (PERF-07) + scan |
| US2-4 / SC-005 crash, resume | injected failing module step → player stays purging, `nextAttemptAtUtc` set, metric + log, next run (clock advanced) completes with identical end state; `StoreFaultInjector` failure mid-batch; budget exhaustion → `Incomplete` then completion | two hosts, kill holder mid-purge, other completes |
| US2-5 / R9 | stale cached session after profile removal → `ACCOUNT_DELETED`; login racing purge: pause login between mapping read and epoch transaction (test hook), purge completes, login → `ACCOUNT_DELETED`, no profile document recreated; first login after a crashed mapping insert still succeeds | anchor conflict vs mapping removal |
| US3 GM | view pending (by id, mapping, display name) / purging / purged; cancel 200 + one audit with 7 fields + login again with same credentials and data, old token still revoked; 403 without `player.ban`; 409 not pending; 409 grace over (clock), purge started, purged; CSRF missing → 403; re-request after cancel → new grace; race: cancel and `BeginAsync` concurrently → exactly one wins; OpenAPI document matches | cancel vs purge start race on real transactions |
| US4 lease | acquire/renew/release, token increments, takeover only after `leaseUntil`, released lease honours `nextRunAtUtc`, renewal CAS loss → run cancelled, stale holder's purge write → `PurgeFencedOutException`, shutdown releases with `nextRunAtUtc = now`, duplicate job name / bad name → startup fail, disabled job never runs | two hosts, short lease: one holder; holder stopped → other acquires with larger token; paused holder refused |
| US5 modules | missing treatment (module collection, projection target), conflicting treatment, ModuleStep without step, duplicate step name → startup fail naming module + collection (SC-007); counter_log anonymized; test module step called with both ids, rerun changes nothing, failing step keeps purge pending; eraser refuses foreign collections and `pa_*`/`receipts` | — |
| Store primitives | conformance suite (in-memory **and** Couchbase): transactional remove (removed document reads as missing later in the same attempt and after commit; write-write conflict with a concurrent replace; rollback keeps the document), due query ordering and unset filter, reference query | same suite |
| SDK | `RequestAccountDeletionAsync` OK → session cleared, `AccountBlocked` with `purgeAfter`; retry answered `ACCOUNT_DELETED` → exception with `purgeAfter`, session cleared | — |
| Arch | existing rules (no `ITransactionContext`/`ITransactionalDocumentStore` in modules; Core/business independent of Platform) cover steps and jobs; new rule: inside `GameServer.Platform`, only types in namespaces `Commands`, `Aggregates`, `Persistence` and `Privacy` depend on `ITransactionContext` (keeps `Jobs` and future code off the transactional write path) | — |

ACC-37 is covered by the US1 + US3 + US2 end-to-end tests (session revoked, identity hidden, cancel within grace, purge
and anonymized ledger/receipts after grace).

## Implementation phases (input for tasks.md)

1. **Foundations (blocking)** — Core contracts (`DeletionTreatment`, `CollectionSpec` members, `JobDescriptor`,
   `DeletionStepRegistration`, `ILeaseJob`, `IJobContext`, `IAccountDeletionStep`, `IPlayerDataEraser`);
   `ITransactionContext.RemoveAsync` (Couchbase + in-memory); `IMaintenanceQueryStore` (both stores) + conformance
   tests; registry `ValidateDeletion`/`ValidateJobs`/`DeletionPlan()`, platform collections `leases`,
   `account_deletions` (TransactionOnly); treatments declared in identity, economy, gm, sample counter; provisioner
   indexes (`ix_receipts_player` moved, due and reference indexes); `GameServerOptions.Jobs`/`Purge` with validation.
2. **US4 lease runner** — `LeaseStore`, `LeaseJobRunner` (start after store init, poll/renew timers, shutdown release,
   `RunOnceAsync`), metrics, `TestHost` default `Jobs:Enabled=false`, lease tests (in-memory two instances).
3. **US1 request + login semantics** — `DeletionInfo.PurgeStartedAtUtc`; `RequestDeletionHandler` + route + proto +
   `afterExecute` hook; `AccountChecks` details; `ProfileCache` nullable + bearer invariant; creation-anchor overload
   + `LoginCompletion`; provider login order; `BanCommandGuard` exemption; error codes 14005/14006; identity metrics;
   SDK `RequestAccountDeletionAsync`; US1 tests.
4. **US3 GM** — `CancelDeletionHandler` + route; `IGmTargetValidator` operation parameter; `AccountDeletionRecords`;
   `GmDeletionView` + purged view; OpenAPI regeneration; US3 tests (race test after phase 5 provides `BeginAsync`).
5. **US2 + US5 purge** — `AnonymousIds`, `AccountPurgeWriter` (begin, apply with receipt rules, step, complete,
   failure, fencing, relay of pending receipts), `PlayerDataEraser`, `AccountPurgeService` (steps, budget, verify),
   `AccountPurgeJob` (discovery, start decision, overdue gauge, per-run limits) registered in the identity descriptor;
   test module with a step; US2/US5 tests including crash/fence/race.
6. **Integration and docs** — Couchbase integration tests (lease contention, purge, race, `EXPLAIN`), full suite;
   PCR updates (platform: jobs, purge, treatments, extensible capabilities, breaking `IGmTargetValidator`; identity;
   gm; unity-sdk; economy; sample-counter); resume file `AgentMemories/features/p05-speckit/00_summary.md` after review.

Phases 3 and 4 depend only on phase 1; phase 5 depends on 1–2 (job) and 3 (profile field, anchor); the US3 race test
waits for phase 5.

## Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| A relay that read a receipt **before** its rewrite inserts a projection with the old id after the anonymize step | Old id survives in one projection document | Purge relays pending receipts itself after rewriting; `verify` step re-queries every treated collection; receipts are normally relayed ≥ 30 days before purge (no commands during grace). Residual window = one in-flight sweeper batch; documented in PCR |
| Couchbase keeps tombstones of removed documents (keys = player id for `pa_*`) and ATR entries until metadata purge (default 3 days) | Old id visible to administrators at the storage layer for a few days | Not reachable through any API or query; documented in PCR platform/runbook (slice 013) |
| Sweeper KV `MutateIn` on a receipt concurrent with its purge transaction | `outboxPending` flag write lost | One extra idempotent relay; the sweeper never writes `playerId`, so anonymization cannot be undone |
| Deletion backlog above ≈ 7 200/day | SC-006 missed | `account_purge_overdue_seconds` alert; `MaxPlayersPerRun`/interval configurable |
| STJ vs `FormatTime` string formats in due queries | ± 1 s scheduling error | Query parameter formatted like the field; acceptable for a 30-day grace |
| `IGmTargetValidator` signature change | Game validators stop compiling | Announced in PCR platform (breaking extension point); trivial migration |
| Restoring a backup taken before a purge brings data back | Privacy breach after restore | Out of scope; restore runbook (slice 013) must re-run purges for `account_deletions` records in state `purged` |
| Background loops making tests flaky | CI noise (cf. 009 telemetry flake) | Jobs disabled by default in `TestHost`; tests drive `RunOnceAsync`; only lease host tests enable the loop with short timings |

## Complexity Tracking

| Violation | Why needed | Simpler alternative rejected because |
| --- | --- | --- |
| CMD-08/SEC-14 "ledger and audit never deleted, audit immutable" (`AuditedStore` refuses audit replacement): the purge overwrites player-id values in `gm_audit` and `ledger` | PRIV-01/CMD-09 require anonymizing `playerId` by overwriting the field while keeping the records | Deleting the records breaks accounting (SC-003) and CMD-08; leaving them breaks PRIV-01. The exception is confined to `AccountPurgeWriter`, field values equal to the player id only, transactional and fenced |
| Constitution I "pa_* and rcpt::* only through the transaction runner": receipts and `pa_*` are rewritten/removed by `IAccountPurgeWriter` outside `ICommandRunner` | The purge is not a player command (no request id, no receipt of its own); DAT-09 explicitly allows the writer path for purge | Running the purge as synthetic commands would need one receipt per batch (more personal-data-free noise, Touches ≤ 4 cannot cover 16 aggregates) and could not remove documents |
| Login now writes the identity mapping (unchanged content) inside the profile-creating transaction | Only a write-write conflict makes "no credential can authenticate as a purged id" hold under arbitrary pauses (research R9) | Read-only checks are not conflict-detected by Couchbase transactions; timing-based ordering fails under GC/VM pauses |
