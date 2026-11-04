# PCR — sample.game

Module game mẫu (slice 003). Nguồn: SMP-01..05, 8.4, PLY-04/05, G7.

Đúng với framework v0.7.0 (chưa phát hành).

## Aggregate

| Aggregate | Collection | Client-visible | State |
| --- | --- | --- | --- |
| `progression` | `pa_progression` | có | `{level, xp, stage}` — `stage` = stage cao nhất đã mở; level = 1 + xp/100 |
| `battle` | `pa_battle` | có | `{activeBattle?, stats{started, won, lost, abandoned, expired}}` |

`activeBattle = {battleId, stage, startedAtUtc, expiresAtUtc, seed, configVersion, xp, rewards[], drops[]}` — phần
thưởng chụp lúc start (PLY-05).

## Command → Touches / Reads

| Operation | Route | Touches | Reads |
| --- | --- | --- | --- |
| `battle.start` | `POST /api/v1/battle/start` | `battle` | `progression` |
| `battle.end` | `POST /api/v1/battle/end` | `battle`, `progression`, `wallet`, `inventory` | — |
| `shop.buy` | `POST /api/v1/shop/buy` | `wallet`, `inventory` | — |
| `progression.set` (GM) | `POST /gm/v1/players/{playerId}/progression` | `progression` | — |

Login (SMP-01) = module identity; IAPurchase (SMP-04) = module iap.

## Quy tắc

- Một trận mở; StartBattle khi đang có trận: `IBattleStartPolicy` mặc định Replace (`abandoned++`), Reject →
  `BATTLE_IN_PROGRESS`; trận đã quá hạn tính `expired++`.
- `battleId`, `seed` sinh xác định từ receipt (`ctx.NewId`/`NewSeed`); hạn 24 h (`battle.json` `battleHours`).
- EndBattle: không trận/sai id → `NO_ACTIVE_BATTLE`; quá hạn → commit xoá trận + `BATTLE_EXPIRED` (không thưởng);
  thua → `lost++`; thắng → thưởng qua Economy (`sourceType = battle`), xp, mở stage kế tiếp.
- Evidence = eligibility-only (`IBattleResultPolicy` thay được).
- Shop: `shop.json` `{items: {id: {price{resource, amount}, grant{resource, quantity}}}}`; quantity 1–100.

## GM (slice 004)

`progression.set`: profile `Gm`, permission `progression.edit` (catalog của module này). Body `{level?, xp?, stage?, reason}`;
ít nhất một trường, `level ≥ 1`, `xp ≥ 0`, `stage ≥ 1` (sai → 400); `stage > maxStage` → `STAGE_LOCKED`. Trường null giữ
nguyên. Audit ghi `before`/`after` (progression không có ledger).

## Error codes (40000–40099)

`STAGE_LOCKED` 40001, `BATTLE_IN_PROGRESS` 40002, `NO_ACTIVE_BATTLE` 40003, `BATTLE_EXPIRED` 40004 (commit),
`UNKNOWN_SHOP_ITEM` 40005, `BATTLE_RESULT_REJECTED` 40006.

## Kiểm `battle.json` và `shop.json` khi khởi động (slice 015 — FG-018, FR-070)

Module khai hai file bắt buộc, mỗi file một luật (bảng dưới), và hai catalog
`ConfigCatalogDeclaration.Of(c => BattleCatalog.Load(c.Config), "battle.json")`,
`ConfigCatalogDeclaration.Of(c => ShopCatalog.Load(c.Config), "shop.json")` (platform build và đăng ký; `SampleGameModule` không
còn factory lazy, BC-8). Resource được so với các row hợp lệ của `resources.json`.

| Luật | Kiểm (mỗi lỗi một dòng `[module sample.game] config {file}{location}: {problem}. Fix: {fix}.`) |
| --- | --- |
| `sample.battle` | `maxStage` số nguyên ≥ 1 (fix `set the highest stage, at least 1`); `battleHours` vắng hoặc 1..168; mảng `stages`; mỗi row: `stage` 1..maxStage và không trùng (`stage {n} is already defined in row {first}`), `xp` số nguyên ≥ 0 (`'xp' must be an integer of at least 0, found "70". Fix: give every stage an integer xp of at least 0.`), `rewards`/`drops` là mảng `{resource, quantity ≥ 1}` (vị trí `at $.stages[{i}].rewards[{j}]`); mỗi stage 1..maxStage có một row (`stage {n} has no row`; quá 10 stage thiếu → một dòng tóm tắt) |
| `sample.shop` | object `items`; key không rỗng; `price {resource, amount ≥ 1}`; `grant {resource, quantity 1..maxQuantity}`; resource chưa định nghĩa → `resource '{resource}' is not defined in resources.json. Fix: add it to resources.json or correct the line (ECO-07).` |

BC-1 trong chính repo: item `broken` (grant `unknown_item`) đã được bỏ khỏi `tests/Shared/config` (case `UNKNOWN_ITEM` chuyển sang
test handler không host). `samples/SampleHost/config` và `tests/Shared/config` qua mọi luật. Định dạng dòng chung: PCR platform §21.

## Route catalog và section (slice 014 — FG-026, FG-027, FG-028)

`SampleGameRoutes` (`samples/SampleGame/SampleGame/SampleGameRoutes.cs`, namespace server `SampleGame`; class client
cùng tên nằm ở namespace `SampleGame.Client`), liệt kê ở `SampleGameModuleDefinition.Routes`; endpoint map từ
declaration. Bảng route lấy từ export của SampleHost (`--environment Local --export-wire-contract`, format
`gameserver.wire-contract/1`). Endpoint check lúc khởi động, quy tắc đặt tên và điều kiện hosting: PCR platform mục 20.
Hằng số Unity sinh vào KestrelDemo (`samples/SampleGame/wiregen.json` → `Assets/SampleGame/Generated/`):
`SampleGameRoutes.BattleStart`, `BattleEnd`, `ShopBuy`; route GM không sinh.

| Route | Method | Template | Profile | Operation | Permission | Condition |
| --- | --- | --- | --- | --- | --- | --- |
| `battle.end` | POST | `/api/v1/battle/end` | Player | `battle.end` | — | — |
| `battle.start` | POST | `/api/v1/battle/start` | Player | `battle.start` | — | — |
| `progression.set` | POST | `/gm/v1/players/{playerId}/progression` | Gm | `progression.set` | `progression.edit` | — |
| `shop.buy` | POST | `/api/v1/shop/buy` | Player | `shop.buy` | — | — |

Section (mapper typed `AddAggregateWireMapper<ProgressionState, Wire.ProgressionState>` và `<BattleState,
Wire.BattleState>` trong `SampleGameModule.cs`; Unity `SampleGameSections.Progression` / `Battle`, client gọi
`client.Sections.Register(SampleGameSections.Table)`):

| Section | Message | Hidden (không lên wire) | May-stay-default |
| --- | --- | --- | --- |
| `battle` | `samplegame.v1.BattleState` | `ActiveBattle.ConfigVersion`, `ActiveBattle.Drops`, `ActiveBattle.Rewards`, `ActiveBattle.Xp` — phần thưởng chụp lúc start (PLY-05) và config version của trận chỉ dùng ở server | — |
| `progression` | `samplegame.v1.ProgressionState` | — | — |

`WireConformance` (TestKit) kiểm mapper mang mọi member không hidden lên wire và set mọi field proto
(`tests/GameServer.Platform.Web.Tests/WireConformanceTests.cs`).

## Account initializer và `account.json` (slice 016 — FG-015, FR-050)

- Initializer `sample.starting_gold` (`SampleAccountInitializer`, touches `wallet`, `ResourceCatalog` qua constructor) cấp
  `startingGold` gold bằng `EconomyOperations.Grant` với ledger source `account_init`, trong transaction tạo tài khoản.
- Đọc file **tuỳ chọn, chỉ server** `account.json` (`{ "startingGold": 100 }`) chỉ khi `Config.HasFile("account.json")`;
  không có file, hoặc `startingGold = 0` → không thay đổi gì, không receipt tạo tài khoản. `samples/SampleHost/config/base` có
  file (liệt kê trong `files`, không trong `clientVisible`, nên version client `sample-1+9b25b8b6bc0b` không đổi; version server
  đổi một lần: `sample-1+8a37f29ee2dc` → `sample-1+53ca1d498d7a`); `tests/Shared/config` không có, nên test full game giữ
  nguyên kỳ vọng.
- Luật `sample.account` (khai `ConfigFileDeclaration.Optional("account.json", …)`): `startingGold` là số nguyên từ 0 đến
  `maxQuantity` của gold trong `resources.json`:

```text
[module sample.game] config account.json: startingGold must be an integer from 0 to {maxQuantity} (the maxQuantity of gold in resources.json); found {value}. Fix: set the gold a new account starts with.
[module sample.game] config account.json: the file must be an object, found {kind}. Fix: write {"startingGold": …}.
[module sample.game] config account.json: startingGold grants 'gold', which is not defined in resources.json. Fix: add it to resources.json or remove account.json.
```

  Hàng gold không có `maxQuantity` hợp lệ thì luật bỏ qua giới hạn (lỗi đó do luật economy báo một lần).
- Người chơi mới trên SampleHost: login đầu tiên `is_new_player = true`, `wallet` ở `rev = 1` với 100 gold, một ledger entry
  `account_init`, một receipt `identity.create_account` (receipt 1068 byte, PCR platform §22).
