# Implementation Plan: Platform Core (P0 slice 001)

**Branch**: `001-platform-core` | **Date**: 2026-10-26 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/001-platform-core/spec.md`

## Summary

Xây nền của framework: host đăng ký module tường minh và tự kiểm khi khởi động; Player API
Protobuf / GM API JSON với `ErrorEnvelope`/Problem Details và error-code catalog; registry player
aggregate (`pa_<id>`, `rev`, `schemaVersion`, upgrader, ngân sách kích thước); command runner chạy
mỗi command trong **một transaction** (đọc bundle aggregate + receipt, handler thuần, ghi aggregate
đổi + Insert receipt + claim marker), replay theo receipt, outbox trong receipt với relay inline +
sweeper. Logic runner viết trên abstraction store nhỏ có hai implementation: Couchbase (SDK 3.10.0,
`GetMulti`, `txn_meta`, `Majority`) và in-memory (unit test, tiêm lỗi). Module mẫu `Sample.Counter`
chứng minh mở rộng không sửa Core. TST-04 viết sẵn, skip khi không có Couchbase.

## Technical Context

**Language/Version**: C# 14 / .NET 10 (SDK 10.0.403, runtime 10.0.14; `global.json`)

**Primary Dependencies**: ASP.NET Core Minimal API + Kestrel; CouchbaseNetClient 3.10.0;
Google.Protobuf 3.36.2 + Grpc.Tools 2.84.0 (codegen only); System.Text.Json; System.Threading.RateLimiting

**Storage**: Couchbase Server ≥ 7.6 (scope `g_<gameId>`, collection per module/aggregate, `txn_meta`);
in-memory store cho test/dev

**Testing**: xUnit v3 4.0.2 + Shouldly 4.3.0; NetArchTest 1.3.2; Microsoft.AspNetCore.Mvc.Testing
10.0.14; BannedApiAnalyzers; integration test gated bởi `COUCHBASE_CONNSTR`

**Target Platform**: Linux server (container) baseline; dev trên Windows

**Project Type**: framework library + sample host (web-service)

**Performance Goals**: theo giả định TBD-06 (50 RPS command/instance, p95 ≤ 300 ms) — đo ở P0.5; slice
001 chỉ đảm bảo đọc aggregate ≈ 1 RTT (DAT-11) và không có bước tuần tự theo số aggregate

**Constraints**: body Player ≤ 64 KiB / GM ≤ 1 MiB; timeout 10 s / 30 s; transaction expiration
10 s, `KvDurableTimeout` 5 s; aggregate 32/64 KiB; receipt 4 KiB; resultCompact 512 B; ≤ 16
aggregate/game, ≤ 4 Touches, ≤ 2 marker; limiter `command` 200 / `gm` 50, queue 2×; per-player
concurrency 1, queue 2; sweeper 30 s; replay window 24 h

**Scale/Scope**: ~10 project src + 5 project test; 1 module mẫu; ~45 FR

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Nguyên tắc | Cách plan đáp ứng | Kết quả |
| --- | --- | --- |
| I. Một transaction / command | `CommandRunner` là đường ghi duy nhất cho `pa_*`/`rcpt::*`; một `RunTransactionAsync` gồm aggregate + receipt + marker; handler chỉ qua `ICommandContext`; arch test + analyzer | PASS |
| II. Đăng ký tường minh | `AddModule<T>()`, descriptor, Minimal API map tường minh qua `IPlayerApiModule`/`IGmApiModule`; validator phát hiện trùng; không Controllers | PASS |
| III. Deny-by-default | metadata profile bắt buộc; validator + middleware chặn khi thiếu; GM permission kiểm ở backend; scheme test chỉ ngoài Production | PASS (audit GM đầy đủ ở slice 004) |
| IV. Async, tải có giới hạn | BannedApiAnalyzers; limiter có số; commit deadline tách request token | PASS |
| V. Contract ổn định | `core.proto` + proto per module; error catalog có dải; PCR `docs/pcr/platform.md`; package pin | PASS |
| VI. Kiểm thử nhiều tầng | unit (in-memory), web test, arch test, store conformance + TST-04 gated | PASS |
| VII. Observability | correlation id = `X-Request-Id`; `Meter`/`ActivitySource` cơ bản | PASS (OTel export ở slice 005) |
| Ràng buộc kỹ thuật | .NET 10, không AOT, SDK Couchbase 3.10.0, không Hangfire/Quartz, một game/deployment | PASS |

**Re-check sau Phase 1**: PASS. Một điểm lệch câu chữ với tài liệu yêu cầu 4.1 (runner nằm ở
`GameServer.Platform` thay vì Platform.Couchbase) — giữ nguyên trách nhiệm, ghi ở Complexity Tracking
và ADR-0001.

## Project Structure

### Documentation (this feature)

```text
specs/001-platform-core/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── core.proto
│   ├── sample_counter.proto
│   ├── http-routes.md
│   └── module-api.md
├── checklists/requirements.md
└── tasks.md            # /speckit-tasks
```

### Source Code (repository root)

```text
global.json
GameServer.slnx
Directory.Build.props           # net10.0, nullable, warnings-as-errors, analyzers
Directory.Packages.props        # central package versions (research R2)
BannedSymbols.txt
proto/
└── gameserver/core/v1/core.proto
src/
├── GameServer.Core/                    # ActorContext, IClock, ErrorCode, descriptors, IGameModule,
│                                       # ICommandContext, CommandOutcome — không phụ thuộc web/DB/proto
├── GameServer.Platform/                # registry + validator (descriptor), CommandRunner, receipt,
│                                       # aggregate serializer/upgrader, outbox relay/sweeper,
│                                       # config loader (CFG-01), store abstraction, metrics
├── GameServer.Platform.Couchbase/      # ITransactionalDocumentStore (SDK 3.10), GetMulti adapter,
│                                       # provisioning, txn_meta, sweeper query
├── GameServer.Platform.Testing/        # InMemoryDocumentStore + fault injection, FakeClock
├── GameServer.Platform.Web/            # AddGameServer/UseGameServer, Protobuf binding, ErrorEnvelope,
│                                       # profiles, endpoint validator, limiters, test auth, core.proto codegen
└── GameServer.Modules.Identity/        # 001: chỉ aggregate bắt buộc `profile` (login ở slice 002)
modules/
└── Sample.Counter/
    ├── Sample.Counter/                 # aggregate counter, Increment/Get, ICounterLimitPolicy
    └── Sample.Counter.Adapters/        # endpoint map, proto mapping, wire mapper, outbox consumer
samples/
└── SampleHost/                         # composition root thử nghiệm (SampleGame đầy đủ ở slice 003)
templates/
└── module/                             # template 2 project + README
docs/
├── pcr/platform.md
├── pcr/sample-counter.md
├── adr/0001-runner-store-abstraction.md
└── guides/module-authoring.md
tests/
├── GameServer.Platform.Tests/          # unit: registry, validator, runner, outbox, config, upgrader
├── GameServer.Platform.Web.Tests/      # WebApplicationFactory: routes, errors, limits, startup fail
├── GameServer.Architecture.Tests/      # NetArchTest rules
├── Sample.Counter.Tests/               # ACC-13 extension/override
└── GameServer.Platform.Couchbase.IntegrationTests/  # store conformance + TST-04 (gated)
```

**Structure Decision**: framework theo mục 4.1–4.2 của tài liệu yêu cầu (Core / Platform.* / module
2 project / Host). `GameServer.Platform.Testing` là package phát hành cho game viết unit test.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
| --- | --- | --- |
| Thêm project `GameServer.Platform` ngoài danh sách 4.1 (runner DB-agnostic) | Unit test được toàn bộ ngữ nghĩa idempotency/transaction khi chưa có Couchbase; store conformance test giữ hai implementation giống nhau | Đặt runner trong Platform.Couchbase → không có DB thì không test được ACC-04/05/07/44 |
| Project `GameServer.Platform.Testing` phát hành | Game cần store giả để unit test command của mình | Mock từng test → không tái hiện conflict/ambiguous |
