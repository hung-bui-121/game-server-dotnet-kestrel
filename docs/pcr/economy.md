# PCR — economy

Module framework `economy` (slice 003, version 0.2.0 từ slice 004). Nguồn: ECO-01..08, OPS-09, GM-03.

Đúng với framework v0.7.0 (chưa phát hành).

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
| `TryClaimExternalAsync` | claim `ExternalClaimKey(sourceType, externalReference)` trong `extref_claims` (dưới) | `ALREADY_GRANTED` (do caller trả) |

### Claim key (ECO-08; review slice 012 T066)

`EconomyOperations.ExternalClaimKey`: `extref::{sourceType}::{externalReference}` khi key ≤ `MaxClaimKeyBytes` = **246 byte
UTF-8**, ngược lại `extrefh::{sourceType}::{SHA-256 hex chữ thường của UTF-8(externalReference)}` (85 byte với `iap_google`).
Một reference luôn ra cùng một key; hai prefix không bao giờ trùng nhau. 246 = giới hạn key **đo được** trên Couchbase EE
7.6.12 (collection có tên, insert trong transaction lẫn KV thường, 2026-10-31): 246 byte được nhận, 247–248 bị server từ chối
(`InvalidArgumentException`), từ 249 SDK 3.10 từ chối phía client ("251 bytes including the collection ID prefix, and the
maximum is 250"); không phụ thuộc collection (đo trên hai collection, uid 4515 và 4528); ký tự nhiều byte tính theo byte. Mọi
key trước đây chạy được trên Couchbase giữ nguyên dạng. Store in-memory và file nhận key dài bất kỳ (file store tự băm tên file
dài, MAX_PATH Windows) nên trước bản sửa một reference dài chỉ lỗi `INTERNAL` trên Couchbase (người chơi đã trả tiền); dữ liệu
local cũ (file store) có claim dạng `extref::` dài sẽ không chặn lần cấp lại của cùng reference — chỉ ảnh hưởng dữ liệu dev.
`sourceType` là hằng số ngắn của code (ví dụ `iap_google`). Ledger giữ `externalReference` nguyên văn (field, không phải key).

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

## Kiểm `resources.json` khi khởi động (slice 015 — FG-018, ECO-07)

Module khai `ConfigFileDeclaration.Required("resources.json", EconomyConfigRules.Resources)` và catalog
`ConfigCatalogDeclaration.Of(c => ResourceCatalog.Load(c.Config), "resources.json")`: platform build `ResourceCatalog` một lần
trước traffic và đăng ký nó (module không còn factory lazy trong `ConfigureServices`, BC-8). Luật `economy.resources` (một dòng
mỗi lỗi, row đếm từ 1; property lạ bị bỏ qua; `kind` so không phân biệt hoa thường):

| Lỗi | Dòng |
| --- | --- |
| không có mảng `resources` | `[module economy] config resources.json: 'resources' must be an array, found {kind}. Fix: put the resource rows in a "resources" array (ECO-07).` |
| row không phải object | `[module economy] config resources.json row {n}: the row must be an object, found {kind}. Fix: write each resource as an object (ECO-07).` |
| `id` thiếu / null / sai kiểu / rỗng | `[module economy] config resources.json row {n}: 'id' is missing. Fix: give every resource a non-empty id (ECO-07).` (cùng fix cho `is null`, `must be a string, found {kind}`, `must not be empty`) |
| `id` trùng (dòng ở row **sau**) | `[module economy] config resources.json row {n}: id '{id}' is already defined in row {first}. Fix: give every resource a unique id (ECO-07).` |
| `kind` lạ | `[module economy] config resources.json row {n}: 'kind' is '{value}'. Fix: use "currency" or "item" (ECO-07).` (kind kiểu số: `'kind' must be a string, found number`) |
| `maxQuantity` < 1 hoặc không phải số nguyên | `[module economy] config resources.json row {n}: 'maxQuantity' must be an integer of at least 1, found {value}. Fix: set the largest quantity a player may hold (ECO-07).` |
| `displayName` thiếu / sai kiểu | `[module economy] config resources.json row {n}: 'displayName' is missing. Fix: give every resource a display name (ECO-07).` |

BC-1: một `resources.json` trước đây chỉ hỏng khi có request đọc nó giờ làm startup fail với các dòng trên. Game có thể thêm luật
của mình trên cùng file (ví dụ CASH/GOLD phải là currency); mỗi dòng nêu module khai báo. `ResourceReferences.Read(context)` đọc
lỏng các row hợp lệ cho luật của module khác (IAP, sample game). Định dạng dòng chung: PCR platform §21.

## Route catalog và section (slice 014 — FG-026, FG-027, FG-028)

Route khai trong business project: `EconomyRoutes` (`src/GameServer.Modules.Economy/EconomyRoutes.cs`, module `economy`)
và `CheatRoutes` (`src/GameServer.Modules.Economy/CheatRoutes.cs`, module `economy.cheat`), liệt kê ở
`ModuleDescriptor.Routes` của từng module; endpoint map từ declaration. Bảng route lấy từ export của SampleHost
(`--environment Local --export-wire-contract`, format `gameserver.wire-contract/1`). Endpoint check lúc khởi động, quy
tắc đặt tên và điều kiện hosting: PCR platform mục 20. `cheat.grant` (profile Dev) có hằng số
`FrameworkRoutes.CheatGrant` trong gói Unity; route GM không sinh.

| Route | Method | Template | Profile | Operation | Permission | Condition |
| --- | --- | --- | --- | --- | --- | --- |
| `cheat.grant` | POST | `/api/v1/cheat/grant` | Dev | `cheat.grant` | — | — |
| `economy.adjust` | POST | `/gm/v1/players/{playerId}/resources/adjust` | Gm | `economy.adjust` | `resource.adjust` | — |
| `economy.ledger` | GET | `/gm/v1/players/{playerId}/ledger` | Gm | — | `player.read` | — |

Section (mapper typed `AddAggregateWireMapper<WalletState, Wire.WalletState>` và `<InventoryState, Wire.InventoryState>`
trong `EconomyModule.cs`, không member hidden; Unity `FrameworkSections.Wallet` / `Inventory` và `WalletId` /
`InventoryId`):

| Section | Message | Hidden (không lên wire) | May-stay-default |
| --- | --- | --- | --- |
| `inventory` | `gameserver.economy.v1.InventoryState` | — | — |
| `wallet` | `gameserver.economy.v1.WalletState` | — | — |

`WireConformance` (TestKit) kiểm mapper mang mọi member không hidden lên wire và set mọi field proto
(`tests/GameServer.Platform.Web.Tests/WireConformanceTests.cs`).

## Grant từ account initializer (slice 016 — FG-015)

`EconomyOperations.Grant`/`Debit` dùng được trong `IAccountInitializer` như trong command (initializer phải khai `wallet`/
`inventory` trong `Touches`). Ledger entry đi theo receipt tạo tài khoản `identity.create_account` và được relay như outbox
của command: `operation = identity.create_account`, `receiptId` = id receipt tạo tài khoản, `actorId` = id của player (actor
`system` với `ActorId` = player id, nên row ledger và payload `actorId` đều là player), `sourceType` do initializer đặt (sample và
game đầu tiên: `account_init`). Grant vượt `maxQuantity` hoặc id lạ làm hỏng việc tạo tài khoản (không tạo gì, login
`INTERNAL`). Mỗi entry khoảng 278 byte trong receipt: ở `ReceiptMaxBytes` mặc định 4096 vừa khoảng 11 entry (PCR platform §22).
