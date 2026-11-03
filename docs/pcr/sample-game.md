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
