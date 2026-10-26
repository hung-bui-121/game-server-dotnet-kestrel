---
description: "Task list for 003 sample-game"
---

# Tasks: SampleGame, Economy, Config, IAP (P0 slice 003)

**Tests**: REQUIRED (ACC-06, ACC-07, ACC-21, ACC-32, ACC-35, ACC-36, ACC-42, ACC-46, ACC-47, ACC-53).

## Phase 1: Setup

- [X] T001 Create projects `src/GameServer.Modules.Economy`, `src/GameServer.Modules.Economy.Adapters`, `src/GameServer.Modules.Iap`, `src/GameServer.Modules.Iap.Adapters`, `samples/SampleGame/SampleGame`, `samples/SampleGame/SampleGame.Adapters`, `tests/SampleGame.Tests`; add to `GameServer.slnx`
- [X] T002 [P] Add protos `economy.proto`, `iap.proto`, `sample_game.proto`; add `GetConfigRequest/GetConfigResponse/ConfigFile` to `proto/gameserver/core/v1/core.proto`
- [X] T003 [P] Add config files `resources.json`, `shop.json`, `battle.json`, `iap.json` to `samples/SampleHost/config/base` and `tests/Shared/config/base`; list them in manifests (clientVisible: resources, shop, battle)

## Phase 2: Foundational (platform)

- [X] T004 Add `CommandOutcome<T>.Commit(result, code)` and `ICommandContext.NewId(purpose)` / `NewSeed(purpose)` in `src/GameServer.Core/Commands/ICommandHandler.cs`; implement in `CommandContext` and `CommandRunner` (receipt `resultCode`, replay code)
- [X] T005 Implement `GetConfig` core endpoint (`POST /api/v1/config/get`, Player, `Limits:ConfigBundleBytes` = 1 MiB → 413) in `src/GameServer.Platform.Web/Endpoints/CoreEndpoints.cs`
- [X] T006 [P] Unit tests for Commit-with-code (receipt resultCode, replay) and NewId stability across attempts — covered at HTTP level by `BattleTests` (BATTLE_EXPIRED commit + replay, start retry returns the same battleId/seed)

## Phase 3: US1 — Economy + shop (P1)

- [X] T007 [P] [US1] Economy domain: `WalletState`, `InventoryState`, `ResourceCatalog` (`resources.json`), `EconomyErrors` (11000–11099), `Economy.Debit/Grant/TryClaimExternal` with ledger outbox in `src/GameServer.Modules.Economy/`
- [X] T008 [US1] Economy adapters: `EconomyModule` (aggregates, ledger projection `ledger`, collection `extref_claims`, wire mappers, catalog DI) in `src/GameServer.Modules.Economy.Adapters/EconomyModule.cs`
- [X] T009 [US1] Cheat module `CheatModule` (`POST /api/v1/cheat/grant`, Dev profile, `sourceType = cheat`) in `src/GameServer.Modules.Economy.Adapters/CheatModule.cs`
- [X] T010 [US1] SampleGame shop: `ShopCatalog` (`shop.json`), `BuyItemHandler` (`shop.buy`) in `samples/SampleGame/SampleGame/`
- [X] T011 [US1] Unit tests: buy ok/insufficient/unknown item/invalid quantity/overflow/maxQuantity; ledger sums in `tests/SampleGame.Tests/ShopTests.cs`

## Phase 4: US2 — Battles (P1)

- [X] T012 [P] [US2] SampleGame battle domain: `ProgressionState`, `BattleState`, `BattleCatalog` (`battle.json`), `IBattleStartPolicy` (Replace/Reject), `IBattleResultPolicy`, `StartBattleHandler`, `EndBattleHandler`, `SampleGameErrors` (40000–40099) in `samples/SampleGame/SampleGame/`
- [X] T013 [US2] SampleGame adapters: `SampleGameModule` (aggregates, commands, endpoints `/api/v1/battle/start`, `/api/v1/battle/end`, `/api/v1/shop/buy`, wire mappers) in `samples/SampleGame/SampleGame.Adapters/SampleGameModule.cs`
- [X] T014 [US2] Unit tests: start/locked/replace/reject/end win/end wrong id/expired/ACC-42/ambiguous EndBattle in `tests/SampleGame.Tests/BattleTests.cs`

## Phase 5: US3 — IAP (P1)

- [X] T015 [P] [US3] IAP domain: `VerifiedPurchase`, `IStoreReceiptVerifier`, `IapCatalog` (`iap.json`), `IapPurchaseHandler` (`iap.purchase`, claim `extref::iap_{store}::{ref}`), `IapErrors` (12000–12099) in `src/GameServer.Modules.Iap/`
- [X] T016 [US3] IAP adapters: `IapModule` (endpoint verifies before runner, 503 on store outage), `FakeStoreReceiptVerifier` in `src/GameServer.Modules.Iap.Adapters/`
- [X] T017 [US3] `AppleJwsReceiptVerifier` (x5c chain to configured root, ES256, bundleId/environment/productId) in `src/GameServer.Modules.Iap.Adapters/AppleJwsReceiptVerifier.cs`
- [X] T018 [US3] `GooglePlayReceiptVerifier` (service-account JWT → token, purchases.products.get, purchaseState) in `src/GameServer.Modules.Iap.Adapters/GooglePlayReceiptVerifier.cs`
- [X] T019 [US3] Tests: grant once, ALREADY_GRANTED, INVALID_RECEIPT, unknown product, store outage 503; Apple verifier with self-signed chain; Google verifier with fake HTTP handler in `tests/SampleGame.Tests/IapTests.cs`

## Phase 6: US4/US5 — Config, cheat, end-to-end

- [X] T020 [US4] HTTP tests: GetConfig full / not_modified / 413 / receipt configVersion in `tests/SampleGame.Tests/ConfigTests.cs`
- [X] T021 [US5] HTTP tests: cheat grant + ledger cheat; cheat module in Production fails startup in `tests/SampleGame.Tests/CheatTests.cs`
- [X] T022 HTTP end-to-end ACC-53 + ACC-06 on two hosts sharing store (login → start → end → buy → iap) in `tests/SampleGame.Tests/SampleGameEndToEndTests.cs`
- [X] T023 Register modules in `samples/SampleHost/Program.cs` (Identity, Economy, Iap, SampleGame, Counter; Cheat only in Development/Test)

## Phase 7: Polish

- [X] T024 [P] Arch tests include new assemblies (business projects independent of Platform/HTTP/Protobuf) in `tests/GameServer.Architecture.Tests/ArchitectureTests.cs`
- [X] T025 [P] PCR `docs/pcr/economy.md`, `docs/pcr/iap.md`, `docs/pcr/sample-game.md`; update `docs/pcr/platform.md` (Commit-with-code, NewId, GetConfig)
- [X] T026 Full suite, quickstart log, AgentMemories
