---
description: "Task list for 006 unity-sdk"
---

# Tasks: Unity Client SDK (P0 slice 006)

**Tests**: REQUIRED (ACC-04, ACC-20, ACC-34, ACC-35, ACC-43).

## Phase 1: Setup

- [X] T001 Create `sdk/GameServer.Client` (netstandard2.1, C# 9, codegen into `sdk/unity/com.gameserver.client/Runtime/Generated`), `tests/GameServer.Client.Tests`; add to solution
- [X] T002 [P] `GateHeaders` constants in `src/GameServer.Platform.Web/Middleware/GateMiddleware.cs`

## Phase 2: Foundational

- [X] T003 Transport abstraction (`IHttpTransport`, `HttpClientTransport`), `IClientClock`, `IDelay`, `ISessionStore`, `IConfigCache` (in-memory defaults)
- [X] T004 `GameServerError`, `GameServerException`, error mapping from `ErrorEnvelope`

## Phase 3: US1 + US2 — Session, sections, retry

- [X] T005 [US1] `SectionStore` (rev, payload, apply update, mismatch detection) and server time offset
- [X] T006 [US1] [US2] `GameServerClient`: login guest, logout, snapshot, command with cached bytes/requestId and retry policy
- [X] T007 [US1] [US2] Tests: login/snapshot/time offset, command updates revs, lost response replay (one receipt), COMMIT_UNKNOWN_RETRY retry, retryAfter respected, non-retryable not retried, stale rev applied via full update, non-full mismatch refetch in `tests/GameServer.Client.Tests/`

## Phase 4: US3 — Session/account events

- [X] T008 [US3] Events for SESSION_REPLACED (clear token, no re-login), ACCOUNT_SUSPENDED/DELETED, SWITCH_ACCOUNT, MAINTENANCE, UPDATE_REQUIRED
- [X] T009 [US3] Tests: takeover by second device; gate decision via test `IGateEvaluator` → events

## Phase 5: US4 — Config

- [X] T010 [US4] Track `configVersion`, `ConfigVersionChanged`, `RefreshConfigAsync` via GetConfig + cache
- [X] T011 [US4] Tests: refresh downloads clientVisible files, second refresh not_modified, version change event

## Phase 6: US5 — Unity package

- [X] T012 [US5] `UnityWebRequestTransport` (`#if UNITY_5_3_OR_NEWER`), asmdef, `link.xml`, `package.json`, `Plugins/Google.Protobuf.dll`, sample client in `samples/SampleGame/KestrelDemo` (Unity 6, per owner request)
- [X] T013 [US5] Try Unity batch-mode compile of the package with the installed editor (record result; device/IL2CPP acceptance is manual)

## Phase 7: Polish

- [X] T014 [P] PCR `docs/pcr/unity-sdk.md`, platform PCR (gate headers), README, quickstart notes for device acceptance
- [X] T015 Full suite, AgentMemories, final report
