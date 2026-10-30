# PCR — economy

Module framework `economy` (slice 003, version 0.2.0 từ slice 004). Nguồn: ECO-01..08, OPS-09, GM-03.

## Aggregate

| Aggregate | Collection | Schema | Client-visible | State / wire |
| --- | --- | --- | --- | --- |
| `wallet` | `pa_wallet` | 1 | có | `balances: resource → int64` (`WalletState`) |
| `inventory` | `pa_inventory` | 1 | có | `items: itemId → int64` (`InventoryState`), chỉ stackable ở P0 |

Số dư 0 không được lưu (key bị xoá).

## Resource Catalog (`resources.json`, game sở hữu dữ liệu)

`{resources: [{id, kind: currency|item, maxQuantity, displayName}]}`; currency → `wallet`, item → `inventory`.

## Thao tác (semantic)

| Thao tác | Kiểm tra | Lỗi |
| --- | --- | --- |
| `EconomyOperations.Grant` | quantity > 0, id trong catalog, không tràn int64, ≤ `maxQuantity` | `INVALID_QUANTITY`, `UNKNOWN_ITEM`, `QUANTITY_LIMIT_EXCEEDED` |
| `EconomyOperations.Debit` | như trên, số dư ≥ quantity | `INSUFFICIENT_FUNDS` |
| `TryClaimExternalAsync` | claim `extref::{sourceType}::{externalReference}` trong `extref_claims` | `ALREADY_GRANTED` (do caller trả) |

Mọi thay đổi chạy trong transaction của command gọi (command phải khai `wallet`/`inventory` trong Touches); mỗi delta
sinh outbox `ledger` → projection `ledger::{entryId}` trong collection `ledger`
`{playerId, actorId, sourceType, resource, delta, balanceAfter, externalReference, orderId}` + `receiptId`, `committedAtUtc`.

## Error codes (11000–11099)

| Number | Name |
| --- | --- |
| 11001 | INSUFFICIENT_FUNDS |
| 11002 | UNKNOWN_ITEM |
| 11003 | QUANTITY_LIMIT_EXCEEDED |
| 11004 | INVALID_QUANTITY |
| 11005 | ALREADY_GRANTED |

## GM (slice 004 — GM-03, SEC-14)

Permission `resource.adjust` và `player.read` (cả hai khai trong catalog của module này; `player.read` dùng chung tên với
module gm nên Economy chạy được khi không đăng ký module gm). Index `ledger(playerId, committedAtUtc DESC, META().id DESC)`.

| Route | Permission | Command |
| --- | --- | --- |
| `POST /gm/v1/players/{playerId}/resources/adjust` `{resource, delta, reason}` | `resource.adjust` | `economy.adjust` |
| `GET /gm/v1/players/{playerId}/ledger?pageSize=&cursor=` | `player.read` | — (list `ledger`, mới nhất trước) |

`economy.adjust`: profile `Gm`, Touches `wallet`, `inventory`. `delta > 0` → Grant, `delta < 0` → Debit, `delta = 0` →
`INVALID_QUANTITY`; ledger `sourceType = gm_adjust`, `actorId` = staff; audit `{permission: resource.adjust, action:
economy.adjust, reason}` tham chiếu `receiptId` (không chép delta). Result `{balanceBefore, balanceAfter}`. Quy tắc
chung của command GM (reason, `X-Request-Id`, replay) ở PCR gm.

## Cheat (module `economy.cheat`, chỉ Development/Test)

`POST /api/v1/cheat/grant` profile `Dev`; command `cheat.grant` Touches `wallet`, `inventory`; ledger `sourceType = cheat`.

## Xoá tài khoản (slice 010 — PRIV-01, FR-019)

Deletion treatment: `ledger` = `Anonymize` (index `ix_ledger_player` phục vụ truy vấn của purge, sort theo player — PCR platform
mục 16), `extref_claims` = `Anonymize` (index `ix_extref_claims_player_ref` do platform tạo). Purge ghi đè mọi giá trị bằng player
id (`playerId`, `actorId` khi là player…) bằng anonymous id; số bản ghi, `delta`, `balanceAfter`, `externalReference`, `orderId`,
`receiptId`, thời gian giữ nguyên — tổng theo resource không đổi (SC-003). Ledger không bao giờ bị xoá (ngoại lệ ghi đè CMD-08,
PCR platform mục 16). `economy.adjust` trên player đang chờ xoá/đang purge → 404 (`IGmTargetValidator`).
