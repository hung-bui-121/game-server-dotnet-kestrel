# Implementation Plan: Identity & Session — guest (P0 slice 002)

**Branch**: `002-identity-session` | **Date**: 2026-10-26 | **Spec**: [spec.md](spec.md)

## Summary

Guest login (`localId` + secret, HMAC-pepper) tạo/tìm player qua mapping `identities`; mỗi login tăng
`pa_profile.sessionEpoch` bằng `IPlayerAggregateWriter` và cấp session token 256-bit (document `sessions` theo hash,
expiry trượt 30 ngày/tuyệt đối 90 ngày). Scheme `PlayerBearer` xác thực theo thứ tự deleted → ban → epoch → token,
cache ≤ 60 s. Xác thực chuyển vào `ProfileEnforcementMiddleware` theo profile (SEC-02). Ban theo nhóm command qua
hook `ICommandGuard`. Rate limit login per `localId`/IP. Platform thêm `IKeyValueStore`, `IPlayerAggregateWriter`,
`ExpiringCache`, `FixedWindowCounter`, `ICommandGuard`.

## Technical Context

Như slice 001 (C# 14 / .NET 10, ASP.NET Core Minimal API, CouchbaseNetClient 3.10.0, Google.Protobuf, xUnit v3,
Shouldly, NetArchTest). Không thêm package.

**Constraints**: session 256-bit; TTL 30/90 ngày, touch > 24 h; cache 60 s; login 10/phút/localId, 60 thất bại/phút/IP,
cảnh báo 600/phút/IP (Phụ lục B).

## Constitution Check

| Nguyên tắc | Đáp ứng | Kết quả |
| --- | --- | --- |
| I | Epoch đổi qua `IPlayerAggregateWriter` (transaction); `identities`/`sessions` ngoài transaction qua `IKeyValueStore` từ chối `pa_*`/`receipts` | PASS |
| II | Identity đăng ký scheme, guard, public route qua descriptor/DI; không sửa Core ngoài contract chung | PASS |
| III | Xác thực theo profile, deny-by-default giữ nguyên; token không log; pepper từ secret config | PASS |
| IV | Rate limit có số; cache có TTL | PASS |
| V | Mã lỗi mới vào catalog core; `ErrorEnvelope.details` thêm field mới (không breaking) | PASS |
| VI | Unit + HTTP test; hai host chung store cho ACC-20 | PASS |
| VII | Metric `login_*`, `session_replaced_total` | PASS |

## Project Structure

```text
src/GameServer.Core/Errors/CoreErrorCodes.cs          # + 5 codes
src/GameServer.Core/Modules/ModuleDescriptor.cs       # + PublicEndpoints
src/GameServer.Platform/Persistence/KeyValueStore.cs  # IKeyValueStore
src/GameServer.Platform/Aggregates/PlayerAggregateWriter.cs
src/GameServer.Platform/Caching/ExpiringCache.cs
src/GameServer.Platform/Limits/FixedWindowCounter.cs
src/GameServer.Platform/Commands/ICommandGuard.cs     # + runner hook
src/GameServer.Platform.Testing/InMemoryDocumentStore.cs   # + IKeyValueStore, clock-aware expiry
src/GameServer.Platform.Couchbase/CouchbaseDocumentStore.cs # + IKeyValueStore
src/GameServer.Platform.Web/Auth/ActorSchemeOptions.cs
src/GameServer.Platform.Web/Middleware/ProfileEnforcementMiddleware.cs  # authenticates per profile
src/GameServer.Modules.Identity/                      # ProfileState (+ban, deleted, identities), credential rules
src/GameServer.Modules.Identity.Adapters/             # GuestLoginService, PlayerBearer handler, BanCommandGuard, endpoints
tests/GameServer.Platform.Tests/Identity*/…           # unit
tests/GameServer.Platform.Web.Tests/IdentityTests.cs  # HTTP, two-instance takeover
```

## Complexity Tracking

Không có vi phạm.
