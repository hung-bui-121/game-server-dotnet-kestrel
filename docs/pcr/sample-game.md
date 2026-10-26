# PCR — sample.game

Module game mẫu (slice 003). Nguồn: SMP-01..05, 8.4, PLY-04/05, G7.

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

Login (SMP-01) = module identity; IAPurchase (SMP-04) = module iap.

## Quy tắc

- Một trận mở; StartBattle khi đang có trận: `IBattleStartPolicy` mặc định Replace (`abandoned++`), Reject →
  `BATTLE_IN_PROGRESS`; trận đã quá hạn tính `expired++`.
- `battleId`, `seed` sinh xác định từ receipt (`ctx.NewId`/`NewSeed`); hạn 24 h (`battle.json` `battleHours`).
- EndBattle: không trận/sai id → `NO_ACTIVE_BATTLE`; quá hạn → commit xoá trận + `BATTLE_EXPIRED` (không thưởng);
  thua → `lost++`; thắng → thưởng qua Economy (`sourceType = battle`), xp, mở stage kế tiếp.
- Evidence = eligibility-only (`IBattleResultPolicy` thay được).
- Shop: `shop.json` `{items: {id: {price{resource, amount}, grant{resource, quantity}}}}`; quantity 1–100.

## Error codes (40000–40099)

`STAGE_LOCKED` 40001, `BATTLE_IN_PROGRESS` 40002, `NO_ACTIVE_BATTLE` 40003, `BATTLE_EXPIRED` 40004 (commit),
`UNKNOWN_SHOP_ITEM` 40005, `BATTLE_RESULT_REJECTED` 40006.
