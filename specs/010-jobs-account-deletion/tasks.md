# Tasks: Lease jobs and account deletion (P0.5 slice 010)

**Input**: [plan.md](plan.md) (authoritative; deviations D1–D9 override the spec), [spec.md](spec.md),
[research.md](research.md), [data-model.md](data-model.md), [contracts/account-deletion-api.md](contracts/account-deletion-api.md)

**Tests**: mandatory (constitution VI). Each story phase carries its own in-memory tests; Couchbase integration in
Phase 6. Scenario ids (`US1-3`) refer to the spec's acceptance scenarios.

**Format**: `- [ ] T0NN [P] [USx] description with paths`. `[P]` = different files, no open dependency.

## Phase 1: Foundational (blocks every story)

- [ ] T001 Core descriptor contracts: `DeletionTreatment` (`Unspecified`, `NoPlayerData`, `Anonymize`, `Remove`, `ModuleStep`), `CollectionSpec(..., Deletion = Unspecified, PlayerField = "playerId")`, `JobDescriptor(Name, JobType, DefaultIntervalSeconds)`, `DeletionStepRegistration(Name, StepType)`, `ModuleDescriptor.Jobs` / `DeletionSteps` in `src/GameServer.Core/Modules/ModuleDescriptor.cs`
- [ ] T002 [P] Core `ILeaseJob`, `IJobContext` (`JobName`, `FencingToken`, `Cancellation`) in `src/GameServer.Core/Jobs/ILeaseJob.cs`; `IAccountDeletionStep`, `IPlayerDataEraser` (`PlayerId`, `AnonymousId`, `BatchSize`, `FindAsync`, `RewriteAsync`, `ScrubOutboxAsync`) in `src/GameServer.Core/Privacy/IAccountDeletionStep.cs`
- [ ] T003 [P] `ITransactionContext.RemoveAsync(StoredDocument existing)` in `src/GameServer.Platform/Persistence/DocumentStore.cs`; in-memory staged remove with version check (reads as missing later in the attempt, conflicts with a concurrent replace, rollback keeps it) in `src/GameServer.Platform.Testing/InMemoryDocumentStore.cs`; `CouchbaseTransactionContext.RemoveAsync` → `AttemptContext.RemoveAsync` in `src/GameServer.Platform.Couchbase/CouchbaseDocumentStore.cs`
- [ ] T004 `DueQuery`, `ReferenceQuery`, `IMaintenanceQueryStore` (`QueryDueAsync`, `QueryReferencesAsync`; fields via `PagedQuery.IsValidField`, collection pattern, RequestPlus, oldest first / key order, limit) in `src/GameServer.Platform/Persistence/MaintenanceQueries.cs`; implementations in `InMemoryDocumentStore.cs` and `CouchbaseDocumentStore.cs` (after T003, same files)
- [ ] T005 Conformance tests in shared suite `tests/GameServer.Platform.Tests/Persistence/StoreConformanceTests.cs` (runs in-memory and via `CouchbaseStoreConformanceTests`): transactional remove (missing later in attempt and after commit, write-write conflict with concurrent replace, rollback keeps document); due query (≤ now, unset field missing/null filtered, order, limit); reference query (equality, key order, limit)
- [ ] T006 [P] Options: `JobSettings` (`Enabled` true, `LeaseSeconds` 60 [30..600, ≥ 3 × `Transactions:ExpirationSeconds`], `RenewSeconds` 20 [≤ Lease/3], `PollSeconds` 30 [5..Lease], `Settings[job] {Enabled?, IntervalSeconds? 10..86400}`) in `src/GameServer.Platform/Jobs/JobSettings.cs`; `PurgeSettings` (`MaxPlayersPerRun` 50, `BatchSize` 25 [1..100], `MaxDocumentsPerRun` 5000, `RetryDelayMinutes` 10, `MaxRetryDelayMinutes` 1440) in `src/GameServer.Platform/Privacy/PurgeSettings.cs`; `GameServerOptions.Jobs` / `Purge` + startup validation of the data-model bounds in `src/GameServer.Platform/GameServerOptions.cs`
- [ ] T007 [P] Identity business: `DeletionInfo.PurgeStartedAtUtc` (nullable, additive, `schemaVersion` stays 1) in `src/GameServer.Modules.Identity/ProfileState.cs`; codes `DELETION_NOT_PENDING` (14005, 409) and `DELETION_GRACE_OVER` (14006, 409, not retryable) added to the identity `ErrorCodeSet` in `src/GameServer.Modules.Identity/ProfileCommands.cs` (moved here from plan phases 3/4 so US1 and US3 do not depend on each other)
- [ ] T008 Registry in `src/GameServer.Platform/Registry/ModuleRegistry.cs`: `PlatformCollections.Leases` (KV) and `AccountDeletions` (TransactionOnly) in `RequiredCollections()` / `TransactionOnlyCollections()`; `ValidateDeletion` (research R5 rules 1–5: fixed platform treatments, exactly one explicit treatment per module collection and projection target, conflict, projection target without `CollectionSpec`, `ModuleStep` without a step in the same module, `PlayerField` path, duplicate step name, step type implements `IAccountDeletionStep`; messages name module + collection); `ValidateJobs` (name `^[a-z][a-z0-9._-]{1,63}$`, duplicate across modules, type implements `ILeaseJob`); `CollectionDeletion` + `DeletionPlan()` (platform first, registration order) (after T001, T002)
- [ ] T009 Built-in treatments: identity `identities`, `sessions` → Remove + index-only `pa_profile` with `ix_profile_deletion_due` (`state.deleted.purgeAfterUtc` WHERE `state.deleted.purgeStartedAtUtc IS NOT VALUED`) in `src/GameServer.Modules.Identity.Adapters/IdentityModule.cs`; economy `ledger`, `extref_claims` → Anonymize in `src/GameServer.Modules.Economy/EconomyModuleDefinition.cs`; gm `gm_staff`, `gm_staff_identities`, `gm_sessions` → NoPlayerData, drop the `receipts` re-declaration (index moves to platform) in `src/GameServer.Modules.Gm.Adapters/GmModule.cs`; `counter_log` → Anonymize in `modules/Sample.Counter/Sample.Counter/CounterModuleDefinition.cs` (after T001)
- [ ] T010 [P] Test descriptors get treatments so existing suites keep starting: `probe_claims` in `tests/GameServer.Platform.Tests/Infrastructure/TestModules.cs`; `conf_docs`, `tst04`, `gm_staff` in `tests/GameServer.Platform.Couchbase.IntegrationTests/Infrastructure/CouchbaseFixture.cs`; any inline descriptors in `tests/GameServer.Platform.Tests/Registry/ModuleRegistryTests.cs` (after T001)
- [ ] T011 Provisioner: `ix_receipts_player` platform-provisioned, `ix_account_deletions_due` (`nextAttemptAtUtc` WHERE `purgedAtUtc IS NOT VALUED`), `ix_{collection}_player_ref` for every Anonymize/Remove entry of `DeletionPlan()` unless a declared index leads with `PlayerField`, in `src/GameServer.Platform.Couchbase/CouchbaseProvisioner.cs` (after T008)
- [ ] T012 [P] `AccountDeletionRecord` (data-model shape) + `AccountDeletionRecords.GetAsync(playerId)` read side in `src/GameServer.Platform/Privacy/AccountDeletionRecords.cs` (after T008; shared by US3 view/validator and US2 writer)
- [ ] T013 Registry tests in `tests/GameServer.Platform.Tests/Registry/ModuleRegistryTests.cs`: missing treatment on module collection and on projection target, conflicting treatments, projection target without spec, platform collection redeclared with a different treatment, `ModuleStep` without step, duplicate step name, duplicate job name, bad job name, job type not `ILeaseJob` → startup error naming module + collection/job (SC-007, US4-5, US5-1); `DeletionPlan()` lists built-ins with expected treatments (after T008–T010)
- [ ] T014 [P] Arch rule in `tests/GameServer.Architecture.Tests/ArchitectureTests.cs`: inside `GameServer.Platform` only namespaces `Commands`, `Aggregates`, `Persistence`, `Privacy` depend on `ITransactionContext`; existing module rules (no `ITransactionContext`/`ITransactionalDocumentStore`, business assemblies not on Platform) unchanged and green

**Checkpoint**: build green, full existing suite green with treatments declared.

## Phase 2: US4 — Lease job runner (P2)

- [ ] T015 [P] [US4] Metrics `job_runs_total{job,result}`, `job_run_duration_seconds{job}`, `job_lease_acquisitions_total{job}`, `job_lease_losses_total{job}` in `src/GameServer.Platform/Diagnostics/PlatformMetrics.cs`
- [ ] T016 [P] [US4] `LeaseStore` (`TryAcquireAsync`: insert token 1 / CAS replace when released and `nextRunAtUtc <= now` or held and `leaseUntilUtc <= now`, token + 1; `TryRenewAsync`: CAS, ambiguous write re-read by owner + token; `ReleaseAsync`: owner null, `leaseUntilUtc = now`, `nextRunAtUtc`, `lastRun`), `JobLease`, `LeaseLostException`; times from `IClock`, no expiry, owner `{machine}:{pid}:{8 hex}` in `src/GameServer.Platform/Jobs/LeaseStore.cs`
- [ ] T017 [US4] `JobContext`, `JobRunResult`, `LeaseJobRunner : BackgroundService` in `src/GameServer.Platform/Jobs/LeaseJobRunner.cs`, `JobContext.cs`: await `IStoreInitializer.EnsureInitializedAsync`; poll every `PollSeconds` ± 20 %; renew on `PeriodicTimer`, loss (CAS lost or not renewed before `leaseUntil − 5 s`) cancels the context; release after run (`now + interval` finished, `now` interrupted; 2 s budget, `CancellationToken.None`); `stoppingToken` → stop at safe point + release (OPS-03); disabled job never acquires; per-job interval override; `RunOnceAsync(jobName, ct)`; activity `job {name}`; lease logs carry job/owner/token (after T015, T016)
- [ ] T018 [US4] Register `LeaseStore`, `LeaseJobRunner` (hosted + resolvable for `RunOnceAsync`), descriptor job types in `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs`; `GameServer:Jobs:Enabled = false` in `TestHost.DefaultSettings()` in `tests/Shared/TestHost.cs`
- [ ] T019 [US4] Unit tests `tests/GameServer.Platform.Tests/Jobs/LeaseTests.cs` (in-memory, `FakeClock`): first acquire token 1; second owner refused while held; renew keeps token; takeover only after `leaseUntilUtc` with token + 1 (US4-2); released lease waits for `nextRunAtUtc`; ambiguous renew recognized; renew after takeover → lost; release sets `nextRunAtUtc` per rule; document has no expiry
- [ ] T020 [US4] Host tests `tests/GameServer.Platform.Web.Tests/LeaseJobHostTests.cs`: two `TestHost` on one store, test job with short timings and loop enabled → never two concurrent runs, runs only on holder (US4-1); `RunOnceAsync` results `NotDue`/`HeldElsewhere`/`Completed`/`Failed`; renewal loss cancels the job token (`LeaseLost`); graceful stop of holder releases with `nextRunAtUtc = now` and the other host runs at once (US4-4, FR-004); disabled job never runs; duplicate job name → host start fails (US4-5)

**Checkpoint**: runner usable; US4-3 (stale holder refused) is verified with the purge record in T041/T047.

## Phase 3: US1 — Player requests deletion (P1) MVP

- [ ] T021 [US1] `AccountDeletionPolicy(GraceDays)`, `RequestDeletionCommand`, `RequestDeletionResult`, `RequestDeletionHandler` (`identity.request_deletion`, Player, `Touches = [profile]`; already deleted → `Reject(ACCOUNT_DELETED, {purgeAfter})` no receipt; else `Deleted = {now, now + grace, null}`, `SessionEpoch + 1`) in `src/GameServer.Modules.Identity/AccountDeletion.cs`
- [ ] T022 [P] [US1] `AccountChecks.CheckAccess`: `ACCOUNT_DELETED` details `purgeAfter` (`yyyy-MM-ddTHH:mm:ssZ`) in `src/GameServer.Modules.Identity/GuestCredentials.cs`
- [ ] T023 [P] [US1] `MapPlayerCommand` overload with `afterExecute: Func<CommandExecution, HttpContext, Task>` (after commit or replay) in `src/GameServer.Platform.Web/Commands/CommandEndpointFactory.cs`
- [ ] T024 [US1] Proto `RequestAccountDeletionRequest {}` / `RequestAccountDeletionResult {requested_at_utc_ms, purge_after_utc_ms}` in `src/GameServer.Modules.Identity.Adapters/Protos/identity.proto`; `Identity:Deletion:GraceDays` (30, 1..90) + validation in `IdentityOptions.cs`; `IdentityModule.cs`: register command + policy, route `POST /api/v1/identity/delete-account` with `afterExecute` → `ProfileCache.ReadFreshAsync`, metric `account_deletion_requests_total`, log with player id only (after T021, T023)
- [ ] T025 [P] [US1] `BanCommandGuard`: exempt `identity.request_deletion` from command-group bans (US1-6); null profile = no ban, in `src/GameServer.Modules.Identity.Adapters/BanCommandGuard.cs`
- [ ] T026 [US1] Invariant "session ⇒ profile document exists" (R9, D9): `ProfileCache` caches `ProfileState?` (missing `pa_profile` = null) in `ProfileCache.cs`; `PlayerBearerAuthenticationHandler` null → 401 `ACCOUNT_DELETED` without details; `ProviderLoginService.CurrentPlayerAsync` null → no current player
- [ ] T027 [P] [US1] Creation anchor: `IPlayerAggregateWriter.UpdateAsync(..., DocumentKey? creationAnchor, ct)` overload (old signature delegates with null), `AggregateAnchorMissingException` in `src/GameServer.Platform/Aggregates/PlayerAggregateWriter.cs`; tests `tests/GameServer.Platform.Tests/Aggregates/CreationAnchorTests.cs` (aggregate missing + anchor missing or other `playerId` → throws, nothing written; anchor present → replaced unchanged + aggregate created; aggregate present → anchor untouched)
- [ ] T028 [US1] `LoginCompletion.CompleteAsync(playerId, mappingKey, ct)` passes `identities/{mappingKey}`, maps `AggregateAnchorMissingException` → `ACCOUNT_DELETED`, internal test seam to pause between mapping read and epoch transaction, in `LoginCompletion.cs` + guest/provider callers; `ProviderLoginService` runs mapped player's account checks before `SWITCH_ACCOUNT` (D5) (after T026, T027)
- [ ] T029 [US1] Tests `tests/GameServer.Platform.Web.Tests/AccountDeletionTests.cs` (request side): one receipt, `profile.deleted` dates, epoch + 1, response `purgeAfter` (US1-1); old token 401 `ACCOUNT_DELETED {purgeAfter}` at once on serving host, on second `TestHost` after `FakeClock` + 61 s (US1-2, FR-007); commands with old token refused (FR-009); retry same request id → replay or `ACCOUNT_DELETED`, still one receipt (US1-5); command-group ban (incl. group `identity`) allows, login ban → 403 `ACCOUNT_SUSPENDED` (US1-6); metric incremented
- [ ] T030 [US1] Tests same file (after T029), login side: guest and provider login → 401 `ACCOUNT_DELETED {purgeAfter}`, no session, no new player, mapping unchanged (US1-3, FR-008); wrong guest secret still `UNAUTHENTICATED`; provider login with another player's session → `ACCOUNT_DELETED` not `SWITCH_ACCOUNT`; link of the pending player's identity by another player → 409 `IDENTITY_CONFLICT` (US1-4); first login after a crashed mapping insert still succeeds (anchor)
- [ ] T031 [P] [US1] SDK `RequestAccountDeletionAsync` in `sdk/unity/com.gameserver.client/Runtime/Core/GameServerClient.cs` (OK → clear session + sections, raise `AccountBlocked` code `ACCOUNT_DELETED` with `purgeAfter`; server `ACCOUNT_DELETED` → `GameServerException` with details, session cleared; D4); regenerate `Runtime/Generated/Identity.cs`; tests `tests/GameServer.Client.Tests/AccountDeletionClientTests.cs` (after T024)

**Checkpoint**: US1 deliverable alone (lock without purge).

## Phase 4: US3 — GM sees and cancels (P1)

- [ ] T032 [US3] `CancelDeletionCommand`, `CancelDeletionHandler` (`identity.cancel_deletion`, Gm, permission `player.ban`, `Touches = [profile]`; `Deleted == null` → `DELETION_NOT_PENDING`; `PurgeStartedAtUtc != null || now >= PurgeAfterUtc` → `DELETION_GRACE_OVER`; clear `Deleted`, epoch unchanged, `AddAudit(player.ban, identity.cancel_deletion, reason, before: Deleted, after: null)`) in `src/GameServer.Modules.Identity/AccountDeletion.cs` (same file as T021 → after it)
- [ ] T033 [P] [US3] `IGmTargetValidator.ValidateAsync(playerId, operation, ct)` (breaking, D6), `MapGmCommand` passes the operation + `afterExecute` overload in `src/GameServer.Platform.Web/Commands/GmCommandEndpointFactory.cs`; `GmTargetValidator` allows `identity.cancel_deletion` on a pending player, other operations keep 404, purged record + cancel → 409 `DELETION_GRACE_OVER` via `AccountDeletionRecords` in `src/GameServer.Modules.Gm.Adapters/GmTargetValidator.cs`
- [ ] T034 [US3] `IdentityModule.cs`: register cancel command, route `POST /gm/v1/players/{playerId}/deletion/cancel` (`MapGmCommand`, reason, `X-Request-Id`, CSRF), `afterExecute` → `ProfileCache.ReadFreshAsync`, metric `account_deletion_cancellations_total`, log (after T024, T032, T033)
- [ ] T035 [P] [US3] `GmDeletionView {state, requestedAtUtc, purgeAfterUtc, purgeStartedAtUtc, purgedAtUtc}` + `GmPlayerView.Deletion` (pending/purging from `profile.deleted`, purged from `AccountDeletionRecords`); purged → 200 `aggregates: []` (D7); 404 only without aggregates and record; anonymous id never returned, in `src/GameServer.Modules.Gm.Adapters/GmPlayerEndpoints.cs` (after T012)
- [ ] T036 [US3] Regenerate `docs/api/gm-openapi.json` (`UPDATE_OPENAPI=1`); `tests/GameServer.Gm.Tests/OpenApiTests.cs` green with cancel route `x-gm-permission: player.ban`, `x-gm-csrf` (after T034, T035)
- [ ] T037 [US3] Tests `tests/GameServer.Gm.Tests/DeletionGmTests.cs`: view pending by id, mapping key, display name (US3-1); cancel 200 + exactly one `gm_audit` with 7 fields (US3-2); login again with same guest + provider credentials, data intact, old token still 401 (US3-3); no `player.ban` → 403 (US3-4); missing CSRF → 403; not pending → 409 `DELETION_NOT_PENDING` (US3-5); `FakeClock` past `purgeAfter` and seeded `PurgeStartedAtUtc` → 409 `DELETION_GRACE_OVER` (US3-6); re-request after cancel → new 30-day grace (US3-7); `identity.ban`/`economy.adjust` on pending → 404 (FR-009); replay same id, payload mismatch 409

**Checkpoint**: US1 + US3 complete (deletion request and GM cancel, ACC-37 except purge).

## Phase 5: US2 + US5 — Purge, anonymization, module steps (P1/P2)

- [ ] T038 [P] [US2] `AnonymousIds.New()` (`anon_` + 26 base32, 128 bits, `RandomNumberGenerator`) in `src/GameServer.Platform/Privacy/AnonymousIds.cs`; `PurgeTicket`, `PurgeStart<T>`, `PurgeAction`, `PurgeFencedOutException` in `Privacy/PurgeTicket.cs`
- [ ] T039 [P] [US2] Metrics `account_purges_total{result}`, `account_purge_step_failures_total{step}`, `account_purge_documents_total{action}`, observable `account_purge_overdue_seconds` in `src/GameServer.Platform/Diagnostics/PlatformMetrics.cs`
- [ ] T040 [US2] `IAccountPurgeWriter` + `AccountPurgeWriter` in `src/GameServer.Platform/Privacy/AccountPurgeWriter.cs`: `BeginAsync` (no record: profile rev + 1 and record insert `purging`, new anonymous id, `nextAttemptAtUtc = now`; purging: claim; purged: null), `ApplyAsync` (≤ `BatchSize`, missing = done, refuses `pa_profile`, Anonymize on `pa_*`, treatment mismatch, inserts; generic rule "string == playerId → anonymous id"; receipt rules `resultCompact`/`bodyHash` = "", Remove-collection `claimKeys` → `{collection}/redacted` (D1)), `CompleteStepAsync`, `CompleteAsync` (remove `pa_profile`, record `purged`, drop `anonymousId`/`completedSteps`/`attempts`/`lastFailure`), `RecordFailureAsync` (backoff `Retry × 2^(n−1)` capped); every call one transaction checking `record.fencingToken <= ticket.FencingToken` and storing it; uses `ITransactionalDocumentStore` (not `AuditedStore`), commit without caller token (after T003, T008, T012, T038)
- [ ] T041 [US2] Tests `tests/GameServer.Platform.Tests/Privacy/AccountPurgeWriterTests.cs`: stale token → `PurgeFencedOutException`, purged record never reverted (US4-3, FR-003); batch limit; refusals; nested payload anonymized, other fields unchanged; receipt rules; missing documents count as done; backoff values; begin vs concurrent profile replace → one commits
- [ ] T042 [US5] `PlayerDataEraser : IPlayerDataEraser` (only collections of the step's own module; refuses `pa_*`, `receipts`, `gm_audit`, foreign; `FindAsync` via `ReferenceQuery`; `RewriteAsync` = one fenced writer transaction, null = remove; `ScrubOutboxAsync` over the player's receipts for the module's kind) in `src/GameServer.Platform/Privacy/PlayerDataEraser.cs` (after T040)
- [ ] T043 [US2] `PurgeBudget`, `PurgeOutcome`, `AccountPurgeService.RunAsync(ticket, budget, ct)` in `src/GameServer.Platform/Privacy/AccountPurgeService.cs`: skip `completedSteps`; `receipts` (relay rewritten `outboxPending` receipts via `IOutboxRelay`) → `anonymize:{c}` → `step:{name}` (module order) → `remove:{c}` (sessions, identities) → `verify` (≤ 3 rounds) → `aggregates` (all `pa_*` except profile) → `CompleteAsync`; budget → `Incomplete`; failure → `RecordFailureAsync` + metric + `LogError` (player id, step, exception type; never anonymous id); activity `purge` (after T004, T039, T040, T042)
- [ ] T044 [US2] `AccountPurgeJob : ILeaseJob` (`identity.account-purge`, default 600 s) in `src/GameServer.Modules.Identity.Adapters/AccountPurgeJob.cs`: resume list (`account_deletions` due) then start list (`pa_profile` due) up to `MaxPlayersPerRun`; pure start decision (deleted, not started, `PurgeAfterUtc <= now` → `PurgeStartedAtUtc = now`) in `src/GameServer.Modules.Identity/AccountDeletion.cs`; per player `BeginAsync` → `RunAsync`; fenced/cancelled → stop run; overdue gauge update; declared in `IdentityModule.cs` `Jobs` (after T017, T028, T034, T043)
- [ ] T045 [US2] DI in `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs`: `IAccountPurgeWriter`, `AccountPurgeService`, `AccountDeletionRecords`, eraser factory, descriptor deletion steps, `IMaintenanceQueryStore` for both stores (after T043)
- [ ] T046 [US2] Tests `tests/GameServer.Platform.Web.Tests/AccountPurgeTests.cs` (end-to-end, `FakeClock` past `purgeAfter`, `RunOnceAsync`): every `pa_*` gone (US2-1); full scan of every collection via `Store.All` finds old id only in `account_deletions` (SC-002); receipts, ledger, `gm_audit`, `extref_claims`, `counter_log` anonymized with one id, record counts and per-resource ledger sums unchanged (US2-2, SC-003); identities and sessions removed, guest + provider login create a new player (US2-3); not-yet-due player untouched (US2-6); record `purged` without `anonymousId` (FR-018); anonymous id absent from captured logs; purged within one run (SC-006)
- [ ] T047 [US2] Tests same file (after T046): failing step → player stays purging, `nextAttemptAtUtc` set, metric + log, next run after clock advance completes with identical end state (US2-4, FR-017); `StoreFaultInjector` failure mid-batch then resume; budget exhaustion → `Incomplete` then complete; `MaxPlayersPerRun` limit; stale holder after takeover → fenced, run stops (US4-3, SC-005 in-memory)
- [ ] T048 [US2] Tests same file (after T047): cached session after profile removal → `ACCOUNT_DELETED`; login paused at the T028 seam while purge completes → `ACCOUNT_DELETED`, no `pa_profile` recreated; credentials during purging → 401 (US2-5, FR-016)
- [ ] T049 [US3] Tests `tests/GameServer.Gm.Tests/DeletionGmTests.cs` (after T037, T044): view `purging` and `purged` (200 by id, no personal data, no anonymous id; 404 by mapping and display name) (US2-7, FR-012); cancel while purging or purged → 409 `DELETION_GRACE_OVER`; cancel and `BeginAsync` concurrently → exactly one commits (US3-6)
- [ ] T050 [US5] Test module with `ModuleStep` collection + `IAccountDeletionStep` (records ids, fail switch) and tests `tests/Sample.Counter.Tests/DeletionExtensionTests.cs`: `counter_log` anonymized; step gets player id and anonymous id, runs before identities/profile removal (US5-2, FR-020); rerun changes nothing (US5-3); failing step keeps purge pending and retried next run; eraser refuses foreign collection and `pa_*`/`receipts`; host with collection lacking treatment / `ModuleStep` without step fails to start naming module + collection (US5-1, SC-007)

**Checkpoint**: ACC-37 end-to-end in memory.

## Phase 6: Integration and polish

- [ ] T051 [P] Couchbase lease tests `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseLeaseAndPurgeTests.cs`: two hosts, short lease → one holder; holder stopped without release → other acquires after `leaseUntilUtc` with larger token; paused holder's purge write refused (SC-005)
- [ ] T052 Couchbase purge tests same file (after T051): request + login refusal; full purge + scan; crash mid-purge on host A, host B completes with same end state; cancel vs purge start race on real transactions; login vs purge race (anchor write conflict); `EXPLAIN` of due and reference queries uses the partial/reference indexes (PERF-07)
- [ ] T053 [P] PCR: `docs/pcr/platform.md` (jobs + lease config, purge config + throughput, `DeletionTreatment`/`DeletionSteps`/`Jobs` extension points, breaking `IGmTargetValidator`, `afterExecute`, `RemoveAsync`, CMD-08 overwrite exception, tombstone and in-flight relay residual risks, metrics + overdue alert > 24 h), `identity.md` (delete-account, `ACCOUNT_DELETED` details, GraceDays 30, provider order change, 14005/14006, metrics), `gm.md` (cancel route, `deletion` view, purged 200), `unity-sdk.md`, `economy.md`, `sample-counter.md` (treatments)
- [ ] T054 tech-lead-reviewer on the slice; fix Blocker/Major; append review summary to this file
- [ ] T055 Final gate: `build GameServer.slnx -c Release` clean; `test --solution GameServer.slnx -c Release --no-build` all green **including Couchbase** (`COUCHBASE_*` env) and `GameServer.Architecture.Tests` (modules still open no transactions); commit `feat:` (no signature lines); update `AgentMemories/features/p05-speckit/00_summary.md`

## Dependencies

- Phase 1 blocks everything. Inside: T001/T002/T003/T006/T007 parallel → T004 → T005; T008 → T011, T012, T013; T009/T010 after T001.
- US4 (Phase 2), US1 (Phase 3), US3 (Phase 4) depend only on Phase 1. US1 and US3 share `AccountDeletion.cs` and `IdentityModule.cs`: with one implementer run US1 → US3 (T032 after T021, T034 after T024).
- Phase 5 needs Phases 1–3 and T034 (identity module file); T049 needs Phase 4 tests and T044.
- Phase 6 after Phase 5. T055 last.
- Parallel examples: T015 ∥ T016; T022 ∥ T023 ∥ T025 ∥ T027; T033 ∥ T035; T038 ∥ T039; T051 ∥ T053.

## Implementation risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| `ValidateDeletion` breaks every existing host/test descriptor | High (whole suite red) | T009 + T010 in Phase 1; checkpoint runs full suite before stories |
| Background loop makes tests flaky | Med | `Jobs:Enabled=false` in `TestHost` (T018); only T020/T051 enable it with short timings |
| `ProfileCache` nullable change ripples through login/ban/bearer | Med | Single task T026; T029/T030 + existing `AccountStateTests`/`SessionTests` must stay green |
| Race tests (T048, T049, T052) nondeterministic | Med | Explicit seam (T028) and concurrent `Task.WhenAll` with conflict assertion, not sleeps |
| `IGmTargetValidator` signature break | Low | Only in-repo implementer is `GmTargetValidator`; PCR announcement (T053) |

## FR coverage

| FR | Tasks |
| --- | --- |
| FR-001 | T001, T002, T006, T008, T017, T018, T020 |
| FR-002 | T016, T019, T020, T051 |
| FR-003 | T040, T041, T047, T051 |
| FR-004 | T017, T020 |
| FR-005 | T003, T040, T042, T014 |
| FR-006 | T021, T024, T025, T029 |
| FR-007 | T023, T024, T029, T043 (sessions removed) |
| FR-008 | T022, T026, T027, T028, T030 |
| FR-009 | T029, T033, T037 |
| FR-010 | T024, T053 |
| FR-011 | T007, T032, T033, T034, T037, T049, T052 |
| FR-012 | T035, T036, T037, T049 |
| FR-013 | T004, T009, T011, T044, T047 |
| FR-014 | T040, T043, T046 |
| FR-015 | T040, T043, T046 |
| FR-016 | T026, T027, T028, T043, T046, T048 |
| FR-017 | T040, T043, T047, T052 |
| FR-018 | T038, T040, T046 |
| FR-019 | T001, T008, T009, T010, T011, T013, T050 |
| FR-020 | T002, T042, T043, T050 |
| FR-021 | T031 |
| FR-022 | T007, T053 |
| FR-023 | T015, T024, T034, T039, T043, T044, T046 |

Scenarios: US1-1..6 → T029/T030; US2-1,2,3,6 → T046; US2-4 → T047, T052; US2-5 → T048, T052; US2-7 → T049;
US3-1..5,7 → T037; US3-6 → T037, T049, T052; US4-1,2,4,5 → T019/T020, T051; US4-3 → T041, T047, T051;
US5-1..3 → T013, T050.
