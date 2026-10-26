---
description: "Task list for 006 unity-sdk"
---

# Tasks: Unity Client SDK (P0 slice 006)

**Tests**: REQUIRED (ACC-04, ACC-20, ACC-34, ACC-35, ACC-43).

## Phase 1: Setup

- [ ] T001 Create `sdk/GameServer.Client` (netstandard2.1, C# 9, codegen into `sdk/unity/com.gameserver.client/Runtime/Generated`), `tests/GameServer.Client.Tests`; add to solution
- [ ] T002 [P] `GateHeaders` constants in `src/GameServer.Platform.Web/Middleware/GateMiddleware.cs`

## Phase 2: Foundational

- [ ] T003 Transport abstraction (`IHttpTransport`, `HttpClientTransport`), `IClientClock`, `IDelay`, `ISessionStore`, `IConfigCache` (in-memory defaults)
- [ ] T004 `GameServerError`, `GameServerException`, error mapping from `ErrorEnvelope`

## Phase 3: US1 + US2 — Session, sections, retry

- [ ] T005 [US1] `SectionStore` (rev, payload, apply update, mismatch detection) and server time offset
- [ ] T006 [US1] [US2] `GameServerClient`: login guest, logout, snapshot, command with cached bytes/requestId and retry policy
- [ ] T007 [US1] [US2] Tests: login/snapshot/time offset, command updates revs, lost response replay (one receipt), COMMIT_UNKNOWN_RETRY retry, retryAfter respected, non-retryable not retried, stale rev applied via full update, non-full mismatch refetch in `tests/GameServer.Client.Tests/`

## Phase 4: US3 — Session/account events

- [ ] T008 [US3] Events for SESSION_REPLACED (clear token, no re-login), ACCOUNT_SUSPENDED/DELETED, SWITCH_ACCOUNT, MAINTENANCE, UPDATE_REQUIRED
- [ ] T009 [US3] Tests: takeover by second device; gate decision via test `IGateEvaluator` → events

## Phase 5: US4 — Config

- [ ] T010 [US4] Track `configVersion`, `ConfigVersionChanged`, `RefreshConfigAsync` via GetConfig + cache
- [ ] T011 [US4] Tests: refresh downloads clientVisible files, second refresh not_modified, version change event

## Phase 6: US5 — Unity package

- [ ] T012 [US5] `UnityWebRequestTransport` (`#if UNITY_5_3_OR_NEWER`), asmdef, `link.xml`, `package.json`, `Plugins/Google.Protobuf.dll`, sample client in `Samples~/SampleGame`
- [ ] T013 [US5] Try Unity batch-mode compile of the package with the installed editor (record result; device/IL2CPP acceptance is manual)

## Phase 7: Polish

- [ ] T014 [P] PCR `docs/pcr/unity-sdk.md`, platform PCR (gate headers), README, quickstart notes for device acceptance
- [ ] T015 Full suite, AgentMemories, final report
