# Feature Specification: SampleGame commands, Economy, Config, IAP (P0 slice 003)

**Feature Branch**: `003-sample-game`

**Created**: 2026-10-26

**Status**: Draft

**Input**: "003 SampleGame commands — economy (wallet/inventory, resource catalog, ledger), GetConfig, IAP verify +
grant idempotent (verifier giả + adapter Google/Apple), StartBattle/EndBattle/BuyVirtualItem/IAPurchase, cheat Dev.
Nguồn: Requirements v1.2 rev 4 (SMP-01..05, ECO-01..08, CFG-01..03, IAP-01/02, PLY-04/05, 8.4, OPS-09)."

Phụ thuộc 001 (runner, receipt, outbox) và 002 (login, session). GM adjust (GM-03) thuộc slice 004.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Mua vật phẩm bằng tiền ảo an toàn (Priority: P1)

Player mua một gói trong shop: server lấy giá và phần thưởng từ catalog config, trừ tiền và cộng vật phẩm trong
cùng một lần commit, ghi hai dòng ledger (trừ, cộng) cùng receipt. Thiếu tiền → `INSUFFICIENT_FUNDS`, không đổi gì.

**Why this priority**: Đường đi chuẩn của mọi thay đổi resource (ECO-01..07, SMP-05, ACC-06, ACC-21, ACC-36).

**Independent Test**: Cấp tiền bằng cheat (Development), mua 1 gói, kiểm wallet/inventory/ledger; hai lần mua đồng
thời khi chỉ đủ tiền một lần → một thành công, một `INSUFFICIENT_FUNDS`.

**Acceptance Scenarios**:

1. **Given** ví có 100 gold, giá gói 60 gold → 1 potion, **When** mua 1, **Then** ví 40, inventory potion = 1, hai ledger
   (gold −60 balanceAfter 40, potion +1 balanceAfter 1) cùng `receiptId` (SMP-05, ECO-06).
2. **Given** ví có 60 gold, **When** hai lệnh mua khác `requestId` chạy đồng thời trên hai instance, **Then** một thành công,
   một `INSUFFICIENT_FUNDS`; ledger khớp số dư (ACC-06).
3. **Given** shop item trỏ tới `itemId` không có trong Resource Catalog hoặc số lượng ≤ 0, **When** mua, **Then**
   `UNKNOWN_ITEM` / `INVALID_QUANTITY`, không đổi state, không ledger (ECO-07, ACC-21, ACC-36).
4. **Given** inventory đã gần `maxQuantity` hoặc phép nhân giá tràn int64, **When** mua, **Then**
   `QUANTITY_LIMIT_EXCEEDED`, không đổi (ECO-02, ACC-21).

---

### User Story 2 - Bắt đầu và kết thúc trận (Priority: P1)

Player bắt đầu trận ở một stage đã mở; server cấp `battleId`, seed, hạn 24 h và chụp lại bảng thưởng tại thời
điểm bắt đầu. Kết thúc trận thắng → cộng XP, gold, vật phẩm rơi, mở stage kế tiếp và xoá trận đang mở, tất cả
trong một commit. Chỉ một trận mở mỗi lúc.

**Why this priority**: Command gameplay mẫu chạm nhiều aggregate (8.4, SMP-02, SMP-03, ACC-53, ACC-07, ACC-42).

**Independent Test**: start → end thắng → kiểm 3–4 aggregate đổi cùng lúc; các nhánh lỗi theo ACC-53.

**Acceptance Scenarios**:

1. **Given** stage 1 mở, **When** StartBattle(stage 1), **Then** `battle.activeBattle` có `battleId`, `seed`, `expiresAt = now + 24h`,
   phần thưởng chụp lại; response trả `battleId` (SMP-02).
2. **Given** stage 3 chưa mở (progression.stage = 1), **When** StartBattle(3), **Then** `STAGE_LOCKED`, không đổi.
3. **Given** đang có trận chưa hết hạn, **When** StartBattle, **Then** policy mặc định Replace: trận cũ `stats.abandoned++`,
   trận mới thay thế; game có thể thay policy Reject → `BATTLE_IN_PROGRESS` (TBD-02).
4. **Given** trận đang mở, **When** EndBattle(battleId đúng, thắng), **Then** `battle`, `progression`, `wallet` (+ `inventory` nếu
   có drop) đổi trong một commit, có ledger, `activeBattle` bị xoá; response có `SectionUpdate` cho từng aggregate (SMP-03, ACC-53).
5. **Given** `battleId` sai hoặc không có trận, **When** EndBattle, **Then** `NO_ACTIVE_BATTLE`, không đổi.
6. **Given** đã quá `expiresAt`, **When** EndBattle, **Then** `BATTLE_EXPIRED` và `activeBattle` bị xoá (commit, không thưởng).
7. **Given** EndBattle đã thưởng, **When** retry cùng `requestId` sau 24 h, **Then** xử lý như mới và bị `NO_ACTIVE_BATTLE` (ACC-42).
8. **Given** commit của EndBattle báo ambiguous, **When** retry, **Then** hoặc đủ cả 3 aggregate + receipt hoặc không gì (ACC-07).

---

### User Story 3 - Mua trong ứng dụng (IAP) cấp quà đúng một lần (Priority: P1)

Client gửi biên nhận của store; server xác minh với store (trước transaction), rồi cấp quà theo catalog `iap` đúng
một lần cho mỗi `externalReference` (Google `purchaseToken`, Apple `transactionId`). Gửi lại biên nhận đã dùng →
`ALREADY_GRANTED`; biên nhận giả/sai môi trường → `INVALID_RECEIPT`.

**Why this priority**: IAP-01/02, ECO-08, SMP-04, ACC-46, ACC-47 (verifier giả ở P0).

**Independent Test**: verifier giả trả hợp lệ/đã dùng/giả; kiểm grant một lần, claim marker, ledger `sourceType = iap`.

**Acceptance Scenarios**:

1. **Given** biên nhận hợp lệ cho `gems_100`, **When** IAPurchase, **Then** ví gem +100, ledger `sourceType = iap` có `orderId`,
   claim `extref::iap_google::{token}` (IAP-02).
2. **Given** cùng biên nhận đã cấp, **When** IAPurchase lần nữa (requestId khác), **Then** `ALREADY_GRANTED`, một ledger (ACC-46).
3. **Given** biên nhận không hợp lệ hoặc sandbox khi deployment Production, **When** IAPurchase, **Then** `INVALID_RECEIPT` (ACC-47).
4. **Given** sản phẩm không có trong catalog `iap`, **When** IAPurchase, **Then** `INVALID_RECEIPT`.

---

### User Story 4 - Lấy game config theo version (Priority: P2)

Login trả `configVersion`; client gọi `GetConfig(configVersion?)` để lấy bundle Protobuf gồm các file
`clientVisible` đã overlay, và cache theo version. Gửi version đang có → không trả lại nội dung.

**Why this priority**: CFG-03, ACC-35.

**Acceptance Scenarios**:

1. **Given** config `sample-1`, **When** GetConfig không kèm version, **Then** trả `configVersion` và chỉ các file `clientVisible`.
2. **Given** client đã có đúng version, **When** GetConfig(version đó), **Then** `not_modified = true`, không file.
3. **Given** bundle vượt 1 MiB, **When** GetConfig, **Then** 413 `PAYLOAD_TOO_LARGE`.
4. **Given** command thành công, **When** xem receipt, **Then** có `configVersion` của instance xử lý (CFG-02).

---

### User Story 5 - Cheat ở Development (Priority: P3)

Ở Development, endpoint profile `Dev` cấp resource để test; mọi cheat vẫn ghi ledger `sourceType = cheat`. Ở
Production module cheat không được đăng ký (startup fail).

**Acceptance Scenarios**:

1. **Given** Development, **When** cheat grant 100 gold, **Then** ví +100, ledger `sourceType = cheat` (OPS-09, ACC-32).
2. **Given** Production, **When** host đăng ký module cheat, **Then** startup fail (ACC-32).

### Edge Cases

- Retry cùng `requestId` của BuyVirtualItem/IAPurchase → replay, không trừ/cấp lần hai.
- Verifier store lỗi mạng/timeout → 503 retryable, không ghi gì (verify chạy trước transaction).
- Config đổi giữa StartBattle và EndBattle → thưởng theo bảng đã chụp lúc start (PLY-05, CFG-02).
- `battleId`/seed phải giống nhau giữa các lần SDK retry attempt (sinh xác định từ receipt).
- Cộng/trừ làm tràn int64 hoặc âm → từ chối, không đổi state.

## Requirements *(mandatory)*

### Functional Requirements

**Economy (framework module `economy`)**

- **FR-001**: Aggregate `wallet {balances: resourceType → int64}` và `inventory {items: itemId → int64}`, client-visible (DAT-09).
- **FR-002**: Resource Catalog từ file config `resources.json`: `{id, kind: currency|item, maxQuantity, displayName}`; id lạ →
  `UNKNOWN_ITEM` (ECO-07). P0 chỉ stackable.
- **FR-003**: Thư viện thao tác Economy trên `ICommandContext`: `Debit`, `Grant` kiểm kiểu/âm/tràn/`maxQuantity`, cập nhật
  aggregate và thêm outbox `ledger` `{entryId, playerId, sourceType, actorId, resource, delta, balanceAfter, receiptId,
  externalReference?, orderId?, committedAtUtc}` (ECO-02, ECO-06).
- **FR-004**: Ledger projection collection `ledger`, key `ledger::{entryId}` (CMD-09).
- **FR-005**: Grant từ nguồn ngoài dùng claim marker `extref::{sourceType}::{externalReference}` trong `extref_claims`;
  tồn tại → `ALREADY_GRANTED` (ECO-08).
- **FR-006**: Mã lỗi Economy (11000–11099): `INSUFFICIENT_FUNDS`, `UNKNOWN_ITEM`, `QUANTITY_LIMIT_EXCEEDED`,
  `INVALID_QUANTITY`, `ALREADY_GRANTED`.
- **FR-007**: Cheat `POST /api/v1/cheat/grant` profile `Dev` (module `economy.cheat`, chỉ đăng ký ở Development/Test),
  ledger `sourceType = cheat` (OPS-09).

**Config**

- **FR-010**: `POST /api/v1/config/get` (Player) nhận `config_version` tuỳ chọn, trả `GetConfigResponse{config_version,
  not_modified, files[{name, content}]}` chỉ gồm file `clientVisible`; bundle > 1 MiB → 413 (CFG-03, API-06).

**IAP (framework module `iap`)**

- **FR-020**: `IStoreReceiptVerifier` (theo store `google`/`apple`) chạy **trước** transaction trong adapter, trả
  `{valid, externalReference, productId, orderId, environment}`; lỗi tạm thời → 503 retryable (IAP-01).
- **FR-021**: Catalog `iap.json`: `"{store}:{productId}" → grants[{resource, quantity}]` (CFG-01).
- **FR-022**: Command `iap.purchase` (Touches `wallet`, `inventory`; 1 claim marker) cấp quà một lần theo
  `externalReference`; receipt invalid/không có trong catalog/môi trường sai → `INVALID_RECEIPT` (IAP-02).
- **FR-023**: Verifier giả cho Development/CI (cấu hình: token → valid/invalid). Adapter thật: Apple JWS (StoreKit 2,
  x5c tới Apple Root CA G3, ES256, `bundleId`, `environment`, `productId`) và Google Play Developer API
  (`purchases.products.get`, `purchaseState = 0`) — [VERIFY] V20/V21, kiểm thực tế ở P0.5.

**SampleGame (module game `sample.game`)**

- **FR-030**: Aggregate `progression {level, xp, stage}`, `battle {activeBattle?, stats{started, won, lost, abandoned, expired}}`.
- **FR-031**: `battle.start(stage)` Touches `battle`, Reads `progression`; stage phải ≤ `progression.stage`
  (`STAGE_LOCKED`); policy `IBattleStartPolicy` (mặc định Replace, Reject → `BATTLE_IN_PROGRESS`); `battleId`, `seed`
  sinh xác định; `expiresAt = now + 24h`; chụp bảng thưởng stage (SMP-02, PLY-05).
- **FR-032**: `battle.end(battleId, won)` Touches `battle`, `progression`, `wallet`, `inventory`; `NO_ACTIVE_BATTLE`;
  hết hạn → commit xoá trận + `BATTLE_EXPIRED`; thắng → thưởng theo bảng đã chụp qua Economy (`sourceType = battle`),
  `xp`, mở stage kế; `IBattleResultPolicy` thay được (SMP-03, eligibility-only).
- **FR-033**: `shop.buy(shopItemId, quantity)` Touches `wallet`, `inventory`; giá/grant từ `shop.json` (SMP-05).
- **FR-034**: Mã lỗi SampleGame (40000–40099): `STAGE_LOCKED`, `BATTLE_IN_PROGRESS`, `NO_ACTIVE_BATTLE`, `BATTLE_EXPIRED`,
  `UNKNOWN_SHOP_ITEM`.

**Platform**

- **FR-040**: `CommandOutcome.Commit(result, code)` — commit thay đổi nhưng trả mã nghiệp vụ (vd `BATTLE_EXPIRED`); receipt
  ghi `resultCode` đó, replay trả lại đúng mã.
- **FR-041**: `ICommandContext.NewId(purpose)` — id xác định theo receipt (ổn định qua các attempt và retry).

### Key Entities

Wallet, Inventory, Resource Catalog entry, Ledger entry, Claim marker `extref`, Progression, Battle (activeBattle,
stats), Shop item, IAP product, Verified purchase, Config bundle.

## Success Criteria *(mandatory)*

- **SC-001**: 0 trường hợp trừ tiền hai lần cho một số dư trong test đồng thời (ACC-06).
- **SC-002**: Tổng ledger theo resource luôn bằng số dư sau bộ test (ECO-06).
- **SC-003**: 100% biên nhận IAP lặp lại bị `ALREADY_GRANTED` với đúng một ledger (ACC-46).
- **SC-004**: 5 command SampleGame (Login, StartBattle, EndBattle, IAPurchase, BuyVirtualItem) chạy end-to-end trên hai
  instance dùng chung store (ACC-53).

## Assumptions

- Evidence trận = eligibility-only (G7): client báo thắng/thua, server chỉ kiểm điều kiện và thưởng theo bảng.
- Lịch sử config không lưu: bảng thưởng được chụp vào `activeBattle` lúc start để EndBattle không phụ thuộc config sau đó.
- Adapter store thật (Apple/Google) được viết và unit test bằng dữ liệu tự ký / HTTP giả; xác minh với store thật (sandbox) là P0.5 thủ công.
- Offset `IClock` theo player ở Dev (OPS-09) để sau; test dùng `FakeClock`.
