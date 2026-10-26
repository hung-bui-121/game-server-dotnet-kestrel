---
description: "Task list for 002 identity-session"
---

# Tasks: Identity & Session — guest (P0 slice 002)

**Input**: `specs/002-identity-session/` (plan.md, spec.md, research.md, data-model.md, contracts/)

**Tests**: REQUIRED (constitution VI; ACC-01, ACC-02, ACC-09, ACC-20, ACC-52).

## Phase 1: Setup

- [ ] T001 Copy `specs/002-identity-session/contracts/identity.proto` over `src/GameServer.Modules.Identity.Adapters/Protos/identity.proto` and add `AdditionalImportDirs="../../proto"` to its `<Protobuf>` item in `src/GameServer.Modules.Identity.Adapters/GameServer.Modules.Identity.Adapters.csproj`
- [ ] T002 Add `map<string, string> details = 9;` to `ErrorEnvelope` in `proto/gameserver/core/v1/core.proto` (new field only)

## Phase 2: Foundational (platform additions)

- [ ] T003 [P] Add core codes 16 `ACCOUNT_SUSPENDED` (403), 17 `ACCOUNT_DELETED` (401), 18 `SESSION_REPLACED` (401), 19 `IDENTITY_CONFLICT` (409), 20 `SWITCH_ACCOUNT` (409) to `src/GameServer.Core/Errors/CoreErrorCodes.cs`; add `IReadOnlyDictionary<string,string>? Details` to `GameServerException`; write details in `ErrorResponseWriter` (protobuf `details`, Problem Details `details`)
- [ ] T004 [P] Add `PublicEndpoints` to `ModuleDescriptor` in `src/GameServer.Core/Modules/ModuleDescriptor.cs`; validator allowlist = options ∪ module declarations in `src/GameServer.Platform.Web/Validation/EndpointSecurityValidator.cs`
- [ ] T005 [P] Implement `IKeyValueStore` (Get, Insert(expiry) → bool, Replace(CAS, expiry) → bool, Remove; reject `pa_*`/`receipts`) in `src/GameServer.Platform/Persistence/KeyValueStore.cs`
- [ ] T006 Implement `IKeyValueStore` in `InMemoryDocumentStore` with clock-aware expiry (ctor `IClock? clock`) in `src/GameServer.Platform.Testing/InMemoryDocumentStore.cs`; register both interfaces in `AddInMemoryDocumentStore`
- [ ] T007 Implement `IKeyValueStore` in `CouchbaseDocumentStore` (KV Insert/Replace(cas)/Remove, Majority, Expiry) in `src/GameServer.Platform.Couchbase/CouchbaseDocumentStore.cs`; register in `AddCouchbaseDocumentStore`
- [ ] T008 [P] Implement `IPlayerAggregateWriter.UpdateAsync<TState>(playerId, aggregateId, mutate, ct)` (one transaction, rev++, size budget, no receipt) in `src/GameServer.Platform/Aggregates/PlayerAggregateWriter.cs`
- [ ] T009 [P] Implement `ExpiringCache<TKey,TValue>` (IClock-based TTL, Remove) in `src/GameServer.Platform/Caching/ExpiringCache.cs`
- [ ] T010 [P] Implement `FixedWindowCounter` (per key, window, limit, IClock; `IsOver`, `Increment`) in `src/GameServer.Platform/Limits/FixedWindowCounter.cs`
- [ ] T011 Implement `ICommandGuard` and call all guards in `CommandRunner.ExecuteAsync` after the per-player limiter in `src/GameServer.Platform/Commands/ICommandGuard.cs`
- [ ] T012 Implement `ActorSchemeOptions` (PlayerSchemes, GmSchemes) and profile-based authentication in `ProfileEnforcementMiddleware` (remove `UseAuthentication`; failure code from `HttpContext.Items["gs.auth.error"]`); test scheme registers into both lists in `src/GameServer.Platform.Web/Auth/ActorSchemeOptions.cs`, `src/GameServer.Platform.Web/Middleware/ProfileEnforcementMiddleware.cs`, `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs`
- [ ] T013 Add `GateMetadata` (gate applies to Player endpoints and endpoints marked `.WithGate()`) in `src/GameServer.Platform.Web/Middleware/GateMiddleware.cs`, `src/GameServer.Platform.Web/Routing/ApiModules.cs`
- [ ] T014 [P] Unit tests for `IKeyValueStore` (in-memory: CAS, expiry, reserved collections), `PlayerAggregateWriter`, `ExpiringCache`, `FixedWindowCounter`, command guard in `tests/GameServer.Platform.Tests/Identity/PlatformAdditionsTests.cs`

## Phase 3: User Story 1 — Guest login (P1) 🎯

- [ ] T015 [P] [US1] Extend `ProfileState` with `Ban` (`{Reason, UntilUtc, Scope: "login" | string[], Actor}`), `Deleted` (`{RequestedAtUtc, PurgeAfterUtc}`), `Identities` in `src/GameServer.Modules.Identity/ProfileState.cs`
- [ ] T016 [P] [US1] Implement `GuestCredentials` (validate localId `^[A-Za-z0-9_-]{1,64}$`, secret ≥ 32 bytes, `Hash(pepper, secret)` HMAC-SHA256 hex, `Verify` fixed-time) and `PlayerIds.New()` (`p_` + 26 base32) in `src/GameServer.Modules.Identity/GuestCredentials.cs`
- [ ] T017 [P] [US1] Implement `IdentityOptions` (GuestPepper, CacheSeconds 60, SessionSlidingDays 30, SessionAbsoluteDays 90, TouchAfterHours 24, login limits 10/60/600) in `src/GameServer.Modules.Identity.Adapters/IdentityOptions.cs`
- [ ] T018 [US1] Implement `SessionService` (create token + document with expiry, validate (cache), touch, remove) in `src/GameServer.Modules.Identity.Adapters/SessionService.cs`
- [ ] T019 [US1] Implement `GuestLoginService` (rate limits → mapping insert-or-verify → profile checks deleted/ban → epoch++ via writer → session → snapshot) in `src/GameServer.Modules.Identity.Adapters/GuestLoginService.cs`
- [ ] T020 [US1] Map `POST /api/v1/identity/login/guest` (Public, protobuf, gated, module PublicEndpoints) and `POST /api/v1/identity/logout` (Player) in `src/GameServer.Modules.Identity.Adapters/IdentityModule.cs`
- [ ] T021 [US1] HTTP tests: new guest, re-login same player, wrong secret 401, concurrent first login one player, logout, payload identity ignored, validation 400, login response fields in `tests/GameServer.Platform.Web.Tests/IdentityLoginTests.cs`

## Phase 4: User Story 2 — Single session takeover (P1)

- [ ] T022 [US2] Implement `PlayerBearerAuthenticationHandler` (Bearer → session → profile checks in IDN-10 order, actor claims, lazy touch) registered into `PlayerSchemes` in `src/GameServer.Modules.Identity.Adapters/PlayerBearerAuthenticationHandler.cs`
- [ ] T023 [US2] HTTP tests: takeover → `SESSION_REPLACED`; two hosts sharing one store with 60 s cache (FakeClock) → old token rejected on both within 60 s; sliding/absolute expiry; lazy touch in `tests/GameServer.Platform.Web.Tests/SessionTests.cs`

## Phase 5: User Story 3 — Account state order (P1)

- [ ] T024 [US3] Implement `BanCommandGuard : ICommandGuard` (group = operation prefix, cached profile) in `src/GameServer.Modules.Identity.Adapters/BanCommandGuard.cs`
- [ ] T025 [US3] HTTP tests: ban login (login + token, `until` in details, priority over SESSION_REPLACED), expired ban, ban group `counter` blocks only counter, deleted → ACCOUNT_DELETED in `tests/GameServer.Platform.Web.Tests/AccountStateTests.cs`

## Phase 6: User Story 4 — Rate limit and schemes (P2)

- [ ] T026 [US4] HTTP tests: 11th login/min per localId 429; 61st failure/min per IP 429; 100 distinct localIds same IP not blocked + warning metric; player token on GM 401, GM on Player 401 in `tests/GameServer.Platform.Web.Tests/LoginLimitAndSchemeTests.cs`

## Phase 7: Polish

- [ ] T027 [P] Update arch tests: modules may use `IKeyValueStore` but not `ITransactionalDocumentStore`/`ITransactionContext` in `tests/GameServer.Architecture.Tests/ArchitectureTests.cs`
- [ ] T028 [P] Write `docs/pcr/identity.md` (aggregate profile fields, collections, session rules, error order, limits, public endpoint) and update `docs/pcr/platform.md` (new codes, details field, IKeyValueStore, guards, auth per profile)
- [ ] T029 Update `samples/SampleHost` (pepper from env/dev default), README, AgentMemories; run full suite and record in quickstart

## Dependencies

Phase 1 → 2 → US1 → US2 → US3 → US4 → Polish. T012 changes middleware used by all HTTP tests (run the 001 suite after it).
