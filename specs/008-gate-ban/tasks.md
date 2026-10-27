# Tasks: Maintenance / version gate, ban, display name (P0.5 slice 008)

**Input**: [plan.md](plan.md), [spec.md](spec.md)

## Phase 1: Foundational

- [ ] T001 Platform `GateDocument` (maintenance, minClientVersions, parse/serialize) + `ClientVersion.TryParse/Compare` trong `src/GameServer.Platform/Gate/GateDocument.cs`; collection `gate` (platform, transaction-only) trong `ModuleRegistry`
- [ ] T002 `IGateEvaluator` trả `GateResult(decision, retryAfterMs, details)`; `GateMiddleware` ghi details; `StoreGateEvaluator` (cache ≤ `GameServer:Gate:CacheSeconds` 10, fail-open, không chặn request khi làm mới) thay `AllowAllGateEvaluator` mặc định trong `src/GameServer.Platform.Web/Middleware/GateMiddleware.cs`, `GameServerOptions`
- [ ] T003 Mã lỗi identity `DISPLAY_NAME_INVALID` (14000), `DISPLAY_NAME_REJECTED` (14001) trong `src/GameServer.Modules.Identity/IdentityErrors.cs`; permission `player.ban`, `ops.maintenance`

## Phase 2: US1 + US2 — Gate (P1)

- [ ] T004 [US1] GM API `GET /gm/v1/ops/gate` (`ops.read`), `PUT /gm/v1/ops/gate/maintenance`, `PUT /gm/v1/ops/gate/min-client-version` (`ops.maintenance`, CSRF, `X-Request-Id`, `reason`) qua `IAuditedStore` + audit (helper chung với staff) trong `src/GameServer.Modules.Gm.Adapters/Ops/GateEndpoints.cs`
- [ ] T005 [US1] Test `tests/GameServer.Gm.Tests/GateTests.cs`: bật bảo trì → Player API/login 503 `MAINTENANCE` có message/expectedEnd/retryAfter trên 2 instance trong ≤ 10 s, GM + health 200, không token vẫn 503; tắt → bình thường; audit 1/thay đổi; thiếu quyền 403; validate input
- [ ] T006 [US2] Test min version: android dưới min → 426 với `updateUrl`; bằng min / nền tảng khác → qua; thiếu/sai header → 426; maintenance ưu tiên (ACC-30)
- [ ] T007 [P] [US2] Test SDK `tests/GameServer.Client.Tests/GateClientTests.cs`: SDK nhận 503/426 thật → sự kiện `Maintenance`/`UpdateRequired` có details, không retry

## Phase 3: US3 — Ban (P1)

- [ ] T008 [US3] Handlers `identity.ban` (`{reason, untilUtc?, scope[]}`, scope login → `SessionEpoch++`, audit before/after) và `identity.unban` trong `src/GameServer.Modules.Identity/BanHandlers.cs`; descriptor (permission `player.ban`, profile Gm)
- [ ] T009 [US3] Endpoint `POST /gm/v1/players/{playerId}/ban`, `/unban` (`MapGmCommand`) trong `IdentityModule` (IGmApiModule)
- [ ] T010 [US3] Test `tests/GameServer.Gm.Tests/BanTests.cs`: ban login → login `ACCOUNT_SUSPENDED` + `until`, phiên cũ bị từ chối ≤ 60 s trên instance khác; ban scope `shop` chặn mua, không thu hồi phiên; unban → login lại được; hết hạn tự mở; audit; 403/404/400 (ACC-31)

## Phase 4: US4 — Display name (P2)

- [ ] T011 [US4] `DisplayNameRules` + `IDisplayNamePolicy` (mặc định chấp nhận, thay qua `ConfigureExtensions`) + handler `profile.set_display_name` (Touches `profile`) trong `src/GameServer.Modules.Identity/DisplayName.cs`; proto `SetDisplayNameRequest/Result`; route `POST /api/v1/profile/display-name`
- [ ] T012 [US4] Test `tests/GameServer.Platform.Web.Tests/DisplayNameTests.cs` (hoặc SampleGame.Tests): hợp lệ, độ dài, ký tự, khoảng trắng, policy thay thế

## Phase 5: Polish

- [ ] T013 PERF-08: validate `Identity:CacheSeconds` 0..60, `GameServer:Gate:CacheSeconds` 0..10; PCR bảng TTL
- [ ] T014 PCR `docs/pcr/platform.md` (gate), `docs/pcr/gm.md` (gate API, `player.ban`), `docs/pcr/identity.md` (ban/unban, display name, mã lỗi); OpenAPI regenerate; SDK proto regen (identity)
- [ ] T015 Toàn bộ test (kể cả Couchbase); tech-lead-reviewer; sửa; commit
