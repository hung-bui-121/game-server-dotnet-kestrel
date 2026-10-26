---
description: "Task list for 004 gm"
---

# Tasks: GM backend + minimal frontend (P0 slice 004)

**Tests**: REQUIRED (ACC-09, ACC-10, ACC-24, ACC-27, ACC-45).

## Phase 1: Setup

- [X] T001 Create `src/GameServer.Modules.Gm`, `src/GameServer.Modules.Gm.Adapters` (embedded `ui/index.html`), `tests/GameServer.Gm.Tests`; add to solution and SampleHost

## Phase 2: Foundational

- [X] T002 [P] `AuditEntry` + `AuditExtensions.AddAudit(ctx, permission, action, target, reason, before?, after?)` (outbox kind `gm_audit`) in `src/GameServer.Core/Commands/Audit.cs`
- [X] T003 [P] `IPagedQueryStore` / `PagedQuery` / `Page` (keyset cursor) in `src/GameServer.Platform/Persistence/PagedQuery.cs`; in-memory impl; Couchbase N1QL impl
- [X] T004 `MapGmCommand` (JSON body, `{playerId}` target, `X-Request-Id`, permission, JSON response) in `src/GameServer.Platform.Web/Commands/GmCommandEndpointFactory.cs`
- [X] T005 Validator: GM endpoints need a permission from the module catalog (SEC-08) in `src/GameServer.Platform.Web/Validation/EndpointSecurityValidator.cs`
- [X] T006 [P] Unit tests: paged query (in-memory) incl. cursor; conformance for Couchbase in `tests/GameServer.Platform.Tests/Persistence/`

## Phase 3: US1 — GM auth, permission, scope (P1)

- [X] T007 [P] [US1] GM domain: `StaffRecord`, `StaffGrant`, `GmRoles` (templates → permissions), `GmPermissions`, `DevPasswordHasher` (PBKDF2) in `src/GameServer.Modules.Gm/`
- [X] T008 [US1] `GmSessionService`, `StaffDirectory` (cache 60 s), `GmCookieAuthenticationHandler` (scope filter, disabled, cross-site mutation) in `src/GameServer.Modules.Gm.Adapters/`
- [X] T009 [US1] `GmModule` (descriptor: permissions, collections `gm_staff`, `gm_sessions`, `gm_audit`, audit projection, public `/gm/v1/auth/login`, `/gm/v1/ui`), Development bootstrap from `Gm:Bootstrap:Staff`, login/logout/me endpoints
- [X] T010 [US1] Tests: login/logout/me, wrong password 401, missing permission 403, other game/env scope 403, disabled staff 401 after cache, player token on GM 401 / GM cookie on Player 401, cross-site mutation 403 in `tests/GameServer.Gm.Tests/GmAuthTests.cs`

## Phase 4: US2 — Lookup (P1)

- [X] T011 [US2] Lookup endpoints (by id, by mapping, search display name), ledger/receipts paged, receipt by id; PII gating in `src/GameServer.Modules.Gm.Adapters/GmPlayerEndpoints.cs`
- [X] T012 [US2] Tests: lookup aggregates rev/size, PII gating, paging validation 400, cursor walk in `tests/GameServer.Gm.Tests/GmLookupTests.cs`

## Phase 5: US3 — Adjust + audit (P1)

- [X] T013 [US3] `economy.adjust` command (Touches wallet, inventory; Gm profile; `resource.adjust`; ledger `gm_adjust`; audit referencing receipt) + endpoint `/gm/v1/players/{playerId}/resources/adjust`
- [X] T014 [US3] `progression.set` command (SampleGame; `progression.edit`; audit before/after) + endpoint `/gm/v1/players/{playerId}/progression`
- [X] T015 [US3] Tests: adjust ok + audit 7 fields + ledger, missing reason 400, unknown item, insufficient, replay, progression edit audit, audit via sweeper when relay disabled in `tests/GameServer.Gm.Tests/GmAdjustTests.cs`

## Phase 6: US4 — UI

- [X] T016 [US4] Static page `ui/index.html` (login, lookup, aggregates, adjust) served at `/gm/v1/ui`; test that it loads

## Phase 7: Polish

- [X] T017 [P] PCR `docs/pcr/gm.md`; update platform/economy/sample-game PCR; arch tests include GM assemblies
- [X] T018 Full suite, quickstart log, AgentMemories
