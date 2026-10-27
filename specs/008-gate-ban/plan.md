# Implementation Plan: Maintenance / version gate, ban, display name (P0.5 slice 008)

**Branch**: `008-gate-ban` | **Date**: 2026-10-27 | **Spec**: [spec.md](spec.md)

## Summary

Platform: document `gate` (collection `gate`, transaction-only) + `StoreGateEvaluator` (cache ≤ 10 s) thay
`AllowAllGateEvaluator`; `IGateEvaluator` trả `GateResult` có details. Module gm: GM API đọc/sửa gate qua `IAuditedStore`
(dùng chung helper audit với staff). Module identity: GM command `identity.ban`/`identity.unban` (`player.ban`) qua
`MapGmCommand`; player command `profile.set_display_name` + `IDisplayNamePolicy`. Kiểm TTL cache (PERF-08). Test
ACC-30/31 trên 2 instance + test SDK với server thật.

## Technical Context

**Language/Version**: C# / .NET 10 (SDK 10.0.403)
**Primary Dependencies**: như 007 (không thêm package)
**Storage**: Couchbase / in-memory; collection mới `gate`
**Testing**: xUnit v3 + Shouldly, `TestHost` 2 instance, `GameServer.Client.Tests` (SDK ↔ host)
**Target Platform**: Linux server
**Project Type**: web-service framework
**Performance Goals**: gate check không I/O trên đường request khi cache trúng (một KV get mỗi ≤ 10 s mỗi instance)
**Constraints**: gate trước authentication; không sync-over-async; ban qua command pipeline (DAT-09)
**Scale/Scope**: 1 document gate/deployment

## Constitution Check

| Nguyên tắc | Đáp ứng | Kết quả |
| --- | --- | --- |
| I | Ban/unban/display name = command qua runner (một transaction, receipt); gate qua `IAuditedStore` (một transaction + audit) | PASS |
| II | Command, permission, error code, collection khai trong descriptor; policy tên thay được (EXT-02) | PASS |
| III | GM endpoint có permission; audit cho mọi thay đổi gate/ban | PASS |
| IV | Cache TTL có số (≤ 10 s / ≤ 60 s), kiểm khi khởi động | PASS |
| V | Mã lỗi mới vào catalog; PCR identity/gm/platform; OpenAPI | PASS |
| VI | Test ACC-30/31 + SDK | PASS |
| VII | Log thay đổi gate/ban | PASS |

## Research / decisions

- **R1 Gate document**: một document `gate/gate`, sửa bằng `IAuditedStore` (gate không phải aggregate player; GATE-01
  ghi "aggregate ngoài player, GM sửa qua pipeline command có receipt/audit" — audit entry trong cùng transaction là
  bản ghi bền của thay đổi, idempotent theo `X-Request-Id`, như staff ở 007).
- **R2 Evaluator**: `StoreGateEvaluator` giữ snapshot + thời điểm đọc; hết TTL → đọc lại (một request làm mới, các request
  khác dùng bản cũ; `SemaphoreSlim` không chặn — `Wait(0)`). Lỗi đọc → giữ bản cũ (fail-open, log Warning).
- **R3 So sánh phiên bản**: tách theo `.`, tối đa 4 phần số nguyên ≥ 0, phần thiếu = 0; chuỗi khác → sai định dạng.
- **R4 Ban**: handler ở `GameServer.Modules.Identity` (business), endpoint GM ở adapter; scope rỗng → `[login]`;
  `untilUtc` phải > now; ban scope login → `SessionEpoch++`; audit before/after = ban cũ/mới.
- **R5 Display name**: đếm grapheme bằng `StringInfo`; regex Unicode `^[\p{L}\p{Nd}_.\-]+( [\p{L}\p{Nd}_.\-]+)*$`;
  `IDisplayNamePolicy.CheckAsync(name)` mặc định chấp nhận; mã lỗi identity 14000–14099.
- **R6 Retry-after bảo trì**: `clamp(expectedEnd - now, 30 s, 1 h)`, không có giờ → 300 s.

## Structure

```text
src/GameServer.Platform/Gate/GateDocument.cs            # model + parse + version compare
src/GameServer.Platform.Web/Middleware/GateMiddleware.cs # GateResult + details + StoreGateEvaluator
src/GameServer.Modules.Gm.Adapters/Ops/GateEndpoints.cs  # GET/PUT gate (IAuditedStore + audit)
src/GameServer.Modules.Identity/BanHandlers.cs, DisplayName.cs, IdentityErrors.cs
src/GameServer.Modules.Identity.Adapters/IdentityModule.cs (GM API + player command), Protos/identity.proto
tests/GameServer.Gm.Tests/GateTests.cs, BanTests.cs; tests/GameServer.Platform.Web.Tests/DisplayNameTests.cs
tests/GameServer.Client.Tests/GateClientTests.cs
docs/pcr/{platform,gm,identity}.md, docs/api/gm-openapi.json
```

## Complexity Tracking

Không có vi phạm (gate dùng `IAuditedStore` đã có ở 007, không phải pipeline player — cùng lý do đã ghi ở 007).
