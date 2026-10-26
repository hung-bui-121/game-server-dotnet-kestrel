# PCR — economy

Module framework `economy` (slice 003, version 0.1.0). Nguồn: ECO-01..08, OPS-09.

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

## Cheat (module `economy.cheat`, chỉ Development/Test)

`POST /api/v1/cheat/grant` profile `Dev`; command `cheat.grant` Touches `wallet`, `inventory`; ledger `sourceType = cheat`.
