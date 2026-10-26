# Implementation Plan: GM backend + minimal frontend (P0 slice 004)

**Branch**: `004-gm` | **Date**: 2026-10-26 | **Spec**: [spec.md](spec.md)

## Summary

Module framework `gm` (business + adapters): staff/grant model, role templates, Development local auth + bootstrap,
scheme `GmCookie`, lookup/search/ledger/receipts (paged), audit projection, static UI. Core thêm `AuditEntry` +
`ICommandContext.AddAudit` để mọi module ghi audit trong receipt. Economy thêm `economy.adjust` (GM); SampleGame thêm
`gm.progression.set`. Platform thêm `IPagedQueryStore` và `MapGmCommand` (JSON, route player đích).

## Research / decisions

- **R1 Audit trong receipt**: helper ở Core (`AuditExtensions.AddAudit`) ghi outbox kind `gm_audit`; module `gm` đăng ký
  projection. Adjust tham chiếu `receiptId` (không chép delta); progression ghi before/after.
- **R2 Actor GM cho command**: endpoint đặt `ActorContext.PlayerId = {playerId}` của route; `receiptId` gồm player đích (001 review).
- **R3 Paging**: keyset cursor = base64(json{v: giá trị orderBy, k: key}); `pageSize` 1–200 bắt buộc.
- **R4 Mật khẩu Development**: PBKDF2-SHA256 100 000 vòng (chỉ dùng cho local auth Development).
- **R5 Cookie session**: id 256-bit, lưu hash trong `gm_sessions` có expiry 8 h; logout xoá.
- **R6 Scope**: grant khớp `gameId` + `environmentId` của deployment; không khớp → không permission → 403.

## Constitution Check

| Nguyên tắc | Đáp ứng | Kết quả |
| --- | --- | --- |
| I | Mutation GM chạy qua runner (receipt, audit cùng transaction) | PASS |
| II | Module `gm` đăng ký qua descriptor; command GM ở module sở hữu dữ liệu | PASS |
| III | GM scheme riêng; permission + scope ở backend mỗi request; validator kiểm permission catalog | PASS |
| IV | Cache 60 s có số; paging ≤ 200 | PASS |
| V | Problem Details JSON; PCR gm | PASS |
| VI | HTTP tests ACC-09/10/24/27/45 | PASS |
| VII | Audit + correlation id | PASS |

## Structure

```text
src/GameServer.Core/Commands/Audit.cs
src/GameServer.Platform/Persistence/PagedQuery.cs (+ in-memory, Couchbase impl)
src/GameServer.Platform.Web/Commands/GmCommandEndpointFactory.cs
src/GameServer.Platform.Web/Validation/EndpointSecurityValidator.cs   # GM permission catalog
src/GameServer.Modules.Gm/                 # staff, roles, password hashing
src/GameServer.Modules.Gm.Adapters/        # module, GmCookie scheme, bootstrap, endpoints, ui/index.html
src/GameServer.Modules.Economy*/           # economy.adjust
samples/SampleGame/*                       # gm.progression.set
tests/GameServer.Gm.Tests/
```
